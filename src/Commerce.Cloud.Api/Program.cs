using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.Application.Management;
using Commerce.Application.Ordering;
using Commerce.Cloud.Api;
using Commerce.Cloud.Api.Authentication;
using Commerce.Cloud.Api.Email;
using Commerce.Cloud.Api.Endpoints;
using Commerce.Cloud.Api.HealthChecks;
using Commerce.Cloud.Api.Management;
using Commerce.Cloud.Api.Ordering;
using Commerce.Cloud.Api.Persistence;
using Commerce.Domain.Identity;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

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
    });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("DeviceBearer", policy => policy
        .AddAuthenticationSchemes(CloudAuthenticationSchemes.DeviceBearer)
        .RequireAuthenticatedUser())
    .AddPolicy("PlatformAdmin", policy => policy
        .AddAuthenticationSchemes(CloudAuthenticationSchemes.PlatformAdminCookie)
        .RequireAuthenticatedUser());

// --- Health -----------------------------------------------------------------
// /health = liveness, no DB dependency. /health/ready = verifies (never
// applies) schema/RLS shape (design.md "Schema/RLS application").
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: new[] { "live" })
    .AddCheck<PostgresReadinessHealthCheck>("postgres-rls-ready", tags: new[] { "ready" });

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

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
app.MapPricingEndpoints();

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
