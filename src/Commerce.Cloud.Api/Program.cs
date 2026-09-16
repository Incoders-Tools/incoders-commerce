using Commerce.Application.Access;
using Commerce.Application.Audit;
using Commerce.Application.Management;
using Commerce.Application.Ordering;
using Commerce.Cloud.Api;
using Commerce.Cloud.Api.Authentication;
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

// --- Credentials: PasswordHasher<UserAccount> is a framework type
// (Microsoft.AspNetCore.Identity, part of the ASP.NET Core shared framework)
// — build spike confirmed zero new PackageReference entries (design.md
// "Build spike result"). BootstrapTokenRegistry is a singleton so its
// in-memory per-org token state survives across requests within one process
// (design.md "Bootstrap token storage").
builder.Services.AddSingleton<PasswordHasher<UserAccount>>();
builder.Services.AddSingleton<BootstrapTokenRegistry>();

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
    })
    .AddScheme<DeviceBearerAuthenticationOptions, DeviceBearerAuthenticationHandler>(
        CloudAuthenticationSchemes.DeviceBearer, _ => { });

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("DeviceBearer", policy => policy
        .AddAuthenticationSchemes(CloudAuthenticationSchemes.DeviceBearer)
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
app.MapSyncEndpoints();
app.MapCatalogEndpoints();
app.MapOrderingEndpoints();

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
