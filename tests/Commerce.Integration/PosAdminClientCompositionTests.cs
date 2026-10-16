using Commerce.Pos.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Integration;

public sealed class PosAdminClientCompositionTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "commerce-pos-admin", Guid.NewGuid().ToString());

    [Fact]
    public void Build_ProvidesOneSharedManagementConnection_AndBranding()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        Assert.Same(host.Services.GetRequiredService<ManagementConnection>(), host.Services.GetRequiredService<ManagementConnection>());
        Assert.Equal("Vaca Verde", host.Services.GetRequiredService<ApplicationBranding>().ApplicationName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }
}
