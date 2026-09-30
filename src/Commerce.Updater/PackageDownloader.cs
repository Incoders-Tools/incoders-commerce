using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Commerce.Updater;

/// <param name="TimedOut">The connection stalled or the HTTP client timed out (not an operator cancel).</param>
public sealed record DownloadResult(bool Ok, string? Path, string? Detail = null, bool TimedOut = false);

/// <summary>
/// <summary>
/// Downloads a release package into a controlled staging directory. Safe to
/// re-run: stale partial downloads and other staged packages this downloader
/// owns are removed first (only files named like
/// <c>Commerce.Pos.Windows-*.msix[.partial]</c>; anything else in a
/// configurable directory is left alone), an already staged file whose hash
/// matches the manifest is reused, and a half-written file never carries the
/// final name (download to <c>.partial</c>, then move).
/// <para>
/// Timeout: an inactivity budget (<see cref="DefaultStallTimeout"/>) covers the
/// response headers and every read of the body, so a large package on a slow
/// link may take as long as it needs while a stalled connection fails as a
/// typed timeout. The caller registers the HttpClient with an infinite
/// <see cref="HttpClient.Timeout"/> so its 100 s default never cuts a long
/// download. An operator cancel (the caller's token) still throws.
/// </para>
/// </summary>
public sealed partial class PackageDownloader(HttpClient http, TimeSpan? stallTimeout = null)
{
    /// <summary>No bytes for this long means the connection is considered stalled.</summary>
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(60);

    private readonly TimeSpan _stallTimeout = stallTimeout ?? DefaultStallTimeout;

    public async Task<DownloadResult> DownloadAsync(
        UpdatePackage package,
        string stagingDirectory,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(package.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return new DownloadResult(false, null, "La URL del paquete debe ser https.");
        }

        var fileName = System.IO.Path.GetFileName(uri.LocalPath);
        if (!SafeFileName().IsMatch(fileName))
        {
            return new DownloadResult(false, null, "El nombre del paquete no es válido.");
        }

        Directory.CreateDirectory(stagingDirectory);
        var target = System.IO.Path.Combine(stagingDirectory, fileName);
        CleanStaging(stagingDirectory, keep: target);

        if (File.Exists(target))
        {
            if (string.Equals(ComputeSha256(target), package.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report(1);
                return new DownloadResult(true, target);
            }

            File.Delete(target);
        }

        var partial = target + ".partial";
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stall.CancelAfter(_stallTimeout);
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new DownloadResult(false, null, $"HTTP {(int)response.StatusCode} al descargar el paquete.");
            }

            var total = response.Content.Headers.ContentLength ?? (package.SizeBytes > 0 ? package.SizeBytes : null);
            await using (var source = await response.Content.ReadAsStreamAsync(stall.Token))
            await using (var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, stall.Token)) > 0)
                {
                    stall.CancelAfter(_stallTimeout);
                    await destination.WriteAsync(buffer.AsMemory(0, read), stall.Token);
                    received += read;
                    if (total is > 0)
                    {
                        progress?.Report(Math.Min(1d, (double)received / total.Value));
                    }
                }
            }

            File.Move(partial, target, overwrite: true);
            progress?.Report(1);
            return new DownloadResult(true, target);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            TryDelete(partial);
            return new DownloadResult(false, null, ex.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our stall budget or the HttpClient timeout, not the operator.
            TryDelete(partial);
            return new DownloadResult(false, null,
                "La conexión se detuvo o tardó demasiado. Compruebe la red y reintente.", TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            throw;
        }
    }

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void CleanStaging(string directory, string keep)
    {
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase) &&
                OwnedFileName().IsMatch(System.IO.Path.GetFileName(file)))
            {
                TryDelete(file);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a locked leftover is cleaned on the next run.
        }
    }

    [GeneratedRegex(@"^Commerce\.Pos\.Windows-[A-Za-z0-9._-]+\.msix(\.partial)?$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnedFileName();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\.msix$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeFileName();
}
