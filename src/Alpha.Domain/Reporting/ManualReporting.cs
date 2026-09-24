using Alpha.Domain.Common;

namespace Alpha.Domain.Reporting;

public enum ManualReportStatus
{
    Draft = 1,
    ReadyForValidation = 2,
    Validated = 3,
    Submitted = 4,
    Cancelled = 5,
    Sent = 6,
    Processing = 7,
    Completed = 8,
    Error = 9
}

public enum ManualReportItemStatus { Draft = 1, ReadyForValidation = 2, Validated = 3, Error = 4 }
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
        OrganizationId = organizationId; EmployerId = employerId;
        ReportingMonth = new DateOnly(reportingMonth.Year, reportingMonth.Month, 1);
        SalaryPaymentDate = salaryPaymentDate; ReportKind = reportKind; SourceReportId = sourceReportId;
    }

    public Guid OrganizationId { get; private set; }
    public Guid EmployerId { get; private set; }
    public DateOnly ReportingMonth { get; private set; }
    public DateOnly? SalaryPaymentDate { get; private set; }
    public ManualReportStatus Status { get; private set; } = ManualReportStatus.Draft;
    public ManualReportKind ReportKind { get; private set; } = ManualReportKind.Current;
    public Guid? SourceReportId { get; private set; }
    public DateTimeOffset? SnapshotTakenAt { get; private set; }
    public DateTimeOffset? ValidatedAt { get; private set; }
    public string ValidationError { get; private set; } = string.Empty;
    public Guid? PaymentAccountId { get; private set; }
    public int? PaymentBankId { get; private set; }
    public int? PaymentBranchId { get; private set; }
    public string PaymentAccountNumberMasked { get; private set; } = string.Empty;
    public string PaymentMandateReference { get; private set; } = string.Empty;

    public void SetPaymentAccountSnapshot(Guid paymentAccountId, int bankId, int branchId,
        string accountNumberMasked, string? mandateReference)
    {
        EnsureEditable();
        if (paymentAccountId == Guid.Empty) throw new ArgumentException("Payment account is required.", nameof(paymentAccountId));
        PaymentAccountId = paymentAccountId;
        PaymentBankId = bankId;
        PaymentBranchId = branchId;
        PaymentAccountNumberMasked = accountNumberMasked?.Trim() ?? string.Empty;
        PaymentMandateReference = mandateReference?.Trim() ?? string.Empty;
        MarkDirty();
    }

    public void UpdateDetails(DateOnly reportingMonth, DateOnly? salaryPaymentDate) { EnsureEditable(); ReportingMonth = new DateOnly(reportingMonth.Year, reportingMonth.Month, 1); SalaryPaymentDate = salaryPaymentDate; MarkDirty(); }
    public void MarkReadyForValidation() { EnsureEditable(); Status = ManualReportStatus.ReadyForValidation; ValidationError = string.Empty; Touch(); }
    public void MarkValidated() { Status = ManualReportStatus.Validated; SnapshotTakenAt ??= DateTimeOffset.UtcNow; ValidatedAt = DateTimeOffset.UtcNow; ValidationError = string.Empty; Touch(); }
    public void MarkValidationError(string? message) { Status = ManualReportStatus.Error; ValidationError = message?.Trim() ?? string.Empty; ValidatedAt = null; Touch(); }
    public void MarkTransmissionStarted() { if (Status != ManualReportStatus.Validated) throw new InvalidOperationException("Only a validated report can be sent."); Status = ManualReportStatus.Processing; Touch(); }
    public void MarkSent() { if (Status != ManualReportStatus.Processing) throw new InvalidOperationException("Report is not being transmitted."); Status = ManualReportStatus.Sent; Touch(); }
    public void MarkTransmissionError(string? message) { Status = ManualReportStatus.Error; ValidationError = message?.Trim() ?? string.Empty; Touch(); }
    public void MarkCompleted() { if (Status is not ManualReportStatus.Sent and not ManualReportStatus.Processing) throw new InvalidOperationException("Only a sent report can be completed."); Status = ManualReportStatus.Completed; Touch(); }
    public void MarkDirty() { if (Status is ManualReportStatus.Sent or ManualReportStatus.Processing or ManualReportStatus.Completed or ManualReportStatus.Submitted or ManualReportStatus.Cancelled) throw new InvalidOperationException("A sent or completed report cannot be edited."); Status = ManualReportStatus.Draft; SnapshotTakenAt = null; ValidatedAt = null; ValidationError = string.Empty; Touch(); }
    public bool IsEditable => Status is ManualReportStatus.Draft or ManualReportStatus.ReadyForValidation or ManualReportStatus.Validated or ManualReportStatus.Error;
    private void EnsureEditable() { if (!IsEditable) throw new InvalidOperationException("This report can no longer be edited."); }
}

