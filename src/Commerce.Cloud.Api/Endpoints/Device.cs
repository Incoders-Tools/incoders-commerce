using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Identity;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// ONE stateless, anonymous endpoint for the whole pairing flow (design.md
/// "Pairing flow: two steps over one stateless endpoint"). Reuses
/// `PostgresUserAccountStore`'s EXACT verification path `/account/sign-in`
/// already uses — no parallel password-check logic anywhere (Component Reuse
/// Policy).
///
/// Every credential-verification failure (unknown email, wrong password,
/// revoked user) returns the SAME generic 401, and the unknown-email path
/// still runs the dummy-hash verification for timing parity — the identical
/// 401 matrix design.md's Threat Matrix requires.
/// </summary>
public static class DeviceEndpoints
{
    /// <summary>
    /// Shared with <see cref="AccountEndpoints"/>'s own dummy hash so both
    /// timing-parity paths cost the same wall-clock time and neither
    /// duplicates the hash computation.
    /// </summary>
    internal static readonly string DummyPasswordHash =
        new PasswordHasher<UserAccount>().HashPassword(
            new UserAccount(Guid.Empty, Guid.Empty, [], []),
            "dummy-password-for-timing-parity-only");

    public static RouteGroupBuilder MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/device");

        group.MapPost("/pair", async (
            DevicePairRequest request,
            PostgresUserAccountStore userStore,
            PostgresOrganizationStore organizationStore,
            PostgresDeviceCredentialStore credentialStore,
            PasswordHasher<UserAccount> hasher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["email and password are required."],
                });
            }

            // Steps 2-5: identical to /account/sign-in, byte for byte — every
            // failure path here returns the same generic 401 and the
            // unknown-email/unknown-user paths still run a dummy hash
            // verification for timing parity.
            var directoryEntry = await userStore.FindDirectoryEntryAsync(request.Email, ct);
            if (directoryEntry is null)
            {
                hasher.VerifyHashedPassword(
                    new UserAccount(Guid.Empty, Guid.Empty, [], []), DummyPasswordHash, request.Password);
                return Results.Unauthorized();
            }

            var scope = new CloudTenantScope(directoryEntry.OrganizationId);
            var credential = await userStore.FindByEmailAsync(scope, request.Email, ct);
            if (credential is null)
            {
                hasher.VerifyHashedPassword(
                    new UserAccount(Guid.Empty, Guid.Empty, [], []), DummyPasswordHash, request.Password);
                return Results.Unauthorized();
            }

            var verification = hasher.VerifyHashedPassword(
                new UserAccount(credential.Id, credential.OrganizationId, [], []), credential.PasswordHash, request.Password);
            if (verification == PasswordVerificationResult.Failed)
            {
                return Results.Unauthorized();
            }

            if (credential.IsRevoked)
            {
                return Results.Unauthorized();
            }

            // Step 6-7: branch scope.
            var actor = await userStore.LoadActorAsync(scope, credential.Id, ct);
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            var branchScope = actor.BranchScope.ToArray();
            if (branchScope.Length == 0)
            {
                return Results.Json(
                    new DevicePairResponse("no-branches-assigned", null, null, null, null, null, null),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // Step 8: org-scoped, ordered branch listing — never trust a
            // caller-submitted branchId until it is checked against this list.
            var branches = await organizationStore.ListBranchesAsync(scope, branchScope, ct);

            if (request.BranchId is null && branches.Count > 1)
            {
                return Results.Ok(new DevicePairResponse(
                    "branch-selection-required",
                    branches.Select(b => new DeviceBranchOption(b.Id, b.Name)).ToList(),
                    null, null, null, null, null));
            }

            var selected = request.BranchId is null
                ? branches[0]
                : branches.FirstOrDefault(b => b.Id == request.BranchId.Value);

            if (selected is null)
            {
                return Results.Json(
                    new DevicePairResponse("branch-not-in-scope", null, null, null, null, null, null),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // Step 11: issue the credential — identity claims will be read
            // exclusively from THIS row going forward, never from the request.
            var issued = await credentialStore.IssueAsync(scope, request.InstallationId, selected.Id, credential.Id, ct);

            return Results.Ok(new DevicePairResponse(
                "paired",
                null,
                scope.OrganizationId,
                selected.Id,
                selected.Name,
                request.InstallationId,
                issued.PlaintextToken));
        }).AllowAnonymous();

        return group;
    }
}

public sealed record DevicePairRequest(string Email, string Password, Guid InstallationId, Guid? BranchId);

public sealed record DeviceBranchOption(Guid Id, string Name);

/// <summary>
/// status: "paired" | "branch-selection-required" | "no-branches-assigned" | "branch-not-in-scope".
/// `DeviceToken` is the plaintext secret, returned in exactly this one
/// response and never again — the server never stores it.
/// </summary>
public sealed record DevicePairResponse(
    string Status,
    IReadOnlyList<DeviceBranchOption>? Branches,
    Guid? OrganizationId,
    Guid? BranchId,
    string? BranchName,
    Guid? InstallationId,
    string? DeviceToken);
