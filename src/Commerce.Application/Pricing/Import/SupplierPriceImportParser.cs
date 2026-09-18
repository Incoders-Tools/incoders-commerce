using ClosedXML.Excel;

namespace Commerce.Application.Pricing.Import;

/// <summary>One parsed row before matching — raw strings only, no interpretation yet.</summary>
public sealed record ParsedImportRow(int RowNumber, string? RawCode, string? RawPrice);

/// <summary>
/// Reads a validated (<see cref="ImportGuards"/> already ran) supplier
/// workbook using ClosedXML. Reads CACHED cell values only — ClosedXML never
/// recalculates a formula unless <c>RecalculateAllFormulas()</c> is called
/// explicitly, which this parser never does (design.md "Executable-file
/// classification": "cached values are read with no formula recalculation").
/// </summary>
public static class SupplierPriceImportParser
{
    public static IReadOnlyList<ParsedImportRow> Parse(byte[] fileContent, SupplierPriceMappingSpec mapping)
    {
        using var stream = new MemoryStream(fileContent, writable: false);
        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.First(w => string.Equals(w.Name, mapping.SheetName, StringComparison.Ordinal));

        var lastRowUsed = worksheet.LastRowUsed()?.RowNumber() ?? mapping.HeaderRow;
        var rows = new List<ParsedImportRow>();

        for (var rowNumber = mapping.HeaderRow + 1; rowNumber <= lastRowUsed; rowNumber++)
        {
            var codeCell = worksheet.Cell(rowNumber, mapping.CodeColumn);
            var priceCell = worksheet.Cell(rowNumber, mapping.PriceColumn);

            if (codeCell.IsEmpty() && priceCell.IsEmpty())
            {
                continue; // a trailing blank row inside the used range
            }

            var rawCode = ReadCachedString(codeCell);
            var rawPrice = ReadCachedString(priceCell);
            rows.Add(new ParsedImportRow(rowNumber, rawCode, rawPrice));
        }

        return rows;
    }

    /// <summary>
    /// <see cref="IXLCell.Value"/> returns whatever value is already cached
    /// on the cell (the literal, or the last-computed-and-saved formula
    /// result) — ClosedXML only recalculates when explicitly asked to, so
    /// this is a pure cache read.
    /// </summary>
    private static string? ReadCachedString(IXLCell cell)
    {
        if (cell.IsEmpty())
        {
            return null;
        }

        var value = cell.Value;
        // InvariantCulture: this test host's locale renders a numeric
        // cell's default ToString() with a ',' decimal separator (the same
        // Argentine-market concern PostgresPriceListStore.AppendEntryAsync
        // already guards against) — without this, "19.99" in the sheet
        // would be parsed downstream as "19,99" and fail decimal.TryParse.
        var text = value.IsNumber
            ? value.GetNumber().ToString(System.Globalization.CultureInfo.InvariantCulture)
            : value.ToString(System.Globalization.CultureInfo.InvariantCulture)?.Trim();

        return string.IsNullOrEmpty(text) ? null : text;
    }
}
