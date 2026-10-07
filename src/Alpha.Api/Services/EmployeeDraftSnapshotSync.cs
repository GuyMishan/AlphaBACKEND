using Alpha.Application.Abstractions;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

public static class EmployeeDraftSnapshotSync
{
    public static async Task SyncPostalAddressAsync(
        IAlphaDbContext db,
        Guid organizationId,
        Guid employerId,
        Guid employmentId,
        string previousPostalCode,
        string previousPostOfficeBox,
        string currentPostalCode,
        string currentPostOfficeBox,
        CancellationToken ct)
    {
        if (string.Equals(previousPostalCode, currentPostalCode, StringComparison.Ordinal)
            && string.Equals(previousPostOfficeBox, currentPostOfficeBox, StringComparison.Ordinal))
            return;

        var editableReports = await db.ManualReports
            .Where(report => report.OrganizationId == organizationId
                && report.EmployerId == employerId
                && (report.Status == ManualReportStatus.Draft
                    || report.Status == ManualReportStatus.ReadyForValidation
                    || report.Status == ManualReportStatus.Error))
            .ToListAsync(ct);
        if (editableReports.Count == 0) return;

        var reportIds = editableReports.Select(report => report.Id).ToArray();
        var matchingSnapshots = await db.ManualReportEmployees
            .Where(snapshot => reportIds.Contains(snapshot.ReportId)
                && snapshot.EmploymentId == employmentId
                && snapshot.OrganizationId == organizationId
                && snapshot.EmployerId == employerId
                && snapshot.PostalCodeSnapshot == previousPostalCode
                && snapshot.PostOfficeBoxSnapshot == previousPostOfficeBox)
            .ToListAsync(ct);

        foreach (var snapshot in matchingSnapshots)
            snapshot.UpdatePostalAddressSnapshot(currentPostalCode, currentPostOfficeBox);

        var touchedReportIds = matchingSnapshots.Select(snapshot => snapshot.ReportId).ToHashSet();
        foreach (var report in editableReports.Where(report => touchedReportIds.Contains(report.Id)))
            report.MarkDirty();
    }
}
