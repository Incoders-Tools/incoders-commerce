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

    public async Task<AdminSignInOutcome> SignInAsync(string email, string password, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "/account/sign-in", new AdminSignInRequestDto(email, password), ct);

            if (response.StatusCode is HttpStatusCode.Unauthorized)
            {
                return AdminSignInOutcome.InvalidCredentials();
            }

            if (!response.IsSuccessStatusCode)
            {
                return AdminSignInOutcome.Failed($"HTTP {(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<AdminSignedInResponseDto>(ct);
            if (body is null)
            {
                return AdminSignInOutcome.Failed("Empty response from server.");
            }

            // UX-only: a signed-in caller lacking ManageUsers simply sees every
            // subsequent call 403; the server re-checks regardless of this bit.
            return AdminSignInOutcome.SignedIn(body.Permissions);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return AdminSignInOutcome.Failed($"Customer management requires connectivity: {ex.Message}");
        }
    }

    public async Task<IReadOnlyList<CustomerAdminRecordDto>?> ListCustomersAsync(CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.GetAsync("/customers", ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadFromJsonAsync<List<CustomerAdminRecordDto>>(ct)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public async Task<CustomerAdminMutationOutcome> CreateCustomerAsync(
        CreateCustomerAdminRequestDto request, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.PostAsJsonAsync("/customers", request, ct);
            return await ToMutationOutcomeAsync(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CustomerAdminMutationOutcome.Failed($"Customer management requires connectivity: {ex.Message}");
        }
    }

    public async Task<CustomerAdminMutationOutcome> UpdateCustomerAsync(
        Guid id, UpdateCustomerAdminRequestDto request, CancellationToken ct = default)
    {
        try
        {
            var response = await _httpClient.PutAsJsonAsync($"/customers/{id}", request, ct);
            return await ToMutationOutcomeAsync(response, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return CustomerAdminMutationOutcome.Failed($"Customer management requires connectivity: {ex.Message}");
        }
    }

    private static async Task<CustomerAdminMutationOutcome> ToMutationOutcomeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden)
        {
            return CustomerAdminMutationOutcome.Forbidden();
        }

        if (response.StatusCode is HttpStatusCode.NotFound)
        {
            return CustomerAdminMutationOutcome.NotFound();
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return CustomerAdminMutationOutcome.Failed($"HTTP {(int)response.StatusCode}: {body}");
        }

        return CustomerAdminMutationOutcome.Succeeded();
    }

    public void Dispose() => _httpClient.Dispose();
}

public sealed record AdminSignInRequestDto(string Email, string Password);

public sealed record AdminSignedInResponseDto(Guid OrganizationId, Guid UserId, string DisplayName, int Permissions);

public sealed record AdminSignInOutcome(AdminSignInOutcomeKind Kind, int Permissions, string? ErrorMessage)
{
    public static AdminSignInOutcome SignedIn(int permissions) => new(AdminSignInOutcomeKind.SignedIn, permissions, null);

    public static AdminSignInOutcome InvalidCredentials() =>
        new(AdminSignInOutcomeKind.InvalidCredentials, 0, "Invalid email or password.");

    public static AdminSignInOutcome Failed(string message) => new(AdminSignInOutcomeKind.Failed, 0, message);
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
        new(CustomerAdminMutationKind.Forbidden, "You do not have permission to manage customers.");

    public static CustomerAdminMutationOutcome NotFound() =>
        new(CustomerAdminMutationKind.NotFound, "Customer not found.");

    public static CustomerAdminMutationOutcome Failed(string message) => new(CustomerAdminMutationKind.Failed, message);
}

public enum CustomerAdminMutationKind
{
    Succeeded,
    Forbidden,
    NotFound,
    Failed,
}
