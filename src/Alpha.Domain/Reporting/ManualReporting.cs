using Alpha.Domain.Common;

namespace Alpha.Domain.Reporting;

public enum ManualReportStatus { Draft = 1, ReadyForValidation = 2, Validated = 3, Submitted = 4, Cancelled = 5 }
public enum ManualReportKind { Current = 1, Differences = 2, Negative = 3 }
public enum PensionProductType { PensionFund = 1, StudyFund = 2, ManagersInsurance = 3, ProvidentFund = 4, Other = 99 }
public enum ContributionParty { Employer = 1, Employee = 2 }
public enum ContributionComponent { Severance = 1, Benefits = 2, Disability = 3, Other = 4 }
public enum SalaryAllocationType { Fixed = 1, Percentage = 2, Cap = 3, Remainder = 4 }

public sealed class ManualReport : Entity
{
    private ManualReport() { }

    public ManualReport(Guid organizationId, Guid employerId, DateOnly reportingMonth, DateOnly? salaryPaymentDate,
        ManualReportKind reportKind = ManualReportKind.Current, Guid? sourceReportId = null)
    {
        if (reportKind != ManualReportKind.Current && sourceReportId is null)
            throw new ArgumentException("A source report is required for differences and negative reports.", nameof(sourceReportId));

        OrganizationId = organizationId;
        EmployerId = employerId;
        ReportingMonth = new DateOnly(reportingMonth.Year, reportingMonth.Month, 1);
        SalaryPaymentDate = salaryPaymentDate;
        ReportKind = reportKind;
        SourceReportId = sourceReportId;
    }

    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public DateOnly ReportingMonth { get; private set; }
    public DateOnly? SalaryPaymentDate { get; private set; }
    public ManualReportStatus Status { get; private set; } = ManualReportStatus.Draft;
    public ManualReportKind ReportKind { get; private set; } = ManualReportKind.Current;
    public Guid? SourceReportId { get; private set; }

    public void UpdateDetails(DateOnly reportingMonth, DateOnly? salaryPaymentDate)
    {
        ReportingMonth = new DateOnly(reportingMonth.Year, reportingMonth.Month, 1);
        SalaryPaymentDate = salaryPaymentDate;
        Touch();
    }
}

public sealed class ManualReportEmployee : Entity
{
    private ManualReportEmployee() { }

    public ManualReportEmployee(Guid reportId, Guid organizationId, Guid employerId, Guid employmentId, Guid personId,
        string nationalId, string firstName, string lastName, string employeeNumber, decimal monthlySalary = 0)
    {
        ReportId = reportId;
        OrganizationId = organizationId;
        EmployerId = employerId;
        EmploymentId = employmentId;
        PersonId = personId;
        NationalId = nationalId.Trim();
        FirstName = firstName.Trim();
        LastName = lastName.Trim();
        EmployeeNumber = employeeNumber.Trim();
        UpdateMonthlySalary(monthlySalary);
    }

    public Guid ReportId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public Guid EmploymentId { get; private set; }
    public Guid PersonId { get; private set; }
    public string NationalId { get; private set; } = string.Empty;
    public string FirstName { get; private set; } = string.Empty;
    public string LastName { get; private set; } = string.Empty;
    public string EmployeeNumber { get; private set; } = string.Empty;
    public decimal MonthlySalary { get; private set; }

    public void UpdateMonthlySalary(decimal monthlySalary)
    {
        if (monthlySalary < 0) throw new ArgumentOutOfRangeException(nameof(monthlySalary));
        MonthlySalary = monthlySalary;
        Touch();
    }
}

public sealed class ManualReportProduct : Entity
{
    private ManualReportProduct() { }

    public ManualReportProduct(Guid reportEmployeeId, PensionProductType productType, string policyNumber,
        DateOnly salaryMonth, decimal salary, string reportingType, string salaryLayer, bool section14,
        DateOnly? section14StartDate, string? fundExternalKey = null, string? fundCode = null,
        string? fundName = null, string? fundCompanyName = null,
        SalaryAllocationType salaryAllocationType = SalaryAllocationType.Fixed,
        decimal? salaryAllocationValue = null, int allocationOrder = 0)
    {
        ReportEmployeeId = reportEmployeeId;
        Update(productType, policyNumber, salaryMonth, salary, reportingType, salaryLayer, section14,
            section14StartDate, fundExternalKey, fundCode, fundName, fundCompanyName,
            salaryAllocationType, salaryAllocationValue, allocationOrder);
    }

    public Guid ReportEmployeeId { get; private set; }
    public PensionProductType ProductType { get; private set; }
    public string PolicyNumber { get; private set; } = string.Empty;
    public DateOnly SalaryMonth { get; private set; }
    public decimal Salary { get; private set; }
    public string ReportingType { get; private set; } = string.Empty;
    public string SalaryLayer { get; private set; } = string.Empty;
    public bool Section14 { get; private set; }
    public DateOnly? Section14StartDate { get; private set; }
    public string FundExternalKey { get; private set; } = string.Empty;
    public string FundCode { get; private set; } = string.Empty;
    public string FundName { get; private set; } = string.Empty;
    public string FundCompanyName { get; private set; } = string.Empty;
    public SalaryAllocationType SalaryAllocationType { get; private set; } = SalaryAllocationType.Fixed;
    public decimal? SalaryAllocationValue { get; private set; }
    public int AllocationOrder { get; private set; }

