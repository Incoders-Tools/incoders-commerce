using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.Application.Management;
using Commerce.Application.Ordering;
using Commerce.Application.Payments;
using Commerce.Cloud.Api;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Email;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.HealthChecks;
using Commerce.Cloud.Api.Management;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Persistence;
using Commerce.Cloud.Api.Tenancy;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Kestrel MUST bind 0.0.0.0 and the environment-injected PORT (Railway), not
// a hardcoded port (design.md "Deploy mechanism").
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(int.Parse(port)));

// --- Persistence -----------------------------------------------------------
// Raw NpgsqlDataSource, no EF Core (design.md "Persistence access").
var connectionString = builder.Configuration.GetConnectionString("Commerce");
if (string.IsNullOrWhiteSpace(connectionString))
{
    throw new InvalidOperationException("Missing ConnectionStrings:Commerce (env: ConnectionStrings__Commerce).");
}
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<ICloudInboxStore, PostgresCloudInboxStore>();
builder.Services.AddSingleton<PostgresUserAccountStore>();
builder.Services.AddSingleton<PostgresOrganizationStore>();
builder.Services.AddSingleton<PostgresDeviceCredentialStore>();
builder.Services.AddSingleton<PostgresPasswordRecoveryStore>();
builder.Services.AddSingleton<PostgresCustomerStore>();
builder.Services.AddSingleton<PostgresCatalogStore>();
builder.Services.AddSingleton<PostgresPriceListStore>();
builder.Services.AddSingleton<PostgresPaymentStore>();
builder.Services.AddSingleton<IPaymentLedgerStore>(sp => sp.GetRequiredService<PostgresPaymentStore>());
builder.Services.AddSingleton<Commerce.Cloud.Api.Payments.PaymentEffectApplier>();
// commerce-customer-identity security fix: the resolver is the ONLY source of
// a CustomerOrderingAccess instance CustomerCatalogAccessService can act on.
builder.Services.AddSingleton<PostgresCustomerOrderingAccessStore>();
builder.Services.AddSingleton<ICustomerOrderingAccessResolver>(sp => sp.GetRequiredService<PostgresCustomerOrderingAccessStore>());

// --- Platform-read datasource (commerce-role-taxonomy design.md
// "Platform-admin cross-org read"): the ONLY cross-organization read
// capability in the system, a distinct least-privilege Postgres LOGIN.
// Registered ONLY when its connection string is configured — absent means
// PostgresPlatformAdminStore.CanListOrganizations is false and
// GET /platform/organizations fails closed with 503, NEVER falling back to
// the shared app_runtime pool.
var platformReadConnectionString = builder.Configuration.GetConnectionString("CommercePlatformRead");
if (!string.IsNullOrWhiteSpace(platformReadConnectionString))
{
    builder.Services.AddKeyedSingleton(
        PostgresPlatformAdminStore.PlatformReadDataSourceKey, NpgsqlDataSource.Create(platformReadConnectionString));
}
builder.Services.AddSingleton<PostgresPlatformAdminStore>();

// --- Credentials: PasswordHasher<UserAccount> is a framework type
// (Microsoft.AspNetCore.Identity, part of the ASP.NET Core shared framework)
// — build spike confirmed zero new PackageReference entries (design.md
// "Build spike result"). BootstrapTokenRegistry is a singleton so its
// in-memory per-org token state survives across requests within one process
// (design.md "Bootstrap token storage").
builder.Services.AddSingleton<PasswordHasher<UserAccount>>();
builder.Services.AddSingleton<PasswordHasher<PlatformAdmin>>();
builder.Services.AddSingleton<BootstrapTokenRegistry>();

// --- Session invalidation (commerce-password-recovery design.md "Session
// invalidation"): a 60s-TTL cache backs OnValidatePrincipal so a stale
// `session_ver` claim (set by any password change) is rejected without a DB
// round-trip on every authenticated request. ---------------------------
builder.Services.AddSingleton<SessionVersionCache>();
builder.Services.AddSingleton<SessionVersionValidator>();

// --- Anti-abuse (commerce-password-recovery design.md "Anti-abuse
// mechanism"): in-memory fixed-window throttle, accepted single-Railway-
// replica assumption, the same one BootstrapTokenRegistry already
// documents. -----------------------------------------------------------
builder.Services.AddSingleton<ResetRequestThrottle>();

