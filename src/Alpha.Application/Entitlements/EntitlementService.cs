using Alpha.Application.Abstractions;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Entitlements;

public sealed class EntitlementService(IAlphaDbContext db)
{
    public const string ReportTransmissionFeature = "report_transmission";

    public async Task<int> GetEmployerLimit(Guid organizationId, CancellationToken ct = default) =>
        (await GetPlanAsync(organizationId, ct)).MaxEmployers;

    public async Task<int> GetEmployeeLimit(Guid organizationId, CancellationToken ct = default) =>
        (await GetPlanAsync(organizationId, ct)).MaxEmployees;

    public async Task<int> GetUserLimit(Guid organizationId, CancellationToken ct = default) =>
        (await GetPlanAsync(organizationId, ct)).MaxUsers;

    public async Task<EntitlementDecision> CanCreateEmployer(Guid organizationId, CancellationToken ct = default)
    {
        var plan = await GetPlanAsync(organizationId, ct);
        var current = await db.Employers.AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.Status != EmployerStatus.Closed, ct);
        return current < plan.MaxEmployers
            ? EntitlementDecision.Allow()
            : EntitlementDecision.LimitReached("employers", current, plan.MaxEmployers);
    }

    public async Task<EntitlementDecision> CanCreateEmployee(Guid organizationId, CancellationToken ct = default)
    {
        var plan = await GetPlanAsync(organizationId, ct);
        var current = await db.Employments.AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.Status == EmploymentStatus.Active, ct);
        return current < plan.MaxEmployees
            ? EntitlementDecision.Allow()
            : EntitlementDecision.LimitReached("active_employees", current, plan.MaxEmployees);
    }

    public async Task<EntitlementDecision> CanInviteUser(Guid organizationId, Guid userId, CancellationToken ct = default)
    {
        var alreadyCounted = await db.OrganizationMemberships.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.UserId == userId && x.IsActive &&
                (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), ct)
            || await db.EmployerUserAccesses.AsNoTracking().AnyAsync(x =>
                x.OrganizationId == organizationId && x.UserId == userId, ct);
        if (alreadyCounted) return EntitlementDecision.Allow();

        var plan = await GetPlanAsync(organizationId, ct);
        var membershipUserIds = db.OrganizationMemberships.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive &&
                        (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow))
            .Select(x => x.UserId);
        var employerUserIds = db.EmployerUserAccesses.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => x.UserId);
        var current = await membershipUserIds.Union(employerUserIds).CountAsync(ct);

        return current < plan.MaxUsers
            ? EntitlementDecision.Allow()
            : EntitlementDecision.LimitReached("users", current, plan.MaxUsers);
    }

    public async Task<EntitlementDecision> CanUseFeature(Guid organizationId, string feature, CancellationToken ct = default)
    {
        var subscription = await db.Subscriptions.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
        if (subscription is null || subscription.Status != SubscriptionStatus.Active ||
            (subscription.ExpiresAt is not null && subscription.ExpiresAt <= DateTimeOffset.UtcNow))
            return EntitlementDecision.FeatureUnavailable(feature);

        // Stage 4 centralizes feature checks. All currently defined active plans include transmission.
        return feature == ReportTransmissionFeature
            ? EntitlementDecision.Allow()
            : EntitlementDecision.FeatureUnavailable(feature);
    }

    public Task<EntitlementDecision> CanTransmitReport(Guid organizationId, CancellationToken ct = default) =>
        CanUseFeature(organizationId, ReportTransmissionFeature, ct);

    public async Task<object> GetSnapshot(Guid organizationId, CancellationToken ct = default)
    {
        var plan = await GetPlanAsync(organizationId, ct);
        var employers = await db.Employers.AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.Status != EmployerStatus.Closed, ct);
        var employees = await db.Employments.AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.Status == EmploymentStatus.Active, ct);
        var membershipUserIds = db.OrganizationMemberships.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive &&
                        (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow))
            .Select(x => x.UserId);
        var employerUserIds = db.EmployerUserAccesses.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => x.UserId);
        var users = await membershipUserIds.Union(employerUserIds).CountAsync(ct);

        return new
        {
            plan = new { plan.Id, plan.Code, plan.Name },
            employers = new { current = employers, maximum = plan.MaxEmployers },
            activeEmployees = new { current = employees, maximum = plan.MaxEmployees },
            users = new { current = users, maximum = plan.MaxUsers }
        };
    }

    private async Task<Plan> GetPlanAsync(Guid organizationId, CancellationToken ct)
    {
        var plan = await (
            from subscription in db.Subscriptions.AsNoTracking()
            join item in db.Plans.AsNoTracking() on subscription.PlanId equals item.Id
            where subscription.OrganizationId == organizationId
            select item).SingleOrDefaultAsync(ct);

        return plan ?? throw new InvalidOperationException($"Organization {organizationId} does not have a subscription plan.");
    }
}
