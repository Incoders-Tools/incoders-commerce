using Commerce.Application.Payments;
using Commerce.Domain.Payments;

namespace Commerce.Integration;

/// <summary>
/// In-memory <see cref="IPaymentLedgerStore"/> fake — Unit 3's own tests (the
/// real Postgres-backed implementation, <c>PostgresPaymentStore</c>, ships in
/// Unit 4). Append-only, mirroring the real store's contract exactly.
/// </summary>
internal sealed class InMemoryPaymentLedgerStore : IPaymentLedgerStore
{
    private readonly List<PaymentEntry> _entries = [];
    public IReadOnlyList<PaymentEntry> AllEntries => _entries;

    public Task AppendAsync(PaymentEntry entry, CancellationToken ct)
    {
        _entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PaymentEntry>> GetEntriesAsync(PaymentSubject subject, Guid organizationId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<PaymentEntry>>(
            _entries.Where(e => e.Subject.Kind == subject.Kind && e.Subject.SubjectId == subject.SubjectId).ToList());
}

/// <summary>
/// Always-declines fake gateway, for the Declined-outcome test — a real
/// gateway implementation for a live provider does not ship in this phase
/// (ADR-011 defers it), so this fake stands in for "a provider that said no".
/// </summary>
internal sealed class AlwaysDeclineGateway : IPaymentApprovalGateway
{
    public Task<PaymentApprovalOutcome> Approve(Guid subjectId, decimal amount, string method, CancellationToken ct) =>
        Task.FromResult(PaymentApprovalOutcome.Declined);
}

/// <summary>
/// Covers Unit 3 tasks 3.6/3.8/3.10 (commerce-payments design.md
/// "Interfaces / Contracts", proposal.md success criteria): recording maps
/// the gateway outcome onto the domain-layer <see cref="PaymentApprovalState"/>
/// and appends via <see cref="Payment.Append"/> only on Approved/Declined —
/// never on Unavailable; reversal appends a new entry without mutating the
/// original; settlement composes the store read with
/// <see cref="SettlementCalculator.Fold"/>.
/// </summary>
public sealed class PaymentRecordingServiceTests
{
    private static PaymentSubject NewOrderSubject() => new(PaymentSubjectKind.Order, Guid.NewGuid());

    [Fact]
    public async Task Record_Approved_AppendsPaymentEntry()
    {
        var store = new InMemoryPaymentLedgerStore();
        var service = new PaymentRecordingService(store, new ManuallyRecordedApproval());
        var subject = NewOrderSubject();

        var outcome = await service.RecordAsync(
            subject, Guid.NewGuid(), PaymentMethod.Cash, 50m, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(PaymentApprovalState.Approved, outcome.ApprovalState);
        Assert.Single(store.AllEntries);
        Assert.Equal(PaymentEntryKind.Payment, store.AllEntries[0].Kind);
    }

    [Fact]
    public async Task Record_Declined_StillAppendsHistoryEntry_DistinguishableFromUnavailable()
    {
        var store = new InMemoryPaymentLedgerStore();
        var service = new PaymentRecordingService(store, new AlwaysDeclineGateway());
        var subject = NewOrderSubject();

        var outcome = await service.RecordAsync(
            subject, Guid.NewGuid(), PaymentMethod.Card, 50m, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(PaymentApprovalState.Declined, outcome.ApprovalState);
        Assert.Single(store.AllEntries);
        Assert.Equal(PaymentApprovalState.Declined, store.AllEntries[0].ApprovalState);
    }

    [Fact]
    public async Task Record_Unavailable_AppendsNothing()
    {
        var store = new InMemoryPaymentLedgerStore();
        var service = new PaymentRecordingService(store, new UnavailablePaymentApproval());
        var subject = NewOrderSubject();

        var outcome = await service.RecordAsync(
            subject, Guid.NewGuid(), PaymentMethod.Card, 50m, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        // Fail-closed: RecordAsync returns null (no representable entry) and
        // appends nothing at all — distinguishable from a Declined entry,
        // which DOES exist in history.
        Assert.Null(outcome);
        Assert.Empty(store.AllEntries);
    }

    [Fact]
    public async Task Reverse_AppendsNewReversalEntry_OriginalNeverMutatedOrRemoved()
    {
        var store = new InMemoryPaymentLedgerStore();
        var service = new PaymentRecordingService(store, new ManuallyRecordedApproval());
        var subject = NewOrderSubject();

        var recorded = await service.RecordAsync(
            subject, Guid.NewGuid(), PaymentMethod.Cash, 50m, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        var originalEntry = store.AllEntries.Single();

        await service.ReverseAsync(
            originalEntry.EntryId, subject, originalEntry.OrganizationId, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(2, store.AllEntries.Count);
        Assert.Contains(store.AllEntries, e => e.EntryId == originalEntry.EntryId);
        Assert.Contains(store.AllEntries, e => e.Kind == PaymentEntryKind.Reversal && e.ReversesEntryId == originalEntry.EntryId);
    }

    [Fact]
    public async Task GetSettlement_ComposesStoreReadWithFold()
    {
        var store = new InMemoryPaymentLedgerStore();
        var service = new PaymentRecordingService(store, new ManuallyRecordedApproval());
        var subject = NewOrderSubject();

        var organizationId = Guid.NewGuid();
        await service.RecordAsync(subject, organizationId, PaymentMethod.Cash, 40m, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        var settlement = await service.GetSettlementAsync(subject, organizationId, target: 100m, CancellationToken.None);

        Assert.Equal(40m, settlement.Settled);
        Assert.Equal(60m, settlement.Outstanding);
    }
}
