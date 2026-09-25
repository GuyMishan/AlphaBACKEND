using Alpha.Api.Validation;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Employees;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class EmployerInterfaceProfileEndpoints
{
    public static IEndpointRouteBuilder MapEmployerInterfaceProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/employer-interface")
            .RequireAuthorization().WithTags("Employer Interface 006");

        group.MapGet("/profile", GetEmployerProfileAsync);
        group.MapPut("/profile", UpdateEmployerProfileAsync);
        group.MapGet("/employees/{employmentId:guid}/profile", GetEmployeeProfileAsync);
        group.MapPut("/employees/{employmentId:guid}/profile", UpdateEmployeeProfileAsync);
        group.MapGet("/reports/{reportId:guid}/products/{reportProductId:guid}/metadata", GetProductMetadataAsync);
        group.MapPut("/reports/{reportId:guid}/products/{reportProductId:guid}/metadata", UpdateProductMetadataAsync);
        return endpoints;
    }

    private static async Task<IResult> GetEmployerProfileAsync(Guid organizationId, Guid employerId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employer = await db.Employers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (employer is null) return Results.NotFound();
        return Results.Ok(new
        {
            employer.ContactFirstName,
            employer.ContactLastName,
            employer.ContactPhone,
            employer.ContactEmail,
            employer.ContactMobile
        });
    }

    private static async Task<IResult> UpdateEmployerProfileAsync(Guid organizationId, Guid employerId,
        EmployerInterfaceEmployerProfileRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employer = await db.Employers.SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (employer is null) return Results.NotFound();
        employer.UpdateInterfaceContact(request.ContactFirstName, request.ContactLastName, request.ContactPhone,
            request.ContactEmail, request.ContactMobile);
        var editableReports = await db.ManualReports
            .Where(x => x.EmployerId == employerId && x.OrganizationId == organizationId
                && (x.Status == ManualReportStatus.Draft || x.Status == ManualReportStatus.ReadyForValidation
                    || x.Status == ManualReportStatus.Validated || x.Status == ManualReportStatus.Error))
            .ToListAsync(ct);
        foreach (var report in editableReports) report.MarkDirty();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            employer.ContactFirstName,
            employer.ContactLastName,
            employer.ContactPhone,
            employer.ContactEmail,
            employer.ContactMobile
        });
    }

    private static async Task<IResult> GetEmployeeProfileAsync(Guid organizationId, Guid employerId, Guid employmentId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var item = await (from employment in db.Employments.AsNoTracking()
                          join person in db.People.AsNoTracking() on employment.PersonId equals person.Id
                          where employment.Id == employmentId && employment.OrganizationId == organizationId && employment.EmployerId == employerId
                          select new
                          {
                              person.BirthDate, person.Gender, person.Email, person.Mobile,
                              person.City, person.Street, person.HouseNumber, person.Apartment,
                              person.PostalCode, person.PostOfficeBox
                          }).SingleOrDefaultAsync(ct);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> UpdateEmployeeProfileAsync(Guid organizationId, Guid employerId, Guid employmentId,
        EmployerInterfaceEmployeeProfileRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employment = await db.Employments.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == employmentId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (employment is null) return Results.NotFound();
        var validationError = ApiInputValidation.EmployeeInterfaceProfile(request.BirthDate, request.Gender, request.Email,
            request.Mobile, request.City, request.Street, request.HouseNumber, request.PostalCode, request.PostOfficeBox);
        if (validationError is not null) return Results.BadRequest(new { error = validationError });
        var person = await db.People.SingleAsync(x => x.Id == employment.PersonId, ct);
        person.UpdateInterfaceDetails(request.BirthDate, request.Gender, request.Email, request.Mobile,
            request.City, request.Street, request.HouseNumber, request.Apartment, request.PostalCode, request.PostOfficeBox);
        // Existing reports keep their immutable Employer Interface snapshot. Updating the
        // employee master profile affects only reports created after this change.
        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            person.BirthDate, person.Gender, person.Email, person.Mobile,
            person.City, person.Street, person.HouseNumber, person.Apartment,
            person.PostalCode, person.PostOfficeBox
        });
    }

    private static async Task<IResult> GetProductMetadataAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportProductId, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var reportKind = await (from product in db.ManualReportProducts.AsNoTracking()
                                join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                                join report in db.ManualReports.AsNoTracking() on employee.ReportId equals report.Id
                                where product.Id == reportProductId && employee.ReportId == reportId
                                    && employee.OrganizationId == organizationId && employee.EmployerId == employerId
                                select (ManualReportKind?)report.ReportKind).SingleOrDefaultAsync(ct);
        if (reportKind is null) return Results.NotFound();
        var item = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        return Results.Ok(new
        {
            reportKind,
            operationCode = item?.OperationCode,
            depositStatus = item?.DepositStatus,
            employeeStatus = item?.EmployeeStatus,
            statusStartDate = item?.StatusStartDate,
            employmentPercentage = item?.EmploymentPercentage,
            workDaysInMonth = item?.WorkDaysInMonth,
            lastDeposit = item?.LastDeposit,
            refundReason = item?.RefundReason,
            paymentMethodCode = item?.PaymentMethodCode,
            employerAccountType = item?.EmployerAccountType,
            receiverAccountType = item?.ReceiverAccountType,
            oldPensionTypeCode = item?.OldPensionTypeCode,
            previousIdentifier = item?.PreviousIdentifier,
            previousClearingIdentifier = item?.PreviousClearingIdentifier,
            previousReferenceExceptionCode = item?.PreviousReferenceExceptionCode
        });
    }

    private static async Task<IResult> UpdateProductMetadataAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportProductId, EmployerInterfaceProductMetadataRequest request, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
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
        if (request.OperationCode.HasValue)
        {
            var allowed = reportKind == ManualReportKind.Negative
                ? request.OperationCode is 5 or 6
                : request.OperationCode is 1 or 2 or 3 or 7;
            if (!allowed)
                return Results.BadRequest(new { error = reportKind == ManualReportKind.Negative
                    ? "Negative Version 006 reports allow OperationCode 5 or 6 only."
                    : "Current Version 006 reports allow OperationCode 1, 2, 3 or 7 only." });
        }

        var item = await db.EmployerInterfaceReportProductData.SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        if (item is null)
        {
            item = new EmployerInterfaceReportProductData(reportProductId);
            db.EmployerInterfaceReportProductData.Add(item);
        }
        try
        {
            item.Update(request.OperationCode, request.DepositStatus, request.EmployeeStatus, request.StatusStartDate,
                request.EmploymentPercentage, request.WorkDaysInMonth, request.LastDeposit, request.RefundReason,
                request.PaymentMethodCode, request.EmployerAccountType, request.ReceiverAccountType,
                request.PreviousIdentifier, request.PreviousClearingIdentifier, request.PreviousReferenceExceptionCode,
                request.OldPensionTypeCode);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or ArgumentException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        report.MarkDirty();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new
        {
            reportKind,
            item.OperationCode,
            item.DepositStatus,
            item.EmployeeStatus,
            item.StatusStartDate,
            item.EmploymentPercentage,
            item.WorkDaysInMonth,
            item.LastDeposit,
            item.RefundReason,
            item.PaymentMethodCode,
            item.EmployerAccountType,
            item.ReceiverAccountType,
            item.OldPensionTypeCode,
            item.PreviousIdentifier,
            item.PreviousClearingIdentifier,
            item.PreviousReferenceExceptionCode
        });
    }
}

public sealed record EmployerInterfaceEmployerProfileRequest(string ContactFirstName, string ContactLastName,
    string ContactPhone, string ContactEmail, string ContactMobile);
public sealed record EmployerInterfaceEmployeeProfileRequest(DateOnly? BirthDate, PersonGender? Gender, string? Email, string? Mobile,
    string? City, string? Street, string? HouseNumber, string? Apartment, string? PostalCode, string? PostOfficeBox);
public sealed record EmployerInterfaceProductMetadataRequest(int? OperationCode, int? DepositStatus, int? EmployeeStatus, DateOnly? StatusStartDate,
    decimal? EmploymentPercentage, int? WorkDaysInMonth, int? LastDeposit, int? RefundReason,
    int? PaymentMethodCode, int? EmployerAccountType, int? ReceiverAccountType, int? OldPensionTypeCode = null,
    string? PreviousIdentifier = null, string? PreviousClearingIdentifier = null, int? PreviousReferenceExceptionCode = null);