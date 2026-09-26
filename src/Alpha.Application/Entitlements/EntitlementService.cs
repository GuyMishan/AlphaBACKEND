using Alpha.Application.Abstractions;
using Alpha.Domain.Billing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Entitlements;

public sealed class EntitlementService(IAlphaDbContext db)
{
    public const string ReportTransmissionFeature = "report_transmission";

    public async Task<int?> GetEmployerLimit(Guid organizationId, CancellationToken ct = default)
    {
        var billing = await GetOrganizationBillingTierAsync(organizationId, ct);
        return billing.IsPaid ? null : 1;
    }

    public async Task<int?> GetEmployeeLimit(Guid organizationId, CancellationToken ct = default)
    {
        var billing = await GetOrganizationBillingTierAsync(organizationId, ct);
        return billing.IsPaid ? null : 3;
    }

    public async Task<int> GetUserLimit(Guid organizationId, CancellationToken ct = default) =>
        (await GetPlanAsync(organizationId, ct)).MaxUsers;

    public async Task<EntitlementDecision> CanCreateEmployer(Guid organizationId, CancellationToken ct = default)
    {
        var limit = await GetEmployerLimit(organizationId, ct);
        if (!limit.HasValue) return EntitlementDecision.Allow();

        var current = await db.Employers.AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.Status != EmployerStatus.Closed, ct);
        return current < limit.Value
            ? EntitlementDecision.Allow()
            : EntitlementDecision.LimitReached("employers", current, limit.Value);
    }

    public async Task<EntitlementDecision> CanCreateEmployee(Guid organizationId, CancellationToken ct = default)
    {
        var limit = await GetEmployeeLimit(organizationId, ct);
        if (!limit.HasValue) return EntitlementDecision.Allow();

        var current = await db.Employments.AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId && x.Status == EmploymentStatus.Active, ct);
        return current < limit.Value
            ? EntitlementDecision.Allow()
            : EntitlementDecision.LimitReached("employees", current, limit.Value);
    }

    public async Task<EntitlementDecision> CanInviteNewUser(Guid organizationId, CancellationToken ct = default)
    {
        var plan = await GetPlanAsync(organizationId, ct);
        var membershipUserIds = db.OrganizationMemberships.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.IsActive &&
                        (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow))
            .Select(x => x.UserId);
        var employerUserIds = db.EmployerUserAccesses.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => x.UserId);
        var current = await membershipUserIds.Union(employerUserIds).CountAsync(ct);
        var pendingInvites = await db.UserInvitations.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.Status == Alpha.Domain.Identity.UserInvitationStatus.Pending &&
                        x.ExpiresAt > DateTimeOffset.UtcNow)
            .Select(x => x.Email)
            .Distinct()
            .CountAsync(ct);

        return current + pendingInvites < plan.MaxUsers
            ? EntitlementDecision.Allow()
            : EntitlementDecision.LimitReached("users", current + pendingInvites, plan.MaxUsers);
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
        var pendingInvites = await db.UserInvitations.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId &&
                        x.Status == Alpha.Domain.Identity.UserInvitationStatus.Pending &&
                        x.ExpiresAt > DateTimeOffset.UtcNow)
            .Select(x => x.Email)
            .Distinct()
            .CountAsync(ct);
        var reserved = current + pendingInvites;

        return reserved < plan.MaxUsers
            ? EntitlementDecision.Allow()
            : EntitlementDecision.LimitReached("users", reserved, plan.MaxUsers);
    }

    public async Task<EntitlementDecision> CanAcceptInvitation(Guid organizationId, CancellationToken ct = default)
    {
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
        var legacyPlan = await GetPlanAsync(organizationId, ct);
        var billing = await GetOrganizationBillingTierAsync(organizationId, ct);
        var employerLimit = billing.IsPaid ? (int?)null : 1;
        var employeeLimit = billing.IsPaid ? (int?)null : 3;
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
            plan = new
            {
                id = legacyPlan.Id,
                code = billing.Code,
                name = billing.Name
            },
            employers = new { current = employers, maximum = employerLimit },
            activeEmployees = new { current = employees, maximum = employeeLimit },
            users = new { current = users, maximum = legacyPlan.MaxUsers }
        };
    }

    private async Task<(bool IsPaid, string Code, string Name)> GetOrganizationBillingTierAsync(
        Guid organizationId, CancellationToken ct)
    {
        var accountId = await db.BillingAccounts.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == null)
            .Select(x => (Guid?)x.Id)
            .SingleOrDefaultAsync(ct);

        if (!accountId.HasValue)
            return (false, "FREE", "Free");

        var component = await db.BillingAccountPricingComponents.AsNoTracking()
            .Where(x => x.BillingAccountId == accountId.Value &&
                        x.EffectiveTo == null &&
                        x.IsEnabled &&
                        (x.MetricType == BillingMetricType.Employee || x.MetricType == BillingMetricType.ReportRow) &&
                        x.UnitPrice > 0)
            .OrderBy(x => x.MetricType)
            .FirstOrDefaultAsync(ct);

        return component?.MetricType switch
        {
            BillingMetricType.Employee => (true, "PER_EMPLOYEE", "פר עובד"),
            BillingMetricType.ReportRow => (true, "PER_REPORT_ROW", "פר שורה"),
            _ => (false, "FREE", "Free")
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