public sealed class ManualReportEmployee : Entity
{
    private ManualReportEmployee() { }
    public ManualReportEmployee(Guid reportId, Guid organizationId, Guid employerId, Guid employmentId, Guid personId, string nationalId, string firstName, string lastName, string employeeNumber, decimal monthlySalary = 0) { ReportId = reportId; OrganizationId = organizationId; EmployerId = employerId; EmploymentId = employmentId; PersonId = personId; NationalId = nationalId.Trim(); FirstName = firstName.Trim(); LastName = lastName.Trim(); EmployeeNumber = employeeNumber.Trim(); UpdateMonthlySalary(monthlySalary); }
    public Guid ReportId { get; private set; } public Guid OrganizationId { get; private set; } public Guid EmployerId { get; private set; } public Guid EmploymentId { get; private set; } public Guid PersonId { get; private set; }
    public string NationalId { get; private set; } = string.Empty; public string FirstName { get; private set; } = string.Empty; public string LastName { get; private set; } = string.Empty; public string EmployeeNumber { get; private set; } = string.Empty;
    public decimal MonthlySalary { get; private set; } public ManualReportItemStatus ValidationStatus { get; private set; } = ManualReportItemStatus.Draft; public string ValidationError { get; private set; } = string.Empty;
    public void UpdateMonthlySalary(decimal monthlySalary) { if (monthlySalary < 0) throw new ArgumentOutOfRangeException(nameof(monthlySalary)); MonthlySalary = monthlySalary; ValidationStatus = ManualReportItemStatus.Draft; ValidationError = string.Empty; Touch(); }
    public void SetValidationResult(bool isValid, string? error = null) { ValidationStatus = isValid ? ManualReportItemStatus.Validated : ManualReportItemStatus.Error; ValidationError = isValid ? string.Empty : error?.Trim() ?? string.Empty; Touch(); }
    public void MarkReadyForValidation() { ValidationStatus = ManualReportItemStatus.ReadyForValidation; ValidationError = string.Empty; Touch(); }
}

public sealed class ManualReportProduct : Entity
{
    private ManualReportProduct() { }
    public ManualReportProduct(Guid reportEmployeeId, PensionProductType productType, string policyNumber, DateOnly salaryMonth, decimal salary, string reportingType, string salaryLayer, bool section14, DateOnly? section14StartDate, string? fundExternalKey = null, string? fundCode = null, string? fundName = null, string? fundCompanyName = null, SalaryAllocationType salaryAllocationType = SalaryAllocationType.Fixed, decimal? salaryAllocationValue = null, int allocationOrder = 0, int? section14Code = null)
    {
        ReportEmployeeId = reportEmployeeId;
        Update(productType, policyNumber, salaryMonth, salary, reportingType, salaryLayer, section14, section14StartDate,
            fundExternalKey, fundCode, fundName, fundCompanyName, salaryAllocationType, salaryAllocationValue, allocationOrder, section14Code);
    }

    public Guid ReportEmployeeId { get; private set; }
    public PensionProductType ProductType { get; private set; }
    public string PolicyNumber { get; private set; } = string.Empty;
    public DateOnly SalaryMonth { get; private set; }
    public decimal Salary { get; private set; }
    public string ReportingType { get; private set; } = string.Empty;
    public string SalaryLayer { get; private set; } = string.Empty;
    public bool Section14 { get; private set; }
    public int Section14Code { get; private set; } = 3;
    public DateOnly? Section14StartDate { get; private set; }
    public string FundExternalKey { get; private set; } = string.Empty;
    public string FundCode { get; private set; } = string.Empty;
    public string FundName { get; private set; } = string.Empty;
    public string FundCompanyName { get; private set; } = string.Empty;
    public SalaryAllocationType SalaryAllocationType { get; private set; } = SalaryAllocationType.Fixed;
    public decimal? SalaryAllocationValue { get; private set; }
    public int AllocationOrder { get; private set; }
    public ManualReportItemStatus ValidationStatus { get; private set; } = ManualReportItemStatus.Draft;
    public string ValidationError { get; private set; } = string.Empty;

