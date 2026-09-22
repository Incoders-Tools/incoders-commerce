using Commerce.Pos.Windows;

namespace Commerce.Integration;

public sealed class ApplicationBrandingTests
{
    [Theory]
    [InlineData(null, null, "Vaca Verde")]
    [InlineData(" Mercado Central ", null, "Mercado Central")]
    [InlineData("Mercado Central", " Tienda Norte ", "Tienda Norte")]
    [InlineData("Mercado Central", "   ", "Vaca Verde")]
    [InlineData("Mercado Central", "Bad\u0001Name", "Vaca Verde")]
    public void Resolve_UsesSelectedSourceOrSafeDefault(string? fileValue, string? environmentValue, string expected)
    {
        var branding = ApplicationBranding.Resolve(fileValue, environmentValue);

        Assert.Equal(expected, branding.ApplicationName);
    }

    [Fact]
    public void Resolve_RejectsValuesOverEightyCharacters_AndComposesAllWindowTitles()
    {
        var branding = ApplicationBranding.Resolve(new string('x', 81), null);

        Assert.Equal("Vaca Verde", branding.ApplicationName);
        Assert.Equal("Vaca Verde POS", branding.MainWindowTitle);
        Assert.Equal("Vaca Verde — Manage staff", branding.UsersWindowTitle);
        Assert.Equal("Vaca Verde — Manage customers", branding.CustomersWindowTitle);
    }

    [Fact]
    public void Load_UsesBrandingFileThenEnvironment_WithoutTouchingInstallationJson()
    {
        var directory = Path.Combine(Path.GetTempPath(), "commerce-branding", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "branding.json"), "{\"Commerce\":{\"ApplicationName\":\"File Brand\"}}");
        File.WriteAllText(Path.Combine(directory, "installation.json"), "{\"Commerce\":{\"ApplicationName\":\"Unsafe\"}}");
        var previous = Environment.GetEnvironmentVariable("Commerce__ApplicationName");
        try
        {
            Environment.SetEnvironmentVariable("Commerce__ApplicationName", null);
            Assert.Equal("File Brand", ApplicationBranding.Load(directory).ApplicationName);
            Environment.SetEnvironmentVariable("Commerce__ApplicationName", "Environment Brand");
            Assert.Equal("Environment Brand", ApplicationBranding.Load(directory).ApplicationName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Commerce__ApplicationName", previous);
            Directory.Delete(directory, recursive: true);
        }

}

    [Fact]
    public void Load_PreservesBrandingWhenApplicationBinariesAreUpgraded()
    {
        var root = Path.Combine(Path.GetTempPath(), "commerce-branding-upgrade", Guid.NewGuid().ToString());
        var applicationBinariesDirectory = Path.Combine(root, "application-binaries");
        var dataDirectory = Path.Combine(root, "local-app-data", "Incoders", "Commerce");
        var brandingFile = Path.Combine(dataDirectory, "branding.json");
        var brandingBytes = "{\"Commerce\":{\"ApplicationName\":\"Upgraded Butcher\"}}"u8.ToArray();
        var previous = Environment.GetEnvironmentVariable("Commerce__ApplicationName");

        Directory.CreateDirectory(applicationBinariesDirectory);
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(Path.Combine(applicationBinariesDirectory, "Commerce.Pos.Windows.exe"), "version-1");
        File.WriteAllBytes(brandingFile, brandingBytes);

        try
        {
            SimulateBinaryUpgrade(applicationBinariesDirectory);

            Assert.Equal("version-2", File.ReadAllText(Path.Combine(applicationBinariesDirectory, "Commerce.Pos.Windows.exe")));
            Assert.True(File.Exists(brandingFile));
            Assert.Equal(brandingBytes, File.ReadAllBytes(brandingFile));

            Environment.SetEnvironmentVariable("Commerce__ApplicationName", null);
            Assert.Equal("Upgraded Butcher", ApplicationBranding.Load(dataDirectory).ApplicationName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Commerce__ApplicationName", previous);
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"Commerce\":null}")]
    [InlineData("[]")]
    public void Load_InvalidJsonStructure_FallsBackToDefault(string json)
    {
        var directory = Path.Combine(Path.GetTempPath(), "commerce-branding", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "branding.json"), json);
        var previous = Environment.GetEnvironmentVariable("Commerce__ApplicationName");
        try
        {
            Environment.SetEnvironmentVariable("Commerce__ApplicationName", null);
            Assert.Equal("Vaca Verde", ApplicationBranding.Load(directory).ApplicationName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Commerce__ApplicationName", previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Load_InvalidJsonStructure_PreservesEnvironmentOverride()
    {
        var directory = Path.Combine(Path.GetTempPath(), "commerce-branding", Guid.NewGuid().ToString());
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "branding.json"), "{\"Commerce\":null}");
        var previous = Environment.GetEnvironmentVariable("Commerce__ApplicationName");
        try
        {
            Environment.SetEnvironmentVariable("Commerce__ApplicationName", "Environment Brand");
            Assert.Equal("Environment Brand", ApplicationBranding.Load(directory).ApplicationName);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Commerce__ApplicationName", previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void SimulateBinaryUpgrade(string applicationBinariesDirectory)
    {
        var binary = Path.Combine(applicationBinariesDirectory, "Commerce.Pos.Windows.exe");
        var stagedBinary = binary + ".next";

        File.WriteAllText(stagedBinary, "version-2");
        File.Move(stagedBinary, binary, overwrite: true);
    }
}
