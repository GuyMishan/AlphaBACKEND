using Alpha.Application.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class FeedbackResolutionContextContractTests
{
    [Fact]
    public void Wire_projection_uses_stable_camel_case_names()
    {
        Assert.Equal("edit", FeedbackResolutionWireProjection.WireName(FeedbackResolutionType.Edit));
        Assert.Equal("identity", FeedbackResolutionWireProjection.WireName(FeedbackResolutionFamily.Identity));
        Assert.Equal("employee", FeedbackResolutionWireProjection.WireName(FeedbackResolverType.Employee));
        Assert.Equal("perEmployee", FeedbackResolutionWireProjection.WireName(FeedbackResolutionGroupStrategy.PerEmployee));
        Assert.Equal("correctionWorkspace", FeedbackResolutionWireProjection.WireName(FeedbackCorrectionBehavior.CorrectionWorkspace));
    }

    [Fact]
    public void Action_projection_expands_flags_without_none()
    {
        var actions = FeedbackResolutionWireProjection.ActionNames(
            FeedbackResolutionAction.Review
            | FeedbackResolutionAction.EditEmployee
            | FeedbackResolutionAction.PrepareCorrection);

        Assert.Equal(new[] { "editEmployee", "review", "prepareCorrection" }, actions);
    }

    [Theory]
    [InlineData(FeedbackResolutionGroupStrategy.PerEmployee, "employee:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(FeedbackResolutionGroupStrategy.PerEmployeeProduct, "employee-product:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")]
    [InlineData(FeedbackResolutionGroupStrategy.PerContribution, "contribution:cccccccccccccccccccccccccccccccc")]
    [InlineData(FeedbackResolutionGroupStrategy.PerReport, "report:dddddddddddddddddddddddddddddddd")]
    [InlineData(FeedbackResolutionGroupStrategy.PerEmployer, "employer:eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee")]
    public void Entity_group_keys_are_deterministic(FeedbackResolutionGroupStrategy strategy, string expected)
    {
        var key = FeedbackResolutionWireProjection.BuildGroupKey(
            strategy,
            Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"),
            Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"),
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            "transfer-1",
            "previous-1",
            53,
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            2);

        Assert.Equal(expected, key);
    }

    [Fact]
    public void Transfer_document_and_original_movement_keys_keep_their_business_identity()
    {
        var employerId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var employmentId = Guid.NewGuid();
        var feedbackId = Guid.NewGuid();

        Assert.Equal(
            $"transfer:{reportId:N}:TRANSFER-1",
            FeedbackResolutionWireProjection.BuildGroupKey(
                FeedbackResolutionGroupStrategy.PerTransfer, employerId, reportId, productId, contributionId,
                employmentId, "transfer-1", null, 45, feedbackId, 0));

        Assert.Equal(
            $"document:110:{employmentId:N}:{productId:N}",
            FeedbackResolutionWireProjection.BuildGroupKey(
                FeedbackResolutionGroupStrategy.PerDocumentRequirement, employerId, reportId, productId, contributionId,
                employmentId, null, null, 110, feedbackId, 0));

        Assert.Equal(
            $"movement:{reportId:N}:PREVIOUS-1:{contributionId:N}",
            FeedbackResolutionWireProjection.BuildGroupKey(
                FeedbackResolutionGroupStrategy.PerOriginalMovement, employerId, reportId, productId, contributionId,
                employmentId, null, "previous-1", 93, feedbackId, 0));
    }

    [Fact]
    public void Problem_id_is_stable_and_distinguishes_individual_feedback_errors()
    {
        var feedbackId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var contributionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var first = FeedbackResolutionWireProjection.BuildProblemId(feedbackId, contributionId, 1, 50);
        var same = FeedbackResolutionWireProjection.BuildProblemId(feedbackId, contributionId, 1, 50);
        var otherSequence = FeedbackResolutionWireProjection.BuildProblemId(feedbackId, contributionId, 2, 50);
        var otherCode = FeedbackResolutionWireProjection.BuildProblemId(feedbackId, contributionId, 1, 56);

        Assert.Equal(first, same);
        Assert.NotEqual(first, otherSequence);
        Assert.NotEqual(first, otherCode);
    }

    [Fact]
    public void Per_error_key_keeps_separate_feedback_rows_separate()
    {
        var feedbackId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var contributionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

        var first = FeedbackResolutionWireProjection.BuildGroupKey(
            FeedbackResolutionGroupStrategy.PerError, Guid.NewGuid(), Guid.NewGuid(), null, contributionId,
            null, null, null, 18, feedbackId, 1);
        var second = FeedbackResolutionWireProjection.BuildGroupKey(
            FeedbackResolutionGroupStrategy.PerError, Guid.NewGuid(), Guid.NewGuid(), null, contributionId,
            null, null, null, 18, feedbackId, 2);

        Assert.NotEqual(first, second);
    }



    [Fact]
    public void Resolution_context_exposes_employee_edit_permission_separately_from_report_resolution_permission()
    {
        var response = new FeedbackResolutionContextResponse(
            ContextType: "deposit",
            EmployerId: Guid.NewGuid(),
            ReportId: Guid.NewGuid(),
            ReportProductId: Guid.NewGuid(),
            CanResolve: true,
            CanCreateReport: true,
            CanEditEmployee: false,
            UnsupportedCodes: Array.Empty<int>(),
            Problems: Array.Empty<FeedbackResolutionProblemDto>(),
            Groups: Array.Empty<FeedbackResolutionGroupDto>());

        Assert.True(response.CanResolve);
        Assert.True(response.CanCreateReport);
        Assert.False(response.CanEditEmployee);
    }

    [Fact]
    public void Employee_resolution_action_requires_matching_employee_group_and_edit_action()
    {
        var employmentId = Guid.NewGuid();
        var group = new FeedbackResolutionGroupDto(
            "employee:employee:one",
            "employee",
            "perEmployee",
            true,
            new[]
            {
                CreateProblem("a", employmentId, new[] { "review", "editEmployee" }),
                CreateProblem("b", employmentId, new[] { "editEmployee", "prepareCorrection" })
            });

        Assert.True(FeedbackResolutionWireProjection.CanExecuteEmployeeEdit(group, employmentId));
        Assert.False(FeedbackResolutionWireProjection.CanExecuteEmployeeEdit(group, Guid.NewGuid()));

        var wrongResolver = group with { ResolverType = "productPolicy" };
        Assert.False(FeedbackResolutionWireProjection.CanExecuteEmployeeEdit(wrongResolver, employmentId));

        var missingAction = group with
        {
            Problems = new[] { CreateProblem("c", employmentId, new[] { "review" }) }
        };
        Assert.False(FeedbackResolutionWireProjection.CanExecuteEmployeeEdit(missingAction, employmentId));

        static FeedbackResolutionProblemDto CreateProblem(
            string id,
            Guid employmentId,
            IReadOnlyList<string> actions) =>
            new(
                ProblemId: id,
                Code: 4,
                Description: "identity",
                Scope: "employee",
                ResolutionType: "decision",
                Family: "identity",
                ResolverType: "employee",
                GroupStrategy: "perEmployee",
                GroupKey: "employee:employee:one",
                CorrectionBehavior: "dynamic",
                AvailableActions: actions,
                CanEscalateExternally: true,
                FeedbackId: Guid.NewGuid(),
                ReportId: Guid.NewGuid(),
                ReportProductId: Guid.NewGuid(),
                ContributionId: Guid.NewGuid(),
                ReportEmployeeId: Guid.NewGuid(),
                EmploymentId: employmentId,
                PersonId: Guid.NewGuid(),
                EmployeeName: "Employee",
                ProductName: "Product",
                FundCompanyName: "Fund",
                PolicyNumber: "P-1",
                ReportedValues: new Dictionary<string, string?>(),
                CurrentValues: new Dictionary<string, string?>(),
                FeedbackValues: new Dictionary<string, string?>(),
                ReceivedAt: DateTimeOffset.UtcNow);
    }

    [Theory]
    [InlineData("contribution", "editContribution")]
    [InlineData("productPolicy", "editProduct")]
    [InlineData("employmentStatus", "editEmployment")]
    [InlineData("payment", "editPayment")]
    [InlineData("documents", "uploadDocument")]
    [InlineData("reportCorrection", "prepareCorrection")]
    public void Internal_correction_resolvers_require_their_playbook_action(
        string resolver,
        string action)
    {
        var problem = new FeedbackResolutionProblemDto(
            ProblemId: "p1",
            Code: 53,
            Description: "test",
            Scope: "contribution",
            ResolutionType: "edit",
            Family: "contribution",
            ResolverType: resolver,
            GroupStrategy: "perContribution",
            GroupKey: $"{resolver}:target",
            CorrectionBehavior: "correctionWorkspace",
            AvailableActions: new[] { action },
            CanEscalateExternally: false,
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
        var group = new FeedbackResolutionGroupDto(
            problem.GroupKey, resolver, problem.GroupStrategy, true, new[] { problem });

        Assert.True(FeedbackResolutionWireProjection.CanPrepareInternalCorrection(group, resolver));
        Assert.False(FeedbackResolutionWireProjection.CanPrepareInternalCorrection(group, "employee"));
        Assert.False(FeedbackResolutionWireProjection.CanPrepareInternalCorrection(
            group with { Problems = new[] { problem with { AvailableActions = new[] { "review" } } } },
            resolver));
        Assert.False(FeedbackResolutionWireProjection.CanPrepareInternalCorrection(
            group with { Problems = new[] { problem with { ResolutionType = "decision" } } },
            resolver));
        Assert.False(FeedbackResolutionWireProjection.CanPrepareInternalCorrection(
            group with { Problems = new[] { problem with { CorrectionBehavior = "revalidateOnly" } } },
            resolver));
        Assert.False(FeedbackResolutionWireProjection.CanPrepareInternalCorrection(
            group with { Problems = new[] { problem with { CorrectionBehavior = "externalFollowUp" } } },
            resolver));
    }

    [Theory]
    [InlineData("payment", "editPayment", "revalidateOnly")]
    [InlineData("documents", "uploadDocument", "externalFollowUp")]
    public void Non_correction_behaviors_cannot_open_a_correction_workspace(
        string resolver,
        string action,
        string correctionBehavior)
    {
        var problem = new FeedbackResolutionProblemDto(
            ProblemId: "p1",
            Code: 50,
            Description: "test",
            Scope: "deposit",
            ResolutionType: "edit",
            Family: "payment",
            ResolverType: resolver,
            GroupStrategy: "perTransfer",
            GroupKey: $"{resolver}:target",
            CorrectionBehavior: correctionBehavior,
            AvailableActions: new[] { action },
            CanEscalateExternally: false,
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
        var group = new FeedbackResolutionGroupDto(
            problem.GroupKey, resolver, problem.GroupStrategy, true, new[] { problem });

        Assert.False(FeedbackResolutionWireProjection.CanPrepareInternalCorrection(group, resolver));
    }

    [Fact]
    public void Resolution_queue_groups_problems_by_resolver_qualified_group_key_in_first_seen_order()
    {
        static FeedbackResolutionProblemDto Problem(string id, string groupKey, string resolver, int code) =>
            new(
                ProblemId: id,
                Code: code,
                Description: $"error-{code}",
                Scope: "employee",
                ResolutionType: "decision",
                Family: "identity",
                ResolverType: resolver,
                GroupStrategy: "perEmployee",
                GroupKey: groupKey,
                CorrectionBehavior: "dynamic",
                AvailableActions: new[] { "review" },
                CanEscalateExternally: false,
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

        var groups = FeedbackResolutionWireProjection.BuildGroups(new[]
        {
            Problem("a", "employee:one", "employee", 4),
            Problem("b", "employee:one", "employee", 11),
            Problem("c", "employmentStatus:employee:one", "employmentStatus", 33),
            Problem("d", "employee:two", "employee", 62)
        }, canCreateReport: false, canEditEmployee: true);

        Assert.Equal(3, groups.Count);
        Assert.Equal(new[] { "employee:one", "employmentStatus:employee:one", "employee:two" },
            groups.Select(group => group.GroupKey).ToArray());
        Assert.Equal(new[] { 4, 11 }, groups[0].Problems.Select(problem => problem.Code).ToArray());
        Assert.Equal("employee", groups[0].ResolverType);
        Assert.True(groups[0].CanExecute);
        Assert.Equal("employmentStatus", groups[1].ResolverType);
        Assert.False(groups[1].CanExecute);
    }

    [Fact]
    public void Every_actionable_playbook_projects_to_a_non_empty_wire_contract()
    {
        foreach (var playbook in FeedbackResolutionPlaybookCatalog.All.Where(x =>
                     x.ResolutionType != FeedbackResolutionType.Informational))
        {
            Assert.False(string.IsNullOrWhiteSpace(FeedbackResolutionWireProjection.WireName(playbook.ResolutionType)));
            Assert.False(string.IsNullOrWhiteSpace(FeedbackResolutionWireProjection.WireName(playbook.Family)));
            Assert.False(string.IsNullOrWhiteSpace(FeedbackResolutionWireProjection.WireName(playbook.Resolver)));
            Assert.NotEmpty(FeedbackResolutionWireProjection.ActionNames(playbook.Actions));
        }
    }
}
