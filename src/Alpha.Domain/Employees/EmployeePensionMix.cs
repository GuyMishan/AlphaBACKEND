using Alpha.Domain.Common;
using Alpha.Domain.Reporting;

namespace Alpha.Domain.Employees;

public sealed class EmployeePensionProduct : Entity
{
    private EmployeePensionProduct() { }

    public EmployeePensionProduct(Guid employmentId, PensionProductType productType, string policyNumber,
        decimal salary, string reportingType, string salaryLayer, bool section14, DateOnly? section14StartDate)
    {
        EmploymentId = employmentId;
        Update(productType, policyNumber, salary, reportingType, salaryLayer, section14, section14StartDate);
    }

    public Guid EmploymentId { get; private set; }
    public PensionProductType ProductType { get; private set; }
    public string PolicyNumber { get; private set; } = string.Empty;
    public decimal Salary { get; private set; }
    public string ReportingType { get; private set; } = "שוטף";
    public string SalaryLayer { get; private set; } = "רובד 1";
    public bool Section14 { get; private set; }
    public DateOnly? Section14StartDate { get; private set; }

    public void Update(PensionProductType productType, string policyNumber, decimal salary, string reportingType,
        string salaryLayer, bool section14, DateOnly? section14StartDate)
    {
        if (salary < 0) throw new ArgumentOutOfRangeException(nameof(salary));
        ProductType = productType;
        PolicyNumber = policyNumber?.Trim() ?? string.Empty;
        Salary = salary;
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
