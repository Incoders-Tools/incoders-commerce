using Commerce.Domain.Audit;

namespace Commerce.Application.Audit;

public interface IAuditSink
{
    void Record(AuditEntry entry);
}
