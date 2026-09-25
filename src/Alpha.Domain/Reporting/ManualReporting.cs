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
        ManualReportKind reportKind = ManualReportKind.Current, Guid? sourceReportId = null, bool externalSourceReference = false)
    {
        if (reportKind != ManualReportKind.Current && sourceReportId is null && !externalSourceReference)
            throw new ArgumentException("A source report is required for differences and negative reports unless the report carries an official external previous-report reference.", nameof(sourceReportId));
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

    // Immutable Employer Interface 006 employer snapshot. Outgoing payloads must not
    // change when the live employer/profile is edited after the report was created.
    public string EmployerLegalNameSnapshot { get; private set; } = string.Empty;
    public string EmployerRegistrationNumberSnapshot { get; private set; } = string.Empty;
    public string EmployerWithholdingFileNumberSnapshot { get; private set; } = string.Empty;
    public string EmployerContactFirstNameSnapshot { get; private set; } = string.Empty;
    public string EmployerContactLastNameSnapshot { get; private set; } = string.Empty;
    public string EmployerContactPhoneSnapshot { get; private set; } = string.Empty;
    public string EmployerContactEmailSnapshot { get; private set; } = string.Empty;
    public string EmployerContactMobileSnapshot { get; private set; } = string.Empty;
    public int DepositorTypeCodeSnapshot { get; private set; } = 1;
    public int EmployerIdentifierTypeCodeSnapshot { get; private set; } = 1;

    public void SetEmployerInterfaceSnapshot(string legalName, string registrationNumber, string withholdingFileNumber,
        string? contactFirstName, string? contactLastName, string? contactPhone, string? contactEmail, string? contactMobile,
        int depositorTypeCode, int employerIdentifierTypeCode)
    {
        EnsureEditable();
        if (string.IsNullOrWhiteSpace(legalName)) throw new ArgumentException("Employer legal name is required.", nameof(legalName));
        if (string.IsNullOrWhiteSpace(registrationNumber)) throw new ArgumentException("Employer registration number is required.", nameof(registrationNumber));
        if (depositorTypeCode is < 1 or > 3) throw new ArgumentOutOfRangeException(nameof(depositorTypeCode));
        if (employerIdentifierTypeCode is not (1 or 2 or 3 or 4 or 5 or 7 or 8 or 9 or 10 or 11 or 12 or 13))
            throw new ArgumentOutOfRangeException(nameof(employerIdentifierTypeCode));

        EmployerLegalNameSnapshot = legalName.Trim();
        EmployerRegistrationNumberSnapshot = registrationNumber.Trim();
        EmployerWithholdingFileNumberSnapshot = withholdingFileNumber?.Trim() ?? string.Empty;
        EmployerContactFirstNameSnapshot = contactFirstName?.Trim() ?? string.Empty;
        EmployerContactLastNameSnapshot = contactLastName?.Trim() ?? string.Empty;
        EmployerContactPhoneSnapshot = contactPhone?.Trim() ?? string.Empty;
        EmployerContactEmailSnapshot = contactEmail?.Trim() ?? string.Empty;
        EmployerContactMobileSnapshot = contactMobile?.Trim() ?? string.Empty;
        DepositorTypeCodeSnapshot = depositorTypeCode;
        EmployerIdentifierTypeCodeSnapshot = employerIdentifierTypeCode;
        Touch();
    }

    public void CopyEmployerInterfaceSnapshotFrom(ManualReport source) =>
        SetEmployerInterfaceSnapshot(source.EmployerLegalNameSnapshot, source.EmployerRegistrationNumberSnapshot,
            source.EmployerWithholdingFileNumberSnapshot, source.EmployerContactFirstNameSnapshot,
            source.EmployerContactLastNameSnapshot, source.EmployerContactPhoneSnapshot,
            source.EmployerContactEmailSnapshot, source.EmployerContactMobileSnapshot,
            source.DepositorTypeCodeSnapshot, source.EmployerIdentifierTypeCodeSnapshot);

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
    public bool IsEditable => Status is ManualReportStatus.Draft or ManualReportStatus.ReadyForValidation or ManualReportStatus.Error;
    private void EnsureEditable() { if (!IsEditable) throw new InvalidOperationException("This report can no longer be edited."); }
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
        InterfaceIdentifierType = 1;
        InterfaceIdentifier = NationalId;
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

    // Employer Interface 006 immutable report snapshot. Export must use these values rather
    // than the live Person/Employment records so a draft/report cannot silently change later.
    public int InterfaceIdentifierType { get; private set; } = 1;
    public string InterfaceIdentifier { get; private set; } = string.Empty;
    public DateOnly? BirthDateSnapshot { get; private set; }
    public int? GenderSnapshot { get; private set; }
    public string EmailSnapshot { get; private set; } = string.Empty;
    public string MobileSnapshot { get; private set; } = string.Empty;
    public string CitySnapshot { get; private set; } = string.Empty;
    public string StreetSnapshot { get; private set; } = string.Empty;
    public string HouseNumberSnapshot { get; private set; } = string.Empty;
    public string ApartmentSnapshot { get; private set; } = string.Empty;
    public string PostalCodeSnapshot { get; private set; } = string.Empty;
    public string PostOfficeBoxSnapshot { get; private set; } = string.Empty;
    public DateOnly? EmploymentStartDateSnapshot { get; private set; }

    public ManualReportItemStatus ValidationStatus { get; private set; } = ManualReportItemStatus.Draft;
    public string ValidationError { get; private set; } = string.Empty;

    public void SetInterfaceSnapshot(int identifierType, string identifier, DateOnly? birthDate, int? gender,
        string? email, string? mobile, string? city, string? street, string? houseNumber, string? apartment,
        string? postalCode, string? postOfficeBox, DateOnly? employmentStartDate)
    {
        if (identifierType <= 0) throw new ArgumentOutOfRangeException(nameof(identifierType));
        if (string.IsNullOrWhiteSpace(identifier)) throw new ArgumentException("Employee interface identifier is required.", nameof(identifier));
        InterfaceIdentifierType = identifierType;
        InterfaceIdentifier = identifier.Trim();
        BirthDateSnapshot = birthDate;
        GenderSnapshot = gender;
        EmailSnapshot = email?.Trim() ?? string.Empty;
        MobileSnapshot = mobile?.Trim() ?? string.Empty;
        CitySnapshot = city?.Trim() ?? string.Empty;
        StreetSnapshot = street?.Trim() ?? string.Empty;
        HouseNumberSnapshot = houseNumber?.Trim() ?? string.Empty;
        ApartmentSnapshot = apartment?.Trim() ?? string.Empty;
        PostalCodeSnapshot = postalCode?.Trim() ?? string.Empty;
        PostOfficeBoxSnapshot = postOfficeBox?.Trim() ?? string.Empty;
        EmploymentStartDateSnapshot = employmentStartDate;
        Touch();
    }

    public void UpdateMonthlySalary(decimal monthlySalary)
    {
        if (monthlySalary < 0) throw new ArgumentOutOfRangeException(nameof(monthlySalary));
        MonthlySalary = monthlySalary;
        ValidationStatus = ManualReportItemStatus.Draft;
        ValidationError = string.Empty;
        Touch();
    }

    public void SetValidationResult(bool isValid, string? error = null)
    {
        ValidationStatus = isValid ? ManualReportItemStatus.Validated : ManualReportItemStatus.Error;
        ValidationError = isValid ? string.Empty : error?.Trim() ?? string.Empty;
        Touch();
    }

    public void MarkReadyForValidation()
    {
        ValidationStatus = ManualReportItemStatus.ReadyForValidation;
        ValidationError = string.Empty;
        Touch();
    }
}

