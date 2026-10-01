using Commerce.Application.Audit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Commerce.Cloud.Api.Auditing;

/// <summary>
/// Registers the durable, fail-open <see cref="PostgresAuditSink"/> as THE shared <see cref="IAuditSink"/>:
/// every consumer (staff authorization, catalog management, customer catalog access) writes to `audit_log`
/// and states its own actor kind on the entry. Expects an <see cref="NpgsqlDataSource"/> and logging.
/// </summary>
public static class DurableAuditSinkRegistration
{
    public static IServiceCollection AddDurableAuditSink(this IServiceCollection services) =>
        services.AddSingleton<IAuditSink>(sp =>
            new PostgresAuditSink(sp.GetRequiredService<NpgsqlDataSource>(), sp.GetRequiredService<ILogger<PostgresAuditSink>>()));
}
