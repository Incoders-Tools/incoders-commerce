namespace Commerce.Application.Pricing;

/// <summary>One price of a batch publish as it arrives: either field may be missing, which is a validation error.</summary>
public sealed record PriceEntryBatchItem(Guid? PresentationId, decimal? UnitPrice);

/// <summary>
/// price-editing-and-desktop-polish T4: the shape rules of a batch of base prices for one list, checked before anything
/// is read or written. Errors are keyed the way the web maps them back to its rows: <c>entries</c> for the batch as a
/// whole and <c>entries[N].presentationId</c> / <c>entries[N].unitPrice</c> for the entry at index N of the batch sent.
/// Whether each presentation exists in the caller's catalog is checked afterwards, against the database.
/// </summary>
public static class PriceEntryBatchRules
{
    public const int MaxEntries = 2000;

    /// <summary>The largest price `price_list_entries.unit_price` (numeric(12,2)) can hold.</summary>
    public const decimal MaxUnitPrice = 9_999_999_999.99m;

    public static string PresentationKey(int index) => $"entries[{index}].presentationId";

    public static string UnitPriceKey(int index) => $"entries[{index}].unitPrice";

    /// <summary>Every shape problem of the batch; empty when it can be checked against the catalog and the floor.</summary>
    public static Dictionary<string, string[]> Validate(IReadOnlyList<PriceEntryBatchItem?>? entries)
    {
        var errors = new Dictionary<string, string[]>();
        if (entries is null || entries.Count == 0)
        {
            errors["entries"] = ["At least one price is required."];
            return errors;
        }

        if (entries.Count > MaxEntries)
        {
            errors["entries"] = [$"At most {MaxEntries} prices can be published at once."];
            return errors;
        }

        var seen = new HashSet<Guid>();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            if (entry?.PresentationId is not { } presentationId || presentationId == Guid.Empty)
            {
                errors[PresentationKey(index)] = ["presentationId is required."];
            }
            else if (!seen.Add(presentationId))
            {
                errors[PresentationKey(index)] = ["This presentation is already in the batch."];
            }

            if (UnitPriceError(entry?.UnitPrice) is { } priceError)
            {
                errors[UnitPriceKey(index)] = [priceError];
            }
        }

        return errors;
    }

    private static string? UnitPriceError(decimal? unitPrice) => unitPrice switch
    {
        null => "unitPrice is required.",
        <= 0 => "unitPrice must be greater than zero.",
        > MaxUnitPrice => "unitPrice is too large.",
        { } price when decimal.Round(price, 2) != price => "unitPrice can have at most 2 decimals.",
        _ => null,
    };
}