public sealed class ManualReportProduct : Entity
{
    private ManualReportProduct() { }
    public ManualReportProduct(Guid reportEmployeeId, PensionProductType productType, string policyNumber, DateOnly salaryMonth, decimal salary, string reportingType, string salaryLayer, bool section14, DateOnly? section14StartDate, string? fundExternalKey = null, string? fundCode = null, string? fundName = null, string? fundCompanyName = null, SalaryAllocationType salaryAllocationType = SalaryAllocationType.Fixed, decimal? salaryAllocationValue = null, int allocationOrder = 0, int? section14Code = null, string? fundClassification = null)
    {
        ReportEmployeeId = reportEmployeeId;
        Update(productType, policyNumber, salaryMonth, salary, reportingType, salaryLayer, section14, section14StartDate,
            fundExternalKey, fundCode, fundName, fundCompanyName, salaryAllocationType, salaryAllocationValue, allocationOrder, section14Code, fundClassification);
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
    public string FundClassification { get; private set; } = string.Empty;
    public SalaryAllocationType SalaryAllocationType { get; private set; } = SalaryAllocationType.Fixed;
    public decimal? SalaryAllocationValue { get; private set; }
    public int AllocationOrder { get; private set; }
    public ManualReportItemStatus ValidationStatus { get; private set; } = ManualReportItemStatus.Draft;
    public string ValidationError { get; private set; } = string.Empty;

    public void Update(PensionProductType productType, string policyNumber, DateOnly salaryMonth, decimal salary,
        string reportingType, string salaryLayer, bool section14, DateOnly? section14StartDate,
        string? fundExternalKey = null, string? fundCode = null, string? fundName = null, string? fundCompanyName = null,
        SalaryAllocationType salaryAllocationType = SalaryAllocationType.Fixed, decimal? salaryAllocationValue = null,
        int allocationOrder = 0, int? section14Code = null, string? fundClassification = null)
    {
        if (salary < 0) throw new ArgumentOutOfRangeException(nameof(salary));
        if ((policyNumber?.Trim().Length ?? 0) > 20)
            throw new ArgumentOutOfRangeException(nameof(policyNumber), "Policy/account number cannot exceed 20 characters in Employer Interface 006.");
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
        FundClassification = fundClassification?.Trim() ?? string.Empty;
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

    public ManualContribution(Guid reportProductId, ContributionParty party, ContributionComponent component,
        decimal amount, decimal percentage, decimal exemptPayments, string? previousRecordIdentifier = null)
    {
        ReportProductId = reportProductId;
        Party = party;
        Component = component;
        Update(amount, percentage, exemptPayments);
        SetPreviousRecordIdentifier(previousRecordIdentifier);
    }

    public Guid ReportProductId { get; private set; }
    public ContributionParty Party { get; private set; }
    public ContributionComponent Component { get; private set; }
    public decimal Amount { get; private set; }
    public decimal Percentage { get; private set; }
    public decimal ExemptPayments { get; private set; }
    public string PreviousRecordIdentifier { get; private set; } = string.Empty;

    public void Update(decimal amount, decimal percentage, decimal exemptPayments)
    {
        if (amount < 0 || percentage < 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Contribution amount and percentage cannot be negative.");
        if (percentage > 100)
            throw new ArgumentOutOfRangeException(nameof(percentage), "Contribution percentage cannot exceed 100%.");
        Amount = amount;
        Percentage = percentage;
        ExemptPayments = exemptPayments;
        Touch();
    }

    public void SetPreviousRecordIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            PreviousRecordIdentifier = string.Empty;
            return;
        }

        if (!Guid.TryParseExact(value.Trim(), "D", out var parsed))
            throw new ArgumentException("Previous contribution record identifier must be a GUID.", nameof(value));
        PreviousRecordIdentifier = parsed.ToString("D").ToUpperInvariant();
        Touch();
    }
}

public sealed class ManualReportPayment : Entity
{
    private ManualReportPayment() { }
    public ManualReportPayment(Guid reportProductId) { ReportProductId = reportProductId; }
    public Guid ReportProductId { get; private set; } public string ProviderName { get; private set; } = string.Empty; public string ProviderAccount { get; private set; } = string.Empty; public string PaymentMethod { get; private set; } = "העברה בנקאית"; public DateOnly? ValueDate { get; private set; } public DateOnly? TrustAccountValueDate { get; private set; } public decimal? ActualDepositAmount { get; private set; } public string MasavSenderCode { get; private set; } = string.Empty; public string ReferenceNumber { get; private set; } = string.Empty; public string EmployerBankName { get; private set; } = string.Empty; public string EmployerBankCode { get; private set; } = string.Empty; public string EmployerBranch { get; private set; } = string.Empty; public string EmployerAccount { get; private set; } = string.Empty; public string ConfirmationFileName { get; private set; } = string.Empty;
    public void Update(string? providerName, string? providerAccount, string? paymentMethod, DateOnly? valueDate,
        string? referenceNumber, string? employerBankName, string? employerBankCode, string? employerBranch,
        string? employerAccount, string? confirmationFileName) =>
        Update(providerName, providerAccount, paymentMethod, valueDate, null, referenceNumber, employerBankName,
            employerBankCode, employerBranch, employerAccount, confirmationFileName);

