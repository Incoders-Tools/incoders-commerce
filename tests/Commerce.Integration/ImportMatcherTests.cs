using Commerce.Application.Pricing.Import;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine Phase 9 tasks 9.7/9.8: <see cref="ImportRowMatcher"/> —
/// every parsed row gets a <see cref="ImportMatchStatus"/>; nothing is
/// silently skipped (design.md "Barcode/SKU Row Matching With Unmatched
/// Reporting").
/// </summary>
public sealed class ImportMatcherTests
{
    private static Func<string, (Guid PresentationId, decimal? CurrentPrice)?> LookupFrom(
        Dictionary<string, (Guid, decimal?)> catalog)
        => code => catalog.TryGetValue(code, out var value) ? value : null;

    [Fact]
    public void Match_UnknownCode_IsUnknownCode()
    {
        var rows = new[] { new ParsedImportRow(2, "GHOST-CODE", "10.00") };

        var results = ImportRowMatcher.Match(rows, LookupFrom([]));

        Assert.Equal(ImportMatchStatus.UnknownCode, results.Single().MatchStatus);
    }

    [Fact]
    public void Match_KnownCodeWithIdenticalPrice_IsNoChange()
    {
        var presentationId = Guid.NewGuid();
        var rows = new[] { new ParsedImportRow(2, "ABC-123", "19.99") };
        var catalog = new Dictionary<string, (Guid, decimal?)> { ["ABC-123"] = (presentationId, 19.99m) };

        var results = ImportRowMatcher.Match(rows, LookupFrom(catalog));

        Assert.Equal(ImportMatchStatus.NoChange, results.Single().MatchStatus);
    }

    [Fact]
    public void Match_KnownCodeWithDifferentPrice_IsMatched()
    {
        var presentationId = Guid.NewGuid();
        var rows = new[] { new ParsedImportRow(2, "ABC-123", "24.99") };
        var catalog = new Dictionary<string, (Guid, decimal?)> { ["ABC-123"] = (presentationId, 19.99m) };

        var results = ImportRowMatcher.Match(rows, LookupFrom(catalog));

        var row = results.Single();
        Assert.Equal(ImportMatchStatus.Matched, row.MatchStatus);
        Assert.Equal(presentationId, row.PresentationId);
        Assert.Equal(24.99m, row.ProposedPrice);
    }

    [Fact]
    public void Match_KnownCodeWithNoExistingPrice_IsMatched()
    {
        var presentationId = Guid.NewGuid();
        var rows = new[] { new ParsedImportRow(2, "ABC-123", "24.99") };
        var catalog = new Dictionary<string, (Guid, decimal?)> { ["ABC-123"] = (presentationId, null) };

        var results = ImportRowMatcher.Match(rows, LookupFrom(catalog));

        Assert.Equal(ImportMatchStatus.Matched, results.Single().MatchStatus);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("-5.00")]
    [InlineData("0")]
    [InlineData("100000000")]
    public void Match_InvalidOrAbsurdPrice_IsInvalidPrice(string rawPrice)
    {
        var presentationId = Guid.NewGuid();
        var rows = new[] { new ParsedImportRow(2, "ABC-123", rawPrice) };
        var catalog = new Dictionary<string, (Guid, decimal?)> { ["ABC-123"] = (presentationId, null) };

        var results = ImportRowMatcher.Match(rows, LookupFrom(catalog));

        Assert.Equal(ImportMatchStatus.InvalidPrice, results.Single().MatchStatus);
    }

    /// <summary>The same code appearing twice in one file — BOTH rows are flagged, neither is guessed at.</summary>
    [Fact]
    public void Match_SameCodeTwiceInFile_BothRowsAreDuplicateInFile()
    {
        var presentationId = Guid.NewGuid();
        var rows = new[]
        {
            new ParsedImportRow(2, "ABC-123", "19.99"),
            new ParsedImportRow(5, "ABC-123", "20.00"),
        };
        var catalog = new Dictionary<string, (Guid, decimal?)> { ["ABC-123"] = (presentationId, null) };

        var results = ImportRowMatcher.Match(rows, LookupFrom(catalog));

        Assert.All(results, r => Assert.Equal(ImportMatchStatus.DuplicateInFile, r.MatchStatus));
    }

    [Fact]
    public void Match_MissingCode_IsUnknownCode()
    {
        var rows = new[] { new ParsedImportRow(2, RawCode: null, "19.99") };

        var results = ImportRowMatcher.Match(rows, LookupFrom([]));

        Assert.Equal(ImportMatchStatus.UnknownCode, results.Single().MatchStatus);
    }

    /// <summary>Every input row produces exactly one output row — nothing is ever silently skipped.</summary>
    [Fact]
    public void Match_EveryRowIsAccountedFor()
    {
        var rows = new[]
        {
            new ParsedImportRow(2, "A", "1.00"),
            new ParsedImportRow(3, "B", "bad"),
            new ParsedImportRow(4, null, "1.00"),
        };

        var results = ImportRowMatcher.Match(rows, LookupFrom([]));

        Assert.Equal(3, results.Count);
    }
}
