using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Commerce.Updater;

public enum ManifestFetchKind
{
    /// <summary>A manifest document was retrieved.</summary>
    Found,

    /// <summary>The source answered but has no manifest to offer (no release, no asset, no file).</summary>
    NotAvailable,

    /// <summary>The source could not be read (network, HTTP status, timeout, malformed payload).</summary>
    Failed
}

public sealed record ManifestFetchResult(ManifestFetchKind Kind, string? Json = null, string? Detail = null);

/// <summary>Where the release manifest comes from (GitHub Releases in production, a file for VM tests).</summary>
public interface IUpdateManifestSource
{
    Task<ManifestFetchResult> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>Explicit override: a manifest JSON file on disk (<c>Commerce:UpdateManifestPath</c>).</summary>
public sealed class LocalFileManifestSource(string path) : IUpdateManifestSource
{
    public async Task<ManifestFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new ManifestFetchResult(ManifestFetchKind.NotAvailable);
        }

        try
        {
            return new ManifestFetchResult(ManifestFetchKind.Found, await File.ReadAllTextAsync(path, cancellationToken));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ManifestFetchResult(ManifestFetchKind.Failed, Detail: ex.Message);
        }
    }
}

/// <summary>
/// Finds the newest release of a channel through the public, unauthenticated
/// GitHub REST API and downloads its <c>commerce-pos-release-manifest.json</c>
/// asset. <c>stable</c> considers only non-prerelease releases; <c>internal</c>
/// also considers prereleases. Every network, HTTP or parse failure is reported
/// as <see cref="ManifestFetchKind.Failed"/>; nothing throws to the caller
/// (except the caller's own cancellation).
/// </summary>
public sealed partial class GitHubReleaseManifestSource : IUpdateManifestSource
{
    public const string ManifestAssetName = "commerce-pos-release-manifest.json";
    public const string InternalChannel = "internal";

    private readonly HttpClient _http;
    private readonly string _repository;
    private readonly bool _includePrereleases;
    private readonly TimeSpan _timeout;

    public GitHubReleaseManifestSource(HttpClient http, string repository, string channel, TimeSpan? timeout = null)
    {
        _http = http;
        _repository = repository;
        _includePrereleases = string.Equals(channel?.Trim(), InternalChannel, StringComparison.OrdinalIgnoreCase);
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    public async Task<ManifestFetchResult> FetchAsync(CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(_timeout);
        try
        {
            var releasesUrl = $"https://api.github.com/repos/{_repository}/releases?per_page=30";
            var (listOk, listBody, listDetail) = await GetAsync(releasesUrl, budget.Token);
            if (!listOk)
            {
                return new ManifestFetchResult(ManifestFetchKind.Failed, Detail: listDetail);
            }

            var newest = PickNewest(listBody!);
            if (newest?.ManifestUrl is null)
            {
                return new ManifestFetchResult(ManifestFetchKind.NotAvailable);
            }

            var (manifestOk, manifestBody, manifestDetail) = await GetAsync(newest.ManifestUrl, budget.Token);
            return manifestOk
                ? new ManifestFetchResult(ManifestFetchKind.Found, manifestBody)
                : new ManifestFetchResult(ManifestFetchKind.Failed, Detail: manifestDetail);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new ManifestFetchResult(ManifestFetchKind.Failed, Detail: "Timed out reading the release feed.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or FormatException)
        {
            return new ManifestFetchResult(ManifestFetchKind.Failed, Detail: ex.Message);
        }
    }

    private async Task<(bool Ok, string? Body, string? Detail)> GetAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("IncodersCommercePOS", "1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (false, null, $"HTTP {(int)response.StatusCode} from {new Uri(url).Host}.");
        }

        return (true, await response.Content.ReadAsStringAsync(cancellationToken), null);
    }

    private ReleaseCandidate? PickNewest(string releasesJson)
    {
        using var document = JsonDocument.Parse(releasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The release feed is not a JSON array.");
        }

        ReleaseCandidate? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object ||
                Flag(release, "draft") ||
                (Flag(release, "prerelease") && !_includePrereleases) ||
                !release.TryGetProperty("tag_name", out var tagElement) ||
                tagElement.GetString() is not { } tag ||
                !TryMapTagToVersion(tag, out var version))
            {
                continue;
            }

            if (best is not null && version <= best.Version)
            {
                continue;
            }

            best = new ReleaseCandidate(version, FindManifestUrl(release));
        }

        return best;
    }

    private static bool Flag(JsonElement release, string name) =>
        release.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? FindManifestUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.TryGetProperty("name", out var name) &&
                string.Equals(name.GetString(), ManifestAssetName, StringComparison.Ordinal) &&
                asset.TryGetProperty("browser_download_url", out var url) &&
                url.GetString() is { Length: > 0 } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// Mirrors <c>deploy/release/build-pos-msix.ps1</c>: <c>1.4.0</c> maps to
    /// <c>1.4.0</c> and <c>1.4.0-internal.3</c> to <c>1.4.0.3</c>, which is the
    /// version the release manifest declares, so releases order like the
    /// updater compares them.
    /// </summary>
    internal static bool TryMapTagToVersion(string tag, out Version version)
    {
        version = new Version(0, 0);
        var match = TagPattern().Match(tag.Trim());
        if (!match.Success)
        {
            return false;
        }

        var core = $"{match.Groups[1].Value}.{match.Groups[2].Value}.{match.Groups[3].Value}";
        var text = core;
        if (match.Groups[4].Success)
        {
            var tail = TrailingNumber().Match(match.Groups[4].Value);
            text = $"{core}.{(tail.Success ? tail.Groups[1].Value : "0")}";
        }

        return Version.TryParse(text, out version!);
    }

    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(-[0-9A-Za-z.-]+)?(\+.*)?$")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"(\d+)$")]
    private static partial Regex TrailingNumber();

    private sealed record ReleaseCandidate(Version Version, string? ManifestUrl);
}

/// <summary>
/// Async check over any <see cref="IUpdateManifestSource"/>. Never throws: an
/// unreachable or unreadable source is the typed
/// <see cref="UpdateCheckStatus.CheckFailedInvalid"/> outcome, never "no update".
/// </summary>
public sealed class UpdateChecker(IUpdateManifestSource source, ReleaseDiscovery discovery)
{
    public async Task<UpdateCheckResult> CheckAsync(
        Version localVersion,
        UpdateEnvironment? environment = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var fetch = await source.FetchAsync(cancellationToken);
            return fetch.Kind switch
            {
                ManifestFetchKind.Found when fetch.Json is not null => discovery.Evaluate(localVersion, fetch.Json, environment),
                ManifestFetchKind.NotAvailable => new UpdateCheckResult(UpdateCheckStatus.ManifestNotConfigured, localVersion),
                _ => new UpdateCheckResult(UpdateCheckStatus.CheckFailedInvalid, localVersion, Detail: fetch.Detail)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.CheckFailedInvalid, localVersion, Detail: ex.Message);
        }
    }
}
