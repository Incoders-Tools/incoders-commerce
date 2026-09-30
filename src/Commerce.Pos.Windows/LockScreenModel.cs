namespace Commerce.Pos.Windows;

public enum LockScreenMode
{
    /// <summary>Tiles of the operators cached on this terminal.</summary>
    Tiles,

    /// <summary>A tile was tapped: the PIN of that operator.</summary>
    PinEntry,

    /// <summary>Email + password, verified online.</summary>
    Credentials,

    /// <summary>The operator was verified online and has no PIN here (or forgot it): choose one.</summary>
    CreatePin,
}

/// <summary>One operator tile on the lock screen.</summary>
public sealed record OperatorTile(Guid UserId, string Email, string Initials, string Role);

/// <summary>
/// The lock screen's logic, UI-free (pos-operator-session "Lock Screen Gates The
/// Sale UI"). Two ways in: a tile of an operator cached on this terminal plus
/// their 6-digit PIN (verified offline), or email + password (verified online),
/// which asks the operator to create a PIN when they are new to this terminal or
/// chose to reset it. The view only mirrors <see cref="Mode"/>, <see cref="Tiles"/>
/// and <see cref="Status"/> and forwards what the operator typed.
///
/// A PIN is never the whole credential: the operator is chosen first, so two
/// operators sharing a PIN is harmless.
/// </summary>
public sealed class LockScreenModel(
    IOperatorVerifier verifier, LocalOperatorStore store, Func<string> deviceToken, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private OperatorVerifyOutcome? _verified;

    public LockScreenMode Mode { get; private set; } = LockScreenMode.Credentials;

    public IReadOnlyList<OperatorTile> Tiles { get; private set; } = [];

    public OperatorTile? SelectedTile { get; private set; }

    /// <summary>The last problem to show inline; null when there is none.</summary>
    public string? Status { get; private set; }

    /// <summary>On the credentials form: the operator forgot their PIN and wants to choose a new one.</summary>
    public bool ResetPin { get; set; }

    /// <summary>Tiles exist, so the credentials form and the PIN screen can lead back to them.</summary>
    public bool CanGoBack => Tiles.Count > 0;

    /// <summary>Raised once when an operator got in (by PIN, or by email + password).</summary>
    public event Action<CachedOperator>? SignedIn;

    /// <summary>Starts over from what the terminal has cached: tiles when there are any, email + password otherwise (first run).</summary>
    public void Refresh()
    {
        SelectedTile = null;
        _verified = null;
        Status = null;
        ResetPin = false;
        Tiles = LoadTiles();
        Mode = Tiles.Count > 0 ? LockScreenMode.Tiles : LockScreenMode.Credentials;
    }

    /// <summary>
    /// Follows a background change of the cached operators (status check, removal in
    /// Personal) without interrupting what the operator is doing: it only reshapes the
    /// tile list, and leaves the PIN screen when its operator is gone.
    /// </summary>
    public void ReloadTiles()
    {
        Tiles = LoadTiles();

        if (Mode == LockScreenMode.PinEntry && Tiles.All(tile => tile.UserId != SelectedTile?.UserId))
        {
            SelectedTile = null;
            Status = null;
            Mode = Tiles.Count > 0 ? LockScreenMode.Tiles : LockScreenMode.Credentials;
        }
        else if (Mode == LockScreenMode.Tiles && Tiles.Count == 0)
        {
            Mode = LockScreenMode.Credentials;
        }
    }

    public void SelectTile(Guid userId)
    {
        var tile = Tiles.FirstOrDefault(t => t.UserId == userId);
        if (tile is null)
        {
            return;
        }

        SelectedTile = tile;
        Status = null;
        Mode = LockScreenMode.PinEntry;
    }

    /// <summary>An error nobody planned for (logged by the caller): shown inline through the one <see cref="Status"/>.</summary>
    public void ReportUnexpected(string message) => Status = message;

    /// <summary>"Ingresar con usuario y contraseña".</summary>
    public void ChooseCredentials()
    {
        SelectedTile = null;
        Status = null;
        Mode = LockScreenMode.Credentials;
    }

    public void Back()
    {
        Status = null;
        switch (Mode)
        {
            case LockScreenMode.PinEntry:
                SelectedTile = null;
                Mode = LockScreenMode.Tiles;
                break;
            case LockScreenMode.Credentials when CanGoBack:
                ResetPin = false;
                Mode = LockScreenMode.Tiles;
                break;
            case LockScreenMode.CreatePin:
                _verified = null;
                Mode = LockScreenMode.Credentials;
                break;
        }
    }

    /// <summary>Checks the PIN of the selected tile against the cached verifier (offline). False shows the error inline.</summary>
    public bool SubmitPin(string pin)
    {
        if (Mode != LockScreenMode.PinEntry || SelectedTile is not { } tile)
        {
            Status = PosMessages.SelectOperatorFirst;
            return false;
        }

        var cached = store.Load().FirstOrDefault(op => op.UserId == tile.UserId);
        if (cached is null)
        {
            ReloadTiles();
            Status = PosMessages.SelectOperatorFirst;
            return false;
        }

        if (cached.IsStale(_clock()))
        {
            ReloadTiles();
            ChooseCredentials();
            Status = PosMessages.PinExpired;
            return false;
        }

        if (pin.Length != OperatorPinCredential.PinLength || !OperatorPinCredential.Verify(pin, cached.Salt, cached.Subkey))
        {
            Status = PosMessages.IncorrectPin;
            return false;
        }

        Status = null;
        SignedIn?.Invoke(cached);
        return true;
    }

    /// <summary>
    /// Verifies email + password online. An operator already cached here (and not stale) gets in at once
    /// (their record is refreshed and their PIN kept) unless they asked to reset it or it expired;
    /// otherwise the screen moves on to <see cref="LockScreenMode.CreatePin"/>.
    /// True when the operator got in or must now choose a PIN; false shows the reason inline.
    /// </summary>
    public async Task<bool> SubmitCredentialsAsync(string email, string password, CancellationToken ct = default)
    {
        Status = null;
        email = email.Trim();
        if (email.Length == 0 || password.Length == 0)
        {
            Status = PosMessages.EmailAndPasswordToSignIn;
            return false;
        }

        var outcome = await verifier.VerifyAsync(email, password, deviceToken(), ct);
        if (outcome.Kind != OperatorVerifyOutcomeKind.Verified)
        {
            Status = outcome.Kind == OperatorVerifyOutcomeKind.InvalidCredentials
                ? PosMessages.InvalidCredentials
                : outcome.ErrorMessage ?? PosMessages.ProvisioningFailed;
            return false;
        }

        Status = null;
        var cached = store.Load().FirstOrDefault(op => op.UserId == outcome.UserId!.Value);
        if (cached is not null && !ResetPin && !cached.IsStale(_clock()))
        {
            var refreshed = cached with
            {
                Email = outcome.Email!,
                OrganizationId = outcome.OrganizationId!.Value,
                Permissions = outcome.Permissions,
                LastVerifiedUtc = _clock(),
            };
            store.Upsert(refreshed);
            SignedIn?.Invoke(refreshed);
            return true;
        }

        _verified = outcome;
        Mode = LockScreenMode.CreatePin;
        return true;
    }

    /// <summary>Caches the verified operator with the chosen PIN (replacing any previous one) and signs them in.</summary>
    public bool SubmitNewPin(string pin, string confirmPin)
    {
        if (_verified is not { } verified)
        {
            Status = PosMessages.EmailAndPasswordToSignIn;
            Mode = LockScreenMode.Credentials;
            return false;
        }

        if (!OperatorPinCredential.IsValidPin(pin))
        {
            Status = PosMessages.InvalidPinFormat;
            return false;
        }

        if (pin != confirmPin)
        {
            Status = PosMessages.PinMismatch;
            return false;
        }

        var (salt, subkey) = OperatorPinCredential.Derive(pin);
        var record = new CachedOperator(
            verified.UserId!.Value, verified.Email!, verified.OrganizationId!.Value, salt, subkey, _clock(), verified.Permissions);
        store.Upsert(record);
        _verified = null;
        Status = null;
        SignedIn?.Invoke(record);
        return true;
    }

    private IReadOnlyList<OperatorTile> LoadTiles()
    {
        var now = _clock();
        return store.Load()
            .Where(op => !op.IsStale(now))
            .OrderBy(op => op.Email, StringComparer.OrdinalIgnoreCase)
            .Select(op => new OperatorTile(op.UserId, op.Email, InitialsOf(op.Email), OperatorMenuPresenter.RoleSummary(op.Permissions)))
            .ToList();
    }

    /// <summary>Up to two initials from the name part of the email ("ana.lopez@x" becomes "AL").</summary>
    public static string InitialsOf(string email)
    {
        var name = email.Split('@')[0];
        var parts = name.Split(['.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        var letters = parts.Take(2).Select(part => char.ToUpperInvariant(part[0])).ToArray();
        return letters.Length == 0 ? "?" : new string(letters);
    }
}