    public void Update(PensionProductType productType, string policyNumber, DateOnly salaryMonth, decimal salary,
        string reportingType, string salaryLayer, bool section14, DateOnly? section14StartDate,
        string? fundExternalKey = null, string? fundCode = null, string? fundName = null, string? fundCompanyName = null,
        SalaryAllocationType salaryAllocationType = SalaryAllocationType.Fixed, decimal? salaryAllocationValue = null,
        int allocationOrder = 0, int? section14Code = null)
    {
        if (salary < 0) throw new ArgumentOutOfRangeException(nameof(salary));
        if (allocationOrder < 0) throw new ArgumentOutOfRangeException(nameof(allocationOrder));
        if (salaryAllocationValue < 0) throw new ArgumentOutOfRangeException(nameof(salaryAllocationValue));
        if (salaryAllocationType == SalaryAllocationType.Percentage && salaryAllocationValue > 100)
            throw new ArgumentOutOfRangeException(nameof(salaryAllocationValue));
        var resolvedSection14Code = section14Code ?? (!section14 && section14StartDate.HasValue ? 4 : !section14 ? 3 : section14StartDate.HasValue ? 2 : 1);
        if (resolvedSection14Code is < 1 or > 5) throw new ArgumentOutOfRangeException(nameof(section14Code));
        if (resolvedSection14Code is 2 or 4 && !section14StartDate.HasValue)
            throw new ArgumentException("Section 14 effective/cancellation date is required for codes 2 and 4.", nameof(section14StartDate));

        ProductType = productType;
        PolicyNumber = policyNumber?.Trim() ?? string.Empty;
        SalaryMonth = new DateOnly(salaryMonth.Year, salaryMonth.Month, 1);
        Salary = salary;
        ReportingType = reportingType?.Trim() ?? string.Empty;
        SalaryLayer = salaryLayer?.Trim() ?? string.Empty;
        Section14Code = resolvedSection14Code;
        Section14 = resolvedSection14Code is 1 or 2;
        Section14StartDate = resolvedSection14Code is 2 or 4 ? section14StartDate : null;
        FundExternalKey = fundExternalKey?.Trim() ?? string.Empty;
        FundCode = fundCode?.Trim() ?? string.Empty;
        FundName = fundName?.Trim() ?? string.Empty;
        FundCompanyName = fundCompanyName?.Trim() ?? string.Empty;
        SalaryAllocationType = salaryAllocationType;
        SalaryAllocationValue = salaryAllocationType == SalaryAllocationType.Remainder ? null : salaryAllocationValue;
        AllocationOrder = allocationOrder;
        ValidationStatus = ManualReportItemStatus.Draft;
        ValidationError = string.Empty;
        Touch();
    }
    public void SetValidationResult(bool isValid, string? error = null) { ValidationStatus = isValid ? ManualReportItemStatus.Validated : ManualReportItemStatus.Error; ValidationError = isValid ? string.Empty : error?.Trim() ?? string.Empty; Touch(); }
    public void MarkReadyForValidation() { ValidationStatus = ManualReportItemStatus.ReadyForValidation; ValidationError = string.Empty; Touch(); }
}

public sealed class ManualContribution : Entity
{
    private ManualContribution() { }
    public ManualContribution(Guid reportProductId, ContributionParty party, ContributionComponent component, decimal amount, decimal percentage, decimal exemptPayments) { ReportProductId = reportProductId; Party = party; Component = component; Update(amount, percentage, exemptPayments); }
    public Guid ReportProductId { get; private set; } public ContributionParty Party { get; private set; } public ContributionComponent Component { get; private set; } public decimal Amount { get; private set; } public decimal Percentage { get; private set; } public decimal ExemptPayments { get; private set; }
    public void Update(decimal amount, decimal percentage, decimal exemptPayments)
    {
        if (amount < 0 || percentage < 0 || exemptPayments < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Contribution values cannot be negative.");
        if (percentage > 100)
            throw new ArgumentOutOfRangeException(nameof(percentage), "Contribution percentage cannot exceed 100%.");
        Amount = amount;
        Percentage = percentage;
        ExemptPayments = exemptPayments;
        Touch();
    }
}

public sealed class ManualReportPayment : Entity
{
    private ManualReportPayment() { }
    public ManualReportPayment(Guid reportProductId) { ReportProductId = reportProductId; }
    public Guid ReportProductId { get; private set; } public string ProviderName { get; private set; } = string.Empty; public string ProviderAccount { get; private set; } = string.Empty; public string PaymentMethod { get; private set; } = "העברה בנקאית"; public DateOnly? ValueDate { get; private set; } public string ReferenceNumber { get; private set; } = string.Empty; public string EmployerBankName { get; private set; } = string.Empty; public string EmployerBankCode { get; private set; } = string.Empty; public string EmployerBranch { get; private set; } = string.Empty; public string EmployerAccount { get; private set; } = string.Empty; public string ConfirmationFileName { get; private set; } = string.Empty;
    public void Update(string? providerName, string? providerAccount, string? paymentMethod, DateOnly? valueDate, string? referenceNumber, string? employerBankName, string? employerBankCode, string? employerBranch, string? employerAccount, string? confirmationFileName) { ProviderName = providerName?.Trim() ?? string.Empty; ProviderAccount = providerAccount?.Trim() ?? string.Empty; PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? "העברה בנקאית" : paymentMethod.Trim(); ValueDate = valueDate; ReferenceNumber = referenceNumber?.Trim() ?? string.Empty; EmployerBankName = employerBankName?.Trim() ?? string.Empty; EmployerBankCode = employerBankCode?.Trim() ?? string.Empty; EmployerBranch = employerBranch?.Trim() ?? string.Empty; EmployerAccount = employerAccount?.Trim() ?? string.Empty; ConfirmationFileName = confirmationFileName?.Trim() ?? string.Empty; Touch(); }
}
