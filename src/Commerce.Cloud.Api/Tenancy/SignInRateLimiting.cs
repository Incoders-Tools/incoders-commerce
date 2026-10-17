using System.Text.Json;
using System.Threading.RateLimiting;

namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Sign-in attempt limiting for everyone (railway-deploy-pipeline S13): staff `POST /account/sign-in` and customer
/// `POST /customer/sign-in`, with the same rule for every account, the platform system administrator included.
///
/// Three fixed windows, all must have room:
/// - per client (the real client address, see the forwarded-headers setup in Program.cs):
///   <see cref="DefaultPerClientPermitLimit"/> attempts per <see cref="PerClientWindow"/>, which stops one address
///   guessing across many accounts;
/// - per submitted account AND client: <see cref="DefaultPerAccountPermitLimit"/> attempts per
///   <see cref="PerAccountWindow"/>, which stops one address guessing one account;
/// - per submitted account from every client: <see cref="DefaultPerAccountCeiling"/> attempts per
///   <see cref="PerAccountWindow"/>, which caps guessing one account from many addresses. One address cannot reach
///   it on its own, so nobody can lock another person out of their account from a single address.
/// The account is the email, trimmed and lower-cased like the user store, kept apart for staff and customers.
/// Every attempt counts, successful or not: the limiter decides before the password is checked, so a parallel burst
/// cannot outrun it, and a rejected attempt never reaches the password check. A person who mistypes a few times
/// still signs in; the per-client ceiling leaves room for an office behind one shared address.
///
/// A request is a sign-in by the endpoint routing matched (<see cref="SignInEndpoint"/> metadata), never by its path
/// text, so every spelling that reaches a sign-in endpoint (a trailing slash, another casing) is limited.
///
/// Built on the shared framework's rate limiter (no package): <see cref="CreateLimiter"/> is the global limiter of
/// <c>AddRateLimiter</c>, so a rejection answers 429 with Retry-After like every other policy. The account partitions
/// need the email from the JSON body, which a limiter's synchronous partitioner cannot read, so
/// <see cref="CaptureAccountAsync"/> runs just before the limiter middleware and leaves the email in
/// <see cref="HttpContext.Items"/>. Both are in-memory per process (the single-replica assumption the other limiters
/// and throttles already make).
/// </summary>
public static class SignInRateLimiting
{
    public const int DefaultPerClientPermitLimit = 20;
    public static readonly TimeSpan PerClientWindow = TimeSpan.FromMinutes(5);
    public const int DefaultPerAccountPermitLimit = 5;
    public const int DefaultPerAccountCeiling = 100;
    public static readonly TimeSpan PerAccountWindow = TimeSpan.FromMinutes(15);

    /// <summary>Endpoint metadata that marks a sign-in endpoint; <paramref name="Audience"/> keeps staff and customer accounts apart.</summary>
    public sealed record SignInEndpoint(string Audience);

    public static readonly SignInEndpoint Staff = new("staff");
    public static readonly SignInEndpoint Customer = new("customer");

    private static readonly object AccountItemKey = new();

    private static SignInEndpoint? SignInEndpointOf(HttpContext httpContext) =>
        httpContext.GetEndpoint()?.Metadata.GetMetadata<SignInEndpoint>();

    public static bool IsSignInRequest(HttpContext httpContext) => SignInEndpointOf(httpContext) is not null;

    /// <summary>
    /// Middleware: on a sign-in request, reads the submitted email from the JSON body into
    /// <see cref="HttpContext.Items"/> and rewinds the body for the endpoint. The property name matches
    /// case-insensitively and the last occurrence wins, exactly like the endpoint's own binding, so no spelling of the
    /// body reaches the endpoint with an email the limiter did not count.
    /// </summary>
    public static async Task CaptureAccountAsync(HttpContext httpContext, RequestDelegate next)
    {
        if (IsSignInRequest(httpContext))
        {
            httpContext.Request.EnableBuffering();
            try
            {
                using var document = await JsonDocument.ParseAsync(httpContext.Request.Body, cancellationToken: httpContext.RequestAborted);
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    string? email = null;
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        if (property.Name.Equals("email", StringComparison.OrdinalIgnoreCase))
                        {
                            email = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(email))
                    {
                        httpContext.Items[AccountItemKey] = SignInEndpointOf(httpContext)!.Audience + ":" + email.Trim().ToLowerInvariant();
                    }
                }
            }
            catch (JsonException)
            {
                // Not JSON: the endpoint rejects the body itself; only the client partition applies.
            }
            finally
            {
                httpContext.Request.Body.Position = 0;
            }
        }

        await next(httpContext);
    }

    /// <summary>The global limiter: no limit outside the sign-in endpoints, the three partitions on them.</summary>
    public static PartitionedRateLimiter<HttpContext> CreateLimiter(
        Func<HttpContext, string> clientPartitionKey, int perClientPermitLimit, int perAccountPermitLimit, int perAccountCeiling) =>
        PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                IsSignInRequest(httpContext)
                    ? RateLimitPartition.GetFixedWindowLimiter("client:" + clientPartitionKey(httpContext), _ => Window(perClientPermitLimit, PerClientWindow))
                    : RateLimitPartition.GetNoLimiter(string.Empty)),
            PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                IsSignInRequest(httpContext) && httpContext.Items[AccountItemKey] is string account
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        "account-client:" + account + "|" + clientPartitionKey(httpContext), _ => Window(perAccountPermitLimit, PerAccountWindow))
                    : RateLimitPartition.GetNoLimiter(string.Empty)),
            PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                IsSignInRequest(httpContext) && httpContext.Items[AccountItemKey] is string account
                    ? RateLimitPartition.GetFixedWindowLimiter("account:" + account, _ => Window(perAccountCeiling, PerAccountWindow))
                    : RateLimitPartition.GetNoLimiter(string.Empty)));

    private static FixedWindowRateLimiterOptions Window(int permitLimit, TimeSpan window) => new()
    {
        Window = window,
        PermitLimit = permitLimit,
        QueueLimit = 0,
    };
}
