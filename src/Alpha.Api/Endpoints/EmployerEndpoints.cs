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
        return endpoints;
    }
}
