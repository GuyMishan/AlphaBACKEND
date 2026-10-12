namespace Alpha.Modules.Reporting.Contracts;

public sealed record BillableReportingItem(
    Guid ReportId,
    Guid EmployerId,
    bool IsCorrection);

public sealed record ReportingUsageCounts(
    int EmployeeCount,
    int ReportRowCount,
    int CorrectedRowCount);

public interface IReportingUsageFactsReader
{
    Task<IReadOnlyList<BillableReportingItem>> ListAcceptedReportsAsync(
        Guid? employerId,
        Guid? organizationId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        CancellationToken cancellationToken = default);

    Task<ReportingUsageCounts> CountAsync(
        IReadOnlyCollection<Guid> reportIds,
        CancellationToken cancellationToken = default);
}
