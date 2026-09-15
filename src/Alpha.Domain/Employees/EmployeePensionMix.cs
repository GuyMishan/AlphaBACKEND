using Alpha.Domain.Common;
using Alpha.Domain.Reporting;

namespace Alpha.Domain.Employees;

public sealed class EmployeePensionProduct : Entity
{
    private EmployeePensionProduct() { }

    public EmployeePensionProduct(Guid employmentId, PensionProductType productType, string policyNumber,
        decimal salary, string reportingType, string salaryLayer, bool section14, DateOnly? section14StartDate,
        bool isActive, DateOnly effectiveFrom, DateOnly? effectiveTo, string institutionalBody, string manufacturer)
    {
        EmploymentId = employmentId;
        Update(productType, policyNumber, salary, reportingType, salaryLayer, section14, section14StartDate,
            isActive, effectiveFrom, effectiveTo, institutionalBody, manufacturer);
    }

    public Guid EmploymentId { get; private set; }
    public PensionProductType ProductType { get; private set; }
    public string PolicyNumber { get; private set; } = string.Empty;
    public decimal Salary { get; private set; }
    public string ReportingType { get; private set; } = "שוטף";
    public string SalaryLayer { get; private set; } = "רובד 1";
    public bool Section14 { get; private set; }
    public DateOnly? Section14StartDate { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly? EffectiveTo { get; private set; }
    public string InstitutionalBody { get; private set; } = string.Empty;
    public string Manufacturer { get; private set; } = string.Empty;

    public void Update(PensionProductType productType, string policyNumber, decimal salary, string reportingType,
        string salaryLayer, bool section14, DateOnly? section14StartDate, bool isActive, DateOnly effectiveFrom,
        DateOnly? effectiveTo, string institutionalBody, string manufacturer)
    {
        if (salary < 0) throw new ArgumentOutOfRangeException(nameof(salary));
        if (effectiveTo is not null && effectiveTo.Value < effectiveFrom)
            throw new ArgumentException("Product effective end date cannot be earlier than the start date.", nameof(effectiveTo));

        ProductType = productType;
        PolicyNumber = policyNumber?.Trim() ?? string.Empty;
        Salary = salary;
        ReportingType = string.IsNullOrWhiteSpace(reportingType) ? "שוטף" : reportingType.Trim();
        SalaryLayer = string.IsNullOrWhiteSpace(salaryLayer) ? "רובד 1" : salaryLayer.Trim();
        Section14 = section14;
        Section14StartDate = section14 ? section14StartDate : null;
        IsActive = isActive;
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
        InstitutionalBody = institutionalBody?.Trim() ?? string.Empty;
        Manufacturer = manufacturer?.Trim() ?? string.Empty;
        Touch();
    }

    public IReadOnlyCollection<string> MissingDetails()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(PolicyNumber)) missing.Add("policyNumber");
        if (Salary <= 0) missing.Add("salary");
        if (string.IsNullOrWhiteSpace(InstitutionalBody)) missing.Add("institutionalBody");
        if (string.IsNullOrWhiteSpace(Manufacturer)) missing.Add("manufacturer");
        if (Section14 && Section14StartDate is null) missing.Add("section14StartDate");
        return missing;
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
