using System.Net;
using System.Net.Http;
using Commerce.Pos.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Integration;

public sealed class PosStaffManagementTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "commerce-pos-staff", Guid.NewGuid().ToString());

    [Fact]
    public async Task UserAdminClient_HasWindowScopedLifetime_AndCallsStaffListEndpoint()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);
        var factory = host.Services.GetRequiredService<Func<UserAdminClient>>();
        using var first = factory();
        using var second = factory();
        var handler = new RecordingHandler();
        using var requestClient = new UserAdminClient(new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") });

        Assert.NotSame(first, second);
        var users = await requestClient.ListUsersAsync();
        Assert.NotNull(users);
        Assert.Equal("/account/users", handler.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Get, handler.Method);
    }

    [Fact]
    public void StaffWindow_AndMainWindowMarkup_ExposeManageUsersFlow()
    {
        Assert.NotNull(typeof(UsersWindow));
        var root = FindRepositoryRoot();
        var mainMarkup = File.ReadAllText(Path.Combine(root, "src", "Commerce.Pos.Windows", "MainWindow.xaml"));
        var usersMarkup = File.ReadAllText(Path.Combine(root, "src", "Commerce.Pos.Windows", "UsersWindow.xaml"));
        Assert.Contains("ManageStaffButton", mainMarkup);
        Assert.Contains("ManageStaffButton_Click", mainMarkup);
        Assert.Contains("RolesItemsControl", usersMarkup);
        Assert.Contains("Reset password", usersMarkup);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Commerce.sln"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }
}
