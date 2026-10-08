using Alpha.Api.Services;
using Alpha.Application.Reporting;
using Alpha.Domain.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class FeedbackResolutionStage9ContractTests
{
    [Fact]
    public void All_errors_simulator_and_playbook_catalog_cover_the_same_actionable_codes()
    {
        var simulated = SimulatedClearinghouseResponder.ExpandScenario(
                SimulatedClearinghouseResponder.ParseScenario("all-errors"))
            .Select(item => item.ErrorCode!.Value)
            .OrderBy(code => code)
            .ToArray();

        Assert.Equal(
            EmployerInterfaceLineFeedbackParser.OfficialFailureCodes.OrderBy(code => code),
            simulated);

        var informational = FeedbackResolutionPlaybookCatalog.All
            .Where(item => item.ResolutionType == FeedbackResolutionType.Informational)
            .Select(item => item.Code)
            .ToHashSet();
        var simulatedActionable = simulated
            .Where(code => !informational.Contains(code))
            .ToArray();
        var actionable = FeedbackResolutionPlaybookCatalog.All
            .Where(item => item.ResolutionType != FeedbackResolutionType.Informational)
            .Select(item => item.Code)
            .OrderBy(code => code)
            .ToArray();

        Assert.Equal(actionable, simulatedActionable);
    }

    [Fact]
    public void Every_actionable_playbook_has_a_real_stage_9_completion_route()
    {
        foreach (var playbook in FeedbackResolutionPlaybookCatalog.All.Where(item =>
                     item.ResolutionType != FeedbackResolutionType.Informational))
        {
            var actions = playbook.Actions;
            var hasTerminalRoute =
                actions.HasFlag(FeedbackResolutionAction.Confirm)
                || actions.HasFlag(FeedbackResolutionAction.PrepareCorrection)
                || actions.HasFlag(FeedbackResolutionAction.PrepareNegative)
                || actions.HasFlag(FeedbackResolutionAction.EditEmployee)
                || actions.HasFlag(FeedbackResolutionAction.EditEmployment)
                || actions.HasFlag(FeedbackResolutionAction.EditProduct)
                || actions.HasFlag(FeedbackResolutionAction.EditContribution)
                || actions.HasFlag(FeedbackResolutionAction.EditPayment)
                || actions.HasFlag(FeedbackResolutionAction.UploadDocument)
                || actions.HasFlag(FeedbackResolutionAction.OpenExternalCase)
                || actions.HasFlag(FeedbackResolutionAction.Reconcile)
                || actions.HasFlag(FeedbackResolutionAction.LinkOriginalRecord);

            Assert.True(hasTerminalRoute, $"Code {playbook.Code} has no executable resolution route.");
        }
    }

    [Fact]
    public void Refund_edit_codes_can_prepare_the_negative_correction_workspace()
    {
        foreach (var code in new[] { 51, 93 })
        {
            var playbook = FeedbackResolutionPlaybookCatalog.Get(code);
            var problem = Problem(playbook, $"p-{code}");
            var group = new FeedbackResolutionGroupDto(
                problem.GroupKey,
                problem.ResolverType,
                problem.GroupStrategy,
                true,
                new[] { problem });

            Assert.Equal(FeedbackResolverType.Refund, playbook.Resolver);
            Assert.True(FeedbackResolutionWireProjection.CanPrepareInternalCorrection(
                group,
                FeedbackResolutionWireProjection.WireName(FeedbackResolverType.Refund)));
        }
    }

    [Fact]
    public void Employee_workspace_snapshot_update_marks_employee_data_as_changed()
    {
        var employee = new ManualReportEmployee(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "123456789", "Old", "Name", "100", 10000m);

        employee.UpdateMasterSnapshot("New", "Name", "101", 11000m);

        Assert.Equal("New", employee.FirstName);
        Assert.Equal("101", employee.EmployeeNumber);
        Assert.Equal(11000m, employee.MonthlySalary);
        Assert.Equal(ManualReportItemStatus.Draft, employee.ValidationStatus);
    }

    [Fact]
    public void Resolution_endpoint_revalidates_and_requires_real_correction_delta()
    {
        var source = Read("src", "Alpha.Api", "Endpoints", "ReportFeedbackEndpoints.cs");

        Assert.Contains("ValidateForFeedbackResolutionAsync", source, StringComparison.Ordinal);
        Assert.Contains("resolution_revalidation_failed", source, StringComparison.Ordinal);
        Assert.Contains("PendingChangeCountAsync", source, StringComparison.Ordinal);
        Assert.Contains("resolution_no_correction_change", source, StringComparison.Ordinal);
        Assert.Contains("resolution_correction_link_missing", source, StringComparison.Ordinal);
        Assert.Contains("reconciliation_case_required", source, StringComparison.Ordinal);
        Assert.Contains("original_movement_link_required", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Correction_backed_resolution_is_effective_only_after_revision_completion()
    {
        var endpoint = Read("src", "Alpha.Api", "Endpoints", "ReportFeedbackEndpoints.cs");

        Assert.Contains("EffectiveResolvedProblemIdsAsync", endpoint, StringComparison.Ordinal);
        Assert.Contains("report.IsRevisionSnapshot", endpoint, StringComparison.Ordinal);
        Assert.Contains("report.Status == ManualReportStatus.Completed", endpoint, StringComparison.Ordinal);
        Assert.Contains("pendingCorrectionProblems", endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void Correction_problem_links_survive_refresh_and_are_server_owned()
    {
        var endpoint = Read("src", "Alpha.Api", "Endpoints", "ReportFeedbackEndpoints.cs");
        var schema = Read("src", "Alpha.Infrastructure", "Persistence", "ReportFeedbackOperationsSchemaInitializer.cs");

        Assert.Contains("CorrectionWorkspaceResolutionLinksAsync", endpoint, StringComparison.Ordinal);
        Assert.Contains("FeedbackCorrectionResolutionLinks", endpoint, StringComparison.Ordinal);
        Assert.Contains("feedback_correction_resolution_links", schema, StringComparison.Ordinal);
        Assert.Contains("UX_feedback_correction_resolution_link_Problem", schema, StringComparison.Ordinal);
    }

    private static FeedbackResolutionProblemDto Problem(FeedbackResolutionPlaybook playbook, string id) =>
        new(
            ProblemId: id,
            Code: playbook.Code,
            Description: "test",
            Scope: FeedbackResolutionWireProjection.WireName(playbook.Scope),
            ResolutionType: FeedbackResolutionWireProjection.WireName(playbook.ResolutionType),
            Family: FeedbackResolutionWireProjection.WireName(playbook.Family),
            ResolverType: FeedbackResolutionWireProjection.WireName(playbook.Resolver),
            GroupStrategy: FeedbackResolutionWireProjection.WireName(playbook.GroupStrategy),
            GroupKey: $"{FeedbackResolutionWireProjection.WireName(playbook.Resolver)}:test",
            CorrectionBehavior: FeedbackResolutionWireProjection.WireName(playbook.CorrectionBehavior),
            AvailableActions: FeedbackResolutionWireProjection.ActionNames(playbook.Actions),
            CanEscalateExternally: playbook.CanEscalateExternally,
            FeedbackId: Guid.NewGuid(),
            ReportId: Guid.NewGuid(),
            ReportProductId: Guid.NewGuid(),
            ContributionId: Guid.NewGuid(),
            ReportEmployeeId: Guid.NewGuid(),
            EmploymentId: Guid.NewGuid(),
            PersonId: Guid.NewGuid(),
            EmployeeName: "Employee",
            ProductName: "Product",
            FundCompanyName: "Fund",
            PolicyNumber: "P-1",
            ReportedValues: new Dictionary<string, string?>(),
            CurrentValues: new Dictionary<string, string?>(),
            FeedbackValues: new Dictionary<string, string?>(),
            ReceivedAt: DateTimeOffset.UtcNow);

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }
}
