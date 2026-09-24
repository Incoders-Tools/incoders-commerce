using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Commerce.Updater;

public enum UpdateCheckStatus
{
    UpToDate,
    Available,
    ManifestNotConfigured,
    CheckFailedInvalid,
    UnsupportedSchema,
    IncompatibleWindows,
    IncompatibleArchitecture,
    NoCompatiblePackage
}

public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    Version LocalVersion,
    Version? AvailableVersion = null,
    UpdatePackage? Package = null,
    string? Detail = null)
{
    public bool IsUpdateAvailable => Status == UpdateCheckStatus.Available;
}

public sealed record UpdateEnvironment(int WindowsBuild, string Architecture)
{
    public static UpdateEnvironment Current() => new(
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? Environment.OSVersion.Version.Build : 0,
        RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
}

public sealed record LocalUpdateManifestSource(string? ManifestPath)
{
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ManifestPath) && File.Exists(ManifestPath);
}

public sealed class ReleaseDiscovery
{
    public const int SupportedManifestSchemaVersion = 1;
    public const string ProductId = "Commerce.Pos.Windows";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public UpdateCheckResult CheckForUpdates(
        Version localVersion,
        LocalUpdateManifestSource source,
        UpdateEnvironment? environment = null)
    {
        if (!source.IsConfigured || source.ManifestPath is null)
        {
            return new UpdateCheckResult(UpdateCheckStatus.ManifestNotConfigured, localVersion);
        }

        ReleaseDiscoveryManifest? manifest;
        try
        {
            var json = File.ReadAllText(source.ManifestPath);
            manifest = JsonSerializer.Deserialize<ReleaseDiscoveryManifest>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UpdateCheckResult(UpdateCheckStatus.CheckFailedInvalid, localVersion, Detail: ex.Message);
        }

        if (manifest is null || string.IsNullOrWhiteSpace(manifest.Product) || string.IsNullOrWhiteSpace(manifest.Version))
        {
            return new UpdateCheckResult(UpdateCheckStatus.CheckFailedInvalid, localVersion);
        }

        if (manifest.SchemaVersion != SupportedManifestSchemaVersion)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UnsupportedSchema, localVersion);
        }

        // Collections are null-checked despite their non-nullable declarations:
        // System.Text.Json overwrites property initializers with an explicit
        // null in the payload.
        if (!string.Equals(manifest.Product, ProductId, StringComparison.OrdinalIgnoreCase) ||
            !Version.TryParse(manifest.Version, out var manifestVersion) ||
            manifest.Compatibility is null ||
            manifest.Packages is null or { Count: 0 })
        {
            return new UpdateCheckResult(UpdateCheckStatus.CheckFailedInvalid, localVersion);
        }

        if (manifestVersion <= localVersion)
        {
            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, localVersion, manifestVersion);
        }

        var targetEnvironment = environment ?? UpdateEnvironment.Current();
        var architecture = NormalizeArchitecture(targetEnvironment.Architecture);

        if (targetEnvironment.WindowsBuild < manifest.Compatibility.MinimumWindowsBuild)
        {
            return new UpdateCheckResult(UpdateCheckStatus.IncompatibleWindows, localVersion, manifestVersion);
        }

        var manifestArchitectures = (manifest.Compatibility.Architectures ?? [])
            .Select(NormalizeArchitecture)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (manifestArchitectures.Count > 0 && !manifestArchitectures.Contains(architecture))
        {
            return new UpdateCheckResult(UpdateCheckStatus.IncompatibleArchitecture, localVersion, manifestVersion);
        }

        var package = manifest.Packages.FirstOrDefault(candidate =>
            candidate is not null &&
            string.Equals(NormalizeArchitecture(candidate.Architecture), architecture, StringComparison.OrdinalIgnoreCase) &&
            targetEnvironment.WindowsBuild >= candidate.MinimumWindowsBuild &&
            !string.IsNullOrWhiteSpace(candidate.Url) &&
            !string.IsNullOrWhiteSpace(candidate.Sha256) &&
            !string.IsNullOrWhiteSpace(candidate.PublisherId));

        return package is null
            ? new UpdateCheckResult(UpdateCheckStatus.NoCompatiblePackage, localVersion, manifestVersion)
            : new UpdateCheckResult(UpdateCheckStatus.Available, localVersion, manifestVersion, package);
    }

    public static string FormatCompactStatus(UpdateCheckResult result) => result.Status switch
    {
        UpdateCheckStatus.UpToDate => "Actualizado",
        UpdateCheckStatus.Available => $"Update {result.AvailableVersion} disponible",
        UpdateCheckStatus.ManifestNotConfigured => "Manifest de updates no configurado",
        UpdateCheckStatus.CheckFailedInvalid => "No se pudo comprobar updates",
        UpdateCheckStatus.UnsupportedSchema => "Manifest de updates no soportado",
        UpdateCheckStatus.IncompatibleWindows => "Update requiere una versión de Windows compatible",
        UpdateCheckStatus.IncompatibleArchitecture => "Update no compatible con esta arquitectura",
        UpdateCheckStatus.NoCompatiblePackage => "Update sin paquete compatible",
        _ => "No se pudo comprobar updates"
    };

    private static string NormalizeArchitecture(string? architecture) => (architecture ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "amd64" => "x64",
        "x86_64" => "x64",
        "arm64" => "arm64",
        "x86" => "x86",
        var value => value
    };
}

public sealed record ReleaseDiscoveryManifest
{
    public int SchemaVersion { get; init; }
    public string Product { get; init; } = string.Empty;
    public string Channel { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string? ReleaseNotesUrl { get; init; }
    public UpdateCompatibility? Compatibility { get; init; }
    public List<UpdatePackage> Packages { get; init; } = [];
}

public sealed record UpdateCompatibility
{
    public int MinimumWindowsBuild { get; init; }
    public List<string> Architectures { get; init; } = [];
    public int SyncContractVersion { get; init; }
    public int SchemaVersion { get; init; }
}

public sealed record UpdatePackage
{
    public PackageFormat Format { get; init; }
    public string Architecture { get; init; } = string.Empty;
    public int MinimumWindowsBuild { get; init; }
    public string Url { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string PublisherId { get; init; } = string.Empty;
    public bool SignatureRequired { get; init; }
    public bool AttestationRequired { get; init; }
}
