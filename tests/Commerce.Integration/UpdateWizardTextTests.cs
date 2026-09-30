using Commerce.Pos.Windows;
using Commerce.Updater;
using Microsoft.Extensions.DependencyInjection;

namespace Commerce.Integration;

public sealed class UpdateWizardTextTests : IDisposable
{
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), "commerce-pos-tests", Guid.NewGuid().ToString());

    public void Dispose()
    {
        if (Directory.Exists(_dataDirectory))
        {
            try
            {
                Directory.Delete(_dataDirectory, recursive: true);
            }
            catch (IOException)
            {
                // A pooled SQLite handle may still hold the file; the temp dir is disposable.
            }
        }
    }

    [Theory]
    [InlineData(0L, "Tamaño no informado")]
    [InlineData(512L, "512 B")]
    [InlineData(2048L, "2,0 KB")]
    [InlineData(71_680_000L, "68,4 MB")]
    public void FormatSize_UsesSpanishDecimalComma(long bytes, string expected)
    {
        Assert.Equal(expected, UpdateWizardText.FormatSize(bytes));
    }

    [Fact]
    public void Details_DescribeCurrentTargetFormatSizeAndCompatibility()
    {
        var package = new UpdatePackage { Format = PackageFormat.Msix, Architecture = "x64", SizeBytes = 71_680_000, PublisherId = "CN=Test" };
        var check = new UpdateCheckResult(UpdateCheckStatus.Available, new Version(0, 1, 0), new Version(0, 2, 0), package);

        var details = UpdateWizardText.Describe(check, new UpdateEnvironment(26100, "x64"));

        Assert.Equal("0.1.0", details.CurrentVersion);
        Assert.Equal("0.2.0", details.TargetVersion);
        Assert.Equal("MSIX", details.Format);
        Assert.Equal("68,4 MB", details.Size);
        Assert.Equal("Compatible con esta terminal (Windows 26100, x64)", details.Compatibility);
    }

    [Theory]
    [InlineData(UpdateInstallStage.Download, "Descargar el paquete")]
    [InlineData(UpdateInstallStage.Verify, "Verificar integridad y firma")]
    [InlineData(UpdateInstallStage.Quiesce, "Preparar la terminal")]
    [InlineData(UpdateInstallStage.Backup, "Copia de seguridad de la base local")]
    [InlineData(UpdateInstallStage.Install, "Instalar y reiniciar")]
    public void StageLabels_AreSpanish(UpdateInstallStage stage, string expected)
    {
        Assert.Equal(expected, UpdateWizardText.StageLabel(stage));
    }

    [Fact]
    public void WizardStages_ExcludePreflight_AndFollowExecutionOrder()
    {
        Assert.Equal(
            [UpdateInstallStage.Download, UpdateInstallStage.Verify, UpdateInstallStage.Quiesce, UpdateInstallStage.Backup, UpdateInstallStage.Install],
            UpdateWizardText.VisibleStages);
    }

    [Theory]
    [InlineData(UpdateFailureReason.HashMismatch, "Verificación fallida")]
    [InlineData(UpdateFailureReason.SignatureInvalid, "Verificación fallida")]
    [InlineData(UpdateFailureReason.SignerMismatch, "Verificación fallida")]
    [InlineData(UpdateFailureReason.PublisherNotTrusted, "Verificación fallida")]
    [InlineData(UpdateFailureReason.SaleInProgress, "Venta en curso")]
    [InlineData(UpdateFailureReason.NotPackaged, "No se puede instalar en esta ejecución")]
    [InlineData(UpdateFailureReason.InstallFailed, "La instalación falló")]
    public void FailureTitle_IsTypedPerReason(UpdateFailureReason reason, string expected)
    {
        Assert.Equal(expected, UpdateWizardText.FailureTitle(reason));
    }

    [Fact]
    public void Composition_ResolvesTheWizardFactory_WithTheInterimPublisherByDefault()
    {
        using var host = PosHostBuilder.Build(_dataDirectory);

        var factory = host.Services.GetRequiredService<UpdateInstallWorkflowFactory>();

        Assert.Equal("CN=Incoders Commerce (Interim)", factory.TrustedPublisher);
        Assert.NotNull(factory.Create(() => false));
    }

    [Fact]
    public void Composition_TrustedPublisherComesFromConfiguration()
    {
        const string variable = "Commerce__UpdateTrustedPublisher";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "CN=Incoders SRL");
        try
        {
            using var host = PosHostBuilder.Build(_dataDirectory);

            Assert.Equal("CN=Incoders SRL", host.Services.GetRequiredService<UpdateInstallWorkflowFactory>().TrustedPublisher);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void UnpackagedTestProcess_IsNotReportedAsPackaged()
    {
        Assert.False(new PackagedAppInfo().IsPackaged);
    }
}
