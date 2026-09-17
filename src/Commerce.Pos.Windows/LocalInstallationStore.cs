using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Commerce.Pos.Windows;

/// <summary>
/// Persists this machine's terminal identity across process restarts
/// (design.md "Device token at rest on the terminal"). `InstallationId` is
/// CLIENT-minted BY DESIGN: it is a terminal label, never an authorization
/// input — the server stores it, never trusts it for authorization. The
/// device credential SECRET is DPAPI-encrypted (`ProtectedData.Protect`/
/// `Unprotect`, `DataProtectionScope.CurrentUser`) at rest; a decrypt failure
/// (e.g. `installation.json` copied to a different machine/user) is treated
/// identically to "no valid credential" — it routes straight into the
/// pairing/re-pair flow and NEVER throws or crashes.
/// </summary>
public sealed class LocalInstallationStore
{
    private readonly string _filePath;

    public LocalInstallationStore(string filePath)
    {
        _filePath = filePath;
    }

    public LocalInstallationRecord LoadOrCreate()
    {
        if (File.Exists(_filePath))
        {
            try
            {
                var json = File.ReadAllText(_filePath);
                var dto = JsonSerializer.Deserialize<PersistedInstallationDto>(json);
                if (dto is not null)
                {
                    var pairing = TryDecryptPairing(dto);
                    return new LocalInstallationRecord(dto.InstallationId, pairing);
                }
            }
            catch (JsonException)
            {
                // Corrupted file entirely: fall through to a fresh identity,
                // matching "decrypt failure => no valid credential", never a crash.
            }
        }

        var record = new LocalInstallationRecord(Guid.NewGuid(), Pairing: null);
        Save(record);
        return record;
    }

    public void Save(LocalInstallationRecord record)
    {
        var dto = new PersistedInstallationDto
        {
            InstallationId = record.InstallationId,
            OrganizationId = record.Pairing?.OrganizationId,
            BranchId = record.Pairing?.BranchId,
            BranchName = record.Pairing?.BranchName,
            OperatorEmail = record.Pairing?.OperatorEmail,
            EncryptedDeviceToken = record.Pairing is null
                ? null
                : Convert.ToBase64String(ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(record.Pairing.DeviceToken),
                    optionalEntropy: null,
                    DataProtectionScope.CurrentUser)),
        };

        File.WriteAllText(_filePath, JsonSerializer.Serialize(dto));
    }

    /// <summary>
    /// Any failure here (missing fields, corrupted ciphertext, DPAPI
    /// decrypting under a different Windows user/machine) is treated
    /// identically to "no valid credential": returns null, never throws.
    /// </summary>
    private static DevicePairing? TryDecryptPairing(PersistedInstallationDto dto)
    {
        if (dto.OrganizationId is null || dto.BranchId is null
            || dto.BranchName is null || dto.OperatorEmail is null
            || dto.EncryptedDeviceToken is null)
        {
            return null;
        }

        try
        {
            var cipherBytes = Convert.FromBase64String(dto.EncryptedDeviceToken);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var deviceToken = Encoding.UTF8.GetString(plainBytes);

            return new DevicePairing(dto.OrganizationId.Value, dto.BranchId.Value, dto.BranchName, dto.OperatorEmail, deviceToken);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    private sealed class PersistedInstallationDto
    {
        public Guid InstallationId { get; set; }
        public Guid? OrganizationId { get; set; }
        public Guid? BranchId { get; set; }
        public string? BranchName { get; set; }
        public string? OperatorEmail { get; set; }
        public string? EncryptedDeviceToken { get; set; }
    }
}

/// <summary>
/// `InstallationId` is client-minted and that is correct: it is a terminal
/// label, never an authorization input, and it survives re-pairing so the
/// server can revoke the terminal's prior credentials.
/// </summary>
public sealed record LocalInstallationRecord(Guid InstallationId, DevicePairing? Pairing);

public sealed record DevicePairing(
    Guid OrganizationId, Guid BranchId, string BranchName, string OperatorEmail, string DeviceToken);
