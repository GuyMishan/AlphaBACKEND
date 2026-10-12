namespace Alpha.Modules.Billing.Contracts;

public sealed record EntitlementDecision(
    bool Allowed,
    string? Error = null,
    string? Limit = null,
    int? Current = null,
    int? Maximum = null,
    string? Feature = null)
{
    public static EntitlementDecision Allow() => new(true);

    public static EntitlementDecision LimitReached(string limit, int current, int maximum) =>
        new(false, "plan_limit_reached", limit, current, maximum);

    public static EntitlementDecision FeatureUnavailable(string feature) =>
        new(false, "feature_not_available", Feature: feature);
}

public sealed record EntitlementUsage(int Current, int? Maximum);

public sealed record EntitlementPlan(string? Id, string Code, string Name);

public sealed record EntitlementSnapshot(
    EntitlementPlan Plan,
    EntitlementUsage Employers,
    EntitlementUsage ActiveEmployees,
    EntitlementUsage Users);

public interface IEntitlementService
{
    Task<int?> GetEmployerLimit(Guid organizationId, CancellationToken cancellationToken = default);
    Task<int?> GetEmployeeLimit(Guid organizationId, CancellationToken cancellationToken = default);
    Task<int?> GetUserLimit(Guid organizationId, CancellationToken cancellationToken = default);
    Task<EntitlementDecision> CanCreateEmployer(Guid organizationId, CancellationToken cancellationToken = default);
    Task<EntitlementDecision> CanCreateEmployee(Guid organizationId, CancellationToken cancellationToken = default);
    Task<EntitlementDecision> CanInviteNewUser(Guid organizationId, CancellationToken cancellationToken = default);
    Task<EntitlementDecision> CanInviteUser(Guid organizationId, Guid userId, CancellationToken cancellationToken = default);
    Task<EntitlementDecision> CanAcceptInvitation(Guid organizationId, CancellationToken cancellationToken = default);
    Task<EntitlementDecision> CanTransmitReport(Guid organizationId, CancellationToken cancellationToken = default);
    Task<EntitlementSnapshot> GetSnapshot(Guid organizationId, CancellationToken cancellationToken = default);
}
