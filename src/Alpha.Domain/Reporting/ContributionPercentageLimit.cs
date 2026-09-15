using Alpha.Domain.Common;

namespace Alpha.Domain.Reporting;

public sealed class ContributionPercentageLimit : Entity
{
    private ContributionPercentageLimit() { }

    public ContributionPercentageLimit(int year, PensionProductType productType, ContributionParty party,
        ContributionComponent component, decimal maxPercentage)
    {
        if (year < 2000 || year > 2200) throw new ArgumentOutOfRangeException(nameof(year));
        if (maxPercentage < 0 || maxPercentage > 100) throw new ArgumentOutOfRangeException(nameof(maxPercentage));
        Year = year;
        ProductType = productType;
        Party = party;
        Component = component;
        MaxPercentage = maxPercentage;
    }

    public int Year { get; private set; }
    public PensionProductType ProductType { get; private set; }
    public ContributionParty Party { get; private set; }
    public ContributionComponent Component { get; private set; }
    public decimal MaxPercentage { get; private set; }

    public void Update(decimal maxPercentage)
    {
        if (maxPercentage < 0 || maxPercentage > 100) throw new ArgumentOutOfRangeException(nameof(maxPercentage));
        MaxPercentage = maxPercentage;
        Touch();
    }
}
