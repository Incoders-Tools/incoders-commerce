using System.Security.Claims;
using System.Text.Encodings.Web;
using Commerce.Cloud.Api.Tenancy;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Commerce.Cloud.Api.Authentication;

public static class CloudAuthenticationSchemes
{
    public const string DeviceBearer = "DeviceBearer";
}

public sealed class DeviceBearerAuthenticationOptions : AuthenticationSchemeOptions;

/// <summary>
/// Minimal installation-bound bearer scheme for Pos.Windows -> Cloud.Api
/// `/sync` (design.md "Device auth"). A desktop process carries no browser
/// cookie, so `/sync` selects this scheme by authorization policy instead of
/// the Identity cookie scheme used by the SPA.
///
/// Token shape for Unit 2 is intentionally minimal:
/// `Bearer {organizationId}.{installationId}` (both GUIDs). Full
/// installation-bound credential issuance/rotation against
/// `InstallationIdentityService` is an explicit open question in design.md
/// ("may deserve its own ADR") and is wired end-to-end alongside the real
/// `CloudSyncClient` in Unit 4 (Pos.Windows WPF shell) — this handler proves
/// the scheme-selection shape (claim -> <see cref="CloudTenantScope"/>, never
/// a caller-submitted org id) that Unit 4 will issue real tokens against.
/// </summary>
public sealed class DeviceBearerAuthenticationHandler : AuthenticationHandler<DeviceBearerAuthenticationOptions>
{
    public const string InstallationClaimType = "installation_id";

    public DeviceBearerAuthenticationHandler(
        IOptionsMonitor<DeviceBearerAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header)
            || !header.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = header.ToString()["Bearer ".Length..].Trim();
        var parts = token.Split('.', 2);
        if (parts.Length != 2
            || !Guid.TryParse(parts[0], out var organizationId) || organizationId == Guid.Empty
            || !Guid.TryParse(parts[1], out var installationId) || installationId == Guid.Empty)
        {
            return Task.FromResult(AuthenticateResult.Fail("Malformed device bearer token."));
        }

        var claims = new[]
        {
            new Claim(TenantScopeResolver.OrganizationClaimType, organizationId.ToString()),
            new Claim(InstallationClaimType, installationId.ToString())
        };
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
