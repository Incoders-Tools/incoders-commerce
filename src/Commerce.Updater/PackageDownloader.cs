using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Commerce.Updater;

public sealed record DownloadResult(bool Ok, string? Path, string? Detail = null);

/// <summary>
/// Downloads a release package into a controlled staging directory. Safe to
/// re-run: stale partial downloads and other staged packages are removed first,
/// an already staged file whose hash matches the manifest is reused, and a
/// half-written file never carries the final name (download to <c>.partial</c>,
/// then move).
/// </summary>
public sealed partial class PackageDownloader(HttpClient http)
{
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
        try
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new DownloadResult(false, null, $"HTTP {(int)response.StatusCode} al descargar el paquete.");
            }

            var total = response.Content.Headers.ContentLength ?? (package.SizeBytes > 0 ? package.SizeBytes : null);
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
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
            if (!string.Equals(file, keep, StringComparison.OrdinalIgnoreCase))
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

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*\.msix$", RegexOptions.IgnoreCase)]
    private static partial Regex SafeFileName();
}
