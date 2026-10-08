using System.Globalization;
using Commerce.Domain.Pricing;
using Commerce.Domain.Tenancy;

namespace Commerce.Domain.Ordering;

/// <summary>
/// Where an order is in its operational life, from confirmation to delivery. Separate from the ADR-003 sync status
/// (<see cref="OrderDeliveryStatus"/>), which only says whether the destination branch applied the order.
/// </summary>
public enum OrderFulfillmentStatus
{
    Confirmed,
    InPreparation,
    ReadyToDispatch,
    OutForDelivery,
    Delivered,
    PartiallyDelivered,
    Cancelled,
}

/// <summary>How a delivered order was settled: on the customer's current account, or paid when it was delivered.</summary>
public enum OrderSettlement
{
    CurrentAccount,
    PaidOnDelivery,
}

/// <summary>
/// The rules of the order life cycle.
/// <para>
/// MANUAL steps (<see cref="CanMoveManually"/>): Confirmed -> InPreparation -> ReadyToDispatch, one step back while
/// preparing (ReadyToDispatch -> InPreparation -> Confirmed), and Cancelled from any of those three.
/// </para>
/// <para>
/// DELIVERY steps happen only through a delivery run: dispatching it takes its orders to OutForDelivery, and settling it
/// on return takes each to Delivered, PartiallyDelivered, or back to ReadyToDispatch when it could not be delivered
/// (<see cref="CanJoinRun"/>, <see cref="AfterReturn"/>). Delivered, PartiallyDelivered and Cancelled are final.
/// </para>
/// </summary>
public static class OrderFulfillmentRules
{
    public const int MaxCancelReasonLength = 200;

    private static readonly HashSet<(OrderFulfillmentStatus, OrderFulfillmentStatus)> ManualSteps =
    [
        (OrderFulfillmentStatus.Confirmed, OrderFulfillmentStatus.InPreparation),
        (OrderFulfillmentStatus.InPreparation, OrderFulfillmentStatus.ReadyToDispatch),
        (OrderFulfillmentStatus.ReadyToDispatch, OrderFulfillmentStatus.InPreparation),
        (OrderFulfillmentStatus.InPreparation, OrderFulfillmentStatus.Confirmed),
        (OrderFulfillmentStatus.Confirmed, OrderFulfillmentStatus.Cancelled),
        (OrderFulfillmentStatus.InPreparation, OrderFulfillmentStatus.Cancelled),
        (OrderFulfillmentStatus.ReadyToDispatch, OrderFulfillmentStatus.Cancelled),
    ];

    public static bool CanMoveManually(OrderFulfillmentStatus from, OrderFulfillmentStatus to) => ManualSteps.Contains((from, to));

    /// <summary>The manual steps open from <paramref name="from"/>, in the order a screen offers them.</summary>
    public static IReadOnlyList<OrderFulfillmentStatus> ManualTargets(OrderFulfillmentStatus from) =>
        Enum.GetValues<OrderFulfillmentStatus>().Where(to => CanMoveManually(from, to)).ToList();

    /// <summary>An order can be put in a (planned) run while it is not yet out, delivered or cancelled.</summary>
    public static bool CanJoinRun(OrderFulfillmentStatus status) =>
        status is OrderFulfillmentStatus.Confirmed or OrderFulfillmentStatus.InPreparation or OrderFulfillmentStatus.ReadyToDispatch;

    public static bool IsFinal(OrderFulfillmentStatus status) =>
        status is OrderFulfillmentStatus.Delivered or OrderFulfillmentStatus.PartiallyDelivered or OrderFulfillmentStatus.Cancelled;

    /// <summary>
    /// The status an order out for delivery takes when the truck returns: nothing delivered -> ReadyToDispatch (it can
    /// go in another run), everything as ordered (or more, a weighted cut can weigh more) -> Delivered, otherwise
    /// PartiallyDelivered.
    /// </summary>
    public static OrderFulfillmentStatus AfterReturn(IReadOnlyList<(decimal Ordered, decimal Delivered)> lines)
    {
        if (lines.Count == 0 || lines.All(line => line.Delivered <= 0m))
        {
            return OrderFulfillmentStatus.ReadyToDispatch;
        }

        return lines.All(line => line.Delivered >= line.Ordered)
            ? OrderFulfillmentStatus.Delivered
            : OrderFulfillmentStatus.PartiallyDelivered;
    }

    /// <summary>What a delivered line is worth: its net unit price times the delivered quantity, rounded once.</summary>
    public static decimal DeliveredAmount(decimal unitNetPrice, decimal deliveredQuantity) =>
        Money.Round2(unitNetPrice * deliveredQuantity);

    /// <summary>A delivered quantity is zero or more with at most three decimals (kilos to the gram).</summary>
    public static bool IsValidDeliveredQuantity(decimal quantity) =>
        quantity >= 0m && decimal.Round(quantity, 3) == quantity;
}

/// <summary>
/// The number of a remito (delivery note): `R{branch}-{sequence:8}`, for example `R01-00000042`. `R` is the document
/// type, the branch is its <see cref="BranchCode"/>, and the sequence counts that branch's remitos from 1. Internal and
/// non-fiscal; assigned once per order.
/// </summary>
public readonly record struct RemitoNumber(BranchCode Branch, int Sequence)
{
    public string Format() => $"R{Branch.Format()}-{Sequence.ToString("D8", CultureInfo.InvariantCulture)}";

    public override string ToString() => Format();
}
