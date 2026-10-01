using Commerce.Domain.Audit;

namespace Commerce.Application.Audit;

public interface IAuditSink
{
    void Record(AuditEntry entry);

    /// <summary>
    /// Asynchronous write for callers that already are async. A durable sink (the cloud's `audit_log`)
    /// overrides it so the write is a real awaited database call; an in-memory sink keeps this default,
    /// which just delegates to <see cref="Record"/>.
    /// </summary>
    Task RecordAsync(AuditEntry entry, CancellationToken ct)
    {
        Record(entry);
        return Task.CompletedTask;
    }
}
