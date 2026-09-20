using Commerce.Domain.Payments;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Input to <see cref="PostgresPaymentStore.AppendAsync"/> — mirrors
/// <c>NewCustomer</c>'s style. No `OrganizationId` (comes from the tenant
/// scope, never a request field).
/// </summary>
public sealed record NewPaymentEntry(
    Guid EntryId,
    Guid OperationId,
    PaymentSubjectKind SubjectKind,
    Guid SubjectId,
    PaymentEntryKind EntryKind,
    PaymentMethod Method,
    decimal Amount,
    PaymentApprovalState ApprovalState,
    Guid? ReversesEntryId,
    string? ProviderReference,
    Guid ActorId);
