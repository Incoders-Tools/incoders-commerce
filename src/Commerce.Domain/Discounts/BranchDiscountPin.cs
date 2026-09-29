using System.Security.Cryptography;

namespace Commerce.Domain.Discounts;

/// <summary>
/// A branch discount PIN verifier: everything a terminal needs to check a typed
/// PIN offline, and nothing that reveals it. The parameters travel with the hash
/// so the algorithm can be strengthened later without breaking old verifiers.
/// </summary>
public sealed record DiscountPinVerifier(string Algorithm, int Iterations, byte[] Salt, byte[] Hash);

/// <summary>
/// Shape and slow salted verifier of the one shared discount PIN a branch has
/// (branch-discount-pin spec). Same primitive, salt size and subkey size as the
/// house operator PIN and <c>PasswordHasher&lt;T&gt;</c> v3: PBKDF2-HMAC-SHA256,
/// 128-bit salt, 256-bit subkey, 210 000 iterations. Pure: no I/O, so the cloud
/// (derive) and the POS (verify) run the same compiled code.
/// </summary>
public static class BranchDiscountPin
{
    public const int MinLength = 4;
    public const int MaxLength = 12;
    public const string Pbkdf2Sha256 = "pbkdf2-sha256";
    public const int DefaultIterations = 210_000;
    private const int SaltSizeBytes = 16;
    private const int SubkeySizeBytes = 32;

    public static bool IsValidPin(string? pin) =>
        pin is { Length: >= MinLength and <= MaxLength } && pin.All(char.IsAsciiDigit);

    public static DiscountPinVerifier Derive(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(pin, salt, DefaultIterations, HashAlgorithmName.SHA256, SubkeySizeBytes);
        return new DiscountPinVerifier(Pbkdf2Sha256, DefaultIterations, salt, hash);
    }

    public static bool Verify(string pin, DiscountPinVerifier verifier)
    {
        if (verifier.Algorithm != Pbkdf2Sha256 || verifier.Iterations <= 0 || verifier.Hash.Length == 0)
        {
            return false;
        }

        var candidate = Rfc2898DeriveBytes.Pbkdf2(pin, verifier.Salt, verifier.Iterations, HashAlgorithmName.SHA256, verifier.Hash.Length);
        return CryptographicOperations.FixedTimeEquals(candidate, verifier.Hash);
    }
}
