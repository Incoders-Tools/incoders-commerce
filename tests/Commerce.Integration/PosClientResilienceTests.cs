using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Commerce.Domain.Sync;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// Every device/HTTP client of the POS turns every response shape into a typed
/// outcome with a friendly Spanish message (PosMessages). None of them may
/// throw for an empty body, an HTML error page, a non-JSON 200, a timeout or
/// an unreachable host. Regression: an empty-body 401 from
/// `/device/operators/verify` used to throw a JsonException out of an
/// `async void` click handler and kill the app.
/// </summary>
public sealed class PosClientResilienceTests
{
    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw exception;
    }

    private static HttpClient Client(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://cloud.invalid") };

    private static HttpClient Respond(HttpStatusCode status, string? body = null, string contentType = "application/json", bool challenge = false) =>
        Client(new StubHandler(() =>
        {
            var response = new HttpResponseMessage(status);
            if (body is not null)
            {
                response.Content = new StringContent(body, Encoding.UTF8, contentType);
            }
            if (challenge)
            {
                response.Headers.WwwAuthenticate.Add(new AuthenticationHeaderValue("Bearer"));
            }
            return response;
        }));

    private const string Html = "<html><body>Bad gateway</body></html>";

    // ---- OperatorProvisioningClient.VerifyAsync ------------------------------

    private static Task<OperatorVerifyOutcome> Verify(HttpClient http) =>
        new OperatorProvisioningClient(http).VerifyAsync("a@b.c", "pw", "token");

    [Fact]
    public async Task Verify_EmptyBody401_IsInvalidCredentials_AndDoesNotThrow()
    {
        var outcome = await Verify(Respond(HttpStatusCode.Unauthorized));

        Assert.Equal(OperatorVerifyOutcomeKind.InvalidCredentials, outcome.Kind);
        Assert.Equal(PosMessages.InvalidCredentials, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Verify_401WithBearerChallenge_IsTerminalNotRecognized()
    {
        var outcome = await Verify(Respond(HttpStatusCode.Unauthorized, challenge: true));

        Assert.Equal(OperatorVerifyOutcomeKind.TerminalNotRecognized, outcome.Kind);
        Assert.Equal(PosMessages.TerminalNotRecognized, outcome.ErrorMessage);
    }

    [Theory]
    [InlineData("branch-not-in-scope", PosMessages.BranchNotInScope)]
    [InlineData("no-branches-assigned", PosMessages.NoBranchesAssigned)]
    [InlineData("operator-not-permitted", PosMessages.OperatorNotPermitted)]
    public async Task Verify_Typed403_MapsToItsFriendlyMessage(string status, string expected)
    {
        var outcome = await Verify(Respond(HttpStatusCode.Forbidden, $$"""{"status":"{{status}}"}"""));

        Assert.Equal(OperatorVerifyOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(expected, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Verify_Forbidden_WithoutTypedBody_IsAccessDenied()
    {
        var outcome = await Verify(Respond(HttpStatusCode.Forbidden));

        Assert.Equal(OperatorVerifyOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PosMessages.AccessDenied, outcome.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Verify_ServerErrorWithHtmlBody_IsServerError(HttpStatusCode status)
    {
        var outcome = await Verify(Respond(status, Html, "text/html"));

        Assert.Equal(OperatorVerifyOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PosMessages.ServerError, outcome.ErrorMessage);
    }

    [Theory]
    [InlineData(Html, "text/html")]
    [InlineData("", "application/json")]
    [InlineData("{not json", "application/json")]
    public async Task Verify_Ok_WithBodyThatIsNotTheExpectedJson_IsUnexpectedResponse(string body, string contentType)
    {
        var outcome = await Verify(Respond(HttpStatusCode.OK, body, contentType));

        Assert.Equal(OperatorVerifyOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PosMessages.UnexpectedResponse, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Verify_Ok_WithNoContent_IsUnexpectedResponse()
    {
        var outcome = await Verify(Respond(HttpStatusCode.OK));

        Assert.Equal(OperatorVerifyOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PosMessages.UnexpectedResponse, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Verify_Timeout_IsServerUnreachable()
    {
        var outcome = await Verify(Client(new ThrowingHandler(new TaskCanceledException("timed out"))));

        Assert.Equal(OperatorVerifyOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PosMessages.ServerUnreachable, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Verify_HostUnreachable_IsServerUnreachable()
    {
        var outcome = await Verify(Client(new ThrowingHandler(new HttpRequestException("no route"))));

        Assert.Equal(OperatorVerifyOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(PosMessages.ServerUnreachable, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Verify_Verified_StillWorks()
    {
        var userId = Guid.NewGuid();
        var orgId = Guid.NewGuid();
        var outcome = await Verify(Respond(HttpStatusCode.OK,
            $$"""{"status":"verified","userId":"{{userId}}","email":"a@b.c","organizationId":"{{orgId}}","permissions":16}"""));

        Assert.Equal(OperatorVerifyOutcomeKind.Verified, outcome.Kind);
        Assert.Equal(userId, outcome.UserId);
        Assert.Equal(16, outcome.Permissions);
    }

    // ---- OperatorProvisioningClient.GetStatusAsync ---------------------------

    private static Task<OperatorStatusOutcome> Status(HttpClient http) =>
        new OperatorProvisioningClient(http).GetStatusAsync(Guid.NewGuid(), "token");

    [Fact]
    public async Task Status_EmptyBody401_IsUnreachable_AndDoesNotThrow()
    {
        Assert.Equal(OperatorStatusOutcome.Unreachable, await Status(Respond(HttpStatusCode.Unauthorized)));
    }

    [Fact]
    public async Task Status_401WithBearerChallenge_IsTerminalNotRecognized()
    {
        Assert.Equal(OperatorStatusOutcome.TerminalNotRecognized, await Status(Respond(HttpStatusCode.Unauthorized, challenge: true)));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, Html, "text/html")]
    [InlineData(HttpStatusCode.Forbidden, "", "application/json")]
    [InlineData(HttpStatusCode.OK, Html, "text/html")]
    [InlineData(HttpStatusCode.OK, "{not json", "application/json")]
    public async Task Status_AnythingButATypedAnswer_IsUnreachable_SoTheCachedOperatorSurvives(HttpStatusCode code, string body, string contentType)
    {
        Assert.Equal(OperatorStatusOutcome.Unreachable, await Status(Respond(code, body, contentType)));
    }

    [Fact]
    public async Task Status_Timeout_IsUnreachable()
    {
        Assert.Equal(OperatorStatusOutcome.Unreachable, await Status(Client(new ThrowingHandler(new TaskCanceledException()))));
    }

    [Theory]
    [InlineData("active", OperatorStatusOutcome.Active)]
    [InlineData("inactive", OperatorStatusOutcome.Inactive)]
    public async Task Status_TypedAnswers_StillWork(string status, OperatorStatusOutcome expected)
    {
        Assert.Equal(expected, await Status(Respond(HttpStatusCode.OK, $$"""{"status":"{{status}}"}""")));
    }

    // ---- DevicePairingClient -------------------------------------------------

    private static Task<PairingOutcome> Pair(HttpClient http) =>
        new DevicePairingClient(http).PairAsync("a@b.c", "pw", Guid.NewGuid(), null);

    [Fact]
    public async Task Pair_401_IsInvalidCredentials()
    {
        var outcome = await Pair(Respond(HttpStatusCode.Unauthorized));

        Assert.Equal(PairingOutcomeKind.InvalidCredentials, outcome.Kind);
        Assert.Equal(PosMessages.InvalidCredentials, outcome.ErrorMessage);
    }

    [Theory]
    [InlineData("no-branches-assigned", PosMessages.NoBranchesAssigned)]
    [InlineData("branch-not-in-scope", PosMessages.SelectedBranchNotInScope)]
    [InlineData("operator-not-permitted", PosMessages.OperatorNotPermitted)]
    [InlineData("something-new", PosMessages.UnexpectedResponse)]
    public async Task Pair_TypedStatuses_MapToFriendlyMessages(string status, string expected)
    {
        var outcome = await Pair(Respond(HttpStatusCode.Forbidden, $$"""{"status":"{{status}}"}"""));

        Assert.Equal(PairingOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(expected, outcome.ErrorMessage);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, Html, "text/html", PosMessages.ServerError)]
    [InlineData(HttpStatusCode.BadGateway, "", "application/json", PosMessages.ServerError)]
    [InlineData(HttpStatusCode.Forbidden, "", "application/json", PosMessages.AccessDenied)]
    [InlineData(HttpStatusCode.BadRequest, Html, "text/html", PosMessages.UnexpectedResponse)]
    [InlineData(HttpStatusCode.OK, Html, "text/html", PosMessages.UnexpectedResponse)]
    [InlineData(HttpStatusCode.OK, "{not json", "application/json", PosMessages.UnexpectedResponse)]
    public async Task Pair_NonSuccessOrNonJson_IsFailedWithFriendlyMessage(HttpStatusCode code, string body, string contentType, string expected)
    {
        var outcome = await Pair(Respond(code, body, contentType));

        Assert.Equal(PairingOutcomeKind.Failed, outcome.Kind);
        Assert.Equal(expected, outcome.ErrorMessage);
    }

    [Fact]
    public async Task Pair_TimeoutAndUnreachable_AreServerUnreachable()
    {
        var timeout = await Pair(Client(new ThrowingHandler(new TaskCanceledException())));
        var unreachable = await Pair(Client(new ThrowingHandler(new HttpRequestException("dns"))));

        Assert.Equal(PosMessages.ServerUnreachable, timeout.ErrorMessage);
        Assert.Equal(PosMessages.ServerUnreachable, unreachable.ErrorMessage);
    }

    [Fact]
    public async Task Pair_Paired_StillWorks()
    {
        var org = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var outcome = await Pair(Respond(HttpStatusCode.OK,
            $$"""{"status":"paired","organizationId":"{{org}}","branchId":"{{branch}}","branchName":"Main","deviceToken":"t"}"""));

        Assert.Equal(PairingOutcomeKind.Paired, outcome.Kind);
        Assert.Equal("Main", outcome.Pairing!.BranchName);
    }

    // ---- UserAdminClient / CustomerAdminClient -------------------------------

    [Fact]
    public async Task UserAdmin_SignIn_MapsEveryResponseShapeWithoutThrowing()
    {
        Assert.Equal(PosMessages.InvalidCredentials, (await new UserAdminClient(Respond(HttpStatusCode.Unauthorized)).SignInAsync("a", "b")).ErrorMessage);
        Assert.Equal(PosMessages.ServerError, (await new UserAdminClient(Respond(HttpStatusCode.InternalServerError, Html, "text/html")).SignInAsync("a", "b")).ErrorMessage);
        Assert.Equal(PosMessages.UnexpectedResponse, (await new UserAdminClient(Respond(HttpStatusCode.OK, Html, "text/html")).SignInAsync("a", "b")).ErrorMessage);
        Assert.Equal(PosMessages.ServerUnreachable, (await new UserAdminClient(Client(new ThrowingHandler(new TaskCanceledException()))).SignInAsync("a", "b")).ErrorMessage);
    }

    [Fact]
    public async Task UserAdmin_ListUsers_NonJsonOk_IsNull_NotAnException()
    {
        Assert.Null(await new UserAdminClient(Respond(HttpStatusCode.OK, Html, "text/html")).ListUsersAsync());
        Assert.Null(await new UserAdminClient(Respond(HttpStatusCode.InternalServerError)).ListUsersAsync());
    }

    [Fact]
    public async Task UserAdmin_Mutations_UseFriendlyMessages_NeverTheRawBody()
    {
        var request = new CreateUserAdminRequestDto("a@b.c", "pw", ["cashier"], [Guid.NewGuid()]);

        var forbidden = await new UserAdminClient(Respond(HttpStatusCode.Forbidden)).CreateUserAsync(request);
        var notFound = await new UserAdminClient(Respond(HttpStatusCode.NotFound)).CreateUserAsync(request);
        var serverError = await new UserAdminClient(Respond(HttpStatusCode.InternalServerError, Html, "text/html")).CreateUserAsync(request);
        var expired = await new UserAdminClient(Respond(HttpStatusCode.Unauthorized)).CreateUserAsync(request);

        Assert.Equal(PosMessages.NoPermissionToManageStaff, forbidden.ErrorMessage);
        Assert.Equal(PosMessages.StaffUserNotFound, notFound.ErrorMessage);
        Assert.Equal(PosMessages.ServerError, serverError.ErrorMessage);
        Assert.DoesNotContain("Bad gateway", serverError.ErrorMessage);
        Assert.Equal(PosMessages.SessionExpired, expired.ErrorMessage);
    }

    [Fact]
    public async Task CustomerAdmin_SignIn_MapsEveryResponseShapeWithoutThrowing()
    {
        Assert.Equal(PosMessages.InvalidCredentials, (await new CustomerAdminClient(Respond(HttpStatusCode.Unauthorized)).SignInAsync("a", "b")).ErrorMessage);
        Assert.Equal(PosMessages.ServerError, (await new CustomerAdminClient(Respond(HttpStatusCode.InternalServerError, Html, "text/html")).SignInAsync("a", "b")).ErrorMessage);
        Assert.Equal(PosMessages.UnexpectedResponse, (await new CustomerAdminClient(Respond(HttpStatusCode.OK, Html, "text/html")).SignInAsync("a", "b")).ErrorMessage);
        Assert.Equal(PosMessages.ServerUnreachable, (await new CustomerAdminClient(Client(new ThrowingHandler(new HttpRequestException()))).SignInAsync("a", "b")).ErrorMessage);
        Assert.Null(await new CustomerAdminClient(Respond(HttpStatusCode.OK, Html, "text/html")).ListCustomersAsync());
    }

    // ---- Sync clients ---------------------------------------------------------

    [Fact]
    public async Task ReplicaClients_NonJsonOk_FailWithoutThrowing()
    {
        var since = DateTimeOffset.UnixEpoch;

        var customers = await new CustomerReplicaClient(Respond(HttpStatusCode.OK, Html, "text/html")).PullAsync(since, "t");
        var catalog = await new CatalogPriceReplicaClient(Respond(HttpStatusCode.OK, Html, "text/html")).PullAsync(since, "t");
        var pin = await new DiscountPinReplicaClient(Respond(HttpStatusCode.OK, Html, "text/html")).PullAsync("t");

        Assert.False(customers.Success);
        Assert.False(catalog.Success);
        Assert.False(pin.Success);
    }

    [Fact]
    public async Task ReplicaClients_Unauthorized_FailWithoutThrowing()
    {
        var since = DateTimeOffset.UnixEpoch;

        Assert.False((await new CustomerReplicaClient(Respond(HttpStatusCode.Unauthorized)).PullAsync(since, "t")).Success);
        Assert.False((await new CatalogPriceReplicaClient(Respond(HttpStatusCode.Unauthorized)).PullAsync(since, "t")).Success);
        Assert.False((await new DiscountPinReplicaClient(Respond(HttpStatusCode.Unauthorized)).PullAsync("t")).Success);
    }

    [Fact]
    public async Task CloudSync_Push_RejectedCredential_ReportsRepairHintInSpanish_AndNonJsonOkStillSucceeds()
    {
        var envelope = new SyncEnvelope(Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, "sale", """{"a":1}""");

        var rejected = await new CloudSyncClient(Respond(HttpStatusCode.Unauthorized)).PushAsync(envelope, "t");
        var okHtml = await new CloudSyncClient(Respond(HttpStatusCode.OK, Html, "text/html")).PushAsync(envelope, "t");
        var timeout = await new CloudSyncClient(Client(new ThrowingHandler(new TaskCanceledException()))).PushAsync(envelope, "t");

        Assert.True(rejected.CredentialWasRejected);
        Assert.Contains(PosMessages.TerminalNotRecognized, rejected.Error);
        Assert.True(okHtml.Success);
        Assert.False(timeout.Success);
    }
}
