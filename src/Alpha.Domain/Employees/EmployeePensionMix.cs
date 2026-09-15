using Alpha.Domain.Common;
using Alpha.Domain.Reporting;

namespace Alpha.Domain.Employees;

public sealed class EmployeePensionProduct : Entity
{
    private EmployeePensionProduct() { }

    public EmployeePensionProduct(Guid employmentId, PensionProductType productType, string policyNumber,
        string reportingType, string salaryLayer, bool section14, DateOnly? section14StartDate)
    {
        EmploymentId = employmentId;
        Update(productType, policyNumber, reportingType, salaryLayer, section14, section14StartDate);
    }

    public Guid EmploymentId { get; private set; }
    public PensionProductType ProductType { get; private set; }
    public string PolicyNumber { get; private set; } = string.Empty;
    public string ReportingType { get; private set; } = "שוטף";
    public string SalaryLayer { get; private set; } = "רובד 1";
    public bool Section14 { get; private set; }
    public DateOnly? Section14StartDate { get; private set; }

    public void Update(PensionProductType productType, string policyNumber, string reportingType,
        string salaryLayer, bool section14, DateOnly? section14StartDate)
    {
        ProductType = productType;
        PolicyNumber = policyNumber?.Trim() ?? string.Empty;
        ReportingType = string.IsNullOrWhiteSpace(reportingType) ? "שוטף" : reportingType.Trim();
        SalaryLayer = string.IsNullOrWhiteSpace(salaryLayer) ? "רובד 1" : salaryLayer.Trim();
        Section14 = section14;
        Section14StartDate = section14 ? section14StartDate : null;
        Touch();
    }
}

public sealed class EmployeePensionContribution : Entity
{
    private EmployeePensionContribution() { }

    public EmployeePensionContribution(Guid employeePensionProductId, ContributionParty party,
        ContributionComponent component, decimal percentage)
    {
        EmployeePensionProductId = employeePensionProductId;
        Party = party;
        Component = component;
        Update(percentage);
    }

    public Guid EmployeePensionProductId { get; private set; }
    public ContributionParty Party { get; private set; }
    public ContributionComponent Component { get; private set; }
    public decimal Percentage { get; private set; }

    public void Update(decimal percentage)
    {
        if (percentage < 0 || percentage > 100) throw new ArgumentOutOfRangeException(nameof(percentage));
        Percentage = percentage;
        Touch();
    }
}
