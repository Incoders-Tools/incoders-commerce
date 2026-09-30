using Commerce.Domain.Identity;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// The lock screen's decisions, UI-free: which mode it opens in, how tiles, PIN
/// entry, email + password and PIN creation lead into each other, and what each
/// online outcome says. Uses the real <see cref="LocalOperatorStore"/> (DPAPI,
/// temp file) and a fake verifier, so the tile + PIN path is proven to work
/// without any network.
/// </summary>
public sealed class PosLockScreenModelTests : IDisposable
{
    private const string GoodPin = "482915";
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly string _file = Path.Combine(Path.GetTempPath(), $"lock-operators-{Guid.NewGuid():N}.json");
    private readonly FakeVerifier _verifier = new();

    public void Dispose()
    {
        try { File.Delete(_file); } catch (IOException) { }
    }

    private sealed class FakeVerifier : IOperatorVerifier
    {
        public int Calls;
        public string? LastEmail;
        public string? LastToken;
        public OperatorVerifyOutcome Outcome = OperatorVerifyOutcome.InvalidCredentials();

        public Task<OperatorVerifyOutcome> VerifyAsync(string email, string password, string deviceToken, CancellationToken ct = default)
        {
            Calls++;
            LastEmail = email;
            LastToken = deviceToken;
            return Task.FromResult(Outcome);
        }
    }

    private LocalOperatorStore Store => new(_file);

    private LockScreenModel Model(DateTimeOffset? now = null) =>
        new(_verifier, Store, () => "device-token", () => now ?? Now);

    private CachedOperator Cache(string email, string pin = GoodPin, int ageDays = 1, int permissions = (int)Permission.OperatePos, Guid? id = null)
    {
        var (salt, subkey) = OperatorPinCredential.Derive(pin);
        var op = new CachedOperator(id ?? Guid.NewGuid(), email, Guid.NewGuid(), salt, subkey, Now.AddDays(-ageDays), permissions);
        Store.Upsert(op);
        return op;
    }

    private static OperatorVerifyOutcome Verified(Guid id, string email, int permissions = (int)Permission.OperatePos) =>
        OperatorVerifyOutcome.Verified(id, email, Guid.NewGuid(), permissions);

    private static CachedOperator? Capture(LockScreenModel model, Action act)
    {
        CachedOperator? signedIn = null;
        model.SignedIn += op => signedIn = op;
        act();
        return signedIn;
    }

    // ---- which mode it opens in ---------------------------------------------------

    [Fact]
    public void FirstRun_NoCachedOperators_OpensOnEmailAndPassword_WithNoWayBack()
    {
        var model = Model();
        model.Refresh();

        Assert.Equal(LockScreenMode.Credentials, model.Mode);
        Assert.Empty(model.Tiles);
        Assert.False(model.CanGoBack);
    }

    [Fact]
    public void WithFreshCachedOperators_OpensOnTiles_ListingOnlyTheNonStaleOnes()
    {
        Cache("ana.lopez@vacaverde.test");
        Cache("vencido@vacaverde.test", ageDays: 30);
        var model = Model();
        model.Refresh();

        Assert.Equal(LockScreenMode.Tiles, model.Mode);
        var tile = Assert.Single(model.Tiles);
        Assert.Equal("ana.lopez@vacaverde.test", tile.Email);
        Assert.Equal("AL", tile.Initials);
        Assert.Equal("Cajero", tile.Role);
    }

    [Fact]
    public void WhenEveryCachedOperatorIsStale_BehavesLikeFirstRun()
    {
        Cache("vencido@vacaverde.test", ageDays: 30);
        var model = Model();
        model.Refresh();

        Assert.Equal(LockScreenMode.Credentials, model.Mode);
    }

    [Theory]
    [InlineData("ana.lopez@x.test", "AL")]
    [InlineData("caja@x.test", "C")]
    [InlineData("juan_perez-g@x.test", "JP")]
    public void Initials_AreTakenFromTheEmailName(string email, string expected)
    {
        Cache(email);
        var model = Model();
        model.Refresh();

        Assert.Equal(expected, model.Tiles.Single().Initials);
    }

    // ---- tile + PIN ---------------------------------------------------------------

