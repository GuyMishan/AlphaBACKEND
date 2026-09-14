using System.Text.Json;
using Alpha.Api.Contracts;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class EmployerEndpoints
{
    public static IEndpointRouteBuilder MapEmployerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers")
            .RequireAuthorization().WithTags("Employers");

        group.MapGet("/", async (Guid organizationId, IAlphaDbContext db, ICurrentUser user,
            OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var query = db.Employers.AsNoTracking().Where(x => x.OrganizationId == organizationId);
            if (!user.IsPlatformAdmin && (await access.GetMembershipAsync(organizationId, ct))?.EmployerAccessMode == EmployerAccessMode.SelectedEmployers)
            {
                var ids = db.EmployerUserAccesses.Where(x => x.UserId == user.UserId).Select(x => x.EmployerId);
                query = query.Where(x => ids.Contains(x.Id));
            }
            return Results.Ok(await query.OrderBy(x => x.LegalName).ToListAsync(ct));
        });

        group.MapPost("/", async (Guid organizationId, CreateEmployerRequest request, IAlphaDbContext db,
            ICurrentUser user, OrganizationAccessService access, HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var item = new Employer(organizationId, request.LegalName, request.RegistrationNumber, request.WithholdingFileNumber);
            db.Employers.Add(item);
            db.AuditEvents.Add(new AuditEvent(user.UserId, "employer.created", nameof(Employer), item.Id,
                organizationId, item.Id, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organizationId}/employers/{item.Id}", item);
        });

        group.MapGet("/{employerId:guid}", async (Guid organizationId, Guid employerId, IAlphaDbContext db,
            OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            var item = await db.Employers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == employerId && x.OrganizationId == organizationId, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapPut("/{employerId:guid}", async (Guid organizationId, Guid employerId,
            UpdateEmployerRequest request, IAlphaDbContext db, ICurrentUser user, OrganizationAccessService access,
            HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            var item = await db.Employers.SingleOrDefaultAsync(x =>
                x.Id == employerId && x.OrganizationId == organizationId, ct);
            if (item is null) return Results.NotFound();
            item.Update(request.LegalName, request.RegistrationNumber, request.WithholdingFileNumber);
            db.AuditEvents.Add(new AuditEvent(user.UserId, "employer.updated", nameof(Employer), item.Id,
                organizationId, item.Id, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Ok(item);
        });

        group.MapGet("/{employerId:guid}/employees", async (Guid organizationId, Guid employerId, IAlphaDbContext db,
            OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            var items = await (from employment in db.Employments.AsNoTracking()
                join person in db.People.AsNoTracking() on employment.PersonId equals person.Id
                where employment.OrganizationId == organizationId && employment.EmployerId == employerId
                orderby person.LastName, person.FirstName
                select new { employment.Id, employment.EmployeeNumber, employment.Status, employment.StartDate,
                    employment.EndDate, PersonId = person.Id, person.NationalId, person.FirstName, person.LastName })
                .ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapPost("/{employerId:guid}/employees", async (Guid organizationId, Guid employerId,
            CreateEmployeeRequest request, IAlphaDbContext db, ICurrentUser user, OrganizationAccessService access,
            HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            if (!await db.Employers.AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
                return Results.NotFound();
            var person = await db.People.SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.NationalId == request.NationalId, ct);
            if (person is null)
            {
                person = new Person(organizationId, request.NationalId, request.FirstName, request.LastName);
                db.People.Add(person);
            }
            if (await db.Employments.AnyAsync(x => x.EmployerId == employerId && x.EmployeeNumber == request.EmployeeNumber, ct))
                return Results.Conflict(new { error = "Employee number already exists for this employer." });
            var employment = new Employment(organizationId, employerId, person.Id, request.StartDate, request.EmployeeNumber);
            db.Employments.Add(employment);
            db.AuditEvents.Add(new AuditEvent(user.UserId, "employment.created", nameof(Employment), employment.Id,
                organizationId, employerId, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organizationId}/employers/{employerId}/employees/{employment.Id}",
                new { employment.Id, PersonId = person.Id });
        });

        group.MapGet("/{employerId:guid}/employees/{employmentId:guid}", async (Guid organizationId,
            Guid employerId, Guid employmentId, IAlphaDbContext db, OrganizationAccessService access,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            var item = await (from employment in db.Employments.AsNoTracking()
                join person in db.People.AsNoTracking() on employment.PersonId equals person.Id
                where employment.Id == employmentId && employment.OrganizationId == organizationId &&
                      employment.EmployerId == employerId
                select new { employment.Id, employment.EmployeeNumber, employment.Status, employment.StartDate,
                    employment.EndDate, PersonId = person.Id, person.NationalId, person.FirstName, person.LastName })
                .SingleOrDefaultAsync(ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapPut("/{employerId:guid}/employees/{employmentId:guid}", async (Guid organizationId,
            Guid employerId, Guid employmentId, UpdateEmployeeRequest request, IAlphaDbContext db,
            ICurrentUser user, OrganizationAccessService access, HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
            var employment = await db.Employments.SingleOrDefaultAsync(x => x.Id == employmentId &&
                x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
            if (employment is null) return Results.NotFound();
            var person = await db.People.SingleAsync(x => x.Id == employment.PersonId, ct);
            var duplicateNationalId = await db.People.AnyAsync(x => x.OrganizationId == organizationId &&
                x.NationalId == request.NationalId && x.Id != person.Id, ct);
            if (duplicateNationalId) return Results.Conflict(new { error = "National ID already exists in this organization." });
            var duplicateEmployeeNumber = await db.Employments.AnyAsync(x => x.EmployerId == employerId &&
                x.EmployeeNumber == request.EmployeeNumber && x.Id != employmentId, ct);
            if (duplicateEmployeeNumber) return Results.Conflict(new { error = "Employee number already exists for this employer." });
            person.Update(request.NationalId, request.FirstName, request.LastName);
            employment.Update(request.EmployeeNumber, request.StartDate);
            db.AuditEvents.Add(new AuditEvent(user.UserId, "employment.updated", nameof(Employment), employment.Id,
                organizationId, employerId, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { employment.Id, employment.EmployeeNumber, employment.Status, employment.StartDate,
                employment.EndDate, PersonId = person.Id, person.NationalId, person.FirstName, person.LastName });
        });
        return endpoints;
    }
}
