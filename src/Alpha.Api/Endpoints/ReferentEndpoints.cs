using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Domain.Auditing;
using Alpha.Domain.Identity;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record SaveReferentRequest(bool Enabled, Guid[] OrganizationIds, Guid[] EmployerIds);

public static class ReferentEndpoints
{
    public static IEndpointRouteBuilder MapReferentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/platform/referents")
            .RequireAuthorization().WithTags("Referents");

        group.MapGet("/", async (IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            var referents = await db.Users.AsNoTracking()
                .Where(x => x.IsReferent)
                .OrderBy(x => x.DisplayName)
                .Select(x => new { x.Id, x.DisplayName, x.Email, x.IsActive })
                .ToListAsync(ct);
            var userIds = referents.Select(x => x.Id).ToArray();
            var organizations = await db.ReferentOrganizationAssignments.AsNoTracking()
                .Where(x => userIds.Contains(x.UserId))
                .Select(x => new { x.UserId, x.OrganizationId }).ToListAsync(ct);
            var employers = await (
                from assignment in db.ReferentEmployerAssignments.AsNoTracking()
                join employer in db.Employers.AsNoTracking() on assignment.EmployerId equals employer.Id
                where userIds.Contains(assignment.UserId)
                select new { assignment.UserId, assignment.EmployerId, employer.OrganizationId }
            ).ToListAsync(ct);
            return Results.Ok(referents.Select(x => new
            {
                x.Id, x.DisplayName, x.Email, x.IsActive,
                organizationIds = organizations.Where(a => a.UserId == x.Id).Select(a => a.OrganizationId),
                employerIds = employers.Where(a => a.UserId == x.Id).Select(a => a.EmployerId)
            }));
        });

        group.MapPut("/{userId:guid}", async (
            Guid userId, SaveReferentRequest request, AlphaDbContext db,
            ICurrentUser currentUser, HttpContext http, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            if (request.OrganizationIds is null || request.EmployerIds is null ||
                request.OrganizationIds.Contains(Guid.Empty) || request.EmployerIds.Contains(Guid.Empty))
                return Results.BadRequest(new { error = "invalid_assignments", detail = "רשימת השיוכים אינה תקינה." });
            var organizationIds = request.OrganizationIds.Distinct().ToArray();
            var employerIds = request.EmployerIds.Distinct().ToArray();
            if (organizationIds.Length > 1000 || employerIds.Length > 5000)
                return Results.BadRequest(new { error = "too_many_assignments" });
            if (!request.Enabled && (organizationIds.Length != 0 || employerIds.Length != 0))
                return Results.BadRequest(new { error = "disabled_referent_cannot_have_assignments" });

            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({userId.ToString()}, 0))", ct);
            var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, ct);
            if (user is null) return Results.NotFound();
            if (request.Enabled && (!user.IsActive || user.IsPlatformAdmin))
                return Results.Conflict(new { error = "invalid_referent_user", detail = "יש לבחור משתמש פעיל שאינו אדמין מערכת." });
            if (request.Enabled)
            {
                var orgCount = await db.Organizations.AsNoTracking()
                    .CountAsync(x => organizationIds.Contains(x.Id), ct);
                var employerCount = await db.Employers.AsNoTracking()
                    .CountAsync(x => employerIds.Contains(x.Id), ct);
                if (orgCount != organizationIds.Length || employerCount != employerIds.Length)
                    return Results.BadRequest(new { error = "unknown_assignment", detail = "ארגון או מעסיק שנבחרו כבר אינם קיימים." });
            }

            var currentOrganizations = await db.ReferentOrganizationAssignments
                .Where(x => x.UserId == userId).ToListAsync(ct);
            var currentEmployers = await db.ReferentEmployerAssignments
                .Where(x => x.UserId == userId).ToListAsync(ct);
            db.ReferentOrganizationAssignments.RemoveRange(
                currentOrganizations.Where(x => !request.Enabled || !organizationIds.Contains(x.OrganizationId)));
            db.ReferentEmployerAssignments.RemoveRange(
                currentEmployers.Where(x => !request.Enabled || !employerIds.Contains(x.EmployerId)));
            if (request.Enabled)
            {
                db.ReferentOrganizationAssignments.AddRange(organizationIds
                    .Where(id => currentOrganizations.All(x => x.OrganizationId != id))
                    .Select(id => new ReferentOrganizationAssignment(userId, id)));
                db.ReferentEmployerAssignments.AddRange(employerIds
                    .Where(id => currentEmployers.All(x => x.EmployerId != id))
                    .Select(id => new ReferentEmployerAssignment(userId, id)));
            }
            user.SetReferent(request.Enabled);
            db.AuditEvents.Add(new AuditEvent(currentUser.UserId,
                request.Enabled ? "referent.assignments.updated" : "referent.disabled",
                nameof(User), userId, null, null,
                JsonSerializer.Serialize(new { userId, request.Enabled, organizationIds, employerIds }),
                http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { userId, enabled = user.IsReferent, organizationIds, employerIds });
        });

        return endpoints;
    }
}
