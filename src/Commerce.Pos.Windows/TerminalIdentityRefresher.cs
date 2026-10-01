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
    /// Fetches the missing identity. <see cref="TerminalIdentityRefresh.Persisted"/> is true ONLY when the
    /// enriched pairing was saved to disk; then <see cref="TerminalIdentityRefresh.Pairing"/> is that copy.
    /// In every other case (complete already, offline, another branch, or the stored pairing changed while
    /// the request was in flight) it is the pairing that was passed in, unchanged.
    /// </summary>
    public async Task<TerminalIdentityRefresh> EnsureAsync(Guid installationId, DevicePairing pairing, CancellationToken ct = default)
    {
        if (IsComplete(pairing)) return new TerminalIdentityRefresh(pairing, Persisted: false);

        var outcome = await _client.FetchAsync(pairing.DeviceToken, ct);
        // A credential bound to another branch than the stored pairing is not ours to apply.
        if (!outcome.Success || outcome.Body is null || outcome.Body.BranchId != pairing.BranchId)
        {
            return new TerminalIdentityRefresh(pairing, Persisted: false);
        }

        var updated = pairing with { BranchCode = outcome.Body.BranchCode, RegisterNumber = outcome.Body.RegisterNumber };

        // The request took time: the terminal may have been re-paired meanwhile, and a stale
        // answer must never overwrite the newer pairing on disk (nor be applied to the window).
        var stored = _store.LoadOrCreate().Pairing;
        if (stored is null || stored.DeviceToken != pairing.DeviceToken || stored.BranchId != pairing.BranchId)
        {
            return new TerminalIdentityRefresh(pairing, Persisted: false);
        }

        _store.Save(new LocalInstallationRecord(installationId, updated));
        return new TerminalIdentityRefresh(updated, Persisted: true);
    }
}

/// <summary>Result of <see cref="TerminalIdentityRefresher.EnsureAsync"/>; see there for the meaning of <paramref name="Persisted"/>.</summary>
public sealed record TerminalIdentityRefresh(DevicePairing Pairing, bool Persisted);

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
