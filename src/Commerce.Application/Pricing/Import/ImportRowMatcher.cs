using System.Globalization;

namespace Commerce.Application.Pricing.Import;

/// <summary>
/// Every row lands with exactly one of these — nothing is ever silently
/// skipped (design.md "Import state machine and untrusted-file handling").
/// </summary>
public enum ImportMatchStatus
{
    Matched,
    NoChange,
    UnknownCode,
    InvalidPrice,
    DuplicateInFile,
}

/// <summary>One row after matching against the catalog — the shape a `price_import_rows` insert and the review table both read.</summary>
public sealed record MatchedImportRow(
    int RowNumber,
    string? RawCode,
    string? RawPrice,
    Guid? PresentationId,
    decimal? CurrentPrice,
    decimal? ProposedPrice,
    ImportMatchStatus MatchStatus);

/// <summary>
/// Matches parsed rows to the catalog by identification code ONLY (decision
/// (b), locked: "no fuzzy/name matching"). A row whose code matches no
/// Presentation is reported as <see cref="ImportMatchStatus.UnknownCode"/>,
/// never guessed at.
/// </summary>
public static class ImportRowMatcher
{
    /// <summary>Design.md: a price must be <c>&gt; 0</c> and <c>&lt; 100_000_000</c>.</summary>
    public const decimal MinPrice = 0m;
    public const decimal MaxPrice = 100_000_000m;

    public static IReadOnlyList<MatchedImportRow> Match(
        IReadOnlyList<ParsedImportRow> rows,
        Func<string, (Guid PresentationId, decimal? CurrentPrice)?> lookupByCode)
    {
        // Every occurrence of a code seen more than once is DuplicateInFile
        // (both/all of them, not just the second) — the file itself is
        // ambiguous about which value is correct, so neither is trusted.
        var codeOccurrences = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.RawCode))
            .GroupBy(r => r.RawCode!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var results = new List<MatchedImportRow>(rows.Count);
        foreach (var row in rows)
        {
            results.Add(MatchOne(row, codeOccurrences, lookupByCode));
        }

        return results;
    }

    private static MatchedImportRow MatchOne(
        ParsedImportRow row,
        IReadOnlyDictionary<string, int> codeOccurrences,
        Func<string, (Guid PresentationId, decimal? CurrentPrice)?> lookupByCode)
    {
        if (string.IsNullOrWhiteSpace(row.RawCode))
        {
            return Unmatched(row, ImportMatchStatus.UnknownCode);
        }

        if (codeOccurrences[row.RawCode] > 1)
        {
            return Unmatched(row, ImportMatchStatus.DuplicateInFile);
        }

        var match = lookupByCode(row.RawCode);
        if (match is null)
        {
            return Unmatched(row, ImportMatchStatus.UnknownCode);
        }

        var (presentationId, currentPrice) = match.Value;

        if (!decimal.TryParse(row.RawPrice, NumberStyles.Number, CultureInfo.InvariantCulture, out var proposedPrice)
            || proposedPrice <= MinPrice || proposedPrice >= MaxPrice)
        {
            return new MatchedImportRow(row.RowNumber, row.RawCode, row.RawPrice, presentationId, currentPrice, null, ImportMatchStatus.InvalidPrice);
        }

        if (currentPrice.HasValue && currentPrice.Value == proposedPrice)
        {
            return new MatchedImportRow(row.RowNumber, row.RawCode, row.RawPrice, presentationId, currentPrice, proposedPrice, ImportMatchStatus.NoChange);
        }

        return new MatchedImportRow(row.RowNumber, row.RawCode, row.RawPrice, presentationId, currentPrice, proposedPrice, ImportMatchStatus.Matched);
    }

    private static MatchedImportRow Unmatched(ParsedImportRow row, ImportMatchStatus status)
        => new(row.RowNumber, row.RawCode, row.RawPrice, null, null, null, status);
}
