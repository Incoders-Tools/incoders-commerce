using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Commerce.Domain.Validation;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Commerce.Cloud.Api.Operations;

/// <summary>
/// `create-system-admin --email &lt;address&gt;` (railway-deploy-pipeline S12): creates the platform system
/// administrator outside Development, or resets its password when it already exists (account recovery).
///
/// The account has the shape the Development seed gives it (<see cref="TestSeedEndpoints"/>): its own organization
/// row named "Platform System Administrator" with a "Main" branch (`users.organization_id` is NOT NULL), zero roles,
/// an empty branch scope, and `is_system_admin = true` — cross-organization capability is the flag alone.
///
/// It writes through `ConnectionStrings:Commerce` (`app_runtime`) with the web's own stores and
/// <see cref="PasswordHasher{TUser}"/>: the RLS policies already let `app_runtime` create an organization with its
/// first user (`/account/bootstrap`) and set the flag (the seed), so no owner connection is needed.
///
/// An email that belongs to an organization user is refused: the command never promotes an existing user.
/// The password and its hash are never printed.
/// </summary>
public static class CreateSystemAdminCommand
{
    public const string Name = "create-system-admin";

    /// <summary>`audit_log.actor_kind` for a write made from the server console, where no user is signed in.</summary>
    public const string ConsoleActorKind = "console";

    public const string OrganizationName = "Platform System Administrator";

    private const string Usage = "usage: dotnet Commerce.Cloud.Api.dll create-system-admin --email <address>";

    private const int Failure = 1;
    private const int UsageError = 2;

    public static async Task<int> RunAsync(string[] args, IConfiguration configuration, CommandConsole console, CancellationToken ct)
    {
        if (!TryParseEmail(args, console, out var email))
        {
            return UsageError;
        }

        var connectionString = configuration.GetConnectionString("Commerce");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            console.Error.WriteLine("error: Missing ConnectionStrings:Commerce (env: ConnectionStrings__Commerce).");
            return Failure;
        }

        var password = console.ReadSecret("Password: ");
        var confirmation = console.ReadSecret("Confirm password: ");
        // The staff password rule of the web (Account.cs: staff creation, admin reset, organization creation).
        if (string.IsNullOrWhiteSpace(password))
        {
            console.Error.WriteLine("error: A password is required.");
            return Failure;
        }

        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            console.Error.WriteLine("error: The passwords do not match.");
            return Failure;
        }

        try
        {
            await using var dataSource = NpgsqlDataSource.Create(connectionString);
            return await CreateOrResetAsync(dataSource, email, password, console, ct);
        }
        catch (NpgsqlException ex)
        {
            console.Error.WriteLine($"error: The database operation failed: {ex.Message}");
            return Failure;
        }
    }

    private static bool TryParseEmail(string[] args, CommandConsole console, out string email)
    {
        email = string.Empty;
        string? raw = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--email" && i + 1 < args.Length)
            {
                raw = args[++i];
            }
            else if (args[i] != "--email")
            {
                console.Error.WriteLine($"error: Unknown argument '{args[i]}'. The password is read from the terminal, never from an argument.");
                console.Error.WriteLine(Usage);
                return false;
            }
        }

        if (!EmailAddressRules.IsValid(raw))
        {
            console.Error.WriteLine(raw is null ? "error: --email is required." : "error: --email must be a valid email address.");
            console.Error.WriteLine(Usage);
            return false;
        }

        email = raw!.Trim().ToLowerInvariant();
        return true;
    }

    private static async Task<int> CreateOrResetAsync(
        NpgsqlDataSource dataSource, string email, string password, CommandConsole console, CancellationToken ct)
    {
        var userStore = new PostgresUserAccountStore(dataSource);
        var organizationStore = new PostgresOrganizationStore(dataSource, userStore);
        var hasher = new PasswordHasher<UserAccount>();

        var existing = await userStore.FindDirectoryEntryAsync(email, ct);
        if (existing is not null)
        {
            var scope = new CloudTenantScope(existing.OrganizationId);
            var user = await userStore.LoadActorAsync(scope, existing.UserId, ct);
            if (user is null || !user.IsSystemAdmin)
            {
                console.Error.WriteLine(
                    $"error: {email} belongs to an organization user; it is not changed and never promoted to system administrator.");
                return Failure;
            }

            var hash = hasher.HashPassword(new UserAccount(existing.UserId, existing.OrganizationId, [], []), password);
            // Bumps session_version, so every session opened with the old password stops validating.
            await new PostgresPasswordRecoveryStore(dataSource).SetPasswordAsync(
                scope, existing.UserId, hash,
                new UserManagementAuditEntry(
                    ConsoleActorKind, Guid.Empty, existing.OrganizationId, "user", existing.UserId, "system-admin.password-reset", null, null),
                ct);
            console.Output.WriteLine($"Password updated for system administrator {email}.");
            if (user.IsRevoked)
            {
                console.Error.WriteLine($"warning: {email} is revoked; it cannot sign in until it is restored.");
            }
            return 0;
        }

        var organizationId = Guid.NewGuid();
        var branchId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var passwordHash = hasher.HashPassword(new UserAccount(userId, organizationId, [], []), password);
        var outcome = await organizationStore.TryCreateSystemAdministratorAsync(
            new CloudTenantScope(organizationId),
            new NewOrganization(organizationId, OrganizationName),
            new NewBranch(branchId, "Main"),
            new NewUserAccount(userId, email, passwordHash, [], []),
            new UserManagementAuditEntry(
                ConsoleActorKind, Guid.Empty, organizationId, "user", userId, "system-admin.created", null,
                JsonSerializer.Serialize(new { email })),
            ct);

        if (outcome != BootstrapOutcome.Created)
        {
            // Only a concurrent registration of the same email reaches this.
            console.Error.WriteLine($"error: {email} could not be created ({outcome}); run the command again.");
            return Failure;
        }

        console.Output.WriteLine($"Created system administrator {email}.");
        return 0;
    }
}
