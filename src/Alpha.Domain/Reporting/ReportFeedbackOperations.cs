using Alpha.Domain.Common;

namespace Alpha.Domain.Reporting;

public sealed class EmployerInterfaceTransferFeedback : Entity
{
    private EmployerInterfaceTransferFeedback() { }

    public EmployerInterfaceTransferFeedback(Guid feedbackId, Guid reportId, string transferIdentifier,
        string? clearingIdentifier, decimal reportedDepositAmount, decimal actualReceivedAmount,
        decimal allocatedAmount, decimal inTransitAmount, decimal? proactiveRefundAmount,
        decimal? employerAccountRefundAmount, int? moneyTreatmentStatus, string? statusDetail,
        string? paymentReference, DateOnly? valueDate, DateOnly? trustAccountValueDate,
        string? correctnessTimestamp, DateTimeOffset receivedAt)
    {
        FeedbackId = feedbackId;
        ReportId = reportId;
        TransferIdentifier = Require(transferIdentifier, nameof(transferIdentifier));
        ClearingIdentifier = clearingIdentifier?.Trim() ?? string.Empty;
        ReportedDepositAmount = reportedDepositAmount;
        ActualReceivedAmount = actualReceivedAmount;
        AllocatedAmount = allocatedAmount;
        InTransitAmount = inTransitAmount;
        ProactiveRefundAmount = proactiveRefundAmount;
        EmployerAccountRefundAmount = employerAccountRefundAmount;
        MoneyTreatmentStatus = moneyTreatmentStatus;
        StatusDetail = statusDetail?.Trim() ?? string.Empty;
        PaymentReference = paymentReference?.Trim() ?? string.Empty;
        ValueDate = valueDate;
        TrustAccountValueDate = trustAccountValueDate;
        CorrectnessTimestamp = correctnessTimestamp?.Trim() ?? string.Empty;
        ReceivedAt = receivedAt;
    }

    public Guid FeedbackId { get; private set; }
    public Guid ReportId { get; private set; }
    public string TransferIdentifier { get; private set; } = string.Empty;
    public string ClearingIdentifier { get; private set; } = string.Empty;
    public decimal ReportedDepositAmount { get; private set; }
    public decimal ActualReceivedAmount { get; private set; }
    public decimal AllocatedAmount { get; private set; }
    public decimal InTransitAmount { get; private set; }
    public decimal? ProactiveRefundAmount { get; private set; }
    public decimal? EmployerAccountRefundAmount { get; private set; }
    public int? MoneyTreatmentStatus { get; private set; }
    public string StatusDetail { get; private set; } = string.Empty;
    public string PaymentReference { get; private set; } = string.Empty;
    public DateOnly? ValueDate { get; private set; }
    public DateOnly? TrustAccountValueDate { get; private set; }
    public string CorrectnessTimestamp { get; private set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; private set; }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim().ToUpperInvariant();
}

public sealed class EmployerInterfaceContributionFeedback : Entity
{
    private EmployerInterfaceContributionFeedback() { }

    public EmployerInterfaceContributionFeedback(Guid feedbackId, Guid reportId, Guid reportProductId,
        Guid contributionId, string recordIdentifier, int sequence, int? intakeStatus, int? errorCode,
        string? errorDescription, decimal? errorAmount, DateOnly? errorDate, int? contributionTypeCode,
        decimal? calculatedSalary, DateOnly? salaryMonth, string? policyNumber, decimal? contributionRate,
        decimal? contributionAmount, string? sourceFileName, DateTimeOffset receivedAt)
    {
        FeedbackId = feedbackId;
        ReportId = reportId;
        ReportProductId = reportProductId;
        ContributionId = contributionId;
        RecordIdentifier = Require(recordIdentifier, nameof(recordIdentifier));
        Sequence = Math.Max(0, sequence);
        IntakeStatus = intakeStatus;
        ErrorCode = errorCode;
        ErrorDescription = errorDescription?.Trim() ?? string.Empty;
        ErrorAmount = errorAmount;
        ErrorDate = errorDate;
        ContributionTypeCode = contributionTypeCode;
        CalculatedSalary = calculatedSalary;
        SalaryMonth = salaryMonth;
        PolicyNumber = policyNumber?.Trim() ?? string.Empty;
        ContributionRate = contributionRate;
        ContributionAmount = contributionAmount;
        SourceFileName = sourceFileName?.Trim() ?? string.Empty;
        ReceivedAt = receivedAt;
    }