// --- Email (commerce-password-recovery design.md "Email seam" / "Missing
// RESEND_API_KEY"): a typed Resend client backs the real sender; when the
// key is absent, LogOnlyEmailSender is registered instead so local dev and
// CI stay usable without a Resend account. -------------------------------
var emailOptions = EmailOptions.FromEnvironment();
builder.Services.AddSingleton(emailOptions);
builder.Services.AddHttpClient<ResendEmailSender>(client =>
{
    client.BaseAddress = new Uri("https://api.resend.com");
});
if (string.IsNullOrWhiteSpace(emailOptions.ResendApiKey))
{
    builder.Services.AddSingleton<IEmailSender, LogOnlyEmailSender>();
}
else
{
    builder.Services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<ResendEmailSender>());
}

// --- Guest ordering public surface (commerce-guest-ordering design.md "Org/
// branch resolution point" / "Verification state shape"): GuestOrderTarget
// is the ONE org/branch resolution point for anonymous requests. Missing or
// unparseable GuestOrdering__* config means guestOrderTargetConfigured is
// false and MapPublicOrderingEndpoints() below is NEVER called — the public
// surface does not exist rather than existing with a wrong target (the
// narrowest rollback: unset the two env vars). ---------------------------
var guestOrderTargetConfigured = GuestOrderTarget.TryFromConfiguration(builder.Configuration, out var guestOrderTarget);
if (guestOrderTargetConfigured)
{
    builder.Services.AddSingleton(guestOrderTarget!);
}
builder.Services.AddSingleton<PostgresGuestVerificationStore>();
builder.Services.AddSingleton<GuestVerificationService>();
builder.Services.AddSingleton<GuestVerificationThrottle>();

// --- Payments (commerce-payments design.md "Fail-closed approval",
// Decision 2): the gateway binding is fail-closed BY DEFAULT.
// `Payments__ManuallyRecordedApprovalEnabled=true` is the ONLY way to bind
// the real (staff-attestation) implementation — absent/false always binds
// UnavailablePaymentApproval, deliberately the inverse of
// LogOnlyEmailSender's fail-open substitution. -------------------------
var manuallyRecordedApprovalEnabled = builder.Configuration.GetValue<bool>("Payments:ManuallyRecordedApprovalEnabled");
if (manuallyRecordedApprovalEnabled)
{
    builder.Services.AddSingleton<IPaymentApprovalGateway, ManuallyRecordedApproval>();
}
else
{
    builder.Services.AddSingleton<IPaymentApprovalGateway, UnavailablePaymentApproval>();
}
builder.Services.AddSingleton<PaymentRecordingService>();

// --- Shared application services (Component Reuse Policy: reused, not
// reimplemented) --------------------------------------------------------
builder.Services.AddSingleton<IAuditSink, InMemoryAuditSink>();
builder.Services.AddSingleton<TenantAuthorizationService>();
builder.Services.AddSingleton<CatalogManagementService>();
builder.Services.AddSingleton<CustomerCatalogAccessService>();
builder.Services.AddSingleton<CustomerOrderingAccessService>();

// --- Cloud.Api-local composition --------------------------------------------
builder.Services.AddSingleton<CloudSyncReceiver>();
builder.Services.AddSingleton<CloudCatalogManagementAdapter>();
builder.Services.AddSingleton<CloudOrderStore>();
builder.Services.AddSingleton<CloudOrderSubmissionService>();

