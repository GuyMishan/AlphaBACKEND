using Alpha.Domain.Common;

namespace Alpha.Domain.Organizations;

public enum OrganizationType { Employer = 1, PayrollOffice = 2, InsuranceAgency = 3, OperationsProvider = 4, CorporateGroup = 5, SelfService = 6 }
public enum OrganizationStatus { Onboarding = 1, Active = 2, Suspended = 3, Closed = 4 }

public sealed class Organization : Entity
{
    private Organization() { }

    public Organization(string name, OrganizationType type)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Organization name is required.", nameof(name))
            : name.Trim();
        Type = type;
    }

    public string Name { get; private set; } = string.Empty;
    public OrganizationType Type { get; private set; }
    public OrganizationStatus Status { get; private set; } = OrganizationStatus.Onboarding;

    public void Update(string name, OrganizationType type)
    {
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("Organization name is required.", nameof(name))
            : name.Trim();
        Type = type;
        Touch();
    }

    public void Activate()
    {
        Status = OrganizationStatus.Active;
        Touch();
    }

    public void ChangeStatus(OrganizationStatus status)
    {
        Status = status;
        Touch();
    }
}
