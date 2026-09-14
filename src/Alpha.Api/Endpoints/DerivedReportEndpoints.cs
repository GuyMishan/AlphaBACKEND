using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class DerivedReportEndpoints
{
    private const int MaxSourceEmployees = 5000;

    public static IEndpointRouteBuilder MapDerivedReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/manual-reports")
            .RequireAuthorization().WithTags("Manual reporting");

        group.MapGet("/source-reports", GetSourceReportsAsync);
        group.MapPost("/derived", CreateDerivedReportAsync);
        group.MapGet("/{reportId:guid}/metadata", GetMetadataAsync);
        return endpoints;
    }

    private static async Task<IResult> GetSourceReportsAsync(Guid organizationId, Guid employerId,
        string? search, int skip, int take, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        skip = Math.Max(0, skip);
        take = Math.Clamp(take == 0 ? 30 : take, 1, 100);

        var query = db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.Status != ManualReportStatus.Cancelled);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (DateOnly.TryParse(term, out var parsed))
            {
                var month = new DateOnly(parsed.Year, parsed.Month, 1);
                query = query.Where(x => x.ReportingMonth == month);
            }
        }

        var page = await query.OrderByDescending(x => x.ReportingMonth).ThenByDescending(x => x.UpdatedAt)
            .Skip(skip).Take(take + 1).ToListAsync(ct);
        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);

        var ids = page.Select(x => x.Id).ToArray();
        var employeeCounts = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => ids.Contains(x.ReportId))
            .GroupBy(x => x.ReportId)
            .Select(g => new { ReportId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ReportId, x => x.Count, ct);

        var reportEmployees = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => ids.Contains(x.ReportId)).Select(x => new { x.Id, x.ReportId }).ToListAsync(ct);
        var employeeToReport = reportEmployees.ToDictionary(x => x.Id, x => x.ReportId);
        var employeeIds = employeeToReport.Keys.ToArray();
        var productCountsByEmployee = await db.ManualReportProducts.AsNoTracking()
            .Where(x => employeeIds.Contains(x.ReportEmployeeId))
            .GroupBy(x => x.ReportEmployeeId)
            .Select(g => new { EmployeeId = g.Key, Count = g.Count() }).ToListAsync(ct);
        var productCounts = productCountsByEmployee.GroupBy(x => employeeToReport[x.EmployeeId])
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));

        return Results.Ok(new
        {
            items = page.Select(x => new
            {
                x.Id, x.ReportingMonth, x.SalaryPaymentDate, x.Status, x.ReportKind, x.SourceReportId,
                x.CreatedAt, x.UpdatedAt,
                employeeCount = employeeCounts.GetValueOrDefault(x.Id),
                productCount = productCounts.GetValueOrDefault(x.Id)
            }),
            hasMore
        });
    }

    private static async Task<IResult> GetMetadataAsync(Guid organizationId, Guid employerId, Guid reportId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId &&
            x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        return report is null ? Results.NotFound() : Results.Ok(new
        {
            report.Id, report.ReportingMonth, report.SalaryPaymentDate, report.Status, report.ReportKind, report.SourceReportId
        });
    }

    private static async Task<IResult> CreateDerivedReportAsync(Guid organizationId, Guid employerId,
        CreateDerivedManualReportRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.ReportKind is not (ManualReportKind.Differences or ManualReportKind.Negative))
            return Results.BadRequest(new { error = "Derived reports must be Differences or Negative." });

        var source = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.SourceReportId &&
            x.OrganizationId == organizationId && x.EmployerId == employerId && x.Status != ManualReportStatus.Cancelled, ct);
        if (source is null) return Results.BadRequest(new { error = "Source report was not found for this employer." });

        var sourceEmployees = await db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == source.Id)
            .OrderBy(x => x.Id).ToListAsync(ct);
        if (sourceEmployees.Count > MaxSourceEmployees)
            return Results.BadRequest(new { error = $"Source reports are limited to {MaxSourceEmployees} employees for derived drafts." });

        var sourceEmployeeIds = sourceEmployees.Select(x => x.Id).ToArray();
        var sourceProducts = await db.ManualReportProducts.AsNoTracking()
            .Where(x => sourceEmployeeIds.Contains(x.ReportEmployeeId)).OrderBy(x => x.Id).ToListAsync(ct);
        var sourceProductIds = sourceProducts.Select(x => x.Id).ToArray();
        var sourceContributions = await db.ManualContributions.AsNoTracking()
            .Where(x => sourceProductIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var sourcePayments = await db.ManualReportPayments.AsNoTracking()
            .Where(x => sourceProductIds.Contains(x.ReportProductId)).ToListAsync(ct);

        var report = new ManualReport(organizationId, employerId, request.ReportingMonth, request.SalaryPaymentDate,
            request.ReportKind, source.Id);
        db.ManualReports.Add(report);

        var employeeMap = new Dictionary<Guid, ManualReportEmployee>();
        foreach (var oldEmployee in sourceEmployees)
        {
            var clone = new ManualReportEmployee(report.Id, organizationId, employerId, oldEmployee.EmploymentId,
                oldEmployee.PersonId, oldEmployee.NationalId, oldEmployee.FirstName, oldEmployee.LastName, oldEmployee.EmployeeNumber);
            db.ManualReportEmployees.Add(clone);
            employeeMap[oldEmployee.Id] = clone;
        }

        var reportingType = request.ReportKind == ManualReportKind.Negative ? "שלילי" : "הפרשים";
        var productMap = new Dictionary<Guid, ManualReportProduct>();
        foreach (var oldProduct in sourceProducts)
        {
            var clone = new ManualReportProduct(employeeMap[oldProduct.ReportEmployeeId].Id, oldProduct.ProductType,
                oldProduct.PolicyNumber, oldProduct.SalaryMonth, oldProduct.Salary, reportingType, oldProduct.SalaryLayer,
                oldProduct.Section14, oldProduct.Section14StartDate);
            db.ManualReportProducts.Add(clone);
            productMap[oldProduct.Id] = clone;
        }

        foreach (var oldContribution in sourceContributions)
        {
            var clone = new ManualContribution(productMap[oldContribution.ReportProductId].Id, oldContribution.Party,
                oldContribution.Component, oldContribution.Amount, oldContribution.Percentage, oldContribution.ExemptPayments);
            db.ManualContributions.Add(clone);
        }

        foreach (var oldPayment in sourcePayments)
        {
            var clone = new ManualReportPayment(productMap[oldPayment.ReportProductId].Id);
            clone.Update(oldPayment.ProviderName, oldPayment.ProviderAccount, oldPayment.PaymentMethod, oldPayment.ValueDate,
                oldPayment.ReferenceNumber, oldPayment.EmployerBankName, oldPayment.EmployerBankCode, oldPayment.EmployerBranch,
                oldPayment.EmployerAccount, oldPayment.ConfirmationFileName);
            db.ManualReportPayments.Add(clone);
        }

        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/organizations/{organizationId}/employers/{employerId}/manual-reports/{report.Id}", new
        {
            report.Id, report.ReportingMonth, report.SalaryPaymentDate, report.Status, report.ReportKind, report.SourceReportId,
            employeeCount = sourceEmployees.Count
        });
    }
}

public sealed record CreateDerivedManualReportRequest(Guid SourceReportId, ManualReportKind ReportKind,
    DateOnly ReportingMonth, DateOnly? SalaryPaymentDate);