    public Guid FeedbackId { get; private set; }
    public Guid ReportId { get; private set; }
    public Guid ReportProductId { get; private set; }
    public Guid ContributionId { get; private set; }
    public string RecordIdentifier { get; private set; } = string.Empty;
    public int Sequence { get; private set; }
    public int? IntakeStatus { get; private set; }
    public int? ErrorCode { get; private set; }
    public string ErrorDescription { get; private set; } = string.Empty;
    public decimal? ErrorAmount { get; private set; }
    public DateOnly? ErrorDate { get; private set; }
    public int? ContributionTypeCode { get; private set; }
    public decimal? CalculatedSalary { get; private set; }
    public DateOnly? SalaryMonth { get; private set; }
    public string PolicyNumber { get; private set; } = string.Empty;
    public decimal? ContributionRate { get; private set; }
    public decimal? ContributionAmount { get; private set; }
    public string SourceFileName { get; private set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; private set; }

    private static string Require(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.", name) : value.Trim().ToUpperInvariant();
}

public sealed class ReportProductTreatment : Entity
{
    private ReportProductTreatment() { }

    public ReportProductTreatment(Guid reportProductId, string statusCode, string? note, Guid updatedByUserId)
    {
        ReportProductId = reportProductId;
        Update(statusCode, note, updatedByUserId);
    }

    public Guid ReportProductId { get; private set; }
    public string StatusCode { get; private set; } = string.Empty;
    public string Note { get; private set; } = string.Empty;
    public Guid UpdatedByUserId { get; private set; }

    public void Update(string statusCode, string? note, Guid updatedByUserId)
    {
        if (string.IsNullOrWhiteSpace(statusCode) || statusCode.Trim().Length > 80)
            throw new ArgumentException("Treatment status is required and limited to 80 characters.", nameof(statusCode));
        if ((note?.Length ?? 0) > 4000)
            throw new ArgumentOutOfRangeException(nameof(note), "Treatment note is limited to 4000 characters.");
        StatusCode = statusCode.Trim();
        Note = note?.Trim() ?? string.Empty;
        UpdatedByUserId = updatedByUserId;
        Touch();
    }
}

public sealed class ReportProductTreatmentHistory : Entity
{
    private ReportProductTreatmentHistory() { }

    public ReportProductTreatmentHistory(Guid reportProductId, string? previousStatusCode, string statusCode,
        string? note, Guid updatedByUserId)
    {
        if (string.IsNullOrWhiteSpace(statusCode) || statusCode.Trim().Length > 80)
            throw new ArgumentException("Treatment status is required.", nameof(statusCode));
        if ((note?.Length ?? 0) > 4000)
            throw new ArgumentOutOfRangeException(nameof(note), "Treatment note is limited to 4000 characters.");
        ReportProductId = reportProductId;
        PreviousStatusCode = previousStatusCode?.Trim() ?? string.Empty;
        StatusCode = statusCode.Trim();
        Note = note?.Trim() ?? string.Empty;
        UpdatedByUserId = updatedByUserId;
    }

    public Guid ReportProductId { get; private set; }
    public string PreviousStatusCode { get; private set; } = string.Empty;
    public string StatusCode { get; private set; } = string.Empty;
    public string Note { get; private set; } = string.Empty;
    public Guid UpdatedByUserId { get; private set; }
}
