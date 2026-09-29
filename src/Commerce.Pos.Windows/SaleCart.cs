using System.Collections.ObjectModel;
using System.ComponentModel;
using Commerce.Application.Pricing;
using Commerce.BranchNode;
using Commerce.Domain.Discounts;

namespace Commerce.Pos.Windows;

/// <summary>Outcome of a cart mutation; <see cref="Message"/> is operator-facing (Spanish) on failure.</summary>
public sealed record SaleCartResult(bool Succeeded, string? Message = null)
{
    public static SaleCartResult Ok { get; } = new(true);

    public static SaleCartResult Fail(string message) => new(false, message);
}

/// <summary>
/// The pending scan/card-composed sale, extracted from <c>MainWindow</c> so it
/// is testable without WPF. Every price and rounding comes from the shared
/// <see cref="PricingResolutionService"/> re-resolved at the new quantity —
/// the cart never computes a price itself, and a line that cannot be priced is
/// never added or changed (no zero-priced substitute).
///
/// Discounts (pos-scan-sale "Percentage Discounts on Lines and on the Whole
/// Sale"): a line discount applies to that line total, a sale discount to the
/// subtotal after line discounts, each amount rounded once half away from zero.
/// Adding or changing one demands a <see cref="DiscountAuthorization"/>: the
/// cart never verifies a PIN itself, it only refuses to record a discount
/// without proof. Removing one needs none.
/// </summary>
public sealed class SaleCart : INotifyPropertyChanged
{
    private readonly PricingResolutionService _pricing;
    private readonly Func<DateOnly> _today;