    public void Update(PensionProductType productType, string policyNumber, DateOnly salaryMonth, decimal salary,
        string reportingType, string salaryLayer, bool section14, DateOnly? section14StartDate,
        string? fundExternalKey = null, string? fundCode = null, string? fundName = null,
        string? fundCompanyName = null, SalaryAllocationType salaryAllocationType = SalaryAllocationType.Fixed,
        decimal? salaryAllocationValue = null, int allocationOrder = 0)
    {
        if (salary < 0) throw new ArgumentOutOfRangeException(nameof(salary));
        if (allocationOrder < 0) throw new ArgumentOutOfRangeException(nameof(allocationOrder));
        if (salaryAllocationValue < 0) throw new ArgumentOutOfRangeException(nameof(salaryAllocationValue));
        if (salaryAllocationType == SalaryAllocationType.Percentage && salaryAllocationValue > 100)
            throw new ArgumentOutOfRangeException(nameof(salaryAllocationValue));

        ProductType = productType;
        PolicyNumber = policyNumber?.Trim() ?? string.Empty;
        SalaryMonth = new DateOnly(salaryMonth.Year, salaryMonth.Month, 1);
        Salary = salary;
        ReportingType = reportingType?.Trim() ?? string.Empty;
        SalaryLayer = salaryLayer?.Trim() ?? string.Empty;
        Section14 = section14;
        Section14StartDate = section14 ? section14StartDate : null;
        FundExternalKey = fundExternalKey?.Trim() ?? string.Empty;
        FundCode = fundCode?.Trim() ?? string.Empty;
        FundName = fundName?.Trim() ?? string.Empty;
        FundCompanyName = fundCompanyName?.Trim() ?? string.Empty;
        SalaryAllocationType = salaryAllocationType;
        SalaryAllocationValue = salaryAllocationType == SalaryAllocationType.Remainder ? null : salaryAllocationValue;
        AllocationOrder = allocationOrder;
        Touch();
    }
}

public sealed class ManualContribution : Entity
{
    private ManualContribution() { }

    public ManualContribution(Guid reportProductId, ContributionParty party, ContributionComponent component,
        decimal amount, decimal percentage, decimal exemptPayments)
    {
        ReportProductId = reportProductId;
        Party = party;
        Component = component;
        Update(amount, percentage, exemptPayments);
    }

    public Guid ReportProductId { get; private set; }
    public ContributionParty Party { get; private set; }
    public ContributionComponent Component { get; private set; }
    public decimal Amount { get; private set; }
    public decimal Percentage { get; private set; }
    public decimal ExemptPayments { get; private set; }

    public void Update(decimal amount, decimal percentage, decimal exemptPayments)
    {
        if (amount < 0 || percentage < 0 || exemptPayments < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Contribution values cannot be negative.");
        Amount = amount;
        Percentage = percentage;
        ExemptPayments = exemptPayments;
        Touch();
    }
}

public sealed class ManualReportPayment : Entity
{
    private ManualReportPayment() { }

    public ManualReportPayment(Guid reportProductId)
    {
        ReportProductId = reportProductId;
    }

    public Guid ReportProductId { get; private set; }
    public string ProviderName { get; private set; } = string.Empty;
    public string ProviderAccount { get; private set; } = string.Empty;
    public string PaymentMethod { get; private set; } = "העברה בנקאית";
    public DateOnly? ValueDate { get; private set; }
    public string ReferenceNumber { get; private set; } = string.Empty;
    public string EmployerBankName { get; private set; } = string.Empty;
    public string EmployerBankCode { get; private set; } = string.Empty;
    public string EmployerBranch { get; private set; } = string.Empty;
    public string EmployerAccount { get; private set; } = string.Empty;
    public string ConfirmationFileName { get; private set; } = string.Empty;

    public void Update(string? providerName, string? providerAccount, string? paymentMethod, DateOnly? valueDate,
        string? referenceNumber, string? employerBankName, string? employerBankCode, string? employerBranch,
        string? employerAccount, string? confirmationFileName)
    {
        ProviderName = providerName?.Trim() ?? string.Empty;
        ProviderAccount = providerAccount?.Trim() ?? string.Empty;
        PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? "העברה בנקאית" : paymentMethod.Trim();
        ValueDate = valueDate;
        ReferenceNumber = referenceNumber?.Trim() ?? string.Empty;
        EmployerBankName = employerBankName?.Trim() ?? string.Empty;
        EmployerBankCode = employerBankCode?.Trim() ?? string.Empty;
        EmployerBranch = employerBranch?.Trim() ?? string.Empty;
        EmployerAccount = employerAccount?.Trim() ?? string.Empty;
        ConfirmationFileName = confirmationFileName?.Trim() ?? string.Empty;
        Touch();
    }
}