    [Fact]
    public void Tile_ThenTheRightPin_SignsIn_WithoutTouchingTheNetwork()
    {
        var ana = Cache("ana@x.test");
        var model = Model();
        model.Refresh();

        model.SelectTile(ana.UserId);
        Assert.Equal(LockScreenMode.PinEntry, model.Mode);
        Assert.Equal("ana@x.test", model.SelectedTile!.Email);

        var signedIn = Capture(model, () => Assert.True(model.SubmitPin(GoodPin)));

        Assert.Equal(ana.UserId, signedIn!.UserId);
        Assert.Equal(0, _verifier.Calls);
    }

    [Fact]
    public void WrongPin_ShowsAnInlineError_AndStaysOnPinEntry()
    {
        var ana = Cache("ana@x.test");
        var model = Model();
        model.Refresh();
        model.SelectTile(ana.UserId);

        var signedIn = Capture(model, () => Assert.False(model.SubmitPin("999111")));

        Assert.Null(signedIn);
        Assert.Equal(PosMessages.IncorrectPin, model.Status);
        Assert.Equal(LockScreenMode.PinEntry, model.Mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("123")]
    [InlineData("abcdef")]
    public void MalformedPin_IsTreatedAsAWrongPin(string pin)
    {
        var ana = Cache("ana@x.test");
        var model = Model();
        model.Refresh();
        model.SelectTile(ana.UserId);

        Assert.False(model.SubmitPin(pin));
        Assert.Equal(PosMessages.IncorrectPin, model.Status);
    }

    [Fact]
    public void Back_FromPinEntry_ReturnsToTiles_AndClearsTheError()
    {
        var ana = Cache("ana@x.test");
        var model = Model();
        model.Refresh();
        model.SelectTile(ana.UserId);
        model.SubmitPin("999111");

        model.Back();

        Assert.Equal(LockScreenMode.Tiles, model.Mode);
        Assert.Null(model.Status);
        Assert.Null(model.SelectedTile);
    }

    [Fact]
    public void SubmitPin_WhenTheTileWasStaleByTheTimeOfSubmit_FallsBackToCredentials_WithAnExplanation()
    {
        var ana = Cache("ana@x.test", ageDays: 13);
        var clock = Now;
        var model = new LockScreenModel(_verifier, Store, () => "t", () => clock);
        model.Refresh();
        model.SelectTile(ana.UserId);

        clock = Now.AddDays(3);
        Assert.False(model.SubmitPin(GoodPin));

        Assert.Equal(LockScreenMode.Credentials, model.Mode);
        Assert.Equal(PosMessages.PinExpired, model.Status);
    }

    [Fact]
    public void SelectTile_ForAnOperatorThatIsNotListed_IsIgnored()
    {
        Cache("ana@x.test");
        var model = Model();
        model.Refresh();

        model.SelectTile(Guid.NewGuid());

        Assert.Equal(LockScreenMode.Tiles, model.Mode);
    }

    // ---- email + password -----------------------------------------------------------

    [Fact]
    public void TheSecondaryLink_SwitchesFromTilesToCredentials_AndBackIsAvailable()
    {
        Cache("ana@x.test");
        var model = Model();
        model.Refresh();

        model.ChooseCredentials();

        Assert.Equal(LockScreenMode.Credentials, model.Mode);
        Assert.True(model.CanGoBack);
        model.Back();
        Assert.Equal(LockScreenMode.Tiles, model.Mode);
    }

    [Fact]
    public async Task Credentials_EmptyFields_AreRefused_WithoutCallingTheServer()
    {
        var model = Model();
        model.Refresh();

        Assert.False(await model.SubmitCredentialsAsync(" ", ""));

        Assert.Equal(PosMessages.EmailAndPasswordToSignIn, model.Status);
        Assert.Equal(0, _verifier.Calls);
    }

    [Fact]
    public async Task Credentials_OfAnOperatorNewToThisTerminal_AskToCreateAPin_ThenCacheAndSignIn()
    {
        var id = Guid.NewGuid();
        _verifier.Outcome = Verified(id, "nuevo@x.test", (int)(Permission.OperatePos | Permission.ManageUsers));
        var model = Model();
        model.Refresh();

        Assert.True(await model.SubmitCredentialsAsync(" nuevo@x.test ", "secret"));
        Assert.Equal(LockScreenMode.CreatePin, model.Mode);
        Assert.Equal("nuevo@x.test", _verifier.LastEmail);
        Assert.Equal("device-token", _verifier.LastToken);
        Assert.Empty(Store.Load()); // nothing cached before the PIN exists

        var signedIn = Capture(model, () => Assert.True(model.SubmitNewPin(GoodPin, GoodPin)));

        Assert.Equal(id, signedIn!.UserId);
        Assert.Equal((int)(Permission.OperatePos | Permission.ManageUsers), signedIn.Permissions);
        var cached = Assert.Single(Store.Load());
        Assert.True(OperatorPinCredential.Verify(GoodPin, cached.Salt, cached.Subkey));
    }

    [Fact]
    public async Task Credentials_OfAnAlreadyCachedOperator_SignInDirectly_KeepingTheirPin_AndRefreshingTheRecord()
    {
        var ana = Cache("ana@x.test", permissions: (int)Permission.OperatePos, ageDays: 10);
        _verifier.Outcome = Verified(ana.UserId, "ana@x.test", (int)(Permission.OperatePos | Permission.ManageUsers));
        var model = Model();
        model.Refresh();

        CachedOperator? signedIn = null;
        model.SignedIn += op => signedIn = op;
        Assert.True(await model.SubmitCredentialsAsync("ana@x.test", "secret"));

        Assert.Equal(ana.UserId, signedIn!.UserId);
        var cached = Assert.Single(Store.Load());
        Assert.True(OperatorPinCredential.Verify(GoodPin, cached.Salt, cached.Subkey));
        Assert.Equal((int)(Permission.OperatePos | Permission.ManageUsers), cached.Permissions);
        Assert.Equal(Now, cached.LastVerifiedUtc);
    }

    [Fact]
    public async Task Credentials_WithForgottenPin_AskForANewPin_AndReplaceTheOldOne()
    {
        var ana = Cache("ana@x.test", pin: GoodPin);
        _verifier.Outcome = Verified(ana.UserId, "ana@x.test");
        var model = Model();
        model.Refresh();
        model.ChooseCredentials();
        model.ResetPin = true;

        Assert.True(await model.SubmitCredentialsAsync("ana@x.test", "secret"));
        Assert.Equal(LockScreenMode.CreatePin, model.Mode);

        Assert.True(model.SubmitNewPin("270813", "270813"));

        var cached = Assert.Single(Store.Load());
        Assert.True(OperatorPinCredential.Verify("270813", cached.Salt, cached.Subkey));
        Assert.False(OperatorPinCredential.Verify(GoodPin, cached.Salt, cached.Subkey));
    }

    [Fact]
    public async Task Credentials_OfAStaleCachedOperator_AskForANewPin_SinceTheOldOneExpired()
    {
        var ana = Cache("ana@x.test", ageDays: 30);
        _verifier.Outcome = Verified(ana.UserId, "ana@x.test");
        var model = Model();
        model.Refresh();

        Assert.True(await model.SubmitCredentialsAsync("ana@x.test", "secret"));
        Assert.Equal(LockScreenMode.CreatePin, model.Mode);

        Assert.True(model.SubmitNewPin("270813", "270813"));

        var cached = Assert.Single(Store.Load());
        Assert.Equal(Now, cached.LastVerifiedUtc);
        Assert.True(OperatorPinCredential.Verify("270813", cached.Salt, cached.Subkey));
    }

    [Fact]
    public async Task CreatePin_RejectsASimplePin_AndAMismatch_ThenBackReturnsToCredentials()
    {
        _verifier.Outcome = Verified(Guid.NewGuid(), "nuevo@x.test");
        var model = Model();
        model.Refresh();
        await model.SubmitCredentialsAsync("nuevo@x.test", "secret");

        Assert.False(model.SubmitNewPin("123456", "123456"));
        Assert.Equal(PosMessages.InvalidPinFormat, model.Status);
        Assert.False(model.SubmitNewPin(GoodPin, "482916"));
        Assert.Equal(PosMessages.PinMismatch, model.Status);
        Assert.Empty(Store.Load());

        model.Back();
        Assert.Equal(LockScreenMode.Credentials, model.Mode);
    }

    [Fact]
    public async Task InvalidCredentials_ShowTheMessage_AndStayOnCredentials()
    {
        _verifier.Outcome = OperatorVerifyOutcome.InvalidCredentials();
        var model = Model();
        model.Refresh();

        Assert.False(await model.SubmitCredentialsAsync("a@x.test", "bad"));

        Assert.Equal(PosMessages.InvalidCredentials, model.Status);
        Assert.Equal(LockScreenMode.Credentials, model.Mode);
    }

    [Fact]
    public async Task TerminalNotRecognized_TellsTheOperatorToPairAgain()
    {
        _verifier.Outcome = OperatorVerifyOutcome.TerminalNotRecognized();
        var model = Model();
        model.Refresh();

        await model.SubmitCredentialsAsync("a@x.test", "x");

        Assert.Equal(PosMessages.TerminalNotRecognized, model.Status);
        Assert.Contains("volvé a configurarla", model.Status);
    }

    [Theory]
    [InlineData(PosMessages.OperatorNotPermitted)]
    [InlineData(PosMessages.BranchNotInScope)]
    [InlineData(PosMessages.NoBranchesAssigned)]
    [InlineData(PosMessages.ServerUnreachable)]
    public async Task FailedOutcomes_ShowTheirOwnMessage(string message)
    {
        _verifier.Outcome = OperatorVerifyOutcome.Failed(message);
        var model = Model();
        model.Refresh();

        Assert.False(await model.SubmitCredentialsAsync("a@x.test", "x"));

        Assert.Equal(message, model.Status);
        Assert.Equal(LockScreenMode.Credentials, model.Mode);
    }

    [Fact]
    public async Task Offline_CredentialsFail_ButTilePinStillWorks()
    {
        var ana = Cache("ana@x.test");
        _verifier.Outcome = OperatorVerifyOutcome.Failed(PosMessages.ServerUnreachable);
        var model = Model();
        model.Refresh();
        model.ChooseCredentials();

        Assert.False(await model.SubmitCredentialsAsync("b@x.test", "x"));
        Assert.Equal(PosMessages.ServerUnreachable, model.Status);

        model.Back();
        model.SelectTile(ana.UserId);
        Assert.True(model.SubmitPin(GoodPin));
        Assert.Equal(1, _verifier.Calls);
    }

    // ---- refresh after a background change --------------------------------------------

    [Fact]
    public void ReloadTiles_DropsAnOperatorRemovedInTheBackground_AndLeavesPinEntryIfItWasTheirs()
    {
        var ana = Cache("ana@x.test");
        var beto = Cache("beto@x.test");
        var model = Model();
        model.Refresh();
        model.SelectTile(ana.UserId);

        Store.Remove(ana.UserId);
        model.ReloadTiles();

        Assert.Equal(LockScreenMode.Tiles, model.Mode);
        Assert.Equal(beto.UserId, model.Tiles.Single().UserId);
    }

    [Fact]
    public void ReloadTiles_WhenTheLastTileGoes_FallsBackToCredentials()
    {
        var ana = Cache("ana@x.test");
        var model = Model();
        model.Refresh();

        Store.Remove(ana.UserId);
        model.ReloadTiles();

        Assert.Equal(LockScreenMode.Credentials, model.Mode);
        Assert.False(model.CanGoBack);
    }

    [Fact]
    public void ReloadTiles_NeverInterruptsTypingCredentials()
    {
        Cache("ana@x.test");
        var model = Model();
        model.Refresh();
        model.ChooseCredentials();

        model.ReloadTiles();

        Assert.Equal(LockScreenMode.Credentials, model.Mode);
    }

    [Fact]
    public void Refresh_ResetsEverything_ForTheNextOperator()
    {
        var ana = Cache("ana@x.test");
        var model = Model();
        model.Refresh();
        model.SelectTile(ana.UserId);
        model.SubmitPin("999111");

        model.Refresh();

        Assert.Equal(LockScreenMode.Tiles, model.Mode);
        Assert.Null(model.Status);
        Assert.Null(model.SelectedTile);
        Assert.False(model.ResetPin);
    }
}
