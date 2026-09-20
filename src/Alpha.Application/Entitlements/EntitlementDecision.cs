namespace Alpha.Application.Entitlements;

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
