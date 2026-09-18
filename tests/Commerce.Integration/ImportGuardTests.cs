using ClosedXML.Excel;
using Commerce.Application.Pricing.Import;

namespace Commerce.Integration;

/// <summary>
/// Covers commerce-pricing-engine Phase 9 (Unit 9, highest risk alongside
/// Phase 3): <see cref="ImportGuards"/> — every check that must run BEFORE
/// any cell is read (design.md "Import state machine and untrusted-file
/// handling"). Fixture workbooks are built in-memory with ClosedXML so no
/// binary fixture files are committed to the repo.
/// </summary>
public sealed class ImportGuardTests
{
    private static readonly SupplierPriceMappingSpec Mapping = new("Prices", HeaderRow: 1, CodeColumn: "A", PriceColumn: "B");

    private static byte[] BuildValidWorkbook(int dataRowCount)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Prices");
        sheet.Cell(1, "A").Value = "Code";
        sheet.Cell(1, "B").Value = "Price";
        for (var i = 0; i < dataRowCount; i++)
        {
            sheet.Cell(i + 2, "A").Value = $"CODE-{i}";
            sheet.Cell(i + 2, "B").Value = 10.00m + i;
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    [Fact]
    public void ValidateFile_WellFormedSmallFile_IsAccepted()
    {
        var content = BuildValidWorkbook(dataRowCount: 3);

        var result = ImportGuards.ValidateFile(content, Mapping);

        Assert.IsType<ImportGuardResult.Accepted>(result);
    }

    /// <summary>A `.zip` renamed `.xlsx` — an arbitrary zip with no OpenXML spreadsheet parts inside — is rejected.</summary>
    [Fact]
    public void ValidateFile_ZipRenamedToXlsx_IsRejected()
    {
        using var stream = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("not-a-spreadsheet.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("just some random zipped text, not an OpenXML package");
        }
        var content = stream.ToArray();

        var result = ImportGuards.ValidateFile(content, Mapping);

        Assert.IsType<ImportGuardResult.Rejected>(result);
    }

    [Fact]
    public void ValidateFile_NotAZipAtAll_IsRejectedByMagicBytes()
    {
        var content = System.Text.Encoding.UTF8.GetBytes("this is plainly not an xlsx file at all");

        var result = ImportGuards.ValidateFile(content, Mapping);

        var rejected = Assert.IsType<ImportGuardResult.Rejected>(result);
        Assert.Equal("not-a-valid-xlsx-file", rejected.Reason);
    }

    [Fact]
    public void ValidateFile_OversizedFile_IsRejected()
    {
        var content = BuildValidWorkbook(dataRowCount: 3);
        // Magic bytes must still pass so this genuinely exercises the SIZE
        // guard, not the magic-byte guard: pad after the real zip content.
        var padded = new byte[ImportGuards.MaxFileSizeBytes + 1024];
        content.CopyTo(padded, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(padded.AsSpan(0, 4), 0x04034B50);

        var result = ImportGuards.ValidateFile(padded, Mapping);

        var rejected = Assert.IsType<ImportGuardResult.Rejected>(result);
        Assert.Equal("file-too-large", rejected.Reason);
    }

    [Fact]
    public void ValidateFile_UsedRangeExceeds5000Rows_IsRejectedBeforeExpansion()
    {
        var content = BuildValidWorkbook(dataRowCount: ImportGuards.MaxDataRows + 1);

        var result = ImportGuards.ValidateFile(content, Mapping);

        var rejected = Assert.IsType<ImportGuardResult.Rejected>(result);
        Assert.Equal("too-many-rows", rejected.Reason);
    }

    [Fact]
    public void ValidateFile_SheetNameNotFound_IsRejected()
    {
        var content = BuildValidWorkbook(dataRowCount: 1);
        var wrongMapping = Mapping with { SheetName = "DoesNotExist" };

        var result = ImportGuards.ValidateFile(content, wrongMapping);

        var rejected = Assert.IsType<ImportGuardResult.Rejected>(result);
        Assert.Equal("sheet-not-found", rejected.Reason);
    }
}
