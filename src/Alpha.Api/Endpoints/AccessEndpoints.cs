using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record AddAccessUserRequest(Guid UserId, OrganizationRole Role, EmployerAccessMode EmployerAccessMode);
public sealed record UpdateAccessUserRequest(OrganizationRole Role, EmployerAccessMode EmployerAccessMode);
public sealed record UpdateEmployerAccessRoleRequest(EmployerRole Role);

public static class AccessEndpoints
{
    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/access")
            .RequireAuthorization().WithTags("Access");

        group.MapGet("/users", async (Guid organizationId, string? search, int? skip, int? take,
            IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var safeTake = Math.Clamp(take ?? 30, 1, 50);
            var safeSkip = Math.Max(skip ?? 0, 0);
            var term = search?.Trim();

            var query = from membership in db.OrganizationMemberships.AsNoTracking()
                        join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
                        where membership.OrganizationId == organizationId && membership.IsActive
                        select new
                        {
                            MembershipId = membership.Id,
                            UserId = user.Id,
                            user.DisplayName,
                            user.Email,
                            user.IsActive,
                            membership.Role,
                            membership.EmployerAccessMode
                        };

            if (!string.IsNullOrWhiteSpace(term))
                query = query.Where(x => x.Email.StartsWith(term.ToLower()) || x.DisplayName.StartsWith(term));

            var rows = await query.OrderBy(x => x.DisplayName).ThenBy(x => x.UserId)
                .Skip(safeSkip).Take(safeTake + 1).ToListAsync(ct);
            return Results.Ok(new { items = rows.Take(safeTake), hasMore = rows.Count > safeTake });
        });

