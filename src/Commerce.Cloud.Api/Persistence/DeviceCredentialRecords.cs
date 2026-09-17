using System.Security.Cryptography;
using System.Text;

namespace Commerce.Cloud.Api.Persistence;

/// <summary>
/// Durable row backing a paired POS terminal (design.md "Credential shape").
/// This IS the installation registration — every field
/// <c>InstallationIdentityService</c> used to model in memory now lives here.
/// </summary>
public sealed record DeviceCredentialRecord(
    Guid Id,
    Guid OrganizationId,
    Guid BranchId,
    Guid InstallationId,
    Guid IssuedToUserId,
    Guid? ReplacesCredentialId,
    bool IsRevoked);

/// <summary>
/// Result of <c>PostgresDeviceCredentialStore.IssueAsync</c>: carries the
/// plaintext secret exactly once — it is never stored and never
/// reconstructable from the persisted row.
/// </summary>
public sealed record IssuedDeviceCredential(DeviceCredentialRecord Record, string PlaintextToken);

/// <summary>
/// The only crypto surface for device credentials (design.md "Hashing
/// algorithm for the secret"). Generates a 32-byte cryptographically random
/// secret, base64url-encodes it for transport, and hashes it with plain
/// SHA-256 — deliberately NOT <c>PasswordHasher</c>: a 256-bit uniformly
/// random secret has no dictionary to attack, so a deliberately slow KDF
/// would add latency to every `/sync` request for zero security gain. That
/// asymmetry with `users.password_hash` (which correctly uses a slow KDF for
/// low-entropy passwords) is intentional, not an oversight.
/// </summary>
public static class DeviceTokenHasher
{
    private const int SecretByteLength = 32;

    public static string GenerateSecret()
    {
        Span<byte> bytes = stackalloc byte[SecretByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    public static string Hash(string plaintextToken)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintextToken);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
