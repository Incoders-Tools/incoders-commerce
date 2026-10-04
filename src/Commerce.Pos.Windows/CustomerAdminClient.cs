using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace Commerce.Pos.Windows;

/// <summary>
/// The Clientes calls (commerce-customer-identity design.md "Desktop authorization for customer create/edit"): the
/// SAME <c>/customers</c> endpoints <c>Commerce.Web</c> uses, plus the <c>/geo</c> reference data of the form. Runs
/// over the shared <see cref="ManagementConnection"/> (device credential + current operator, no cookie, no
/// password); the server re-checks the operator and <c>ManageUsers</c> on EVERY call, this client adds no
/// authorization logic of its own.
/// </summary>
public sealed class CustomerAdminClient
{
    /// <summary>The server's page cap of <c>GET /geo/cities</c>.</summary>
    internal const int CityPageSize = 200;

    /// <summary>Safety stop for the city paging (a province has at most a few thousand localities).</summary>
    private const int MaxCityPages = 50;

    private readonly HttpClient _httpClient;

    /// <summary>Over the shared management client (or a caller-owned one in request-contract tests).</summary>
    public CustomerAdminClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
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

    /// <summary>The provinces of the organization's country (<c>GET /geo/provinces</c>); null when they could not be read.</summary>
    public async Task<IReadOnlyList<ProvinceOptionDto>?> ListProvincesAsync(CancellationToken ct = default)
    {
        const string path = "/geo/provinces";
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, path);
        try
        {
            using var response = await _httpClient.GetAsync(path, ct);
            if (!response.IsSuccessStatusCode)
            {
                await PosHttp.LogFailureWithBodyAsync(endpoint, response, ct);
                return null;
            }

            return await PosHttp.TryReadJsonAsync<List<ProvinceOptionDto>>(response, endpoint, ct);
        }
        catch (Exception ex) when (PosHttp.IsTransportFailure(ex, ct))
        {
            PosHttp.LogTransportFailure(endpoint, ex);
            return null;
        }
    }

    /// <summary>
    /// Every active city of <paramref name="provinceId"/> (<c>GET /geo/cities?provinceId=</c>, read page by page up
    /// to the server's cap); null when a page could not be read.
    /// </summary>
    public async Task<IReadOnlyList<CityOptionDto>?> ListCitiesAsync(string provinceId, CancellationToken ct = default)
    {
        const string path = "/geo/cities";
        var endpoint = PosHttp.Endpoint(HttpMethod.Get, path);
        var cities = new List<CityOptionDto>();
        try
        {
            for (var page = 0; page < MaxCityPages; page++)
            {
                var query = $"/geo/cities?provinceId={Uri.EscapeDataString(provinceId)}&limit={CityPageSize}&offset={page * CityPageSize}";
                using var response = await _httpClient.GetAsync(query, ct);
                if (!response.IsSuccessStatusCode)
                {
                    await PosHttp.LogFailureWithBodyAsync(endpoint, response, ct);
                    return null;
                }

                var batch = await PosHttp.TryReadJsonAsync<List<CityOptionDto>>(response, endpoint, ct);
                if (batch is null)
                {
                    return null;
                }

                cities.AddRange(batch);
                if (batch.Count < CityPageSize)
                {
                    break;
                }
            }

            return cities;
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

            var body = await PosHttp.LogFailureWithBodyAsync(endpoint, response, ct);
            return response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => CustomerAdminMutationOutcome.Failed(PosMessages.TerminalNotRecognized),
                HttpStatusCode.Forbidden when PosHttp.ParseErrorCode(body) == ManagementConnection.OperatorNotAuthorizedError =>
                    CustomerAdminMutationOutcome.Forbidden(PosMessages.OperatorNotAuthorized),
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
}

/// <summary>Mirrors `Commerce.Cloud.Api.Persistence.CustomerRecord`'s wire shape (the fields the desktop form uses).</summary>
public sealed record CustomerAdminRecordDto(
    Guid Id, Guid OrganizationId, string CustomerKind, string DisplayName,
    string TaxIdType, string? TaxId, string TaxCondition, string? Phone, string? Email,
    string? AddressStreet, string? AddressNumber, string? Neighborhood, string? PostalCode, string? DeliveryNotes,
    decimal? DiscountPercentage, string? PaymentTerms, string? Notes, bool IsEnabled, DateTimeOffset CreatedAtUtc,
    Guid CreatedByUserId, DateTimeOffset UpdatedAtUtc,
    Guid? CityId = null, string? CityName = null, string? ProvinceId = null, string? ProvinceName = null,
    string PartyType = "Person");

/// <summary>
/// Mirrors `Commerce.Cloud.Api.Endpoints.CreateCustomerRequest`: one name (`DisplayName`, a person's full name or a
/// company's legal name per `PartyType`) and the city instead of locality / province text.
/// </summary>
public sealed record CreateCustomerAdminRequestDto(
    string CustomerKind, string DisplayName,
    string TaxIdType, string? TaxId, string TaxCondition,
    string? Phone, string? Email,
    string? AddressStreet, string? AddressNumber, string? Neighborhood, string? PostalCode,
    string? DeliveryNotes, decimal? DiscountPercentage, string? PaymentTerms, string? Notes,
    Guid? CityId, string PartyType);

/// <summary>
/// Mirrors `Commerce.Cloud.Api.Endpoints.UpdateCustomerRequest`. `CityId`: null keeps the stored city,
/// <see cref="Guid.Empty"/> clears it (see <see cref="CustomerFormRules.CityChange"/>).
/// </summary>
public sealed record UpdateCustomerAdminRequestDto(
    string DisplayName,
    string TaxIdType, string? TaxId, string TaxCondition,
    string? Phone, string? Email,
    string? AddressStreet, string? AddressNumber, string? Neighborhood, string? PostalCode,
    string? DeliveryNotes, decimal? DiscountPercentage, string? PaymentTerms, string? Notes,
    bool IsEnabled, Guid? CityId, string PartyType);

/// <summary>One row of `GET /geo/provinces`.</summary>
public sealed record ProvinceOptionDto(string Id, string Name);

/// <summary>One row of `GET /geo/cities`: only the city name is shown; its postal code, when known, prefills the form.</summary>
public sealed record CityOptionDto(Guid Id, string Name, string ProvinceId, string? PostalCode = null);

public sealed record CustomerAdminMutationOutcome(CustomerAdminMutationKind Kind, string? ErrorMessage)
{
    public static CustomerAdminMutationOutcome Succeeded() => new(CustomerAdminMutationKind.Succeeded, null);

    public static CustomerAdminMutationOutcome Forbidden(string? message = null) =>
        new(CustomerAdminMutationKind.Forbidden, message ?? PosMessages.NoPermissionToManageCustomers);

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
