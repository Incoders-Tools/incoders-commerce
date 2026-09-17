using System.Security.Cryptography;

namespace Commerce.Pos.Windows;

/// <summary>
/// Local operator PIN policy and verifier (design.md "PIN shape and
/// verifier"). Exactly 6 numeric digits, rejecting all-same-digit and
/// strictly ascending/descending runs. Verifier = PBKDF2-HMAC-SHA256,
/// 128-bit random salt, 256-bit subkey, 210 000 iterations — the same
/// primitive, salt size, and subkey size as `PasswordHasher&lt;T&gt;`'s v3
/// format. Pure, no I/O, no WPF reference: the PIN and its verifier never
/// leave the terminal.
/// </summary>
public static class OperatorPinCredential
{
    public const int PinLength = 6;
    private const int SaltSizeBytes = 16;
    private const int SubkeySizeBytes = 32;
    private const int Iterations = 210_000;

    public static bool IsValidPin(string pin)
    {
        if (pin.Length != PinLength || !pin.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (pin.Distinct().Count() == 1)
        {
            return false;
        }

        if (IsStrictlyAscending(pin) || IsStrictlyDescending(pin))
        {
            return false;
        }

        return true;
    }

    public static (byte[] Salt, byte[] Subkey) Derive(string pin)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        var subkey = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, SubkeySizeBytes);
        return (salt, subkey);
    }

    public static bool Verify(string pin, byte[] salt, byte[] subkey)
    {
        var candidate = Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, subkey.Length);
        return CryptographicOperations.FixedTimeEquals(candidate, subkey);
    }

    private static bool IsStrictlyAscending(string pin)
    {
        for (var i = 1; i < pin.Length; i++)
        {
            if (pin[i] - pin[i - 1] != 1)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsStrictlyDescending(string pin)
    {
        for (var i = 1; i < pin.Length; i++)
        {
            if (pin[i - 1] - pin[i] != 1)
            {
                return false;
            }
        }
        return true;
    }
}
