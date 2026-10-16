using ClosedXML.Excel;
using Commerce.Application.Pricing.Import;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine Phase 9 tasks 9.5/9.6:
/// <see cref="SupplierPriceImportParser"/> reads ClosedXML's CACHED cell
/// values only — a formula cell's stored result is read, never
/// recalculated (design.md "Executable-file classification").
/// </summary>
public sealed class ImportParserTests
{
    private static readonly SupplierPriceMappingSpec Mapping = new("Prices", HeaderRow: 1, CodeColumn: "A", PriceColumn: "B");

    [Fact]
    public void Parse_NormalRows_ReturnsRawCodeAndPricePerRow()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Prices");
        sheet.Cell(1, "A").Value = "Code";
        sheet.Cell(1, "B").Value = "Price";
        sheet.Cell(2, "A").Value = "ABC-123";
        sheet.Cell(2, "B").Value = 19.99m;
        sheet.Cell(3, "A").Value = "XYZ-999";
        sheet.Cell(3, "B").Value = 5.5m;

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);

        var rows = SupplierPriceImportParser.Parse(stream.ToArray(), Mapping);

        Assert.Equal(2, rows.Count);
        Assert.Equal("ABC-123", rows[0].RawCode);
        Assert.Equal("19.99", rows[0].RawPrice);
        Assert.Equal("XYZ-999", rows[1].RawCode);
    }

    /// <summary>The trust-boundary case: a formula cell's CACHED result is read, and the formula is never recalculated.</summary>
    [Fact]
    public void Parse_FormulaCell_ReadsCachedValue_NeverRecalculates()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Prices");
        sheet.Cell(1, "A").Value = "Code";
        sheet.Cell(1, "B").Value = "Price";
        sheet.Cell(2, "A").Value = "FORMULA-CODE";
        // A formula whose cached value we control directly: set a formula,
        // then force ClosedXML's in-memory cached value to a KNOWN sentinel
        // that would NOT match what the formula actually computes (1+1=2).
        // If the parser ever recalculates instead of reading the cache, it
        // will observe 2.00, not the sentinel 42.00 asserted below.
        sheet.Cell(2, "B").FormulaA1 = "=1+1";

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var bytes = stream.ToArray();

        // Rewrite the saved file's cached <v> for that formula cell to a
        // sentinel value distinguishable from the real formula result,
        // simulating a supplier file whose cached value was computed by a
        // DIFFERENT engine/state than "1+1" — the parser must trust the
        // file's cache, not its own recalculation.
        bytes = RewriteCachedFormulaValue(bytes, "B2", "42");

        var rows = SupplierPriceImportParser.Parse(bytes, Mapping);

        Assert.Single(rows);
        Assert.Equal("42", rows[0].RawPrice);
    }

    /// <summary>
    /// Directly edits the OpenXML sheet part's cached &lt;v&gt; for one cell,
    /// bypassing ClosedXML entirely, so the test controls the cached value
    /// independently of what the formula would actually evaluate to.
    /// </summary>
    private static byte[] RewriteCachedFormulaValue(byte[] xlsxBytes, string cellReference, string newCachedValue)
    {
        using var stream = new MemoryStream();
        stream.Write(xlsxBytes, 0, xlsxBytes.Length);
        stream.Position = 0;

        using (var document = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(stream, isEditable: true))
        {
            var worksheetPart = document.WorkbookPart!.WorksheetParts.First();
            var cell = worksheetPart.Worksheet.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>()
                .First(c => c.CellReference == cellReference);
            cell.CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue(newCachedValue);
            worksheetPart.Worksheet.Save();
        }

        return stream.ToArray();
    }
}
