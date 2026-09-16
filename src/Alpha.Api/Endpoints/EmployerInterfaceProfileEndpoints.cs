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

        group.MapPut("/profile", UpdateEmployerProfileAsync);
        group.MapPut("/employees/{employmentId:guid}/profile", UpdateEmployeeProfileAsync);
        group.MapPut("/reports/{reportId:guid}/products/{reportProductId:guid}/metadata", UpdateProductMetadataAsync);
        return endpoints;
    }

    private static async Task<IResult> UpdateEmployerProfileAsync(Guid organizationId, Guid employerId,
        EmployerInterfaceEmployerProfileRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employer = await db.Employers.SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (employer is null) return Results.NotFound();
        employer.Update(employer.LegalName, employer.RegistrationNumber, employer.WithholdingFileNumber,
            request.ContactFirstName, request.ContactLastName, request.ContactPhone, request.ContactEmail, request.ContactMobile);
        await db.SaveChangesAsync(ct);
        return Results.Ok(employer);
    }

    private static async Task<IResult> UpdateEmployeeProfileAsync(Guid organizationId, Guid employerId, Guid employmentId,
        EmployerInterfaceEmployeeProfileRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employment = await db.Employments.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == employmentId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (employment is null) return Results.NotFound();
        var person = await db.People.SingleAsync(x => x.Id == employment.PersonId, ct);
        person.Update(person.NationalId, person.FirstName, person.LastName, request.BirthDate, request.Gender, request.Email, request.Mobile);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { person.Id, person.BirthDate, person.Gender, person.Email, person.Mobile });
    }

    private static async Task<IResult> UpdateProductMetadataAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportProductId, EmployerInterfaceProductMetadataRequest request, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var exists = await (from product in db.ManualReportProducts.AsNoTracking()
                            join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                            where product.Id == reportProductId && employee.ReportId == reportId
                                && employee.OrganizationId == organizationId && employee.EmployerId == employerId
                            select product.Id).AnyAsync(ct);
        if (!exists) return Results.NotFound();

        var item = await db.EmployerInterfaceReportProductData.SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        if (item is null)
        {
            item = new EmployerInterfaceReportProductData(reportProductId);
            db.EmployerInterfaceReportProductData.Add(item);
        }
        try
        {
            item.Update(request.DepositStatus, request.EmployeeStatus, request.StatusStartDate,
                request.EmploymentPercentage, request.WorkDaysInMonth, request.LastDeposit, request.RefundReason,
                request.PaymentMethodCode, request.EmployerAccountType, request.ReceiverAccountType);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        await db.SaveChangesAsync(ct);
        return Results.Ok(item);
    }
}

public sealed record EmployerInterfaceEmployerProfileRequest(string ContactFirstName, string ContactLastName,
    string ContactPhone, string ContactEmail, string ContactMobile);
public sealed record EmployerInterfaceEmployeeProfileRequest(DateOnly? BirthDate, PersonGender? Gender, string? Email, string? Mobile);
public sealed record EmployerInterfaceProductMetadataRequest(int? DepositStatus, int? EmployeeStatus, DateOnly? StatusStartDate,
    decimal? EmploymentPercentage, int? WorkDaysInMonth, int? LastDeposit, int? RefundReason,
    int? PaymentMethodCode, int? EmployerAccountType, int? ReceiverAccountType);
