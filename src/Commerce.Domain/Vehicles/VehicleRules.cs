namespace Commerce.Domain.Vehicles;

/// <summary>
/// The business's vehicles (vehicles.md): a lookup table of the organization that a delivery run picks from. A plate is
/// written any way ("ab 123-cd") and kept in capitals without spaces or dashes ("AB123CD"), unique in the organization.
/// </summary>
public static class VehicleRules
{
    public const int MaxPlateLength = 10;
    public const int MaxBrandLength = 60;
    public const int MaxModelLength = 60;
    public const int MaxNotesLength = 500;
    public const int FirstYear = 1950;

    public static string NormalizePlate(string? plate) =>
        new([.. (plate ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant)]);

    /// <summary>A normalized plate: 5 to 10 letters or digits (old and Mercosur Argentine plates fit).</summary>
    public static bool IsValidPlate(string plate) =>
        plate.Length is >= 5 and <= MaxPlateLength && plate.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9');

    /// <summary>A model year: from 1950 up to next year (a new model is sold the year before).</summary>
    public static bool IsValidYear(int year, DateOnly today) => year >= FirstYear && year <= today.Year + 1;

    /// <summary>How a vehicle reads in a list and on a delivery run: "AB123CD · Ford F-4000".</summary>
    public static string Label(string plate, string? brand, string? model)
    {
        var name = string.Join(' ', new[] { brand, model }.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        return name.Length == 0 ? plate : $"{plate} · {name}";
    }
}
