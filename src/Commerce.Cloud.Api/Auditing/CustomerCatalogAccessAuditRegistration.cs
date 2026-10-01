using Commerce.Application.Audit;
using Commerce.Application.Ordering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commerce.Cloud.Api.Auditing;

/// <summary>
/// Wires the durable, fail-open <see cref="PostgresAuditSink"/> to the customer catalog access path only.
/// The sink is a KEYED <see cref="IAuditSink"/>, so the shared (unkeyed) <see cref="IAuditSink"/> that
/// the staff authorization and catalog management services use is untouched and never writes `customer` rows.
/// Expects an <see cref="NpgsqlDataSource"/>, an <see cref="ICustomerOrderingAccessResolver"/> and logging.
/// </summary>
public static class CustomerCatalogAccessAuditRegistration
{
    public const string SinkKey = "customer-catalog-access-audit";

    public static IServiceCollection AddCustomerCatalogAccessAudit(this IServiceCollection services)
    {
        services.AddKeyedSingleton<IAuditSink>(SinkKey, (sp, _) =>
            new PostgresAuditSink(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<ILogger<PostgresAuditSink>>()));
        services.AddSingleton(sp => new CustomerCatalogAccessService(
            sp.GetRequiredService<ICustomerOrderingAccessResolver>(),
            sp.GetRequiredKeyedService<IAuditSink>(SinkKey)));
        return services;
    }
}
