namespace Alpha.Application.Reporting;

public sealed record FeedbackResolutionContextResponse(
    string ContextType,
    Guid EmployerId,
    Guid? ReportId,
    Guid? ReportProductId,
    bool CanResolve,
    bool CanCreateReport,
    bool CanEditEmployee,
    IReadOnlyList<int> UnsupportedCodes,
    IReadOnlyList<FeedbackResolutionProblemDto> Problems,
    IReadOnlyList<FeedbackResolutionGroupDto> Groups);

public sealed record FeedbackResolutionGroupDto(
    string GroupKey,
    string ResolverType,
    string GroupStrategy,
    bool CanExecute,
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
    DateTimeOffset ReceivedAt,
    string? LatestDecision = null,
    string LatestDecisionNote = "",
    DateTimeOffset? LatestDecisionAt = null,
    Guid? ExternalCaseId = null,
    string ExternalCaseStatus = "",
    string ExternalCaseAssigneeName = "",
    string TargetScope = "deposit");

public static class FeedbackResolutionWireProjection
{
    public static string WireName<TEnum>(TEnum value) where TEnum : struct, Enum =>
        char.ToLowerInvariant(value.ToString()[0]) + value.ToString()[1..];

    public static IReadOnlyList<string> ActionNames(FeedbackResolutionAction actions) =>
        Enum.GetValues<FeedbackResolutionAction>()
            .Where(action => action != FeedbackResolutionAction.None && actions.HasFlag(action))
            .Select(WireName)
            .ToArray();

    public static string BuildProblemId(Guid feedbackId, Guid contributionId, int sequence, int code) =>
        $"{feedbackId:N}:{contributionId:N}:{sequence}:{code}";

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
    public static IReadOnlyList<FeedbackResolutionGroupDto> BuildGroups(
        IReadOnlyList<FeedbackResolutionProblemDto> problems,
        bool canCreateReport = false,
        bool canEditEmployee = false,
        bool canEditEmployer = false) =>
        problems
            .GroupBy(problem => problem.GroupKey, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var isEmployee = string.Equals(
                    first.ResolverType,
                    WireName(FeedbackResolverType.Employee),
                    StringComparison.Ordinal);
                var isDecision = group.Any(problem => string.Equals(
                    problem.ResolutionType,
                    WireName(FeedbackResolutionType.Decision),
                    StringComparison.Ordinal));
                var employerTarget = group.All(problem =>
                    string.Equals(problem.TargetScope, "employer", StringComparison.Ordinal));
                var canExecute = employerTarget
                    ? canEditEmployer
                    : isDecision
                        ? canCreateReport || (isEmployee && canEditEmployee)
                        : isEmployee ? canEditEmployee : canCreateReport;
                return new FeedbackResolutionGroupDto(
                    group.Key,
                    first.ResolverType,
                    first.GroupStrategy,
                    canExecute,
                    group.ToArray());
            })
            .ToArray();

    public static bool CanExecuteEmployeeEdit(
        FeedbackResolutionGroupDto group,
        Guid employmentId)
    {
        var employeeResolver = WireName(FeedbackResolverType.Employee);
        var editEmployee = WireName(FeedbackResolutionAction.EditEmployee);
        return string.Equals(group.ResolverType, employeeResolver, StringComparison.Ordinal)
            && group.Problems.Count > 0
            && group.Problems.All(problem =>
                problem.EmploymentId == employmentId
                && problem.AvailableActions.Contains(editEmployee, StringComparer.Ordinal));
    }

    public static bool CanPrepareInternalCorrection(
        FeedbackResolutionGroupDto group,
        string requestedResolver)
    {
        var correctionResolvers = new HashSet<string>(StringComparer.Ordinal)
        {
            WireName(FeedbackResolverType.Contribution),
            WireName(FeedbackResolverType.ProductPolicy),
            WireName(FeedbackResolverType.EmploymentStatus),
            WireName(FeedbackResolverType.Payment),
            WireName(FeedbackResolverType.Documents),
            WireName(FeedbackResolverType.ReportCorrection),
            WireName(FeedbackResolverType.Refund)
        };
        if (!correctionResolvers.Contains(requestedResolver)
            || !string.Equals(group.ResolverType, requestedResolver, StringComparison.Ordinal)
            || group.Problems.Count == 0)
            return false;

        var edit = WireName(FeedbackResolutionType.Edit);
        var correctionWorkspace = WireName(FeedbackCorrectionBehavior.CorrectionWorkspace);
        var prepareCorrection = WireName(FeedbackResolutionAction.PrepareCorrection);
        var resolverSpecificAction = requestedResolver switch
        {
            var value when value == WireName(FeedbackResolverType.Contribution) => WireName(FeedbackResolutionAction.EditContribution),
            var value when value == WireName(FeedbackResolverType.ProductPolicy) => WireName(FeedbackResolutionAction.EditProduct),
            var value when value == WireName(FeedbackResolverType.EmploymentStatus) => WireName(FeedbackResolutionAction.EditEmployment),
            var value when value == WireName(FeedbackResolverType.Payment) => WireName(FeedbackResolutionAction.EditPayment),
            var value when value == WireName(FeedbackResolverType.Documents) => WireName(FeedbackResolutionAction.UploadDocument),
            var value when value == WireName(FeedbackResolverType.Refund) => WireName(FeedbackResolutionAction.PrepareNegative),
            _ => prepareCorrection
        };

        return group.Problems.All(problem =>
            string.Equals(problem.ResolutionType, edit, StringComparison.Ordinal)
            && string.Equals(problem.CorrectionBehavior, correctionWorkspace, StringComparison.Ordinal)
            && (problem.AvailableActions.Contains(prepareCorrection, StringComparer.Ordinal)
                || problem.AvailableActions.Contains(resolverSpecificAction, StringComparer.Ordinal)));
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
