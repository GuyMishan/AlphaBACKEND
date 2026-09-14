using System.Text.Json;
using Alpha.Api.Contracts;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class OrganizationEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations").RequireAuthorization().WithTags("Organizations");
        group.MapGet("/", async (IAlphaDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var query = db.Organizations.AsNoTracking();
            if (!user.IsPlatformAdmin)
            {
                var ids = db.OrganizationMemberships.Where(x => x.UserId == user.UserId && x.IsActive &&
                    (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow)).Select(x => x.OrganizationId);
                query = query.Where(x => ids.Contains(x.Id));
            }
            return Results.Ok(await query.OrderBy(x => x.Name).ToListAsync(ct));
        });

        group.MapPost("/", async (CreateOrganizationRequest request, IAlphaDbContext db, ICurrentUser user,
            HttpContext http, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            var organization = new Organization(request.Name, request.Type);
            db.Organizations.Add(organization);
            db.AuditEvents.Add(new AuditEvent(user.UserId, "organization.created", nameof(Organization), organization.Id,
                organization.Id, null, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organization.Id}", organization);
        });

        group.MapGet("/{organizationId:guid}", async (Guid organizationId, IAlphaDbContext db,
            OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var item = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == organizationId, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapGet("/{organizationId:guid}/capabilities", async (Guid organizationId,
            OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
            return Results.Ok(new
            {
                canCreateEmployer = await access.CanCreateEmployerAsync(organizationId, ct)
            });
        });

        group.MapPost("/{organizationId:guid}/memberships", async (Guid organizationId, AddMembershipRequest request,
            IAlphaDbContext db, ICurrentUser user, OrganizationAccessService access, HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            if (!await db.Users.AnyAsync(x => x.Id == request.UserId && x.IsActive, ct))
                return Results.BadRequest(new { error = "User does not exist or is inactive." });
            if (await db.OrganizationMemberships.AnyAsync(x => x.UserId == request.UserId && x.OrganizationId == organizationId, ct))
                return Results.Conflict(new { error = "Membership already exists." });
            var item = new OrganizationMembership(request.UserId, organizationId, request.Role,
                request.EmployerAccessMode, user.UserId);
            db.OrganizationMemberships.Add(item);
            db.AuditEvents.Add(new AuditEvent(user.UserId, "membership.created", nameof(OrganizationMembership), item.Id,
                organizationId, null, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organizationId}/memberships/{item.Id}", item);
        });

        group.MapPost("/{organizationId:guid}/memberships/{userId:guid}/employers/{employerId:guid}", async (
            Guid organizationId, Guid userId, Guid employerId, IAlphaDbContext db, ICurrentUser currentUser,
            OrganizationAccessService access, HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.UserId == userId && x.IsActive, ct);
            if (membership is null) return Results.NotFound(new { error = "Active organization membership was not found." });
            if (membership.EmployerAccessMode != EmployerAccessMode.SelectedEmployers)
                return Results.Conflict(new { error = "Membership already grants access to every employer." });
            if (!await db.Employers.AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
                return Results.NotFound(new { error = "Employer was not found in this organization." });
            if (await db.EmployerUserAccesses.AnyAsync(x => x.UserId == userId && x.EmployerId == employerId, ct))
                return Results.NoContent();

            var grant = new EmployerUserAccess(userId, organizationId, employerId);
            db.EmployerUserAccesses.Add(grant);
            db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "employer.access.granted", nameof(EmployerUserAccess),
                grant.Id, organizationId, employerId, JsonSerializer.Serialize(new { userId, employerId }), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organizationId}/memberships/{userId}/employers/{employerId}", grant);
        });
        return endpoints;
    }
}
