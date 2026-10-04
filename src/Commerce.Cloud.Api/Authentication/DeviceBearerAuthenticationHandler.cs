using System.Security.Claims;
using System.Text.Encodings.Web;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Commerce.Cloud.Api.Authentication;

public static class CloudAuthenticationSchemes
{
    public const string DeviceBearer = "DeviceBearer";

    /// <summary>
    /// Policy scheme for the management endpoints a paired terminal may call with a verified operator
    /// (admin-console-field-fixes T5): forwards to <see cref="DeviceBearer"/> for a bearer request and to the
    /// browser cookie otherwise. Never the default scheme.
    /// </summary>
    public const string StaffOrDevice = "StaffOrDevice";


    /// <summary>
    /// Customer-scoped session cookie scheme (commerce-guest-ordering
    /// design.md "Customer session"): the separate staff-cookie pattern
    /// applied a second time — its own <c>Cookie.Name</c> and
    /// <c>Cookie.Path = "/customer"</c>, named explicitly by the "Customer"
    /// authorization policy so the default staff cookie authenticates
    /// nothing under <c>/customer</c> and vice versa.
    /// </summary>
    public const string CustomerCookie = "CustomerCookie";
}

public sealed class DeviceBearerAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Installation-bound bearer scheme for Pos.Windows -> Cloud.Api `/sync`
/// (design.md "Device auth"). A desktop process carries no browser cookie, so
/// `/sync` selects this scheme by authorization policy instead of the
/// Identity cookie scheme used by the SPA.
///
/// Full rewrite (commerce-pos-installation-identity): the token carries ZERO
/// claims. It hashes the presented bearer, looks the row up via
/// <see cref="PostgresDeviceCredentialStore.FindByTokenHashAsync"/> (UNSCOPED
/// — the org is not known until this lookup resolves it), checks revocation,
/// and mints every claim from the STORED ROW — never from the presented
/// string. The prior self-signed `Bearer {organizationId}.{installationId}`
/// shape is rejected outright: it fails to hash to any known row.
/// </summary>
public sealed class DeviceBearerAuthenticationHandler : AuthenticationHandler<DeviceBearerAuthenticationOptions>
{
    public const string BranchClaimType = "branch_id";
    public const string InstallationClaimType = "installation_id";

    private readonly PostgresDeviceCredentialStore _credentialStore;

    public DeviceBearerAuthenticationHandler(
        IOptionsMonitor<DeviceBearerAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        PostgresDeviceCredentialStore credentialStore)
        : base(options, logger, encoder)
    {
        _credentialStore = credentialStore;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header)
            || !header.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var token = header.ToString()["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.Fail("Malformed device bearer token.");
        }

        var tokenHash = DeviceTokenHasher.Hash(token);
        var record = await _credentialStore.FindByTokenHashAsync(tokenHash, Context.RequestAborted);

        if (record is null)
        {
            return AuthenticateResult.Fail("Unknown device credential.");
        }

        if (record.IsRevoked)
        {
            return AuthenticateResult.Fail("Device credential revoked.");
        }

        var claims = new[]
        {
            new Claim(TenantScopeResolver.OrganizationClaimType, record.OrganizationId.ToString()),
            new Claim(BranchClaimType, record.BranchId.ToString()),
            new Claim(InstallationClaimType, record.InstallationId.ToString())
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }

    /// <summary>
    /// Answers an unauthenticated device request with 401 AND a
    /// `WWW-Authenticate` challenge. Endpoint-level credential failures (wrong
    /// email or password on `/device/operators/verify`) return a bare 401
    /// without it, which is how the POS tells "this terminal is not recognized,
    /// pair it again" apart from "wrong credentials" without a body.
    /// </summary>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer realm=\"device\"";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
