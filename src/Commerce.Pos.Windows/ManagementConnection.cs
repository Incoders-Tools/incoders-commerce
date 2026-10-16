using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Commerce.Pos.Windows;

/// <summary>
/// The one connection Clientes and Personal share (admin-console-field-fixes T5): every management call carries the
/// paired device credential plus the signed-in operator (<see cref="OperatorHeader"/>), both read when the request
/// is sent, so an operator change or a re-pair applies to the very next call. There is no password prompt and no
/// cookie: the server authorizes the operator against its own store on every call (same organization, not revoked,
/// <c>ManageUsers</c>, this terminal's branch in scope) and answers <see cref="OperatorNotAuthorizedError"/> when it
/// refuses them, which raises <see cref="OperatorRefused"/> so the shell can leave the section.
/// </summary>
public sealed class ManagementConnection : IDisposable
{
    /// <summary>The signed-in operator's user id on every management request.</summary>
    public const string OperatorHeader = "X-Operator-Id";

    /// <summary>The server's answer when it refuses the operator (403 <c>{"error":"operator-not-authorized"}</c>).</summary>
    public const string OperatorNotAuthorizedError = "operator-not-authorized";

    private readonly HttpClient _httpClient;

    /// <param name="innerHandler">The transport (a plain <see cref="HttpClientHandler"/> in the app, a stub in tests).</param>
    /// <param name="deviceToken">The paired device credential at send time; null sends none (the server refuses).</param>
    /// <param name="operatorId">The signed-in operator at send time; null sends no operator (the server refuses).</param>
    public ManagementConnection(HttpMessageHandler innerHandler, Uri baseAddress, Func<string?> deviceToken, Func<Guid?> operatorId)
    {
        var handler = new DeviceOperatorHandler(deviceToken, operatorId, () => OperatorRefused?.Invoke())
        {
            InnerHandler = innerHandler,
        };
        _httpClient = new HttpClient(handler) { BaseAddress = baseAddress };
        Customers = new CustomerAdminClient(_httpClient);
        Staff = new UserAdminClient(_httpClient);
        Employees = new EmployeeAdminClient(_httpClient);
    }

    /// <summary>The app's connection: no cookie jar, the device credential and operator added per request.</summary>
    public static ManagementConnection Create(string baseUrl, Func<string?> deviceToken, Func<Guid?> operatorId) =>
        new(new HttpClientHandler { UseCookies = false }, new Uri(baseUrl), deviceToken, operatorId);

    public CustomerAdminClient Customers { get; }

    public UserAdminClient Staff { get; }

    /// <summary>Personal → Empleados: the staff file, advances and accounts (<c>/employees</c>).</summary>
    public EmployeeAdminClient Employees { get; }

    /// <summary>
    /// The server refused the current operator. May be raised off the UI thread (from the HTTP pipeline); the
    /// subscriber marshals.
    /// </summary>
    public event Action? OperatorRefused;

    public void Dispose() => _httpClient.Dispose();

    private sealed class DeviceOperatorHandler(Func<string?> deviceToken, Func<Guid?> operatorId, Action refused) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (deviceToken() is { Length: > 0 } token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            request.Headers.Remove(OperatorHeader);
            if (operatorId() is { } id)
            {
                request.Headers.Add(OperatorHeader, id.ToString());
            }

            var response = await base.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Forbidden && response.Content is not null)
            {
                // Buffered so the client can still read the body after this check.
                await response.Content.LoadIntoBufferAsync(cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (PosHttp.ParseErrorCode(body) == OperatorNotAuthorizedError)
                {
                    refused();
                }
            }

            return response;
        }
    }
}
