using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Reporting;
using Alpha.Modules.Reporting.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.Reporting.Application.Usage;

public sealed class ReportingUsageFactsReader(AlphaDbContext db) : IReportingUsageFactsReader
{
    public async Task<IReadOnlyList<BillableReportingItem>> ListAcceptedReportsAsync(
        Guid? employerId,
        Guid? organizationId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        CancellationToken cancellationToken = default)
    {
        var accepted = db.ReportTransmissions.AsNoTracking()
            .Where(item =>
                (item.Status == ReportTransmissionStatus.Sent
                    || item.Status == ReportTransmissionStatus.Accepted)
                && (item.SentAt ?? item.CompletedAt) >= periodStart
                && (item.SentAt ?? item.CompletedAt) < periodEnd);

        accepted = employerId.HasValue
            ? accepted.Where(item => item.EmployerId == employerId.Value)
            : accepted.Where(item => item.OrganizationId == organizationId);

        var reportIds = await accepted.Select(item => item.ReportId)
            .Distinct()
            .ToListAsync(cancellationToken);
        if (reportIds.Count == 0) return [];

        return await db.ManualReports.AsNoTracking()
            .Where(item => reportIds.Contains(item.Id))
            .Select(item => new BillableReportingItem(
                item.Id,
                item.EmployerId,
                item.ReportKind != ManualReportKind.Current))
            .ToListAsync(cancellationToken);
    }

    public async Task<ReportingUsageCounts> CountAsync(
        IReadOnlyCollection<Guid> reportIds,
        CancellationToken cancellationToken = default)
    {
        if (reportIds.Count == 0) return new ReportingUsageCounts(0, 0, 0);

        var ids = reportIds.ToArray();
        var employeeCount = await db.ManualReportEmployees.AsNoTracking()
            .Where(item => ids.Contains(item.ReportId))
            .Select(item => item.EmploymentId)
            .Distinct()
            .CountAsync(cancellationToken);
        var reportEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
            .Where(item => ids.Contains(item.ReportId))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var reportRowCount = await db.ManualReportProducts.AsNoTracking()
            .CountAsync(item => reportEmployeeIds.Contains(item.ReportEmployeeId), cancellationToken);

        var correctionReportIds = await db.ManualReports.AsNoTracking()
            .Where(item => ids.Contains(item.Id) && item.ReportKind != ManualReportKind.Current)
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var correctedRowCount = 0;
        if (correctionReportIds.Count > 0)
        {
            var correctionEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
                .Where(item => correctionReportIds.Contains(item.ReportId))
                .Select(item => item.Id)
                .ToListAsync(cancellationToken);
            correctedRowCount = await db.ManualReportProducts.AsNoTracking()
                .CountAsync(item => correctionEmployeeIds.Contains(item.ReportEmployeeId), cancellationToken);
        }

        return new ReportingUsageCounts(employeeCount, reportRowCount, correctedRowCount);
    }
}
