using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class ReportFeedbackEndpoints
{
    public static IEndpointRouteBuilder MapReportFeedbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/report-feedback")
            .RequireAuthorization()
            .WithTags("Report feedback");

        group.MapGet("/", ListAsync);
        group.MapGet("/{reportId:guid}", DetailsAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId,
        Guid employerId,
        string? feedbackStatus,
        int skip,
        int take,
        IAlphaDbContext db,
        OrganizationAccessService access,
        CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        skip = Math.Max(skip, 0);
        take = Math.Clamp(take == 0 ? 50 : take, 1, 100);

        var reports = await db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId)
            .OrderByDescending(x => x.UpdatedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        var reportIds = reports.Select(x => x.Id).ToArray();
        var transmissions = await db.ReportTransmissions.AsNoTracking()
            .Where(x => reportIds.Contains(x.ReportId))
            .OrderByDescending(x => x.AttemptNumber)
            .ToListAsync(ct);
        var latestByReport = transmissions
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.First());

        var employeeCounts = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => reportIds.Contains(x.ReportId))
            .GroupBy(x => x.ReportId)
            .Select(g => new { ReportId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ReportId, x => x.Count, ct);

        var errorEmployeeCounts = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => reportIds.Contains(x.ReportId) && x.ValidationStatus == ManualReportItemStatus.Error)
            .GroupBy(x => x.ReportId)
            .Select(g => new { ReportId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ReportId, x => x.Count, ct);

        var rows = reports.Select(report =>
        {
            latestByReport.TryGetValue(report.Id, out var transmission);
            var state = FeedbackState(report, transmission);
            var issueCount = (string.IsNullOrWhiteSpace(report.ValidationError) ? 0 : 1)
                + (transmission is null || string.IsNullOrWhiteSpace(transmission.ErrorMessage) ? 0 : 1)
                + errorEmployeeCounts.GetValueOrDefault(report.Id);
            return new
            {
                report.Id,
                report.ReportingMonth,
                report.SalaryPaymentDate,
                report.ReportKind,
                report.Status,
                feedbackStatus = state,
                hasFeedback = transmission is not null && transmission.Status is ReportTransmissionStatus.Accepted or ReportTransmissionStatus.Rejected or ReportTransmissionStatus.Error or ReportTransmissionStatus.Sent,
                issueCount,
                employeeCount = employeeCounts.GetValueOrDefault(report.Id),
                report.CreatedAt,
                report.UpdatedAt,
                lastTransmission = transmission is null ? null : new
                {
                    transmission.Id,
                    transmission.AttemptNumber,
                    transmission.Status,
                    transmission.Provider,
                    transmission.ExternalId,
                    transmission.ErrorMessage,
                    transmission.StartedAt,
                    transmission.SentAt,
                    transmission.CompletedAt
                }
            };
        }).ToList();

        var normalized = feedbackStatus?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalized) && normalized != "all")
            rows = rows.Where(x => string.Equals(x.feedbackStatus, normalized, StringComparison.OrdinalIgnoreCase)).ToList();

        var page = rows.Skip(skip).Take(take + 1).ToList();
        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);
        return Results.Ok(new { items = page, hasMore });
    }

    private static async Task<IResult> DetailsAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        IAlphaDbContext db,
        OrganizationAccessService access,
        CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        var transmissions = await db.ReportTransmissions.AsNoTracking()
            .Where(x => x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId)
            .OrderByDescending(x => x.AttemptNumber)
            .ToListAsync(ct);
        var latest = transmissions.FirstOrDefault();

        var employees = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == reportId)
            .ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking()
            .Where(x => employeeIds.Contains(x.ReportEmployeeId))
            .ToListAsync(ct);
        var employeesById = employees.ToDictionary(x => x.Id);

        var issues = new List<object>();
        if (!string.IsNullOrWhiteSpace(report.ValidationError))
            issues.Add(new { source = "report", code = "REPORT_VALIDATION", description = report.ValidationError, employeeId = (Guid?)null, employeeName = (string?)null, productId = (Guid?)null, productName = (string?)null, actionType = "EditReport" });
        if (latest is not null && !string.IsNullOrWhiteSpace(latest.ErrorMessage))
            issues.Add(new { source = "transmission", code = latest.Status.ToString().ToUpperInvariant(), description = latest.ErrorMessage, employeeId = (Guid?)null, employeeName = (string?)null, productId = (Guid?)null, productName = (string?)null, actionType = "RetryTransmission" });

        foreach (var employee in employees.Where(x => x.ValidationStatus == ManualReportItemStatus.Error && !string.IsNullOrWhiteSpace(x.ValidationError)))
            issues.Add(new { source = "employee", code = "EMPLOYEE_VALIDATION", description = employee.ValidationError, employeeId = (Guid?)employee.EmploymentId, employeeName = $"{employee.FirstName} {employee.LastName}", productId = (Guid?)null, productName = (string?)null, actionType = "EditReport" });

        foreach (var product in products.Where(x => x.ValidationStatus == ManualReportItemStatus.Error && !string.IsNullOrWhiteSpace(x.ValidationError)))
        {
            employeesById.TryGetValue(product.ReportEmployeeId, out var employee);
            issues.Add(new { source = "product", code = "PRODUCT_VALIDATION", description = product.ValidationError, employeeId = employee?.EmploymentId, employeeName = employee is null ? null : $"{employee.FirstName} {employee.LastName}", productId = (Guid?)product.Id, productName = string.IsNullOrWhiteSpace(product.FundName) ? product.PolicyNumber : product.FundName, actionType = "EditReport" });
        }

        return Results.Ok(new
        {
            report = new { report.Id, report.ReportingMonth, report.SalaryPaymentDate, report.ReportKind, report.Status, report.ValidationError, report.CreatedAt, report.UpdatedAt },
            feedbackStatus = FeedbackState(report, latest),
            issueCount = issues.Count,
            issues,
            transmissions = transmissions.Select(x => new { x.Id, x.AttemptNumber, x.Status, x.Provider, x.ExternalId, x.ErrorMessage, x.StartedAt, x.SentAt, x.CompletedAt, x.CreatedAt })
        });
    }

    private static string FeedbackState(ManualReport report, ReportTransmission? transmission)
    {
        if (transmission is null)
            return report.Status == ManualReportStatus.Error ? "error" : "not-sent";
        return transmission.Status switch
        {
            ReportTransmissionStatus.Accepted => "success",
            ReportTransmissionStatus.Rejected or ReportTransmissionStatus.Error => "error",
            ReportTransmissionStatus.Sent => "pending",
            _ => "pending"
        };
    }
}
