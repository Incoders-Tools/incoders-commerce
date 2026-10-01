using System.Reflection;
using System.Text.RegularExpressions;
using Commerce.Pos.Windows;

namespace Commerce.Integration;

/// <summary>
/// human-document-numbers: nothing an operator reads in the POS may carry a GUID (organization,
/// installation, branch, sale or operation ids stay in the log file). Covers the UI-free text
/// builders directly and guards the sources against a GUID being interpolated into screen text.
/// </summary>
public sealed partial class PosOperatorTextTests
{
    [GeneratedRegex("[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")]
    private static partial Regex GuidShape();

    private static readonly DevicePairing Pairing = new(
        Guid.NewGuid(), Guid.NewGuid(), "Ruta 51", "admin@vacaverde.example", "token", BranchCode: 1, RegisterNumber: 1);

    [Fact]
    public void EveryPosMessage_IsFreeOfGuidShapedText()
    {
        var constants = typeof(PosMessages).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

        Assert.All(constants, text => Assert.DoesNotMatch(GuidShape(), text));
    }

    [Fact]
    public void TheSyncSummaries_AreSpanishAndNameNoOperation()
    {
        Assert.Equal("No hay nada pendiente de sincronizar.", PosMessages.SyncNothingPending);
        Assert.Equal("Se sincronizó 1 operación.", PosMessages.SyncSucceeded(1));
        Assert.Equal("Se sincronizaron 3 operaciones.", PosMessages.SyncSucceeded(3));
        Assert.Equal(
            "Se sincronizaron 2 operaciones; 1 no se pudo enviar y se reintentará automáticamente.",
            PosMessages.SyncPartiallyFailed(2, 1));
        Assert.Equal(
            "No se pudo enviar ninguna operación; se reintentará automáticamente.",
            PosMessages.SyncPartiallyFailed(0, 4));
    }

    [Fact]
    public void TheIdentitySummary_ShowsTheTerminalAndTheOperators_WithoutAnyGuid()
    {
        var summary = IdentitySummary.Format(Pairing, "cajera@vacaverde.example");

        Assert.Equal(
            "Terminal: Sucursal 01 · Ruta 51 · Caja 1\n" +
            "Operador de emparejamiento: admin@vacaverde.example\n" +
            "Operador actual: cajera@vacaverde.example",
            summary);
        Assert.DoesNotMatch(GuidShape(), summary);
    }

    [Fact]
    public void TheIdentitySummary_WithoutAnActiveOperator_SaysSo()
    {
        Assert.EndsWith("Operador actual: sin operador activo", IdentitySummary.Format(Pairing, null));
    }

    [Fact]
    public void TheTerminalLabelTooltip_ExplainsEachPartItShows()
    {
        Assert.Equal(
            "Sucursal 01 = código de sucursal · Caja 1 = número de esta caja",
            TerminalLabel.Composition(Pairing));
        Assert.Equal("Sucursal 07 = código de sucursal", TerminalLabel.Composition(Pairing with { BranchCode = 7, RegisterNumber = null }));
        Assert.Equal("Caja 3 = número de esta caja", TerminalLabel.Composition(Pairing with { BranchCode = null, RegisterNumber = 3 }));
        Assert.Null(TerminalLabel.Composition(Pairing with { BranchCode = null, RegisterNumber = null }));
    }

    /// <summary>
    /// Source guard: an interpolated string that embeds an `...Id` value may only be a log line or a
    /// request path. Anything else is text that could reach the screen.
    /// </summary>
    [Fact]
    public void ThePosSources_NeverInterpolateAnIdIntoText_ExceptLogLinesAndRequestPaths()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Commerce.sln"))) root = root.Parent;
        var offenders = new List<string>();
        var idInterpolation = new Regex(@"\$""[^""]*\{[^}]*(Id|Guid)\b[^}]*\}", RegexOptions.CultureInvariant);

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "src", "Commerce.Pos.Windows"), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!idInterpolation.IsMatch(line)) continue;
                var isLog = line.Contains("PosLog.") || line.Contains("logger", StringComparison.OrdinalIgnoreCase)
                            || (i > 0 && lines[i - 1].Contains("PosLog."));
                var isPath = Regex.IsMatch(line, @"\$""/[^""]*""");
                if (!isLog && !isPath) offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "Ids interpolated into possibly operator-facing text:\n" + string.Join("\n", offenders));
    }
}
