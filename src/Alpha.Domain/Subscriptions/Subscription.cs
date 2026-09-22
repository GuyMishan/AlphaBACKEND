using Alpha.Domain.Common;

namespace Alpha.Domain.Subscriptions;

public enum SubscriptionStatus
{
    Active = 1,
    Suspended = 2,
    Expired = 3,
    Cancelled = 4,
    PastDue = 5
}

public sealed class Subscription : Entity
{
    private Subscription() { }

    public Subscription(Guid organizationId, Guid planId, DateTimeOffset? startedAt = null, DateTimeOffset? expiresAt = null)
    {
        if (organizationId == Guid.Empty) throw new ArgumentException("Organization is required.", nameof(organizationId));
        if (planId == Guid.Empty) throw new ArgumentException("Plan is required.", nameof(planId));
        OrganizationId = organizationId;
        PlanId = planId;
        StartedAt = startedAt ?? DateTimeOffset.UtcNow;
        ExpiresAt = expiresAt;
    }

    public Guid OrganizationId { get; private set; }
    public Guid PlanId { get; private set; }
    public SubscriptionStatus Status { get; private set; } = SubscriptionStatus.Active;
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }

    public void ChangeStatus(SubscriptionStatus status)
    {
        Status = status;
        Touch();
    }

    public void ChangePlan(Guid planId)
    {
        if (planId == Guid.Empty) throw new ArgumentException("Plan is required.", nameof(planId));
        PlanId = planId;
        Touch();
    }
}
