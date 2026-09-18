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

        // Operator provisioning/status (design.md "Provisioning endpoint" and
        // "Staleness TTL and reconciliation trigger"): both device-bearer
        // authenticated, so org and branch come from the STORED
        // device_credentials row (via the claims DeviceBearerAuthenticationHandler
        // mints from it), never from the request body.
        var operatorsGroup = group.MapGroup("/operators")
            .RequireAuthorization("DeviceBearer")
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        operatorsGroup.MapPost("/verify", async (
            OperatorVerifyRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PasswordHasher<UserAccount> hasher,
            CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            if (!DeviceIdentity.TryResolve(httpContext.User, out var deviceIdentity) || deviceIdentity is null)
            {
                return Results.Unauthorized();
            }

            // Identical chain to /device/pair, byte for byte: every
            // credential-failure path returns the same generic 401, and the
            // unknown-email/unknown-user paths still run the dummy hash for
            // timing parity.
            var directoryEntry = await userStore.FindDirectoryEntryAsync(request.Email, ct);
            if (directoryEntry is null)
            {
                hasher.VerifyHashedPassword(
                    new UserAccount(Guid.Empty, Guid.Empty, [], []), DummyPasswordHash, request.Password);
                return Results.Unauthorized();
            }

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

            var actor = await userStore.LoadActorAsync(scope, credential.Id, ct);
            if (actor is null)
            {
                return Results.Unauthorized();
            }

            // Server-side assertion the design calls out: the operator's
            // BranchScope must contain THIS terminal's branch, read from the
            // stored device row via the claim, never the request body.
            if (!actor.BranchScope.Contains(deviceIdentity.BranchId))
            {
                return Results.Json(
                    new OperatorVerifyResponse("branch-not-in-scope", null, null, null, 0),
                    statusCode: StatusCodes.Status403Forbidden);
            }

            // No SignInAsync: no cookie, no session, no server-side state —
            // the response is the minimum the client needs to mint a local
            // PIN verifier. Permissions is server-derived (never
            // body-supplied), used ONLY as a UX affordance on the terminal —
            // the real gate is the server re-checking on every subsequent call.
            return Results.Ok(new OperatorVerifyResponse(
                "verified", credential.Id, credential.Email, scope.OrganizationId, (int)actor.EffectivePermissions));
        });

        operatorsGroup.MapGet("/{userId:guid}/status", async (
            Guid userId,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            if (!DeviceIdentity.TryResolve(httpContext.User, out var deviceIdentity) || deviceIdentity is null)
            {
                return Results.Unauthorized();
            }

            // Scoped by the terminal's own organization: a foreign-org
            // userId is invisible under RLS, so `actor` resolves to null and
            // the response is "inactive" — 200, never 404, so this route
            // cannot be used to probe cross-tenant account existence.
            var actor = await userStore.LoadActorAsync(scope, userId, ct);
            var isActive = actor is not null && !actor.IsRevoked && actor.BranchScope.Contains(deviceIdentity.BranchId);

            return Results.Ok(new OperatorStatusResponse(isActive ? "active" : "inactive"));
        });

        // Minimum viable cloud->local customer pull (design.md "BranchNode
        // cloud->local customer replication (built, not reused)"): device
        // bearer required, org/branch come from the STORED device_credentials
        // row via the minted claim, never from the request. `since` filters
        // to changed rows only; `disabledIds` propagates revocation.
        var customersGroup = group.MapGroup("/customers")
            .RequireAuthorization("DeviceBearer")
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        customersGroup.MapGet("/sync", async (
            DateTimeOffset since,
            HttpContext httpContext,
            PostgresCustomerStore customerStore,
            CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            if (!DeviceIdentity.TryResolve(httpContext.User, out var deviceIdentity) || deviceIdentity is null)
            {
                return Results.Unauthorized();
            }

            // Captured BEFORE the reads so the next cursor never skips a row
            // that changed while this request was in flight.
            var serverTimeUtc = DateTimeOffset.UtcNow;

            var changed = await customerStore.ListChangedSinceAsync(scope, since, ct);
            var disabledIds = await customerStore.ListDisabledSinceAsync(scope, since, ct);

            return Results.Ok(new CustomerSyncResponse(changed, disabledIds, serverTimeUtc));
        });

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

public sealed record OperatorVerifyRequest(string Email, string Password);

/// <summary>
/// status: "verified" (200) | "branch-not-in-scope" (403); every credential
/// failure is a bare 401 with no body shape of its own. `Permissions` is the
/// server-derived `int` from `actor.EffectivePermissions` (commerce-customer-
/// identity design.md "Desktop authorization for customer create/edit") —
/// used ONLY to show/hide the terminal's "Manage customers" button, never as
/// the authorization boundary itself.
/// </summary>
public sealed record OperatorVerifyResponse(string Status, Guid? UserId, string? Email, Guid? OrganizationId, int Permissions);

/// <summary>
/// status: "active" | "inactive" — 200 in both cases; "inactive" is an
/// answer, not an error, and is also returned for a foreign-org user id.
/// </summary>
public sealed record OperatorStatusResponse(string Status);

/// <summary>
/// `GET /device/customers/sync` response (design.md "Interfaces / Contracts").
/// `DisabledIds` carries ids that BECAME disabled since `since`, distinct
/// from `Customers` (which only ever carries enabled rows) — the replica
/// deletes these, propagating revocation.
/// </summary>
public sealed record CustomerSyncResponse(
    IReadOnlyList<CustomerReplicaRow> Customers,
    IReadOnlyList<Guid> DisabledIds,
    DateTimeOffset ServerTimeUtc);
