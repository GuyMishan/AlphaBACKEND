using Alpha.Domain.Common;

namespace Alpha.Domain.Reporting;

public sealed class EmployerInterfaceReportProductData : Entity
{
    private EmployerInterfaceReportProductData() { }

    public EmployerInterfaceReportProductData(Guid reportProductId)
    {
        ReportProductId = reportProductId;
    }

    public Guid ReportProductId { get; private set; }
    public int? DepositStatus { get; private set; }
    public int? EmployeeStatus { get; private set; }
    public DateOnly? StatusStartDate { get; private set; }
    public decimal? EmploymentPercentage { get; private set; }
    public int? WorkDaysInMonth { get; private set; }
    public int? LastDeposit { get; private set; }
    public int? RefundReason { get; private set; }
    public int? PaymentMethodCode { get; private set; }
    public int? EmployerAccountType { get; private set; }
    public int? ReceiverAccountType { get; private set; }

    public void Update(int? depositStatus, int? employeeStatus, DateOnly? statusStartDate,
        decimal? employmentPercentage, int? workDaysInMonth, int? lastDeposit, int? refundReason,
        int? paymentMethodCode, int? employerAccountType, int? receiverAccountType)
    {
        DepositStatus = Allowed(depositStatus, [1, 2, 3], nameof(depositStatus));
        EmployeeStatus = Allowed(employeeStatus, [1, 2, 3, 4, 5, 8, 9, 10, 11, 12, 14, 17, 18], nameof(employeeStatus));
        if (employmentPercentage is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(employmentPercentage));
        if (workDaysInMonth is < 0 or > 31) throw new ArgumentOutOfRangeException(nameof(workDaysInMonth));
        EmploymentPercentage = employmentPercentage;
        WorkDaysInMonth = workDaysInMonth;
        StatusStartDate = statusStartDate;
        LastDeposit = Allowed(lastDeposit, [1, 2], nameof(lastDeposit));
        RefundReason = Allowed(refundReason, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10], nameof(refundReason));
        PaymentMethodCode = Allowed(paymentMethodCode, [1, 3, 4, 5, 6, 7, 9], nameof(paymentMethodCode));
        EmployerAccountType = Allowed(employerAccountType, [1, 2], nameof(employerAccountType));
        ReceiverAccountType = Allowed(receiverAccountType, [1, 2], nameof(receiverAccountType));
        Touch();
    }

    private static int? Allowed(int? value, int[] allowed, string name)
    {
        if (value.HasValue && !allowed.Contains(value.Value))
            throw new ArgumentOutOfRangeException(name, $"Unsupported Employer Interface 006 code: {value}.");
        return value;
    }
}
