namespace Commerce.Domain.Tenancy;

/// <summary>
/// A branch already handed out every register number up to <see cref="RegisterNumber.MaxValue"/>
/// (numbers are never reused), so one more terminal cannot be paired to it. The API maps it to
/// a typed 409 (`register-numbers-exhausted`).
/// </summary>
public sealed class RegisterNumbersExhaustedException()
    : InvalidOperationException($"The branch has no register numbers left (maximum {RegisterNumber.MaxValue}).")
{
    /// <summary>The typed error code of that 409, shared by the API and the POS clients.</summary>
    public const string ErrorCode = "register-numbers-exhausted";
}
