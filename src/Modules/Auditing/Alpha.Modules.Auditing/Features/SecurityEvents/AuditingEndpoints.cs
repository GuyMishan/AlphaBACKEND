using Alpha.Infrastructure.Persistence;
using Alpha.Modules.IdentityAccess.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.Auditing.Features.SecurityEvents;

public static class AuditingEndpoints
{
    public static RouteGroupBuilder MapAuditingEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/security/events", async (AlphaDbContext db, ICurrentUser currentUser, int? take, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            var limit = Math.Clamp(take ?? 100, 1, 500);
            var events = await db.AuditEvents.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(limit)
                .Select(x => new { x.Id, x.CreatedAt, x.ActorUserId, x.Action, x.EntityType, x.EntityId, x.OrganizationId, x.EmployerId, x.CorrelationId, x.Data }).ToListAsync(ct);
            return Results.Ok(new { auditLogging = true, rateLimiting = true, securityHeaders = true, retention = true, items = events });
        });

        return group;
    }
}
