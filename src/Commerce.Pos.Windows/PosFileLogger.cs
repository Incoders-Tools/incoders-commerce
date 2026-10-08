using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Commerce.Pos.Windows;

public enum PosLogLevel
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// Minimal, dependency-free technical log for support: one file per day
/// (<c>pos-YYYYMMDD.log</c>) in a single directory, newest 14 files kept.
/// Thread-safe, redacts credentials defensively, and NEVER throws: a logging
/// failure must not take the till down.
/// </summary>
public sealed class PosFileLogger
{
    private const int DefaultRetainedFiles = 14;

    private static readonly Regex[] Redactions =
    [
        new(@"Bearer\s+[^\s""',;]+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"""(?:password|newPassword|pin|token|deviceToken|secret)""\s*:\s*""[^""]*""", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:password|newPassword|pin|token|deviceToken|secret)\s*=\s*[^\s&;,""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private readonly string _directory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly int _retainedFiles;
    private readonly object _gate = new();
    private string? _prunedForFile;

    public PosFileLogger(string directory, Func<DateTimeOffset>? clock = null, int retainedFiles = DefaultRetainedFiles)
    {
        _directory = directory;
        _clock = clock ?? (() => DateTimeOffset.Now);
        _retainedFiles = retainedFiles;
    }

    public void Log(PosLogLevel level, string category, string message, Exception? exception = null)
    {
        try
        {
            var now = _clock();
            var text = new StringBuilder()
                .Append(now.ToString("yyyy-MM-ddTHH:mm:ss.fffzzz"))
                .Append(' ').Append(level.ToString().ToUpperInvariant())
                .Append(' ').Append(category)
                .Append(' ').Append(Redact(message));
            if (exception is not null)
            {
                text.AppendLine().Append(Redact(exception.ToString()));
            }
            text.AppendLine();

            var file = Path.Combine(_directory, $"pos-{now:yyyyMMdd}.log");
            lock (_gate)
            {
                Directory.CreateDirectory(_directory);
                File.AppendAllText(file, text.ToString(), Encoding.UTF8);
                if (_prunedForFile != file)
                {
                    _prunedForFile = file;
                    Prune();
                }
            }
        }
        catch
        {
            // A failing logger must never crash the app.
        }
    }

    public static string Redact(string text)
    {
        foreach (var pattern in Redactions)
        {
            text = pattern.Replace(text, "[redacted]");
        }

        return text;
    }

    private void Prune()
    {
        var files = Directory.GetFiles(_directory, "pos-????????.log").OrderBy(Path.GetFileName, StringComparer.Ordinal).ToList();
        foreach (var stale in files.Take(Math.Max(0, files.Count - _retainedFiles)))
        {
            try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}

/// <summary>
/// Process-wide access point to the configured <see cref="PosFileLogger"/>.
/// Unconfigured (tests, design time) it discards everything, so clients can log
/// without a constructor dependency. Never throws.
/// </summary>
public static class PosLog
{
    private static PosFileLogger? _logger;

    public static void Configure(PosFileLogger? logger) => Volatile.Write(ref _logger, logger);

    public static void Information(string category, string message) =>
        Volatile.Read(ref _logger)?.Log(PosLogLevel.Information, category, message);

    public static void Warning(string category, string message, Exception? exception = null) =>
        Volatile.Read(ref _logger)?.Log(PosLogLevel.Warning, category, message, exception);

    public static void Error(string category, string message, Exception? exception = null) =>
        Volatile.Read(ref _logger)?.Log(PosLogLevel.Error, category, message, exception);
}
