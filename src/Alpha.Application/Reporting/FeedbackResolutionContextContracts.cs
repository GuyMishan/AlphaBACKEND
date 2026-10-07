namespace Alpha.Application.Reporting;

public sealed record FeedbackResolutionContextResponse(
    string ContextType,
    Guid EmployerId,
    Guid? ReportId,
    Guid? ReportProductId,
    bool CanResolve,
    IReadOnlyList<int> UnsupportedCodes,
    IReadOnlyList<FeedbackResolutionProblemDto> Problems);

public sealed record FeedbackResolutionProblemDto(
    string ProblemId,
    int Code,
    string Description,
    string Scope,
    string ResolutionType,
    string Family,
    string ResolverType,
    string GroupStrategy,
    string GroupKey,
    string CorrectionBehavior,
    IReadOnlyList<string> AvailableActions,
    bool CanEscalateExternally,
    Guid FeedbackId,
    Guid ReportId,
    Guid? ReportProductId,
    Guid? ContributionId,
    Guid? ReportEmployeeId,
    Guid? EmploymentId,
    Guid? PersonId,
    string EmployeeName,
    string ProductName,
    string FundCompanyName,
    string PolicyNumber,
    IReadOnlyDictionary<string, string?> ReportedValues,
    IReadOnlyDictionary<string, string?> CurrentValues,
    IReadOnlyDictionary<string, string?> FeedbackValues,
    DateTimeOffset ReceivedAt);

public static class FeedbackResolutionWireProjection
{
    public static string WireName<TEnum>(TEnum value) where TEnum : struct, Enum =>
        char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];

    public static IReadOnlyList<string> ActionNames(FeedbackResolutionAction actions) =>
        Enum.GetValues<FeedbackResolutionAction>()
            .Where(action => action != FeedbackResolutionAction.None && actions.HasFlag(action))
            .Select(WireName)
            .ToArray();

    public static string BuildGroupKey(
        FeedbackResolutionGroupStrategy strategy,
        Guid employerId,
        Guid reportId,
        Guid? reportProductId,
        Guid? contributionId,
        Guid? employmentId,
        string? transferIdentifier,
        string? previousRecordIdentifier,
        int code,
        Guid feedbackId,
        int sequence)
    {
        static string Id(Guid? value) => value?.ToString("N") ?? "none";
        static string Token(string? value) => string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().ToUpperInvariant();

        return strategy switch
        {
            FeedbackResolutionGroupStrategy.PerEmployee => $"employee:{Id(employmentId)}",
            FeedbackResolutionGroupStrategy.PerEmployeeProduct => $"employee-product:{Id(employmentId)}:{Id(reportProductId)}",
            FeedbackResolutionGroupStrategy.PerContribution => $"contribution:{Id(contributionId)}",
            FeedbackResolutionGroupStrategy.PerReport => $"report:{reportId:N}",
            FeedbackResolutionGroupStrategy.PerEmployer => $"employer:{employerId:N}",
            FeedbackResolutionGroupStrategy.PerTransfer => $"transfer:{reportId:N}:{Token(transferIdentifier)}",
            FeedbackResolutionGroupStrategy.PerDocumentRequirement => $"document:{code}:{Id(employmentId)}:{Id(reportProductId)}",
            FeedbackResolutionGroupStrategy.PerOriginalMovement => $"movement:{reportId:N}:{Token(previousRecordIdentifier)}:{Id(contributionId)}",
            _ => $"error:{feedbackId:N}:{Id(contributionId)}:{sequence}:{code}"
        };
    }
    public static string BuildResolutionGroupKey(
        FeedbackResolutionPlaybook playbook,
        Guid employerId,
        Guid reportId,
        Guid? reportProductId,
        Guid? contributionId,
        Guid? employmentId,
        string? transferIdentifier,
        string? previousRecordIdentifier,
        Guid feedbackId,
        int sequence)
    {
        var target = BuildGroupKey(playbook.GroupStrategy, employerId, reportId, reportProductId, contributionId,
            employmentId, transferIdentifier, previousRecordIdentifier, playbook.Code, feedbackId, sequence);
        return $"{WireName(playbook.Resolver)}:{target}";
    }

}