// --- Auth: Identity cookie (browser, same-origin SPA) + device bearer
// (Pos.Windows sync) — design.md "Browser auth" / "Device auth" -------------
builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
    {
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // commerce-password-recovery design.md "Session invalidation": every
        // authenticated request re-validates the `session_ver` claim against
        // SessionVersionCache; missing claim (pre-change cookie) or mismatch
        // (post-password-change cookie) is rejected fail-closed.
        options.Events.OnValidatePrincipal = context =>
        {
            var validator = context.HttpContext.RequestServices.GetRequiredService<SessionVersionValidator>();
            return validator.ValidateAsync(context);
        };
    })
    .AddScheme<DeviceBearerAuthenticationOptions, DeviceBearerAuthenticationHandler>(
        CloudAuthenticationSchemes.DeviceBearer, _ => { })
    // Second, genuinely separate cookie scheme (design.md "Scheme mutual
    // exclusivity"): its OWN Cookie.Name and Cookie.Path = "/platform", so a
    // platform cookie is not even SENT to /account, and an org cookie
    // authenticates nothing under /platform because the "PlatformAdmin"
    // policy below names only this scheme.
    .AddCookie(CloudAuthenticationSchemes.PlatformAdminCookie, options =>
    {
        options.Cookie.Name = "commerce.platform";
        options.Cookie.Path = "/platform";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // API-only scheme: never redirect to an HTML login page — return the
        // real status code instead (spec: "Org-scoped caller cannot reach a
        // platform-admin endpoint" / "Platform-admin cannot reach an
        // org-scoped endpoint").
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    // Fourth, genuinely separate cookie scheme (commerce-guest-ordering
    // design.md "Customer session"): the PlatformAdminCookie pattern applied
    // a second time — its own Cookie.Name and Cookie.Path = "/customer", so
    // a customer cookie is not even SENT to a staff path, and the "Customer"
    // policy below names only this scheme.
    .AddCookie(CloudAuthenticationSchemes.CustomerCookie, options =>
    {
        options.Cookie.Name = "commerce.customer";
        options.Cookie.Path = "/customer";
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("DeviceBearer", policy => policy
        .AddAuthenticationSchemes(CloudAuthenticationSchemes.DeviceBearer)
        .RequireAuthenticatedUser())
    .AddPolicy("PlatformAdmin", policy => policy
        .AddAuthenticationSchemes(CloudAuthenticationSchemes.PlatformAdminCookie)
        .RequireAuthenticatedUser())
    .AddPolicy("Customer", policy => policy
        .AddAuthenticationSchemes(CloudAuthenticationSchemes.CustomerCookie)
        .RequireAuthenticatedUser());

// --- Rate limiting (commerce-guest-ordering design.md "Rate limiting"):
// built-in Microsoft.AspNetCore.RateLimiting, shared framework, zero new
// PackageReference. Four named fixed-window policies attached ONLY to the
// `/public` group via RequireRateLimiting in PublicOrdering.cs — the staff,
// customer, and device endpoint groups mapped below carry NO limiter policy,
// so an abusive public burst cannot degrade them. Partitioned by remote IP;
// rejects with 429 + Retry-After. Sized for a two-branch operation (tens of
// orders/day), each ceiling ~50-100x realistic per-person traffic. --------
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };

    static string IpPartitionKey(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    options.AddPolicy(PublicRateLimitPolicies.GuestVerificationRequest, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(IpPartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(15),
            PermitLimit = 5,
            QueueLimit = 0,
        }));

    options.AddPolicy(PublicRateLimitPolicies.GuestVerificationConfirm, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(IpPartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(15),
            PermitLimit = 10,
            QueueLimit = 0,
        }));

    options.AddPolicy(PublicRateLimitPolicies.GuestOrderSubmit, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(IpPartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromHours(1),
            PermitLimit = 10,
            QueueLimit = 0,
        }));

    options.AddPolicy(PublicRateLimitPolicies.PublicCatalogRead, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(IpPartitionKey(httpContext), _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = 60,
            QueueLimit = 0,
        }));
});

// --- Health -----------------------------------------------------------------
// /health = liveness, no DB dependency. /health/ready = verifies (never
// applies) schema/RLS shape (design.md "Schema/RLS application").
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<PostgresReadinessHealthCheck>("postgres-rls-ready", tags: new[] { "ready" });

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

app.MapAccountEndpoints();
app.MapPlatformAdminEndpoints();
app.MapDeviceEndpoints();
app.MapSyncEndpoints();
app.MapCatalogEndpoints();
app.MapOrderingEndpoints();
app.MapCustomerEndpoints();
app.MapCustomerSessionEndpoints();
app.MapPricingEndpoints();
app.MapPaymentEndpoints();

// Config-gated (design.md "Org/branch resolution point" / Migration and
// Rollout "narrowest rollback"): with GuestOrdering__* absent,
// MapPublicOrderingEndpoints is NEVER called, so every /public/* route 404s
// (never mapped) rather than existing half-configured.
if (guestOrderTargetConfigured)
{
    app.MapPublicOrderingEndpoints();
}

// TEST-ONLY, Development-gated seeding for the Playwright E2E suite (see
// TestSeedEndpoints.cs remarks) — never mapped outside ASPNETCORE_ENVIRONMENT
// = Development, so never reachable in a real deploy.
if (app.Environment.IsDevelopment())
{
    app.MapTestSeedEndpoints();
}

// --- SPA static hosting -------------------------------------------------
// Same-origin SPA (design.md "SPA delivery"): the Dockerfile's Node build
// stage copies the built Commerce.Web bundle into wwwroot. UseDefaultFiles
// must run before UseStaticFiles so `/` resolves to index.html; the
// fallback keeps client-side routes working on a hard refresh without
// swallowing the API route groups mapped above (fallback only applies to
// requests that don't match an existing endpoint).
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapFallbackToFile("index.html");

app.Run();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
