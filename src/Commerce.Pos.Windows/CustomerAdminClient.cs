using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Commerce.Pos.Windows;

/// <summary>
/// Cookie-bearing client for the customer-management window
/// (commerce-customer-identity design.md "Desktop authorization for customer
/// create/edit"): performs a real <c>POST /account/sign-in</c> from
/// <see cref="CustomersWindow"/> and calls the SAME <c>/customers</c>
/// endpoints <c>Commerce.Web</c> uses — zero new auth surface, zero
/// POS-specific customer endpoint. Owns a WINDOW-SCOPED
/// <see cref="HttpClient"/> + <see cref="CookieContainer"/>: never persisted
/// to <c>operators.json</c>/<c>installation.json</c>, discarded when the
/// window (and this instance) closes. The server re-checks
/// <c>ManageUsers</c> on EVERY call — this client adds no authorization logic
/// of its own.
/// </summary>
public sealed class CustomerAdminClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public CustomerAdminClient(string baseUrl)
    {
        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
        };
        _httpClient = new HttpClient(handler) { BaseAddress = new Uri(baseUrl) };
    }

    /// <summary>Injects a caller-owned client for focused request-contract tests.</summary>
    public CustomerAdminClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<AdminSignInOutcome> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        const string path = "/account/sign-in";
        var endpoint = PosHttp.Endpoint(HttpMethod.Post, path);
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(path, new AdminSignInRequestDto(email, password), ct);
            // UX-only: a signed-in caller lacking ManageUsers simply sees every
            // subsequent call 403; the server re-checks regardless of this bit.
            return await AdminSignInOutcome.FromResponseAsync(response, endpoint, ct);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return AdminSignInOutcome.Failed(PosMessages.ServerUnreachable);
        }
    }

    public async Task<IReadOnlyList<CustomerAdminRecordDto>?> ListCustomersAsync(CancellationToken ct = default)
    {
        const string path = "/customers";
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, path);
        try
        {
            using var response = await _httpClient.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                await PosHttp.LogFailureWithBodyAsync(endpoint, response, ct);
                return null;
            }

            return await PosHttp.TryReadJsonAsync<List<CustomerAdminRecordDto>>(response, endpoint, ct);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return null;
        }
    }

    public Task<CustomerAdminMutationOutcome> CreateCustomerAsync(
        CreateCustomerAdminRequestDto request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/customers", () => _httpClient.PostAsJsonAsync("/customers", request, ct), ct);

    public Task<CustomerAdminMutationOutcome> UpdateCustomerAsync(
        Guid id, UpdateCustomerAdminRequestDto request, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Put, $"/customers/{id}", () => _httpClient.PutAsJsonAsync($"/customers/{id}", request, ct), ct);

    private static async Task<CustomerAdminMutationOutcome> SendAsync(
        HttpMethod method, string path, Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        var endpoint = PosHttp.Endpoint(method, path);
        try
        {
            using var response = await send();
            if (response.IsSuccessStatusCode)
            {
                return CustomerAdminMutationOutcome.Succeeded();
            }

            await PosHttp.LogFailureWithBodyAsync(endpoint, response, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => CustomerAdminMutationOutcome.Failed(PosMessages.SessionExpired),
                HttpStatusCode.Forbidden => CustomerAdminMutationOutcome.Forbidden(),
                HttpStatusCode.NotFound => CustomerAdminMutationOutcome.NotFound(),
                HttpStatusCode.BadRequest => CustomerAdminMutationOutcome.Failed(PosMessages.InvalidData),
                _ => CustomerAdminMutationOutcome.Failed(PosHttp.MessageFor(response)),
            };
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return CustomerAdminMutationOutcome.Failed(PosMessages.ServerUnreachable);
        }
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed record AdminSignInRequestDto(string Email, string Password);

public sealed record AdminSignedInResponseDto(Guid OrganizationId, Guid UserId, string DisplayName, int Permissions);

public sealed record AdminSignInOutcome(AdminSignInOutcomeKind Kind, int Permissions, string? ErrorMessage)
{
    public static AdminSignInOutcome SignedIn(int permissions) => new(AdminSignInOutcomeKind.SignedIn, permissions, null);

    public static AdminSignInOutcome InvalidCredentials() =>
        new(AdminSignInOutcomeKind.InvalidCredentials, 0, PosMessages.InvalidCredentials);

    public static AdminSignInOutcome Failed(string message) => new(AdminSignInOutcomeKind.Failed, 0, message);

    /// <summary>Shared by both cookie-session admin clients: status first, JSON only when it is there.</summary>
    internal static async Task<AdminSignInOutcome> FromResponseAsync(HttpResponseMessage response, string endpoint, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized)
        {
            PosHttp.LogFailure(endpoint, response, "email or password rejected");
            return InvalidCredentials();
        }

        if (!response.IsSuccessStatusCode)
        {
            await PosHttp.LogFailureWithBodyAsync(endpoint, response, ct);
            return Failed(PosHttp.MessageFor(response));
        }

        var body = await PosHttp.TryReadJsonAsync<AdminSignedInResponseDto>(response, endpoint, ct);
        if (body is null)
        {
            PosHttp.LogFailure(endpoint, response, "no usable response body");
            return Failed(PosMessages.UnexpectedResponse);
        }

        return SignedIn(body.Permissions);
    }
}

public enum AdminSignInOutcomeKind
{
    SignedIn,
    InvalidCredentials,
    Failed,
}

/// <summary>Mirrors `Commerce.Cloud.Api.Persistence.CustomerRecord`'s wire shape.</summary>
public sealed record CustomerAdminRecordDto(
    Guid Id, Guid OrganizationId, string CustomerKind, string DisplayName, string? LegalName,
    string TaxIdType, string? TaxId, string TaxCondition, string? Phone, string? Email,
    string? AddressStreet, string? AddressNumber, string? Neighborhood, string? Locality,
    string? Province, string? PostalCode, string? DeliveryNotes, decimal? DiscountPercentage,
    string? PaymentTerms, string? Notes, bool IsEnabled, DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId, DateTimeOffset UpdatedAtUtc);

/// <summary>Mirrors `Commerce.Cloud.Api.Endpoints.CreateCustomerRequest`.</summary>
public sealed record CreateCustomerAdminRequestDto(
    string CustomerKind, string DisplayName, string? LegalName,
    string TaxIdType, string? TaxId, string TaxCondition,
    string? Phone, string? Email,
    string? AddressStreet, string? AddressNumber, string? Neighborhood,
    string? Locality, string? Province, string? PostalCode,
    string? DeliveryNotes, decimal? DiscountPercentage, string? PaymentTerms, string? Notes);

/// <summary>Mirrors `Commerce.Cloud.Api.Endpoints.UpdateCustomerRequest`.</summary>
public sealed record UpdateCustomerAdminRequestDto(
    string DisplayName, string? LegalName,
    string TaxIdType, string? TaxId, string TaxCondition,
    string? Phone, string? Email,
    string? AddressStreet, string? AddressNumber, string? Neighborhood,
    string? Locality, string? Province, string? PostalCode,
    string? DeliveryNotes, decimal? DiscountPercentage, string? PaymentTerms, string? Notes,
    bool IsEnabled);

public sealed record CustomerAdminMutationOutcome(CustomerAdminMutationKind Kind, string? ErrorMessage)
{
    public static CustomerAdminMutationOutcome Succeeded() => new(CustomerAdminMutationKind.Succeeded, null);

    public static CustomerAdminMutationOutcome Forbidden() =>
        new(CustomerAdminMutationKind.Forbidden, PosMessages.NoPermissionToManageCustomers);

    public static CustomerAdminMutationOutcome NotFound() =>
        new(CustomerAdminMutationKind.NotFound, PosMessages.CustomerNotFound);

    public static CustomerAdminMutationOutcome Failed(string message) => new(CustomerAdminMutationKind.Failed, message);
}

public enum CustomerAdminMutationKind
{
    Succeeded,
    Forbidden,
    NotFound,
    Failed,
}