    public void Update(string? providerName, string? providerAccount, string? paymentMethod, DateOnly? valueDate, DateOnly? trustAccountValueDate, string? referenceNumber, string? employerBankName, string? employerBankCode, string? employerBranch, string? employerAccount, string? confirmationFileName, decimal? actualDepositAmount = null, string? masavSenderCode = null)
    {
        if (actualDepositAmount < 0) throw new ArgumentOutOfRangeException(nameof(actualDepositAmount));
        if ((masavSenderCode?.Trim().Length ?? 0) > 16) throw new ArgumentOutOfRangeException(nameof(masavSenderCode));
        ProviderName = providerName?.Trim() ?? string.Empty; ProviderAccount = providerAccount?.Trim() ?? string.Empty;
        PaymentMethod = string.IsNullOrWhiteSpace(paymentMethod) ? "העברה בנקאית" : paymentMethod.Trim();
        ValueDate = valueDate; TrustAccountValueDate = trustAccountValueDate; ActualDepositAmount = actualDepositAmount;
        MasavSenderCode = masavSenderCode?.Trim() ?? string.Empty; ReferenceNumber = referenceNumber?.Trim() ?? string.Empty;
        EmployerBankName = employerBankName?.Trim() ?? string.Empty; EmployerBankCode = employerBankCode?.Trim() ?? string.Empty;
        EmployerBranch = employerBranch?.Trim() ?? string.Empty; EmployerAccount = employerAccount?.Trim() ?? string.Empty;
        ConfirmationFileName = confirmationFileName?.Trim() ?? string.Empty; Touch();
    }
}
