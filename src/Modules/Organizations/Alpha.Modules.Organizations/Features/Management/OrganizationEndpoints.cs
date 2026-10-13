using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.Billing.Contracts;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Modules.Organizations.Features.EmployerTransfers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Alpha.Modules.Organizations.Features.Management;

public static class OrganizationEndpoints
{
    public sealed record CreateOrganizationRequest(string Name, OrganizationType Type);
    public sealed record AddMembershipRequest(Guid UserId, OrganizationRole Role, EmployerAccessMode EmployerAccessMode);

    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations").RequireAuthorization().WithTags("Organizations");
        group.MapGet("/", async (AlphaDbContext db, ICurrentUser user, CancellationToken ct) =>
        {
            var query = db.Organizations.AsNoTracking();
            if (!user.IsPlatformAdmin)
            {
                var membershipIds = db.OrganizationMemberships.Where(x => x.UserId == user.UserId && x.IsActive &&
                    (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow)).Select(x => x.OrganizationId);
                var employerAccessIds = db.EmployerUserAccesses.Where(x => x.UserId == user.UserId).Select(x => x.OrganizationId);
                var isReferent = await db.Users.AsNoTracking()
                    .AnyAsync(x => x.Id == user.UserId && x.IsReferent && x.IsActive, ct);
                var referentOrgIds = db.ReferentOrganizationAssignments
                    .Where(x => x.UserId == user.UserId).Select(x => x.OrganizationId);
                var referentEmployerIds = db.ReferentEmployerAssignments
                    .Where(x => x.UserId == user.UserId).Select(x => x.EmployerId);
                var referentEmployerOrgIds = db.Employers
                    .Where(x => referentEmployerIds.Contains(x.Id)).Select(x => x.OrganizationId);
                query = query.Where(x => membershipIds.Contains(x.Id) || employerAccessIds.Contains(x.Id) ||
                    (isReferent && (referentOrgIds.Contains(x.Id) || referentEmployerOrgIds.Contains(x.Id))));
            }
            return Results.Ok(await query.OrderBy(x => x.Name).ToListAsync(ct));
        });

        group.MapPost("/", async (CreateOrganizationRequest request, AlphaDbContext db, ICurrentUser user,
            HttpContext http, CancellationToken ct) =>
        {
            if (!user.IsPlatformAdmin) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200 || !Enum.IsDefined(request.Type))
                return Results.BadRequest(new { error = "Organization name or type is invalid." });
            var organization = new Organization(request.Name, request.Type);
            organization.Activate();
            db.Organizations.Add(organization);
            db.AuditEvents.Add(new AuditEvent(user.UserId, "organization.created", nameof(Organization), organization.Id,
                organization.Id, null, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organization.Id}", organization);
        });

        group.MapGet("/{organizationId:guid}", async (Guid organizationId, AlphaDbContext db,
            IOrganizationAuthorization access, CancellationToken ct) =>
        {
            if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var item = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == organizationId, ct);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        group.MapGet("/{organizationId:guid}/capabilities", async (Guid organizationId,
            IOrganizationAuthorization access, CancellationToken ct) =>
        {
            if (!await access.CanAccessOrganizationScopeAsync(organizationId, ct)) return Results.Forbid();
            return Results.Ok(new
            {
                canManageOrganization = await access.CanManageOrganizationAsync(organizationId, ct),
                canCreateEmployer = await access.CanCreateEmployerAsync(organizationId, ct)
            });
        });

        group.MapGet("/{organizationId:guid}/entitlements", async (Guid organizationId,
            IOrganizationAuthorization access, IEntitlementService entitlements, CancellationToken ct) =>
        {
            if (!await access.CanAccessOrganizationScopeAsync(organizationId, ct)) return Results.Forbid();
            return Results.Ok(await entitlements.GetSnapshot(organizationId, ct));
        });

        group.MapPost("/{organizationId:guid}/memberships", async (Guid organizationId, AddMembershipRequest request,
            AlphaDbContext db, ICurrentUser user, IOrganizationAuthorization access, IEntitlementService entitlements, OrganizationEntitlementLock entitlementLock, HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            if (await db.Users.AnyAsync(x => x.Id == request.UserId && x.IsReferent, ct))
                return Results.Conflict(new { error = "referent_cannot_receive_customer_access" });
            if (!await db.Users.AnyAsync(x => x.Id == request.UserId && x.IsActive, ct))
                return Results.BadRequest(new { error = "user_not_found_or_inactive", detail = "User does not exist or is inactive." });
            if (await db.OrganizationMemberships.AnyAsync(x => x.UserId == request.UserId && x.OrganizationId == organizationId, ct))
                return Results.Conflict(new { error = "membership_exists", detail = "Membership already exists." });
            await using var entitlementLease = await entitlementLock.AcquireAsync(organizationId, ct);
            var entitlement = await entitlements.CanInviteUser(organizationId, request.UserId, ct);
            if (!entitlement.Allowed)
                return Results.Json(new { error = entitlement.Error, limit = entitlement.Limit, current = entitlement.Current, maximum = entitlement.Maximum }, statusCode: StatusCodes.Status409Conflict);
            var item = new OrganizationMembership(request.UserId, organizationId, request.Role,
                request.EmployerAccessMode, user.UserId);
            db.OrganizationMemberships.Add(item);
            db.AuditEvents.Add(new AuditEvent(user.UserId, "membership.created", nameof(OrganizationMembership), item.Id,
                organizationId, null, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            await entitlementLease.CommitAsync(ct);
            return Results.Created($"/api/organizations/{organizationId}/memberships/{item.Id}", item);
        });

        group.MapPost("/{organizationId:guid}/memberships/{userId:guid}/employers/{employerId:guid}", async (
            Guid organizationId, Guid userId, Guid employerId, AlphaDbContext db, ICurrentUser currentUser,
            IOrganizationAuthorization access, HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.UserId == userId && x.IsActive, ct);
            if (membership is null) return Results.NotFound(new { error = "membership_not_found", detail = "Active organization membership was not found." });
            if (membership.EmployerAccessMode != EmployerAccessMode.SelectedEmployers)
                return Results.Conflict(new { error = "membership_has_all_employers", detail = "Membership already grants access to every employer." });
            if (!await db.Employers.AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
                return Results.NotFound(new { error = "employer_not_found", detail = "Employer was not found in this organization." });
            if (await db.EmployerUserAccesses.AnyAsync(x => x.UserId == userId && x.EmployerId == employerId, ct))
                return Results.NoContent();

            if (await db.Users.AnyAsync(x => x.Id == userId && x.IsReferent, ct))
                return Results.Conflict(new { error = "referent_cannot_receive_customer_access" });
            var grant = new EmployerUserAccess(userId, organizationId, employerId, EmployerRole.User);
            db.EmployerUserAccesses.Add(grant);
            db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "employer.access.granted", nameof(EmployerUserAccess),
                grant.Id, organizationId, employerId, JsonSerializer.Serialize(new { userId, employerId }), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organizationId}/memberships/{userId}/employers/{employerId}", grant);
        });
        group.MapEmployerTransferEndpoints();
        return endpoints;
    }
}
