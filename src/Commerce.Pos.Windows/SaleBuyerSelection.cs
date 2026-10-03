using Commerce.BranchNode;

namespace Commerce.Pos.Windows;

/// <summary>
/// The buyer of the open sale as the sale screen shows it. The <see cref="SaleCart"/> OWNS the buyer
/// (<see cref="SaleCart.CustomerId"/>); this presenter only reflects it into the customer picker, so the picker, the
/// prices and the "Lista: ..." label cannot disagree: clearing the sale, a refused change or a customer that vanished
/// from the replica all end with the picker on the buyer the cart is actually pricing for. No WPF, no I/O.
/// </summary>
public sealed class SaleBuyerSelection
{
    private readonly SaleCart _cart;
    private IReadOnlyList<SaleCustomerPickerItem> _items = SaleCustomerPicker.BuildItems([]);

    public SaleBuyerSelection(SaleCart cart)
    {
        _cart = cart;
        _cart.PropertyChanged += (_, _) => Changed?.Invoke();
    }

    /// <summary>Raised whenever the buyer, the picker items or the message may have changed (UI thread).</summary>
    public event Action? Changed;

    /// <summary>The rows of the picker: walk-in first, then the synced customers.</summary>
    public IReadOnlyList<SaleCustomerPickerItem> Items => _items;

    /// <summary>The picker row of the cart's buyer (0 = walk-in); always an index into <see cref="Items"/>.</summary>
    public int SelectedIndex
    {
        get
        {
            for (var i = 0; i < _items.Count; i++)
            {
                if (_items[i].CustomerId == _cart.CustomerId)
                {
                    return i;
                }
            }

            return 0;
        }
    }

    /// <summary>What the operator should read about the last buyer change (refusal, vanished customer), or null.</summary>
    public string? Message { get; private set; }

    /// <summary>The operator picked a buyer: the cart re-prices (or refuses) and the picker follows the cart.</summary>
    public async Task ChooseAsync(Guid? customerId)
    {
        if (customerId != _cart.CustomerId)
        {
            var result = await _cart.SetCustomerAsync(customerId);
            Message = result.Succeeded ? null : result.Message;
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The customers replica changed: rebuild the rows. A selected customer that is gone falls back to walk-in in the cart
    /// and the picker, with a message; if walk-in cannot price the open sale the customer stays (visible in the picker)
    /// so the two never disagree, and the message says why.
    /// </summary>
    public async Task RefreshAsync(IReadOnlyList<CustomerReplica> customers)
    {
        var previousLabel = _items.FirstOrDefault(i => i.CustomerId == _cart.CustomerId)?.Label;
        var items = SaleCustomerPicker.BuildItems(customers);
        _items = items;

        if (_cart.CustomerId is { } selected && items.All(i => i.CustomerId != selected))
        {
            var name = previousLabel ?? "seleccionado";
            var result = await _cart.SetCustomerAsync(null);
            if (result.Succeeded)
            {
                Message = $"El cliente {name} ya no está disponible: la venta pasó a {SaleCustomerPicker.WalkInLabel}.";
            }
            else
            {
                // Keep the buyer of the sale visible: the cart is still pricing for it.
                _items = [.. items, new SaleCustomerPickerItem(selected, $"{name} (ya no disponible)")];
                Message = $"El cliente {name} ya no está disponible, pero la venta no puede pasar a {SaleCustomerPicker.WalkInLabel}. {result.Message}";
            }
        }

        Changed?.Invoke();
    }
}
