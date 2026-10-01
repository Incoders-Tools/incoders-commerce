using Commerce.Domain.Tenancy;

namespace Commerce.Pos.Windows;

/// <summary>
/// Completes the terminal identity (branch code + register number) of a pairing
/// that does not have it yet: a terminal paired before registers existed, or one
/// whose pairing response was stored without them. UI-free on purpose so the
/// rule is tested without a window: nothing is fetched when the identity is
/// complete, an unreachable server keeps the pairing as it is (the POS keeps
/// working offline, the identity is simply still unknown), and a result is only
/// persisted while the stored pairing is still the one that was asked about.
/// </summary>
public sealed class TerminalIdentityRefresher
{
    private readonly DeviceIdentityClient _client;
    private readonly LocalInstallationStore _store;

    public TerminalIdentityRefresher(DeviceIdentityClient client, LocalInstallationStore store)
    {
        _client = client;
        _store = store;
    }

    public static bool IsComplete(DevicePairing pairing) => pairing.BranchCode is not null && pairing.RegisterNumber is not null;

    /// <summary>
    /// Returns <paramref name="pairing"/> itself when nothing changed, otherwise a copy
    /// carrying the branch code and register number (already persisted).
    /// </summary>
    public async Task<DevicePairing> EnsureAsync(Guid installationId, DevicePairing pairing, CancellationToken ct = default)
    {
        if (IsComplete(pairing)) return pairing;

        var outcome = await _client.FetchAsync(pairing.DeviceToken, ct);
        // A credential bound to another branch than the stored pairing is not ours to apply.
        if (!outcome.Success || outcome.Body is null || outcome.Body.BranchId != pairing.BranchId) return pairing;

        var updated = pairing with { BranchCode = outcome.Body.BranchCode, RegisterNumber = outcome.Body.RegisterNumber };

        // The request took time: the terminal may have been re-paired meanwhile, and a stale
        // answer must never overwrite the newer pairing on disk.
        var stored = _store.LoadOrCreate().Pairing;
        if (stored is not null && stored.DeviceToken == pairing.DeviceToken && stored.BranchId == pairing.BranchId)
        {
            _store.Save(new LocalInstallationRecord(installationId, updated));
        }

        return updated;
    }
}

/// <summary>
/// The human label of a terminal: `Sucursal 01 · Ruta 51 · Caja 2`. Parts that are not
/// known yet (an old pairing, offline since) are left out; a GUID never appears.
/// </summary>
public static class TerminalLabel
{
    public static string Format(DevicePairing pairing)
    {
        var parts = new List<string>(3);
        if (pairing.BranchCode is { } code && code is >= BranchCode.MinValue and <= BranchCode.MaxValue)
        {
            parts.Add($"Sucursal {new BranchCode(code).Format()}");
        }
        parts.Add(pairing.BranchName);
        if (pairing.RegisterNumber is { } register && register is >= RegisterNumber.MinValue and <= RegisterNumber.MaxValue)
        {
            parts.Add($"Caja {register}");
        }
        return string.Join(" · ", parts);
    }
}
