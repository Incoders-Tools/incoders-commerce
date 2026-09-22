using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Commerce.Cloud.Api.Auditing;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Email;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;

namespace Commerce.Cloud.Api.Endpoints;

/// <summary>
/// Real Postgres-backed credential verification (commerce-user-credentials
/// design.md "Org resolution at sign-in" / "Bootstrap"). Replaces the prior
/// walking-skeleton sign-in that trusted caller-submitted
/// organizationId/userId with zero password verification.
///
/// Sign-in never reveals WHICH check failed (unknown email vs. wrong
/// password vs. revoked user) — every failure path returns the same generic
/// 401, and the unknown-email path still runs a dummy hash verification for
/// timing parity (design.md risk #4).
/// </summary>
public static class AccountEndpoints
{
    /// <summary>
    /// A hash of a fixed, never-used dummy password, computed once. Verified
    /// against on the "unknown email" path so that path costs roughly the
    /// same wall-clock time as a real (wrong-password) verification — this is
    /// the ONLY use of this hash; it never gates real access.
    /// </summary>
    private static readonly string DummyPasswordHash =
        new PasswordHasher<UserAccount>().HashPassword(
            new UserAccount(Guid.Empty, Guid.Empty, [], []),
            "dummy-password-for-timing-parity-only");

    public static RouteGroupBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/account");

        group.MapPost("/sign-in", async (
            SignInRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore store,
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

            var directoryEntry = await store.FindDirectoryEntryAsync(request.Email, ct);
            if (directoryEntry is null)
            {
                // Timing parity: run a real (failing) hash verification even
                // though there is no account to check against.
                hasher.VerifyHashedPassword(
                    new UserAccount(Guid.Empty, Guid.Empty, [], []), DummyPasswordHash, request.Password);
                return Results.Unauthorized();
            }

            var scope = new CloudTenantScope(directoryEntry.OrganizationId);
            var credential = await store.FindByEmailAsync(scope, request.Email, ct);
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

            // SuccessRehashNeeded is treated as success — rehash-on-login is
            // explicitly out of scope (design.md "Hashing").

            // commerce-customer-identity "Web admin gating": server-derived,
            // never a claim — computed fresh from the store on every sign-in.
            var signedInActor = await store.LoadActorAsync(scope, credential.Id, ct);
            var permissions = signedInActor is null ? 0 : (int)signedInActor.EffectivePermissions;

            var claims = new[]
            {
                new Claim(TenantScopeResolver.OrganizationClaimType, credential.OrganizationId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, credential.Id.ToString()),
                new Claim(ClaimTypes.Name, credential.Email),
                new Claim("session_ver", credential.SessionVersion.ToString()),
            };
            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

            return Results.Ok(new SignedInResponse(credential.OrganizationId, credential.Id, credential.Email, permissions, signedInActor?.IsSystemAdmin ?? false));
        });

