using Alpha.Domain.Reporting;

namespace Alpha.Api.Services;

/// <summary>
/// Feedback is active for the most recent attempt of EACH recipient route,
/// never merely the last attempt for the whole employer report.
/// </summary>
public static class ReportTransmissionFeedbackSelection
{
    public static IReadOnlySet<Guid> LatestAttemptIds(IEnumerable<ReportTransmission> transmissions) =>
        transmissions
            .OrderByDescending(transmission => transmission.AttemptNumber)
            .GroupBy(transmission => string.IsNullOrWhiteSpace(transmission.RoutingKey)
                ? "legacy" : transmission.RoutingKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Id)
            .ToHashSet();

    public static bool IsActive(
        Guid? feedbackTransmissionId,
        IReadOnlySet<Guid> activeTransmissionIds,
        bool hasAnyTransmission) =>
        hasAnyTransmission
            ? feedbackTransmissionId.HasValue
                && activeTransmissionIds.Contains(feedbackTransmissionId.Value)
            : feedbackTransmissionId is null;
}
