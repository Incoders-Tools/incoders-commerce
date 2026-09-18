using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace Commerce.Application.Pricing.Import;

/// <summary>
/// A supplier's saved column mapping (commerce-pricing-engine design.md
/// "Per-supplier column mapping"): sheet name, header row, and price/code
/// COLUMN LETTERS — not column names — configured once by an admin and
/// reused for every later import from that supplier.
/// </summary>
public sealed record SupplierPriceMappingSpec(string SheetName, int HeaderRow, string CodeColumn, string PriceColumn);

/// <summary>
/// Every guard runs BEFORE any cell is read (design.md "Import state
/// machine and untrusted-file handling"). A file that fails ANY guard never
/// creates a <c>price_import_batches</c> row — there is no `Uploaded` state
/// to leave a half-processed batch in.
/// </summary>
public abstract record ImportGuardResult
{
    public sealed record Accepted : ImportGuardResult;

    public sealed record Rejected(string Reason) : ImportGuardResult;
}

public static class ImportGuards
{
    /// <summary>Design.md "Import state machine": untrusted upload size ceiling.</summary>
    public const long MaxFileSizeBytes = 5 * 1024 * 1024;

    /// <summary>Design.md: used-range row ceiling, checked before any cell is materialized.</summary>
    public const int MaxDataRows = 5000;

    // The ZIP local-file-header signature ("PK\x03\x04"). `.xlsx` is a ZIP
    // container; this alone does NOT prove it is a valid OpenXML
    // spreadsheet (an arbitrary `.zip` renamed `.xlsx` also starts this
    // way) — it only lets a non-ZIP file fail fast, before ever touching
    // the OpenXML package reader.
    private static readonly byte[] ZipMagicBytes = [0x50, 0x4B, 0x03, 0x04];

    /// <summary>
    /// Runs every untrusted-file guard in order, returning at the FIRST
    /// failure: magic bytes -&gt; size -&gt; valid OpenXML package -&gt; the
    /// mapping's named sheet exists -&gt; used-range row count. No cell value
    /// is ever read here.
    /// </summary>
    public static ImportGuardResult ValidateFile(byte[] fileContent, SupplierPriceMappingSpec mapping)
    {
        if (fileContent.Length < ZipMagicBytes.Length || !fileContent.AsSpan(0, ZipMagicBytes.Length).SequenceEqual(ZipMagicBytes))
        {
            return new ImportGuardResult.Rejected("not-a-valid-xlsx-file");
        }

        if (fileContent.Length > MaxFileSizeBytes)
        {
            return new ImportGuardResult.Rejected("file-too-large");
        }

        using var stream = new MemoryStream(fileContent, writable: false);
        SpreadsheetDocument document;
        try
        {
            document = SpreadsheetDocument.Open(stream, isEditable: false);
        }
        catch (Exception)
        {
            // Covers the ".zip renamed .xlsx" case: a ZIP with no OpenXML
            // spreadsheet parts inside fails to open as a SpreadsheetDocument.
            return new ImportGuardResult.Rejected("not-a-valid-openxml-package");
        }

        using (document)
        {
            var workbookPart = document.WorkbookPart;
            var sheetElement = workbookPart?.Workbook.Sheets?.Elements<Sheet>()
                .FirstOrDefault(s => string.Equals(s.Name?.Value, mapping.SheetName, StringComparison.Ordinal));
            if (workbookPart is null || sheetElement?.Id?.Value is null
                || workbookPart.GetPartById(sheetElement.Id.Value) is not WorksheetPart worksheetPart)
            {
                return new ImportGuardResult.Rejected("sheet-not-found");
            }

            // Allow up to `HeaderRow` extra rows for the header itself (and
            // any blank rows above it) on top of the data-row ceiling.
            var rowCeiling = MaxDataRows + mapping.HeaderRow;
            if (CountRows(worksheetPart, stopAfter: rowCeiling) > rowCeiling)
            {
                return new ImportGuardResult.Rejected("too-many-rows");
            }
        }

        return new ImportGuardResult.Accepted();
    }

    /// <summary>
    /// Streams &lt;row&gt; elements via <see cref="OpenXmlReader"/> — genuinely
    /// forward-only over the part's XML, never materializing the sheet's
    /// cell grid — so a zip-bomb-shaped sheet is refused before expansion
    /// into memory (design.md "Executable-file classification"). Bails out
    /// the moment <paramref name="stopAfter"/> is exceeded, so a hostile
    /// sheet with millions of rows is never fully counted.
    /// </summary>
    private static int CountRows(WorksheetPart worksheetPart, int stopAfter)
    {
        using var reader = OpenXmlReader.Create(worksheetPart);
        var count = 0;
        while (reader.Read())
        {
            if (reader.ElementType == typeof(Row))
            {
                count++;
                if (count > stopAfter)
                {
                    return count;
                }
            }
        }

        return count;
    }
}