        // --- Renew: authenticated, self-service, known-current-password
        // change (commerce-password-recovery design.md "Renew (authenticated)").
        group.MapPost("/renew-password", async (
            RenewPasswordRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPasswordRecoveryStore recoveryStore,
            SessionVersionCache sessionVersionCache,
            PasswordHasher<UserAccount> hasher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.CurrentPassword) || string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["currentPassword and newPassword are required."],
                });
            }

            if (!TenantScopeResolver.TryResolve(httpContext.User, out var scope, out _))
            {
                return Results.Unauthorized();
            }

            var nameClaim = httpContext.User.FindFirst(ClaimTypes.Name)?.Value;
            var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (nameClaim is null || userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
            {
                return Results.Unauthorized();
            }

            var credential = await userStore.FindByEmailAsync(scope!, nameClaim, ct);
            if (credential is null || credential.IsRevoked)
            {
                return Results.Unauthorized();
            }

            var verification = hasher.VerifyHashedPassword(
                new UserAccount(credential.Id, credential.OrganizationId, [], []), credential.PasswordHash, request.CurrentPassword);
            if (verification == PasswordVerificationResult.Failed)
            {
                return Results.Unauthorized();
            }

            var newPasswordHash = hasher.HashPassword(
                new UserAccount(credential.Id, credential.OrganizationId, [], []), request.NewPassword);
            var newVersion = await recoveryStore.SetPasswordAsync(scope!, userId, newPasswordHash, ct);
            sessionVersionCache.Set(userId, newVersion);

            // Re-issue the acting browser's own cookie carrying the NEW
            // session_ver — every OTHER cookie is now stale (design.md
            // "Renew"). SignOutAsync then SignInAsync avoids stacking claims.
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            var refreshedClaims = new[]
            {
                new Claim(TenantScopeResolver.OrganizationClaimType, credential.OrganizationId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, credential.Id.ToString()),
                new Claim(ClaimTypes.Name, credential.Email),
                new Claim("session_ver", newVersion.ToString()),
            };
            var refreshedIdentity = new ClaimsIdentity(refreshedClaims, CookieAuthenticationDefaults.AuthenticationScheme);
            await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(refreshedIdentity));

            return Results.NoContent();
        }).RequireAuthorization();

        // --- Reset-request: anonymous, uniform-response forgot-password
        // start (commerce-password-recovery design.md "Reset request
        // (anonymous)"). Every branch returns the SAME empty-body 202 so the
        // response never discloses whether the email matched a user.
        group.MapPost("/reset-password/request", async (
            ResetPasswordRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPasswordRecoveryStore recoveryStore,
            ResetRequestThrottle throttle,
            IEmailSender emailSender,
            EmailOptions emailOptions,
            PasswordHasher<UserAccount> hasher,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["email"] = ["email is required."],
                });
            }

            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            if (!throttle.TryAcquire(normalizedEmail, httpContext.Connection.RemoteIpAddress))
            {
                // Throttled: still 202, no token, no mail, no log of the
                // address (design.md "Throttled response").
                return Results.StatusCode(StatusCodes.Status202Accepted);
            }

            var directoryEntry = await userStore.FindDirectoryEntryAsync(request.Email, ct);
            if (directoryEntry is null)
            {
                // Timing parity with the known-email path.
                hasher.VerifyHashedPassword(
                    new UserAccount(Guid.Empty, Guid.Empty, [], []), DummyPasswordHash, "dummy-password-for-timing-parity-only");
                return Results.StatusCode(StatusCodes.Status202Accepted);
            }

            var scope = new CloudTenantScope(directoryEntry.OrganizationId);
            var credential = await userStore.FindByEmailAsync(scope, request.Email, ct);
            if (credential is null || credential.IsRevoked)
            {
                hasher.VerifyHashedPassword(
                    new UserAccount(Guid.Empty, Guid.Empty, [], []), DummyPasswordHash, "dummy-password-for-timing-parity-only");
                return Results.StatusCode(StatusCodes.Status202Accepted);
            }

            var tokenBytes = RandomNumberGenerator.GetBytes(32);
            var token = Convert.ToBase64String(tokenBytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
            var expiresAt = DateTimeOffset.UtcNow.AddHours(1);

            await recoveryStore.IssueTokenAsync(scope, credential.Id, tokenHash, expiresAt, ct);

            var link = $"{emailOptions.PublicBaseUrl}/reset-password/{token}";
            var textBody =
                $"Reset your Commerce password by visiting {link}\n\n" +
                "This link expires in 1 hour and can be used once. " +
                "If you did not request this, ignore this email.";
            var htmlBody =
                $"<p>Reset your Commerce password by <a href=\"{link}\">clicking here</a>.</p>" +
                "<p>This link expires in 1 hour and can be used once. " +
                "If you did not request this, ignore this email.</p>";

            var sent = await emailSender.SendAsync(
                new EmailMessage(credential.Email, "Reset your Commerce password", htmlBody, textBody), ct);
            if (!sent)
            {
                var logger = loggerFactory.CreateLogger("Commerce.Cloud.Api.PasswordRecovery");
                logger.LogError("Failed to send a password-reset email; the token was still issued.");
            }

            return Results.StatusCode(StatusCodes.Status202Accepted);
        }).AllowAnonymous();

        // --- Confirm: anonymous, single-use token consumption
        // (commerce-password-recovery design.md "Reset confirm
        // (anonymous)"). Every failure branch returns the SAME generic 401.
        group.MapPost("/reset-password/confirm", async (
            ConfirmResetPasswordRequest request,
            PostgresPasswordRecoveryStore recoveryStore,
            SessionVersionCache sessionVersionCache,
            PasswordHasher<UserAccount> hasher,
            CancellationToken ct) =>
        {
            // Validate BEFORE consuming the token (design.md "Weak-input
            // ordering") — a caller mistake must not burn a single-use token.
            if (string.IsNullOrWhiteSpace(request.Token) || string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["request"] = ["token and newPassword are required."],
                });
            }

            var tokenHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(request.Token)));
            var tokenRecord = await recoveryStore.FindTokenAsync(tokenHash, ct);
            if (tokenRecord is null || tokenRecord.ConsumedAt is not null || tokenRecord.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                return Results.Unauthorized();
            }

            var scope = new CloudTenantScope(tokenRecord.OrganizationId);
            var newPasswordHash = hasher.HashPassword(
                new UserAccount(tokenRecord.UserId, tokenRecord.OrganizationId, [], []), request.NewPassword);
            var newVersion = await recoveryStore.ConsumeAndSetPasswordAsync(
                scope, tokenRecord.UserId, tokenHash, newPasswordHash, ct);
            sessionVersionCache.Set(tokenRecord.UserId, newVersion);

            return Results.NoContent();
        }).AllowAnonymous();

        var organizationGroup = app.MapGroup("/account/organizations").RequireAuthorization();
        organizationGroup.MapGet("", async (HttpContext httpContext, PostgresUserAccountStore userStore, PostgresOrganizationStore organizationStore, CancellationToken ct) =>
        {
            if (!TenantScopeResolver.TryResolve(httpContext.User, out var scope, out _)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (claim is null || !Guid.TryParse(claim, out var userId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await userStore.LoadActorAsync(scope!, userId, ct);
            if (actor is null || !actor.IsSystemAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!organizationStore.CanListOrganizations) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            return Results.Ok(await organizationStore.ListOrganizationsAsync(ct));
        });
        organizationGroup.MapPost("", async (CreateOrganizationRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresOrganizationStore organizationStore, PasswordHasher<UserAccount> hasher, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.OrganizationName) || string.IsNullOrWhiteSpace(request.AdminEmail) || string.IsNullOrWhiteSpace(request.AdminPassword)) return Results.ValidationProblem(new Dictionary<string,string[]> { ["request"] = ["organizationName, adminEmail and adminPassword are required."] });
            if (!TenantScopeResolver.TryResolve(httpContext.User, out var scope, out _)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (claim is null || !Guid.TryParse(claim, out var actorId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var actor = await userStore.LoadActorAsync(scope!, actorId, ct);
            if (actor is null || !actor.IsSystemAdmin) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var organizationId=Guid.NewGuid(); var branchId=Guid.NewGuid(); var userId=Guid.NewGuid(); var target=new CloudTenantScope(organizationId);
            var hash=hasher.HashPassword(new UserAccount(userId, organizationId, [], []),request.AdminPassword);
            var outcome=await organizationStore.TryCreateBootstrapAsync(target,new NewOrganization(organizationId,request.OrganizationName.Trim()),new NewBranch(branchId,string.IsNullOrWhiteSpace(request.BranchName)?"Main":request.BranchName.Trim()),new NewUserAccount(userId,request.AdminEmail,hash,[branchId],[new RoleDto(RoleCatalog.BusinessAdmin,Permission.ViewSales|Permission.ManageCatalog|Permission.ManageUsers|Permission.ManageBranchSettings)]),new UserManagementAuditEntry("org-user",actorId,organizationId,"organization",organizationId,"organization.bootstrapped",null,JsonSerializer.Serialize(new { organizationName=request.OrganizationName.Trim(),adminEmail=request.AdminEmail.Trim().ToLowerInvariant()})),ct);
            return outcome==BootstrapOutcome.Created ? Results.Created($"/account/organizations/{organizationId}",new CreateOrganizationResponse(organizationId,branchId,userId)) : Results.Conflict();
        });
        var branchGroup = app.MapGroup("/account/branches")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        branchGroup.MapPost("", async (CreateBranchRequest request, HttpContext httpContext, PostgresUserAccountStore userStore, PostgresOrganizationStore organizationStore, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.BranchName)) return Results.ValidationProblem(new Dictionary<string, string[]> { ["branchName"] = ["branchName is required."] });
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (claim is null || !Guid.TryParse(claim, out var callerId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var caller = await userStore.LoadActorAsync(scope, callerId, ct);
            if (caller is null || caller.IsRevoked || !caller.EffectivePermissions.HasFlag(Permission.ManageBranchSettings)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var branchId = Guid.NewGuid();
            await organizationStore.CreateBranchAsync(scope, new NewBranch(branchId, request.BranchName), ct);
            return Results.Created($"/account/branches/{branchId}", new CreateBranchResponse(branchId));
        });

        branchGroup.MapGet("", async (HttpContext httpContext, PostgresUserAccountStore userStore, PostgresOrganizationStore organizationStore, CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            var claim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (claim is null || !Guid.TryParse(claim, out var callerId)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var caller = await userStore.LoadActorAsync(scope, callerId, ct);
            if (caller is null || caller.IsRevoked || !caller.EffectivePermissions.HasFlag(Permission.ManageBranchSettings)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var branches = await organizationStore.ListBranchesAsync(scope, ct);
            return Results.Ok(branches.Select(branch => new BranchSummaryDto(branch.Id, branch.Name)));
        });
        // --- Admin-forced reset: authenticated, ManageUsers-gated, same-org
        // only (commerce-password-recovery design.md "Admin-forced reset" /
        // "Admin authorization shape"). Catalog.cs's exact pattern:
        // RequireAuthorization + TenantScopeEndpointFilter, actor loaded from
        // the store, target loaded scoped to the CALLER's org so RLS makes a
        // cross-org target indistinguishable from "no such user".
        var adminGroup = app.MapGroup("/account/users")
            .RequireAuthorization()
            .AddEndpointFilter<TenantScopeEndpointFilter>();

        adminGroup.MapGet("", async (
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);
            var callerIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (callerIdClaim is null || !Guid.TryParse(callerIdClaim, out var callerId))
            {
                return Results.Forbid();
            }

            var caller = await userStore.LoadActorAsync(scope, callerId, ct);
            if (caller is null || caller.IsRevoked || !caller.EffectivePermissions.HasFlag(Permission.ManageUsers))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            return Results.Ok(await userStore.ListStaffAsync(scope, ct));
        });
        adminGroup.MapPost("/{userId:guid}/reset-password", async (
            Guid userId,
            AdminResetPasswordRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresPasswordRecoveryStore recoveryStore,
            SessionVersionCache sessionVersionCache,
            PasswordHasher<UserAccount> hasher,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.NewPassword))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["newPassword"] = ["newPassword is required."],
                });
            }

            var scope = TenantScopeEndpointFilter.GetScope(httpContext);

            var callerIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (callerIdClaim is null || !Guid.TryParse(callerIdClaim, out var callerId))
            {
                return Results.Forbid();
            }

            var caller = await userStore.LoadActorAsync(scope, callerId, ct);
            if (caller is null || caller.IsRevoked)
            {
                return Results.Forbid();
            }

            if (!caller.EffectivePermissions.HasFlag(Permission.ManageUsers))
            {
                return Results.Forbid();
            }

            // Scoped to the CALLER's org: users_tenant_isolation RLS returns
            // zero rows for a cross-org target, so this is null identically
            // to "no such user" — no explicit organization_id comparison
            // needed (design.md "Admin authorization shape").
            var target = await userStore.LoadActorAsync(scope, userId, ct);
            if (target is null)
            {
                return Results.NotFound();
            }

            var newPasswordHash = hasher.HashPassword(
                new UserAccount(userId, scope.OrganizationId, [], []), request.NewPassword);
            var newVersion = await recoveryStore.SetPasswordAsync(scope, userId, newPasswordHash, ct);
            sessionVersionCache.Set(userId, newVersion);

            return Results.NoContent();
        });

        // --- Staff user creation and role assignment (commerce-role-taxonomy
        // design.md "Data Flow"): reuses adminGroup's exact authorization
        // shape (RequireAuthorization + TenantScopeEndpointFilter + a
        // store-loaded caller + ManageUsers), plus RoleGrantPolicy's pure
        // grant-cap check BEFORE any I/O. Permissions are read from
        // RoleCatalog only — the request carries role NAMES, never a
        // Permission set (spec: "Catalog permissions are used, not
        // body-supplied ones").
        adminGroup.MapPost("", async (
            CreateUserRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            PostgresCustomerStore customerStore,
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

            var scope = TenantScopeEndpointFilter.GetScope(httpContext);

            var callerIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (callerIdClaim is null || !Guid.TryParse(callerIdClaim, out var callerId))
            {
                return Results.Forbid();
            }

            var caller = await userStore.LoadActorAsync(scope, callerId, ct);
            if (caller is null || caller.IsRevoked)
            {
                return Results.Forbid();
            }

            if (!caller.EffectivePermissions.HasFlag(Permission.ManageUsers))
            {
                return Results.Forbid();
            }

            // commerce-customer-identity follow-up (user-credentials
            // "Optional Customer Link"): a customer-linked account cannot
            // also hold staff roles — `EffectivePermissions` already denies
            // this by construction, so rejecting the combination explicitly
            // here gives the caller a clean 400 instead of silently accepting
            // a structurally-inert account.
            if (request.CustomerId is not null && request.RoleNames is { Length: > 0 })
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["customerId"] = ["a customer-linked account cannot hold staff roles."],
                });
            }

            // Pure, no I/O, checked BEFORE any write (design.md "Grant-cap
            // location"): unknown role -> 400, reserved role or a grant that
            // exceeds the caller's own permissions -> 403, regardless of
            // what the caller otherwise holds.
            if (!RoleGrantPolicy.TryAuthorize(caller, request.RoleNames ?? [], out var roles, out var denial))
            {
                return denial == GrantDenial.UnknownRole
                    ? Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["roleNames"] = ["one or more role names are not recognized."],
                    })
                    : Results.Forbid();
            }

            var branchIds = request.BranchIds ?? [];
            if (!await userStore.BranchesBelongToOrganizationAsync(scope, branchIds, ct))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["branchIds"] = ["one or more branches do not belong to the caller's organization."],
                });
            }

            // commerce-customer-identity follow-up: the target Customer must
            // exist in the CALLER's own organization. RLS scopes the read to
            // the caller's org, so a cross-org id is indistinguishable from a
            // nonexistent one (same non-disclosure pattern as the cross-org
            // user lookups elsewhere in this file).
            Guid? customerId = null;
            if (request.CustomerId is { } requestedCustomerId)
            {
                var customer = await customerStore.FindAsync(scope, requestedCustomerId, ct);
                if (customer is null)
                {
                    return Results.NotFound();
                }
                customerId = requestedCustomerId;
            }

            var userId = Guid.NewGuid();
            var passwordHash = hasher.HashPassword(new UserAccount(userId, scope.OrganizationId, [], []), request.Password);
            var roleDtos = roles!.Select(r => new RoleDto(r.Name, r.Permissions)).ToList();
            var newValueJson = JsonSerializer.Serialize(roleDtos.Select(r => r.Name));

            var outcome = await userStore.CreateStaffUserAsync(
                scope,
                new NewUserAccount(userId, request.Email, passwordHash, branchIds, roleDtos, customerId),
                new UserManagementAuditEntry(
                    "org-user", callerId, scope.OrganizationId, "user", userId, "user.created", null, newValueJson),
                ct);

            if (outcome != CreateStaffUserOutcome.Created)
            {
                return Results.Conflict();
            }

            return Results.Created($"/account/users/{userId}", new CreateUserResponse(userId));
        });

        adminGroup.MapPut("/{userId:guid}/roles", async (
            Guid userId,
            AssignRolesRequest request,
            HttpContext httpContext,
            PostgresUserAccountStore userStore,
            CancellationToken ct) =>
        {
            var scope = TenantScopeEndpointFilter.GetScope(httpContext);

            var callerIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (callerIdClaim is null || !Guid.TryParse(callerIdClaim, out var callerId))
            {
                return Results.Forbid();
            }

            var caller = await userStore.LoadActorAsync(scope, callerId, ct);
            if (caller is null || caller.IsRevoked)
            {
                return Results.Forbid();
            }

            if (!caller.EffectivePermissions.HasFlag(Permission.ManageUsers))
            {
                return Results.Forbid();
            }

            // Scoped to the CALLER's org: a cross-org target is null
            // identically to "no such user" (same pattern as
            // /reset-password above).
            var target = await userStore.LoadActorAsync(scope, userId, ct);
            if (target is null)
            {
                return Results.NotFound();
            }

            // commerce-customer-identity follow-up (verify-report WARNING:
            // this was previously enforced ONLY by the `users_customer_has_no_roles`
            // DB CHECK, surfacing a raw Postgres exception): reject a
            // customer-linked target with a clean 400 before any write.
            if (target.CustomerId is not null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["userId"] = ["a customer-linked account cannot be granted staff roles."],
                });
            }

            if (!RoleGrantPolicy.TryAuthorize(caller, request.RoleNames ?? [], out var roles, out var denial))
            {
                return denial == GrantDenial.UnknownRole
                    ? Results.ValidationProblem(new Dictionary<string, string[]>
                    {
                        ["roleNames"] = ["one or more role names are not recognized."],
                    })
                    : Results.Forbid();
            }

            var roleDtos = roles!.Select(r => new RoleDto(r.Name, r.Permissions)).ToList();
            await userStore.ReplaceRolesAsync(scope, userId, roleDtos, "org-user", callerId, ct);

            return Results.NoContent();
        });

        group.MapPost("/sign-out", async (HttpContext httpContext) =>
        {
            await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Ok();
        });

        group.MapGet("/me", async (HttpContext httpContext, PostgresUserAccountStore userStore, CancellationToken ct) =>
        {
            if (!TenantScopeResolver.TryResolve(httpContext.User, out var scope, out _))
            {
                return Results.Unauthorized();
            }

            var userIdClaim = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var displayName = httpContext.User.FindFirst(ClaimTypes.Name)?.Value;
            if (userIdClaim is null || !Guid.TryParse(userIdClaim, out var userId))
            {
                return Results.Unauthorized();
            }

            // commerce-customer-identity "Web admin gating": /me gains one
            // store call so `permissions` is derived fresh, not baked into
            // the cookie (design.md "Session impact": none for existing
            // cookies — no claim change).
            var actor = await userStore.LoadActorAsync(scope!, userId, ct);
            var permissions = actor is null ? 0 : (int)actor.EffectivePermissions;

            return Results.Ok(new SignedInResponse(scope!.OrganizationId, userId, displayName ?? string.Empty, permissions, actor?.IsSystemAdmin ?? false));
        });

        // --- Bootstrap: one-time first-admin creation gated by a log-only
        // token (design.md "Bootstrap token delivery") ------------------------

        group.MapPost("/bootstrap/request-token", async (
            BootstrapTokenRequest request,
            PostgresUserAccountStore store,
            BootstrapTokenRegistry registry,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var scope = new CloudTenantScope(request.OrganizationId);
            if (await store.HasAnyUserAsync(scope, ct))
            {
                return Results.Conflict();
            }

            var token = registry.Issue(request.OrganizationId);
            var logger = loggerFactory.CreateLogger("Commerce.Cloud.Api.Bootstrap");
            // Plaintext token reaches ONLY server stdout (`railway logs`),
            // never the HTTP response — the anonymous caller gets 202 + empty
            // body regardless of whether they are the legitimate operator.
            logger.LogInformation(
                "Bootstrap token for organization {OrganizationId}: {Token}", request.OrganizationId, token);

            return Results.StatusCode(StatusCodes.Status202Accepted);
        }).AllowAnonymous();

        group.MapPost("/bootstrap", async (
            BootstrapRequest request,
            PostgresOrganizationStore organizationStore,
            BootstrapTokenRegistry registry,
            PasswordHasher<UserAccount> hasher,
            CancellationToken ct) =>
        {
            // Validate BEFORE token consumption (design.md "Interfaces /
            // Contracts"): a blank organizationName is a caller mistake, not
            // a spent bootstrap attempt, so it must not burn the token.
            if (string.IsNullOrWhiteSpace(request.OrganizationName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["organizationName"] = ["organizationName is required."],
                });
            }

            // Bootstrap token vs. transaction ordering (design.md): TryConsume
            // stays BEFORE the transaction. A failed transaction after this
            // point therefore burns the token by design — single-use is the
            // security property; re-usability after failure is not
            // (rejected alternative: peek-then-consume-after-commit, which
            // opens a replay window).
            if (!registry.TryConsume(request.OrganizationId, request.Token))
            {
                return Results.Unauthorized();
            }

            var scope = new CloudTenantScope(request.OrganizationId);
            var branchId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var branchName = string.IsNullOrWhiteSpace(request.BranchName) ? "Main" : request.BranchName.Trim();
            var passwordHash = hasher.HashPassword(
                new UserAccount(userId, request.OrganizationId, [], []), request.Password);

            var outcome = await organizationStore.TryCreateBootstrapAsync(
                scope,
                new NewOrganization(request.OrganizationId, request.OrganizationName.Trim()),
                new NewBranch(branchId, branchName),
                new NewUserAccount(
                    userId,
                    request.Email,
                    passwordHash,
                    [branchId],
                    [new RoleDto(RoleCatalog.BusinessAdmin, Permission.ViewSales | Permission.ManageCatalog | Permission.ManageUsers | Permission.ManageBranchSettings)]),
                ct);

            if (outcome != BootstrapOutcome.Created)
            {
                return Results.Conflict();
            }

            return Results.Ok(new BootstrapResponse(request.OrganizationId, branchId, userId));
        }).AllowAnonymous();

        return group;
    }
}

