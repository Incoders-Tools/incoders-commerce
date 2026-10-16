namespace Commerce.Domain.Tenancy;

/// <summary>
/// An organization already holds every branch code up to <see cref="BranchCode.MaxValue"/>,
/// so the database refused to number one more branch. The API maps it to a typed 409
/// (`branch-codes-exhausted`) instead of leaking a constraint violation as a 500.
/// </summary>
public sealed class BranchCodesExhaustedException()
    : InvalidOperationException($"The organization has no branch codes left (maximum {BranchCode.MaxValue}).");