    public SaleCart(PricingResolutionService pricing, Func<DateOnly>? today = null)
    {
        _pricing = pricing;
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.UtcNow));
    }

    public ObservableCollection<ScannedSaleLineViewModel> Lines { get; } = new();

    /// <summary>Sum of the undiscounted line totals.</summary>
    public decimal Subtotal => Lines.Sum(l => l.LineTotal);

    /// <summary>Sum of the line totals after line discounts; the base of the sale discount.</summary>
    public decimal NetSubtotal => Lines.Sum(l => l.NetTotal);

    public decimal? SaleDiscountPercent { get; private set; }

    /// <summary>The rounded whole-sale discount over <see cref="NetSubtotal"/>; zero when none.</summary>
    public decimal SaleDiscountAmount => SaleDiscountPercent is { } percent ? DiscountMath.Amount(NetSubtotal, percent) : 0m;

    /// <summary>Line discounts plus the sale discount.</summary>
    public decimal DiscountTotal => Subtotal - NetSubtotal + SaleDiscountAmount;

    /// <summary>The FINAL amount to charge, after every discount.</summary>
    public decimal Total => NetSubtotal - SaleDiscountAmount;

    public bool HasDiscount => SaleDiscountPercent is not null || Lines.Any(l => l.HasDiscount);

    /// <summary>The latest authorization behind the discounts on this sale; null when there are none.</summary>
    public DiscountAuthorization? Authorization { get; private set; }

    /// <summary>The whole-sale discount as it is committed, or null.</summary>
    public SaleDiscount? SaleDiscount => SaleDiscountPercent is { } percent ? new SaleDiscount(percent, SaleDiscountAmount) : null;

    public bool IsEmpty => Lines.Count == 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Adds one unit of the presentation, or bumps the existing line by one.</summary>
    public async Task<SaleCartResult> AddAsync(CatalogPriceReplicaItem item)
    {
        var existing = IndexOf(item.PresentationId);
        var quantity = (existing >= 0 ? Lines[existing].Quantity : 0m) + 1m;
        return await ApplyAsync(existing, item.PresentationId, item.IdentificationCode, item.ProductName, item.PresentationName, quantity);
    }

    public Task<SaleCartResult> IncrementAsync(Guid presentationId) => ChangeByAsync(presentationId, 1m);

    /// <summary>Decrements by one; a line at quantity one is removed.</summary>
    public Task<SaleCartResult> DecrementAsync(Guid presentationId) => ChangeByAsync(presentationId, -1m);

    /// <summary>Sets an absolute quantity; zero or less removes the line.</summary>
    public async Task<SaleCartResult> SetQuantityAsync(Guid presentationId, decimal quantity)
    {
        var index = IndexOf(presentationId);
        if (index < 0)
        {
            return SaleCartResult.Fail("El producto no está en la venta.");
        }

        if (quantity <= 0m)
        {
            Remove(presentationId);
            return SaleCartResult.Ok;
        }

        var line = Lines[index];
        return await ApplyAsync(index, line.PresentationId, line.IdentificationCode, line.ProductName, line.PresentationName, quantity);
    }

    public bool Remove(Guid presentationId)
    {
        var index = IndexOf(presentationId);
        if (index < 0)
        {
            return false;
        }

        Lines.RemoveAt(index);
        NormalizeDiscounts();
        RaiseChanged();
        return true;
    }

    public void Clear()
    {
        Lines.Clear();
        SaleDiscountPercent = null;
        Authorization = null;
        RaiseChanged();
    }

    /// <summary>Applies or changes a percentage discount on one line; needs an authorization.</summary>
    public SaleCartResult SetLineDiscount(Guid presentationId, decimal percent, DiscountAuthorization authorization)
    {
        var index = IndexOf(presentationId);
        if (index < 0)
        {
            return SaleCartResult.Fail("El producto no está en la venta.");
        }

        if (!DiscountMath.IsValidPercent(percent))
        {
            return SaleCartResult.Fail(InvalidPercentMessage);
        }

        var line = Lines[index];
        Lines[index] = line with { LineDiscountPercent = percent, LineDiscountAmount = DiscountMath.Amount(line.LineTotal, percent) };
        Authorization = authorization;
        RaiseChanged();
        return SaleCartResult.Ok;
    }

    /// <summary>Applies or changes the whole-sale percentage discount; needs an authorization.</summary>
    public SaleCartResult SetSaleDiscount(decimal percent, DiscountAuthorization authorization)
    {
        if (IsEmpty)
        {
            return SaleCartResult.Fail("La venta está vacía.");
        }

        if (!DiscountMath.IsValidPercent(percent))
        {
            return SaleCartResult.Fail(InvalidPercentMessage);
        }

        SaleDiscountPercent = percent;
        Authorization = authorization;
        RaiseChanged();
        return SaleCartResult.Ok;
    }

    /// <summary>Removes one line discount; no authorization needed. False when it had none.</summary>
    public bool RemoveLineDiscount(Guid presentationId)
    {
        var index = IndexOf(presentationId);
        if (index < 0 || !Lines[index].HasDiscount)
        {
            return false;
        }

        Lines[index] = Lines[index] with { LineDiscountPercent = null, LineDiscountAmount = null };
        NormalizeDiscounts();
        RaiseChanged();
        return true;
    }

    /// <summary>Removes the whole-sale discount; no authorization needed. False when there was none.</summary>
    public bool RemoveSaleDiscount()
    {
        if (SaleDiscountPercent is null)
        {
            return false;
        }

        SaleDiscountPercent = null;
        NormalizeDiscounts();
        RaiseChanged();
        return true;
    }

    private const string InvalidPercentMessage = "El descuento debe ser mayor que 0 y hasta 100, con hasta 2 decimales.";

    /// <summary>An empty sale keeps no sale discount, and a sale without discounts keeps no authorization marker.</summary>
    private void NormalizeDiscounts()
    {
        if (IsEmpty)
        {
            SaleDiscountPercent = null;
        }

        if (!HasDiscount)
        {
            Authorization = null;
        }
    }

    private async Task<SaleCartResult> ChangeByAsync(Guid presentationId, decimal delta)
    {
        var index = IndexOf(presentationId);
        return index < 0
            ? SaleCartResult.Fail("El producto no está en la venta.")
            : await SetQuantityAsync(presentationId, Lines[index].Quantity + delta);
    }

    private async Task<SaleCartResult> ApplyAsync(
        int existingIndex, Guid presentationId, string? code, string productName, string presentationName, decimal quantity)
    {
        var effectiveOn = _today();
        var outcome = await _pricing.ResolveAsync(presentationId, quantity, discountPercentage: null, effectiveOn, CancellationToken.None);
        if (outcome is not PriceResolutionOutcome.Resolved resolved)
        {
            return SaleCartResult.Fail(
                $"No hay precio vigente para {presentationName} el {effectiveOn:yyyy-MM-dd}. Use venta manual o sincronice.");
        }

        // Re-locate: an await separates the lookup from the write.
        existingIndex = IndexOf(presentationId);

        // A quantity change keeps the line discount percentage (it is not a
        // new or changed discount) and recomputes the amount over the new total.
        var percent = existingIndex >= 0 ? Lines[existingIndex].LineDiscountPercent : null;
        var line = new ScannedSaleLineViewModel(
            presentationId, code, productName, presentationName, quantity, resolved.UnitNetPrice, resolved.LineTotal,
            percent, percent is { } p ? DiscountMath.Amount(resolved.LineTotal, p) : null);

        if (existingIndex >= 0)
        {
            Lines[existingIndex] = line;
        }
        else
        {
            Lines.Add(line);
        }

        RaiseChanged();
        return SaleCartResult.Ok;
    }

    private int IndexOf(Guid presentationId)
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].PresentationId == presentationId)
            {
                return i;
            }
        }

        return -1;
    }

    private void RaiseChanged()
    {
        foreach (var name in new[] { nameof(Total), nameof(Subtotal), nameof(DiscountTotal), nameof(HasDiscount), nameof(IsEmpty) })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