public sealed record SignInRequest(string Email, string Password);

public sealed record RenewPasswordRequest(string CurrentPassword, string NewPassword);

public sealed record ResetPasswordRequest(string Email);

public sealed record ConfirmResetPasswordRequest(string Token, string NewPassword);

public sealed record AdminResetPasswordRequest(string NewPassword);

/// <summary>
/// `Permissions` is server-derived (`actor.EffectivePermissions`), never a
/// caller-supplied value — commerce-customer-identity design.md "Web admin
/// gating".
/// </summary>
public sealed record SignedInResponse(Guid OrganizationId, Guid UserId, string DisplayName, int Permissions, bool IsSystemAdmin);

public sealed record BootstrapTokenRequest(Guid OrganizationId);

public sealed record BootstrapRequest(
    Guid OrganizationId, string Token, string OrganizationName, string? BranchName, string Email, string Password);

public sealed record BootstrapResponse(Guid OrganizationId, Guid BranchId, Guid UserId);

/// <summary>
/// `CustomerId` is optional (commerce-customer-identity follow-up,
/// user-credentials "Optional Customer Link"): when present, the created
/// account is linked to that `Customer` (which must exist in the caller's own
/// organization) and MUST NOT also carry `RoleNames` — a customer-linked
/// account cannot hold staff roles.
/// </summary>
public sealed record CreateUserRequest(string Email, string Password, string[] RoleNames, Guid[] BranchIds, Guid? CustomerId = null);

public sealed record CreateUserResponse(Guid UserId);

public sealed record UserSummaryDto(Guid UserId, string Email, IReadOnlyList<string> RoleNames, bool IsRevoked);
public sealed record CreateBranchRequest(string BranchName);
public sealed record CreateBranchResponse(Guid BranchId);
public sealed record BranchSummaryDto(Guid BranchId, string BranchName);
public sealed record OrganizationSummary(Guid Id, string Name, DateTimeOffset CreatedAt);
public sealed record CreateOrganizationRequest(string OrganizationName, string? BranchName, string AdminEmail, string AdminPassword);
public sealed record CreateOrganizationResponse(Guid OrganizationId, Guid BranchId, Guid UserId);

public sealed record AssignRolesRequest(string[] RoleNames);
