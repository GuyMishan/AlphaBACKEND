using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Entitlements;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record ChangeSubscriptionPlanRequest(Guid PlanId);
public sealed record ChangeSubscriptionStatusRequest(Alpha.Domain.Subscriptions.SubscriptionStatus Status);
public sealed record ChangeOrganizationStatusRequest(Alpha.Domain.Organizations.OrganizationStatus Status);

public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/organizations/{organizationId:guid}/entitlements", async (
            Guid organizationId,
            OrganizationAccessService access,
            EntitlementService entitlements,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessOrganizationScopeAsync(organizationId, ct)) return Results.Forbid();
            return Results.Ok(await entitlements.GetSnapshot(organizationId, ct));
        }).RequireAuthorization().WithTags("Subscriptions");

        endpoints.MapGet("/api/organizations/{organizationId:guid}/subscription", async (
            Guid organizationId,
            IAlphaDbContext db,
            OrganizationAccessService access,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessOrganizationScopeAsync(organizationId, ct)) return Results.Forbid();

            var result = await (
                from subscription in db.Subscriptions.AsNoTracking()
                join plan in db.Plans.AsNoTracking() on subscription.PlanId equals plan.Id
                where subscription.OrganizationId == organizationId
                select new
                {
                    subscription.Id,
                    subscription.OrganizationId,
                    subscription.PlanId,
                    subscription.Status,
                    subscription.StartedAt,
                    subscription.ExpiresAt,
                    plan.Code,
                    plan.Name,
                    plan.MaxEmployers,
                    plan.MaxEmployees,
                    plan.MaxUsers,
                    plan.IsActive
                }).SingleOrDefaultAsync(ct);

            return result is null ? Results.NotFound() : Results.Ok(result);
        }).RequireAuthorization().WithTags("Subscriptions");

        endpoints.MapGet("/api/organizations/{organizationId:guid}/subscription/plans", async (
            Guid organizationId,
            IAlphaDbContext db,
            OrganizationAccessService access,
            CancellationToken ct) =>
        {
            if (!await access.CanAccessOrganizationScopeAsync(organizationId, ct)) return Results.Forbid();

            var plans = await db.Plans.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.MaxEmployees).ToListAsync(ct);
            var ids = plans.Select(x => x.Id).ToArray();
            var pricing = await db.PlanPricingComponents.AsNoTracking()
                .Where(x => ids.Contains(x.PlanId) && x.EffectiveTo == null && x.IsEnabled)
                .ToListAsync(ct);

            return Results.Ok(plans.Select(plan => new
            {
                plan.Id,
                plan.Code,
                plan.Name,
                plan.MaxEmployers,
                plan.MaxEmployees,
                plan.MaxUsers,
                plan.IsActive,
                employeeUnitPrice = pricing.FirstOrDefault(x => x.PlanId == plan.Id && (int)x.MetricType == 3)?.UnitPrice,
                rowUnitPrice = pricing.FirstOrDefault(x => x.PlanId == plan.Id && (int)x.MetricType == 3)?.UnitPrice
            }));
        }).RequireAuthorization().WithTags("Subscriptions");

        endpoints.MapPut("/api/organizations/{organizationId:guid}/subscription/plan", async (
            Guid organizationId,
            ChangeSubscriptionPlanRequest request,
            IAlphaDbContext db,
            ICurrentUser currentUser,
            OrganizationAccessService access,
            HttpContext http,
            CancellationToken ct) =>
        {
            var canManage = await access.CanManageOrganizationAsync(organizationId, ct);
            if (!canManage)
            {
                var employerIds = await db.Employers.AsNoTracking()
                    .Where(x => x.OrganizationId == organizationId)
                    .Select(x => x.Id).Take(2).ToListAsync(ct);
                canManage = employerIds.Count == 1 && await access.CanManageEmployerAsync(organizationId, employerIds[0], ct);
            }
            if (!canManage) return Results.Forbid();

            var target = await db.Plans.SingleOrDefaultAsync(x => x.Id == request.PlanId && x.IsActive, ct);
            if (target is null) return Results.BadRequest(new { error = "plan_not_found_or_inactive" });

            var subscription = await db.Subscriptions.SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
            if (subscription is null) return Results.NotFound();
            if (subscription.PlanId == target.Id) return Results.Ok(new { unchanged = true });

            var employers = await db.Employers.AsNoTracking()
                .CountAsync(x => x.OrganizationId == organizationId && x.Status != EmployerStatus.Closed, ct);
            var activeEmployees = await db.Employments.AsNoTracking()
                .CountAsync(x => x.OrganizationId == organizationId && x.Status == EmploymentStatus.Active, ct);
            var membershipUserIds = db.OrganizationMemberships.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.IsActive &&
                            (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow))
                .Select(x => x.UserId);
            var employerUserIds = db.EmployerUserAccesses.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId).Select(x => x.UserId);
            var users = await membershipUserIds.Union(employerUserIds).CountAsync(ct);

            var violations = new List<object>();
            if (target.MaxEmployers >= 0 && employers > target.MaxEmployers)
                violations.Add(new { resource = "employers", current = employers, maximum = target.MaxEmployers });
            if (target.MaxEmployees >= 0 && activeEmployees > target.MaxEmployees)
                violations.Add(new { resource = "activeEmployees", current = activeEmployees, maximum = target.MaxEmployees });
            if (target.MaxUsers >= 0 && users > target.MaxUsers)
                violations.Add(new { resource = "users", current = users, maximum = target.MaxUsers });
            if (violations.Count > 0)
                return Results.Conflict(new { error = "plan_downgrade_limits_exceeded", violations, contactSupport = true });

            var previousPlanId = subscription.PlanId;
            subscription.ChangePlan(target.Id);
            db.AuditEvents.Add(new AuditEvent(
                currentUser.UserId, "subscription.plan.self_service_changed", "Subscription",
                subscription.Id, organizationId, null,
                JsonSerializer.Serialize(new { previousPlanId, planId = target.Id, target.Code }), http.TraceIdentifier));
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                subscription.Id, subscription.OrganizationId, subscription.PlanId, subscription.Status,
                subscription.StartedAt, subscription.ExpiresAt, target.Code, target.Name,
                target.MaxEmployers, target.MaxEmployees, target.MaxUsers, target.IsActive
            });
        }).RequireAuthorization().WithTags("Subscriptions");

        var platform = endpoints.MapGroup("/api/platform/subscriptions")
            .RequireAuthorization().WithTags("Platform Subscriptions");

        platform.MapGet("/plans", async (IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            return Results.Ok(await db.Plans.AsNoTracking()
                .OrderBy(x => x.Name)
                .Select(x => new
                {
                    x.Id,
                    x.Code,
                    x.Name,
                    x.MaxEmployers,
                    x.MaxEmployees,
                    x.MaxUsers,
                    x.IsActive
                }).ToListAsync(ct));
        });

        platform.MapGet("/", async (IAlphaDbContext db, ICurrentUser currentUser, CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();

            var items = await (
                from organization in db.Organizations.AsNoTracking()
                join subscription in db.Subscriptions.AsNoTracking() on organization.Id equals subscription.OrganizationId
                join plan in db.Plans.AsNoTracking() on subscription.PlanId equals plan.Id
                orderby organization.Name
                select new
                {
                    organizationId = organization.Id,
                    organizationName = organization.Name,
                    organizationStatus = organization.Status,
                    subscriptionId = subscription.Id,
                    subscription.Status,
                    subscription.StartedAt,
                    subscription.ExpiresAt,
                    planId = plan.Id,
                    plan.Code,
                    plan.Name,
                    plan.MaxEmployers,
                    plan.MaxEmployees,
                    plan.MaxUsers
                }).ToListAsync(ct);

            return Results.Ok(items);
        });

        platform.MapPut("/{organizationId:guid}/plan", async (
            Guid organizationId,
            ChangeSubscriptionPlanRequest request,
            IAlphaDbContext db,
            ICurrentUser currentUser,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();

            var plan = await db.Plans.SingleOrDefaultAsync(x => x.Id == request.PlanId && x.IsActive, ct);
            if (plan is null) return Results.BadRequest(new { error = "plan_not_found_or_inactive" });

            var subscription = await db.Subscriptions.SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
            if (subscription is null) return Results.NotFound();

            if (subscription.PlanId != plan.Id)
            {
                var previousPlanId = subscription.PlanId;
                subscription.ChangePlan(plan.Id);
                db.AuditEvents.Add(new AuditEvent(
                    currentUser.UserId,
                    "subscription.plan.changed",
                    "Subscription",
                    subscription.Id,
                    organizationId,
                    null,
                    JsonSerializer.Serialize(new { previousPlanId, planId = plan.Id, plan.Code }),
                    http.TraceIdentifier));
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new
            {
                subscription.Id,
                subscription.OrganizationId,
                subscription.PlanId,
                subscription.Status,
                subscription.StartedAt,
                subscription.ExpiresAt,
                plan.Code,
                plan.Name,
                plan.MaxEmployers,
                plan.MaxEmployees,
                plan.MaxUsers
            });
        });

        platform.MapPut("/{organizationId:guid}/status", async (
            Guid organizationId,
            ChangeSubscriptionStatusRequest request,
            IAlphaDbContext db,
            ICurrentUser currentUser,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            if (!Enum.IsDefined(request.Status)) return Results.BadRequest(new { error = "invalid_subscription_status" });

            var subscription = await db.Subscriptions.SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
            if (subscription is null) return Results.NotFound();

            var previousStatus = subscription.Status;
            if (previousStatus != request.Status)
            {
                subscription.ChangeStatus(request.Status);
                db.AuditEvents.Add(new AuditEvent(
                    currentUser.UserId,
                    "subscription.status.changed",
                    "Subscription",
                    subscription.Id,
                    organizationId,
                    null,
                    JsonSerializer.Serialize(new { previousStatus, status = request.Status }),
                    http.TraceIdentifier));
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new
            {
                subscription.Id,
                subscription.OrganizationId,
                subscription.PlanId,
                subscription.Status,
                subscription.StartedAt,
                subscription.ExpiresAt
            });
        });

        platform.MapPut("/{organizationId:guid}/organization-status", async (
            Guid organizationId,
            ChangeOrganizationStatusRequest request,
            IAlphaDbContext db,
            ICurrentUser currentUser,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!currentUser.IsPlatformAdmin) return Results.Forbid();
            if (!Enum.IsDefined(request.Status)) return Results.BadRequest(new { error = "invalid_organization_status" });
            var organization = await db.Organizations.SingleOrDefaultAsync(x => x.Id == organizationId, ct);
            if (organization is null) return Results.NotFound();
            var previousStatus = organization.Status;
            organization.ChangeStatus(request.Status);
            db.AuditEvents.Add(new AuditEvent(
                currentUser.UserId,
                "organization.status.changed",
                "Organization",
                organization.Id,
                organizationId,
                null,
                JsonSerializer.Serialize(new { previousStatus, status = request.Status }),
                http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        return endpoints;
    }
}
