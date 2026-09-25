using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class EmployerInterfacePreviousReferenceEndpoints
{
    public static IEndpointRouteBuilder MapEmployerInterfacePreviousReferenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/employer-interface")
            .RequireAuthorization().WithTags("Employer Interface 006");
        group.MapGet("/reports/{reportId:guid}/products/{reportProductId:guid}/previous-reference", GetAsync);
        group.MapPut("/reports/{reportId:guid}/products/{reportProductId:guid}/previous-reference", PutAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var exists = await (from product in db.ManualReportProducts.AsNoTracking()
                            join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                            where product.Id == reportProductId && employee.ReportId == reportId
                                && employee.OrganizationId == organizationId && employee.EmployerId == employerId
                            select product.Id).AnyAsync(ct);
        if (!exists) return Results.NotFound();
        var item = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        return Results.Ok(new
        {
            previousIdentifier = item?.PreviousIdentifier ?? string.Empty,
            previousClearingIdentifier = item?.PreviousClearingIdentifier ?? string.Empty,
            previousReferenceExceptionCode = item?.PreviousReferenceExceptionCode
        });
    }

    private static async Task<IResult> PutAsync(Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId,
        EmployerInterfacePreviousReferenceRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.SingleOrDefaultAsync(x => x.Id == reportId
            && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (!report.IsEditable) return Results.Conflict(new { error = "report_not_editable" });

        var belongsToReport = await (from product in db.ManualReportProducts.AsNoTracking()
                                     join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                                     where product.Id == reportProductId && employee.ReportId == reportId
                                         && employee.OrganizationId == organizationId && employee.EmployerId == employerId
                                     select product.Id).AnyAsync(ct);
        if (!belongsToReport) return Results.NotFound();
        var reportKind = report.ReportKind;

        var item = await db.EmployerInterfaceReportProductData.SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        if (item is null)
        {
            item = new EmployerInterfaceReportProductData(reportProductId);
            db.EmployerInterfaceReportProductData.Add(item);
        }
        try
        {
            item.Update(item.OperationCode, item.DepositStatus, item.EmployeeStatus, item.StatusStartDate,
                item.EmploymentPercentage, item.WorkDaysInMonth, item.LastDeposit, item.RefundReason,
                item.PaymentMethodCode, item.EmployerAccountType, item.ReceiverAccountType,
                request.PreviousIdentifier, request.PreviousClearingIdentifier, request.PreviousReferenceExceptionCode,
                item.OldPensionTypeCode);
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        report.MarkDirty();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            reportKind,
            item.PreviousIdentifier,
            item.PreviousClearingIdentifier,
            item.PreviousReferenceExceptionCode
        });
    }
}

public sealed record EmployerInterfacePreviousReferenceRequest(string? PreviousIdentifier,
    string? PreviousClearingIdentifier, int? PreviousReferenceExceptionCode);