        group.MapGet("/user-candidates", async (Guid organizationId, string? search, int? take,
            IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var safeTake = Math.Clamp(take ?? 20, 1, 30);
            var term = search?.Trim();
            if (string.IsNullOrWhiteSpace(term) || term.Length < 2)
                return Results.Ok(Array.Empty<object>());

            var existingIds = db.OrganizationMemberships.Where(x => x.OrganizationId == organizationId && x.IsActive)
                .Select(x => x.UserId);
            var normalized = term.ToLower();
            var items = await db.Users.AsNoTracking()
                .Where(x => x.IsActive && !existingIds.Contains(x.Id) &&
                    (x.Email.StartsWith(normalized) || x.DisplayName.StartsWith(term)))
                .OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
                .Select(x => new { x.Id, x.DisplayName, x.Email })
                .Take(safeTake).ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapPost("/users", async (Guid organizationId, AddAccessUserRequest request,
            IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
            HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            if (!await db.Users.AnyAsync(x => x.Id == request.UserId && x.IsActive, ct))
                return Results.BadRequest(new { error = "User does not exist or is inactive." });

            var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.UserId == request.UserId, ct);
            if (membership is null)
            {
                membership = new OrganizationMembership(request.UserId, organizationId, request.Role,
                    request.EmployerAccessMode, currentUser.UserId);
                db.OrganizationMemberships.Add(membership);
            }
            else if (membership.IsActive)
            {
                return Results.Conflict(new { error = "User already has access to this organization." });
            }
            else
            {
                membership.Reactivate(request.Role, request.EmployerAccessMode);
            }

            db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "membership.created", nameof(OrganizationMembership),
                membership.Id, organizationId, null, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/organizations/{organizationId}/access/users/{request.UserId}", new { membership.Id });
        });

        group.MapPut("/users/{userId:guid}", async (Guid organizationId, Guid userId, UpdateAccessUserRequest request,
            IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
            HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.UserId == userId && x.IsActive, ct);
            if (membership is null) return Results.NotFound();

            membership.ChangeAccess(request.Role, request.EmployerAccessMode);
            if (request.EmployerAccessMode == EmployerAccessMode.AllEmployers)
                await db.EmployerUserAccesses.Where(x => x.OrganizationId == organizationId && x.UserId == userId)
                    .ExecuteDeleteAsync(ct);

            db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "membership.updated", nameof(OrganizationMembership),
                membership.Id, organizationId, null, JsonSerializer.Serialize(request), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/users/{userId:guid}", async (Guid organizationId, Guid userId,
            IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
            HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            if (currentUser.UserId == userId && !currentUser.IsPlatformAdmin)
                return Results.BadRequest(new { error = "You cannot remove your own organization access." });

            var membership = await db.OrganizationMemberships.SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.UserId == userId && x.IsActive, ct);
            if (membership is null) return Results.NotFound();
            membership.Deactivate();
            await db.EmployerUserAccesses.Where(x => x.OrganizationId == organizationId && x.UserId == userId)
                .ExecuteDeleteAsync(ct);
            db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "membership.removed", nameof(OrganizationMembership),
                membership.Id, organizationId, null, JsonSerializer.Serialize(new { userId }), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapGet("/users/{userId:guid}/employers", async (Guid organizationId, Guid userId, string? search,
            int? skip, int? take, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var safeTake = Math.Clamp(take ?? 30, 1, 50);
            var safeSkip = Math.Max(skip ?? 0, 0);
            var term = search?.Trim();
            var query = from grant in db.EmployerUserAccesses.AsNoTracking()
                        join employer in db.Employers.AsNoTracking() on grant.EmployerId equals employer.Id
                        where grant.OrganizationId == organizationId && grant.UserId == userId
                        select new { employer.Id, employer.LegalName, employer.RegistrationNumber, grant.Role };
            if (!string.IsNullOrWhiteSpace(term))
                query = query.Where(x => x.LegalName.StartsWith(term) || x.RegistrationNumber.StartsWith(term));
            var rows = await query.OrderBy(x => x.LegalName).ThenBy(x => x.Id)
                .Skip(safeSkip).Take(safeTake + 1).ToListAsync(ct);
            return Results.Ok(new { items = rows.Take(safeTake), hasMore = rows.Count > safeTake });
        });

        group.MapGet("/employer-options", async (Guid organizationId, Guid userId, string? search, int? take,
            IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var term = search?.Trim();
            if (string.IsNullOrWhiteSpace(term) || term.Length < 2)
                return Results.Ok(Array.Empty<object>());
            var safeTake = Math.Clamp(take ?? 20, 1, 25);

            var query = db.Employers.AsNoTracking().Where(x => x.OrganizationId == organizationId &&
                (x.LegalName.StartsWith(term) || x.RegistrationNumber.StartsWith(term) ||
                 x.WithholdingFileNumber.StartsWith(term)));
            var items = await query.OrderBy(x => x.LegalName).ThenBy(x => x.Id)
                .Select(x => new
                {
                    x.Id,
                    x.LegalName,
                    x.RegistrationNumber,
                    Assigned = db.EmployerUserAccesses.Any(g => g.OrganizationId == organizationId &&
                        g.UserId == userId && g.EmployerId == x.Id)
                })
                .Take(safeTake).ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapPost("/users/{userId:guid}/employers/{employerId:guid}", async (Guid organizationId, Guid userId,
            Guid employerId, IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
            HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var membership = await db.OrganizationMemberships.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.UserId == userId && x.IsActive, ct);
            if (membership is null) return Results.NotFound(new { error = "Membership was not found." });
            if (membership.EmployerAccessMode != EmployerAccessMode.SelectedEmployers)
                return Results.Conflict(new { error = "User already has access to every employer." });
            if (!await db.Employers.AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
                return Results.NotFound(new { error = "Employer was not found in this organization." });
            if (await db.EmployerUserAccesses.AnyAsync(x => x.OrganizationId == organizationId &&
                x.UserId == userId && x.EmployerId == employerId, ct)) return Results.NoContent();

            var grant = new EmployerUserAccess(userId, organizationId, employerId, EmployerRole.User);
            db.EmployerUserAccesses.Add(grant);
            db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "employer.access.granted", nameof(EmployerUserAccess),
                grant.Id, organizationId, employerId, JsonSerializer.Serialize(new { userId, employerId }), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapPut("/users/{userId:guid}/employers/{employerId:guid}/role", async (Guid organizationId, Guid userId,
            Guid employerId, UpdateEmployerAccessRoleRequest request, IAlphaDbContext db, ICurrentUser currentUser,
            OrganizationAccessService access, HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            if (!Enum.IsDefined(request.Role)) return Results.BadRequest(new { error = "Invalid employer role." });

            var grant = await db.EmployerUserAccesses.SingleOrDefaultAsync(x =>
                x.OrganizationId == organizationId && x.UserId == userId && x.EmployerId == employerId, ct);
            if (grant is null) return Results.NotFound();

            grant.ChangeRole(request.Role);
            db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "employer.access.role.updated", nameof(EmployerUserAccess),
                grant.Id, organizationId, employerId, JsonSerializer.Serialize(new { userId, employerId, request.Role }), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        group.MapDelete("/users/{userId:guid}/employers/{employerId:guid}", async (Guid organizationId, Guid userId,
            Guid employerId, IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
            HttpContext http, CancellationToken ct) =>
        {
            if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
            var grant = await db.EmployerUserAccesses.SingleOrDefaultAsync(x => x.OrganizationId == organizationId &&
                x.UserId == userId && x.EmployerId == employerId, ct);
            if (grant is null) return Results.NoContent();
            db.EmployerUserAccesses.Remove(grant);
            db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "employer.access.revoked", nameof(EmployerUserAccess),
                grant.Id, organizationId, employerId, JsonSerializer.Serialize(new { userId, employerId }), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return endpoints;
    }
}
