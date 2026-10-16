using Commerce.Application.Pricing;

namespace Commerce.Integration;

/// <summary>
/// customer-price-lists T2: WHICH price list prices a sale is decided by the BUYER, never by the channel. The rule is
/// pure (no I/O) and lives in <c>Commerce.Application</c> so the cloud API and the POS apply the very same one.
/// </summary>
public sealed class BuyerPriceListSelectorTests
{
    private static readonly Guid Mostrador = Guid.NewGuid();
    private static readonly Guid Reparto = Guid.NewGuid();
    private static readonly Guid Special = Guid.NewGuid();

    [Fact]
    public void AWalkInBuyer_IsPricedFromTheOrganizationDefaultList()
    {
        Assert.Equal(Mostrador, BuyerPriceListSelector.Select(
            isCustomer: false, customerPriceListId: null, organizationDefaultCustomerPriceListId: Reparto, organizationDefaultPriceListId: Mostrador));
    }

    [Fact]
    public void AWalkInBuyer_IgnoresAnyCustomerList()
    {
        Assert.Equal(Mostrador, BuyerPriceListSelector.Select(false, Special, Reparto, Mostrador));
    }

    [Fact]
    public void ACustomerWithAList_IsPricedFromThatList()
    {
        Assert.Equal(Special, BuyerPriceListSelector.Select(true, Special, Reparto, Mostrador));
    }

    [Fact]
    public void ACustomerWithoutAList_IsPricedFromTheOrganizationDefaultCustomerList()
    {
        Assert.Equal(Reparto, BuyerPriceListSelector.Select(true, null, Reparto, Mostrador));
    }

    [Fact]
    public void ACustomerWithoutAListAndNoCustomerDefault_FallsBackToTheOrganizationDefaultList()
    {
        Assert.Equal(Mostrador, BuyerPriceListSelector.Select(true, null, null, Mostrador));
    }

    [Fact]
    public void AListThatIsNotAvailableInTheSellingBranch_IsSkipped_FallingBackDownTheChain()
    {
        // The customer's own list belongs to another branch: the organization default customer list is next, then the default.
        Assert.Equal(Reparto, BuyerPriceListSelector.Select(true, Special, Reparto, Mostrador, id => id != Special));
        Assert.Equal(Mostrador, BuyerPriceListSelector.Select(true, Special, Reparto, Mostrador, id => id == Mostrador));
    }

    [Fact]
    public void WithNoListAtAll_NothingIsSelected()
    {
        Assert.Null(BuyerPriceListSelector.Select(true, null, null, null));
        Assert.Null(BuyerPriceListSelector.Select(false, null, null, null));
    }
}
