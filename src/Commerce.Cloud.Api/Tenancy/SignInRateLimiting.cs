using System.Text.Json;
using System.Threading.RateLimiting;

namespace Commerce.Cloud.Api.Tenancy;

/// <summary>
/// Sign-in attempt limiting for everyone (railway-deploy-pipeline S13): staff `POST /account/sign-in` and customer
/// `POST /customer/sign-in`, with the same rule for every account, the platform system administrator included.
///
/// Two fixed windows, both must have room: per client (the real client address, see the forwarded-headers setup in
/// Program.cs) <see cref="DefaultPerClientPermitLimit"/> attempts per <see cref="PerClientWindow"/>, which stops one
/// address guessing across many accounts; and per submitted account (the email, trimmed and lower-cased like the user
/// store) <see cref="DefaultPerAccountPermitLimit"/> attempts per <see cref="PerAccountWindow"/>, which stops many
/// addresses guessing one account. Every attempt counts, successful or not: the limiter decides before the password is
/// checked, so a parallel burst cannot outrun it, and a rejected attempt never reaches the password check. A person who
/// mistypes a few times still signs in; the per-client ceiling leaves room for an office behind one shared address.
///
/// Built on the shared framework's rate limiter (no package): <see cref="CreateLimiter"/> is the global limiter of
/// <c>AddRateLimiter</c>, so a rejection answers 429 with Retry-After like every other policy. The account partition
/// needs the email from the JSON body, which a limiter's synchronous partitioner cannot read, so
/// <see cref="CaptureAccountAsync"/> runs just before the limiter middleware and leaves the email in
/// <see cref="HttpContext.Items"/>. Both are in-memory per process (the single-replica assumption the other limiters
/// and throttles already make).
/// </summary>
public static class SignInRateLimiting
{
    public const int DefaultPerClientPermitLimit = 20;
    public static readonly TimeSpan PerClientWindow = TimeSpan.FromMinutes(5);
    public const int DefaultPerAccountPermitLimit = 5;
    public static readonly TimeSpan PerAccountWindow = TimeSpan.FromMinutes(15);

    private static readonly string[] SignInPaths = ["/account/sign-in", "/customer/sign-in"];

    private static readonly object AccountItemKey = new();

    public static bool IsSignInRequest(HttpContext httpContext) =>
        HttpMethods.IsPost(httpContext.Request.Method)
        && SignInPaths.Any(path => httpContext.Request.Path.Equals(path, StringComparison.OrdinalIgnoreCase));

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
                        httpContext.Items[AccountItemKey] = email.Trim().ToLowerInvariant();
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

    /// <summary>The global limiter: no limit outside the sign-in routes, both partitions on them.</summary>
    public static PartitionedRateLimiter<HttpContext> CreateLimiter(
        Func<HttpContext, string> clientPartitionKey, int perClientPermitLimit, int perAccountPermitLimit) =>
        PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                IsSignInRequest(httpContext)
                    ? RateLimitPartition.GetFixedWindowLimiter("client:" + clientPartitionKey(httpContext), _ => Window(perClientPermitLimit, PerClientWindow))
                    : RateLimitPartition.GetNoLimiter(string.Empty)),
            PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
                IsSignInRequest(httpContext) && httpContext.Items[AccountItemKey] is string account
                    ? RateLimitPartition.GetFixedWindowLimiter("account:" + account, _ => Window(perAccountPermitLimit, PerAccountWindow))
                    : RateLimitPartition.GetNoLimiter(string.Empty)));

    private static FixedWindowRateLimiterOptions Window(int permitLimit, TimeSpan window) => new()
    {
        Window = window,
        PermitLimit = permitLimit,
        QueueLimit = 0,
    };
}
