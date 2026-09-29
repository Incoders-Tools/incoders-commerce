using System.Collections.ObjectModel;
using System.ComponentModel;
using Commerce.Application.Pricing;
using Commerce.BranchNode;

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

    public decimal Total => Lines.Sum(l => l.LineTotal);

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
        RaiseChanged();
        return true;
    }

    public void Clear()
    {
        Lines.Clear();
        RaiseChanged();
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

        var line = new ScannedSaleLineViewModel(
            presentationId, code, productName, presentationName, quantity, resolved.UnitNetPrice, resolved.LineTotal);

        // Re-locate: an await separates the lookup from the write.
        existingIndex = IndexOf(presentationId);
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Total)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEmpty)));
    }
}
