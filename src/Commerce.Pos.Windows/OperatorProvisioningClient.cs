using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Commerce.Pos.Windows;

/// <summary>
/// Typed device-bearer `HttpClient` for `POST /device/operators/verify` and
/// `GET /device/operators/{userId}/status` (design.md "File Changes").
/// Follows <see cref="DevicePairingClient"/>'s discriminated-outcome shape.
/// No branching logic beyond deserializing these two responses — covered by
/// Unit 2's server-side integration tests and Unit 3's manual walkthrough.
/// </summary>
public sealed class OperatorProvisioningClient : IOperatorVerifier
{
    private readonly HttpClient _httpClient;

    public OperatorProvisioningClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<OperatorVerifyOutcome> VerifyAsync(
        string email, string password, string deviceToken, CancellationToken ct = default)
    {
        const string path = "/device/operators/verify";
        var endpoint = PosHttp.Endpoint(HttpMethod.Post, path);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(new OperatorVerifyRequestDto(email, password)),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

            using var response = await _httpClient.SendAsync(request, ct);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                if (PosHttp.IsTerminalNotRecognized(response))
                {
                    PosHttp.LogFailure(endpoint, response, "device credential not recognized; the terminal must be paired again");
                    return OperatorVerifyOutcome.TerminalNotRecognized();
                }

                PosHttp.LogFailure(endpoint, response, "email or password rejected");
                return OperatorVerifyOutcome.InvalidCredentials();
            }

            var body = await PosHttp.TryReadJsonAsync<OperatorVerifyResponseDto>(response, endpoint, ct);
            if (body is null)
            {
                PosHttp.LogFailure(endpoint, response, "no usable response body");
                return OperatorVerifyOutcome.Failed(PosHttp.MessageFor(response));
            }

            switch (body.Status)
            {
                case "verified" when response.IsSuccessStatusCode && body.UserId is not null && body.Email is not null && body.OrganizationId is not null:
                    return OperatorVerifyOutcome.Verified(body.UserId.Value, body.Email, body.OrganizationId.Value, body.Permissions);
                case "branch-not-in-scope":
                    return OperatorVerifyOutcome.Failed(PosMessages.BranchNotInScope);
                case "no-branches-assigned":
                    return OperatorVerifyOutcome.Failed(PosMessages.NoBranchesAssigned);
                case "operator-not-permitted":
                    return OperatorVerifyOutcome.Failed(PosMessages.OperatorNotPermitted);
                default:
                    PosHttp.LogFailure(endpoint, response, $"unrecognized status '{body.Status}'");
                    return OperatorVerifyOutcome.Failed(PosHttp.MessageFor(response));
            }
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return OperatorVerifyOutcome.Failed(PosMessages.ServerUnreachable);
        }
    }

    public async Task<OperatorStatusOutcome> GetStatusAsync(Guid userId, string deviceToken, CancellationToken ct = default)
    {
        var path = $"/device/operators/{userId}/status";
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, path);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", deviceToken);

            using var response = await _httpClient.SendAsync(request, ct);

            if (PosHttp.IsTerminalNotRecognized(response))
            {
                PosHttp.LogFailure(endpoint, response, "device credential not recognized; the terminal must be paired again");
                return OperatorStatusOutcome.TerminalNotRecognized;
            }

            // Anything but a typed answer leaves the cached operator alone: an
            // ambiguous reply must never deprovision anyone.
            var body = response.IsSuccessStatusCode
                ? await PosHttp.TryReadJsonAsync<OperatorStatusResponseDto>(response, endpoint, ct)
                : null;
            if (body is null)
            {
                PosHttp.LogFailure(endpoint, response, "no usable status answer");
                return OperatorStatusOutcome.Unreachable;
            }

            return body.Status == "active" ? OperatorStatusOutcome.Active : OperatorStatusOutcome.Inactive;
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return OperatorStatusOutcome.Unreachable;
        }
    }
}

public sealed record OperatorVerifyRequestDto(string Email, string Password);

/// <summary>
/// `Permissions` (commerce-customer-identity design.md "Desktop authorization
/// for customer create/edit") is server-derived from `actor.EffectivePermissions`,
/// never body-supplied — used ONLY to show/hide the terminal's "Manage
/// customers" button.
/// </summary>
public sealed record OperatorVerifyResponseDto(string Status, Guid? UserId, string? Email, Guid? OrganizationId, int Permissions = 0);

public sealed record OperatorStatusResponseDto(string Status);

public sealed record OperatorVerifyOutcome(
    OperatorVerifyOutcomeKind Kind, Guid? UserId, string? Email, Guid? OrganizationId, int Permissions, string? ErrorMessage)
{
    public static OperatorVerifyOutcome Verified(Guid userId, string email, Guid organizationId, int permissions) =>
        new(OperatorVerifyOutcomeKind.Verified, userId, email, organizationId, permissions, null);

    public static OperatorVerifyOutcome InvalidCredentials() =>
        new(OperatorVerifyOutcomeKind.InvalidCredentials, null, null, null, 0, PosMessages.InvalidCredentials);

    public static OperatorVerifyOutcome TerminalNotRecognized() =>
        new(OperatorVerifyOutcomeKind.TerminalNotRecognized, null, null, null, 0, PosMessages.TerminalNotRecognized);

    public static OperatorVerifyOutcome Failed(string message) =>
        new(OperatorVerifyOutcomeKind.Failed, null, null, null, 0, message);
}

public enum OperatorVerifyOutcomeKind
{
    Verified,
    InvalidCredentials,
    /// <summary>The server does not know this terminal's device credential: pair it again.</summary>
    TerminalNotRecognized,
    Failed,
}

public enum OperatorStatusOutcome
{
    Active,
    Inactive,
    Unreachable,
    /// <summary>The server does not know this terminal's device credential: pair it again.</summary>
    TerminalNotRecognized,
}

/// <summary>The one online call the lock screen needs; lets the screen's logic run against a fake.</summary>
public interface IOperatorVerifier
{
    Task<OperatorVerifyOutcome> VerifyAsync(string email, string password, string deviceToken, CancellationToken ct = default);
}
