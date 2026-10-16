using System.Collections.Concurrent;
using Commerce.Domain.Audit;

namespace Commerce.Application.Audit;

/// <summary>
/// Reusable in-memory audit sink shared across walking-skeleton units until a
/// durable sink (branch SQLite outbox / cloud Postgres) lands in Unit 3.
/// Later units (sync, management, ordering, upgrades) should reuse this
/// rather than authoring another in-memory audit collector.
/// </summary>
public sealed class InMemoryAuditSink : IAuditSink
{
    private readonly ConcurrentQueue<AuditEntry> _entries = new();

    public void Record(AuditEntry entry) => _entries.Enqueue(entry);

    public IReadOnlyList<AuditEntry> Entries => _entries.ToArray();
}
