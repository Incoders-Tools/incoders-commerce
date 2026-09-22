using System.IO;
using System.Text.Json;

namespace Commerce.Pos.Windows;

/// <summary>Validated display branding for one POS installation. It never changes the executable identity.</summary>
public sealed record ApplicationBranding(string ApplicationName)
{
    public const string DefaultApplicationName = "Vaca Verde";
    public string MainWindowTitle => $"{ApplicationName} POS";
    public string UsersWindowTitle => $"{ApplicationName} — Manage staff";
    public string CustomersWindowTitle => $"{ApplicationName} — Manage customers";

    public static ApplicationBranding Resolve(string? fileValue, string? environmentValue)
    {
        // The highest precedence source is selected first. An invalid selected
        // value fails safely to the known default; it never falls through.
        var selected = environmentValue ?? fileValue ?? DefaultApplicationName;
        var normalized = selected.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > 80 || normalized.Any(char.IsControl)
            ? new ApplicationBranding(DefaultApplicationName)
            : new ApplicationBranding(normalized);
    }

    public static ApplicationBranding Load(string dataDirectory)
    {
        string? fileValue = null;
        var path = Path.Combine(dataDirectory, "branding.json");
        if (File.Exists(path))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.TryGetProperty("Commerce", out var commerce) &&
                    commerce.ValueKind == JsonValueKind.Object &&
                    commerce.TryGetProperty("ApplicationName", out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    fileValue = value.GetString();
                }
            }
            catch (JsonException)
            {
                fileValue = string.Empty;
            }
            catch (IOException)
            {
                fileValue = string.Empty;
            }
            catch (UnauthorizedAccessException)
            {
                fileValue = string.Empty;
            }
        }

        return Resolve(fileValue, Environment.GetEnvironmentVariable("Commerce__ApplicationName"));
    }
}
