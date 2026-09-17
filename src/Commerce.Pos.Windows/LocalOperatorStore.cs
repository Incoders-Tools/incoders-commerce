using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace Commerce.Pos.Windows;

/// <summary>
/// Structural clone of <see cref="LocalInstallationStore"/> (design.md
/// "LocalOperatorStore file and DPAPI scope"): `operators.json` beside
/// `installation.json` in the same `dataDirectory`, a list of operators
/// rather than a single record. Each entry's plaintext scalars are stored
/// as-is; only the PIN verifier blob (`salt‖subkey`) is DPAPI-protected
/// (`DataProtectionScope.CurrentUser`, identical scope to
/// `LocalInstallationStore`). Decrypt failure, malformed base64, or a
/// corrupt file yields "that entry is not cached" (whole-file JSON failure
/// yields "no operators cached") — NEVER a throw, mirroring
/// `TryDecryptPairing`'s exact contract.
/// </summary>
public sealed class LocalOperatorStore
{
    private readonly string _filePath;

    public LocalOperatorStore(string filePath)
    {
        _filePath = filePath;
    }

    public IReadOnlyList<CachedOperator> Load()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var dtos = JsonSerializer.Deserialize<List<PersistedOperatorDto>>(json);
            if (dtos is null)
            {
                return [];
            }

            var operators = new List<CachedOperator>();
            foreach (var dto in dtos)
            {
                var op = TryDecrypt(dto);
                if (op is not null)
                {
                    operators.Add(op);
                }
            }
            return operators;
        }
        catch (JsonException)
        {
            // Corrupted file entirely: "no operators cached", never a crash.
            return [];
        }
    }

    public void Upsert(CachedOperator op)
    {
        var operators = Load().Where(o => o.UserId != op.UserId).ToList();
        operators.Add(op);
        SaveAll(operators);
    }

    public void Remove(Guid userId)
    {
        var operators = Load().Where(o => o.UserId != userId).ToList();
        SaveAll(operators);
    }

    public void TouchVerified(Guid userId, DateTimeOffset now)
    {
        var operators = Load()
            .Select(o => o.UserId == userId ? o with { LastVerifiedUtc = now } : o)
            .ToList();
        SaveAll(operators);
    }

    private void SaveAll(IReadOnlyList<CachedOperator> operators)
    {
        var dtos = operators.Select(op => new PersistedOperatorDto
        {
            UserId = op.UserId,
            Email = op.Email,
            OrganizationId = op.OrganizationId,
            LastVerifiedUtc = op.LastVerifiedUtc,
            ProtectedVerifier = Convert.ToBase64String(ProtectedData.Protect(
                CombineSaltAndSubkey(op.Salt, op.Subkey),
                optionalEntropy: null,
                DataProtectionScope.CurrentUser)),
        }).ToList();

        File.WriteAllText(_filePath, JsonSerializer.Serialize(dtos));
    }

    /// <summary>
    /// Any failure here (corrupted ciphertext, DPAPI decrypting under a
    /// different Windows user/machine, malformed base64) is treated
    /// identically to "that entry is not cached": returns null, never
    /// throws — `TryDecryptPairing`'s exact contract, applied per-entry.
    /// </summary>
    private static CachedOperator? TryDecrypt(PersistedOperatorDto dto)
    {
        try
        {
            var cipherBytes = Convert.FromBase64String(dto.ProtectedVerifier);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var (salt, subkey) = SplitSaltAndSubkey(plainBytes);

            return new CachedOperator(dto.UserId, dto.Email, dto.OrganizationId, salt, subkey, dto.LastVerifiedUtc);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private const int SaltLengthBytes = 16;

    private static byte[] CombineSaltAndSubkey(byte[] salt, byte[] subkey)
    {
        var combined = new byte[salt.Length + subkey.Length];
        Buffer.BlockCopy(salt, 0, combined, 0, salt.Length);
        Buffer.BlockCopy(subkey, 0, combined, salt.Length, subkey.Length);
        return combined;
    }

    private static (byte[] Salt, byte[] Subkey) SplitSaltAndSubkey(byte[] combined)
    {
        var salt = combined[..SaltLengthBytes];
        var subkey = combined[SaltLengthBytes..];
        return (salt, subkey);
    }

    private sealed class PersistedOperatorDto
    {
        public Guid UserId { get; set; }
        public string Email { get; set; } = "";
        public Guid OrganizationId { get; set; }
        public DateTimeOffset LastVerifiedUtc { get; set; }
        public string ProtectedVerifier { get; set; } = "";
    }
}
