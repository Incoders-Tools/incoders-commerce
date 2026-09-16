using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Commerce.Pos.Windows;

/// <summary>
/// Composition root entry point (design.md "BranchNode hosting"). Delegates
/// the actual DI wiring to <see cref="PosHostBuilder"/> so it stays
/// unit-testable outside the WPF application lifecycle.
/// </summary>
// Fully qualified: this namespace (Commerce.Pos.Windows) sits under the
// enclosing Commerce namespace alongside Commerce.Application, so the bare
// name "Application" resolves to that sibling namespace before System.Windows.
public partial class App : System.Windows.Application
{
    private IHost? _host;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _host = PosHostBuilder.Build();
        _host.Start();

        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
