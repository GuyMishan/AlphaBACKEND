using Alpha.Domain.Common;
using Alpha.Domain.Reporting;

namespace Alpha.Domain.Employees;

public sealed class EmployeePensionProduct : Entity
{
    private EmployeePensionProduct() { }

    public EmployeePensionProduct(Guid employmentId, PensionProductType productType, string policyNumber,
        decimal salary, string reportingType, string salaryLayer, bool section14, DateOnly? section14StartDate,
        bool isActive, DateOnly effectiveFrom, DateOnly? effectiveTo, string institutionalBody, string manufacturer,
        string? fundExternalKey = null, string? fundCode = null, string? fundName = null, string? fundCompanyName = null,
        SalaryAllocationType salaryAllocationType = SalaryAllocationType.Fixed, decimal? salaryAllocationValue = null,
        int allocationOrder = 0, int? section14Code = null)
    {
        EmploymentId = employmentId;
        Update(productType, policyNumber, salary, reportingType, salaryLayer, section14, section14StartDate,
            isActive, effectiveFrom, effectiveTo, institutionalBody, manufacturer, fundExternalKey, fundCode, fundName,
            fundCompanyName, salaryAllocationType, salaryAllocationValue, allocationOrder, section14Code);
    }

    public Guid EmploymentId { get; private set; }
    public PensionProductType ProductType { get; private set; }
    public string PolicyNumber { get; private set; } = string.Empty;
    public decimal Salary { get; private set; }
    public string ReportingType { get; private set; } = "1";
    public string SalaryLayer { get; private set; } = "1";
    public bool Section14 { get; private set; }
    public int Section14Code { get; private set; } = 3;
    public DateOnly? Section14StartDate { get; private set; }
    public bool IsActive { get; private set; } = true;
    public DateOnly EffectiveFrom { get; private set; }
    public DateOnly? EffectiveTo { get; private set; }
    public string InstitutionalBody { get; private set; } = string.Empty;
    public string Manufacturer { get; private set; } = string.Empty;
    public string FundExternalKey { get; private set; } = string.Empty;
    public string FundCode { get; private set; } = string.Empty;
    public string FundName { get; private set; } = string.Empty;
    public string FundCompanyName { get; private set; } = string.Empty;
    public SalaryAllocationType SalaryAllocationType { get; private set; } = SalaryAllocationType.Fixed;
    public decimal? SalaryAllocationValue { get; private set; }
    public int AllocationOrder { get; private set; }

    public void Update(PensionProductType productType, string policyNumber, decimal salary, string reportingType,
        string salaryLayer, bool section14, DateOnly? section14StartDate, bool isActive, DateOnly effectiveFrom,
        DateOnly? effectiveTo, string institutionalBody, string manufacturer, string? fundExternalKey = null,
        string? fundCode = null, string? fundName = null, string? fundCompanyName = null,
        SalaryAllocationType salaryAllocationType = SalaryAllocationType.Fixed, decimal? salaryAllocationValue = null,
        int allocationOrder = 0, int? section14Code = null)
    {
        if (salary < 0) throw new ArgumentOutOfRangeException(nameof(salary));
        if (effectiveTo is not null && effectiveTo.Value < effectiveFrom)
            throw new ArgumentException("Product effective end date cannot be earlier than the start date.", nameof(effectiveTo));
        if (allocationOrder < 0) throw new ArgumentOutOfRangeException(nameof(allocationOrder));
        if (salaryAllocationType != SalaryAllocationType.Remainder && (!salaryAllocationValue.HasValue || salaryAllocationValue.Value <= 0))
            salaryAllocationValue = salary > 0 ? salary : null;
        if (salaryAllocationValue < 0) throw new ArgumentOutOfRangeException(nameof(salaryAllocationValue));
        if (salaryAllocationType == SalaryAllocationType.Percentage && salaryAllocationValue > 100)
            throw new ArgumentOutOfRangeException(nameof(salaryAllocationValue), "Salary allocation percentage cannot exceed 100%.");

        var resolvedSection14Code = section14Code ?? (!section14 && section14StartDate.HasValue ? 4 : !section14 ? 3 : section14StartDate.HasValue ? 2 : 1);
        if (resolvedSection14Code is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(section14Code), "Section 14 code must be 1-4.");
        if (resolvedSection14Code is 2 or 4 && !section14StartDate.HasValue)
            throw new ArgumentException("Section 14 effective/cancellation date is required for codes 2 and 4.", nameof(section14StartDate));

        ProductType = productType;
        PolicyNumber = policyNumber?.Trim() ?? string.Empty;
        Salary = salary;
        ReportingType = string.IsNullOrWhiteSpace(reportingType) ? "1" : reportingType.Trim();
        SalaryLayer = string.IsNullOrWhiteSpace(salaryLayer) ? "1" : salaryLayer.Trim();
        Section14Code = resolvedSection14Code;
        Section14 = resolvedSection14Code is 1 or 2;
        Section14StartDate = resolvedSection14Code is 2 or 4 ? section14StartDate : null;
        IsActive = isActive;
        EffectiveFrom = effectiveFrom;
        EffectiveTo = effectiveTo;
        InstitutionalBody = institutionalBody?.Trim() ?? string.Empty;
        Manufacturer = manufacturer?.Trim() ?? string.Empty;
        FundExternalKey = fundExternalKey?.Trim() ?? string.Empty;
        FundCode = fundCode?.Trim() ?? string.Empty;
        FundName = fundName?.Trim() ?? string.Empty;
        FundCompanyName = fundCompanyName?.Trim() ?? string.Empty;
        SalaryAllocationType = salaryAllocationType;
        SalaryAllocationValue = salaryAllocationType == SalaryAllocationType.Remainder ? null : salaryAllocationValue;
        AllocationOrder = allocationOrder;
        Touch();
    }

    public IReadOnlyCollection<string> MissingDetails()
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(PolicyNumber)) missing.Add("policyNumber");
        if (ProductType != PensionProductType.Other && string.IsNullOrWhiteSpace(FundExternalKey)) missing.Add("fund");
        if (string.IsNullOrWhiteSpace(InstitutionalBody)) missing.Add("institutionalBody");
        if (string.IsNullOrWhiteSpace(Manufacturer)) missing.Add("manufacturer");
        if (Section14Code is < 1 or > 4) missing.Add("section14Code");
        if (Section14Code is 2 or 4 && Section14StartDate is null) missing.Add("section14StartDate");
        if (SalaryAllocationType != SalaryAllocationType.Remainder && (!SalaryAllocationValue.HasValue || SalaryAllocationValue.Value <= 0))
            missing.Add("salaryAllocationValue");
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
