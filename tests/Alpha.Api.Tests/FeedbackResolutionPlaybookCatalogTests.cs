using Alpha.Api.Services;
using Alpha.Application.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class FeedbackResolutionPlaybookCatalogTests
{
    [Fact]
    public void Every_official_v006_feedback_code_has_exactly_one_playbook()
    {
        var official = EmployerInterfaceLineFeedbackParser.OfficialErrorCodes.OrderBy(x => x).ToArray();
        var mapped = FeedbackResolutionPlaybookCatalog.All.Select(x => x.Code).OrderBy(x => x).ToArray();

        Assert.Equal(official, mapped);
        Assert.Equal(mapped.Length, mapped.Distinct().Count());
    }

    [Fact]
    public void Playbook_scope_matches_existing_feedback_business_scope_for_every_official_code()
    {
        foreach (var code in EmployerInterfaceLineFeedbackParser.OfficialErrorCodes)
        {
            var expected = EmployerInterfaceLineFeedbackParser.ErrorScope(code).ToString();
            var actual = FeedbackResolutionPlaybookCatalog.Get(code).Scope.ToString();

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void Informational_codes_are_not_exposed_as_actionable_resolution_steps()
    {
        var informational = FeedbackResolutionPlaybookCatalog.All
            .Where(x => x.ResolutionType == FeedbackResolutionType.Informational)
            .OrderBy(x => x.Code)
            .ToArray();

        Assert.Equal(new[] { 1, 31 }, informational.Select(x => x.Code).ToArray());
        Assert.All(informational, item =>
        {
            Assert.Equal(FeedbackResolverType.Information, item.Resolver);
            Assert.Equal(FeedbackCorrectionBehavior.None, item.CorrectionBehavior);
            Assert.Equal(FeedbackResolutionAction.None, item.Actions);
        });
    }

    [Fact]
    public void Every_actionable_playbook_has_a_resolver_grouping_and_action()
    {
        var actionable = FeedbackResolutionPlaybookCatalog.All
            .Where(x => x.ResolutionType != FeedbackResolutionType.Informational);

        Assert.All(actionable, item =>
        {
            Assert.NotEqual(FeedbackResolverType.Information, item.Resolver);
            Assert.NotEqual(FeedbackResolutionAction.None, item.Actions);
        });
    }

    [Fact]
    public void External_resolution_steps_have_an_external_case_action()
    {
        var external = FeedbackResolutionPlaybookCatalog.All
            .Where(x => x.ResolutionType == FeedbackResolutionType.External);

        Assert.All(external, item =>
            Assert.True(item.Actions.HasFlag(FeedbackResolutionAction.OpenExternalCase),
                $"Code {item.Code} is External but cannot open an external case."));
    }

    [Theory]
    [InlineData(4, FeedbackResolutionType.Decision, FeedbackResolutionFamily.Identity, FeedbackResolverType.Employee, FeedbackResolutionGroupStrategy.PerEmployee)]
    [InlineData(53, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Contribution, FeedbackResolverType.Contribution, FeedbackResolutionGroupStrategy.PerContribution)]
    [InlineData(55, FeedbackResolutionType.Edit, FeedbackResolutionFamily.Documents, FeedbackResolverType.Documents, FeedbackResolutionGroupStrategy.PerDocumentRequirement)]
    [InlineData(83, FeedbackResolutionType.External, FeedbackResolutionFamily.Payment, FeedbackResolverType.ExternalCase, FeedbackResolutionGroupStrategy.PerTransfer)]
    [InlineData(100, FeedbackResolutionType.Edit, FeedbackResolutionFamily.ReportCorrection, FeedbackResolverType.ReportCorrection, FeedbackResolutionGroupStrategy.PerReport)]
    public void Representative_codes_keep_their_intended_resolution_route(
        int code,
        FeedbackResolutionType type,
        FeedbackResolutionFamily family,
        FeedbackResolverType resolver,
        FeedbackResolutionGroupStrategy group)
    {
        var playbook = FeedbackResolutionPlaybookCatalog.Get(code);

        Assert.Equal(type, playbook.ResolutionType);
        Assert.Equal(family, playbook.Family);
        Assert.Equal(resolver, playbook.Resolver);
        Assert.Equal(group, playbook.GroupStrategy);
    }

    [Fact]
    public void Unknown_code_fails_closed()
    {
        Assert.False(FeedbackResolutionPlaybookCatalog.TryGet(999, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => FeedbackResolutionPlaybookCatalog.Get(999));
    }
    [Theory]
    [InlineData(102, FeedbackResolutionGroupStrategy.PerReport)]
    [InlineData(103, FeedbackResolutionGroupStrategy.PerReport)]
    [InlineData(109, FeedbackResolutionGroupStrategy.PerEmployee)]
    [InlineData(110, FeedbackResolutionGroupStrategy.PerEmployee)]
    [InlineData(111, FeedbackResolutionGroupStrategy.PerReport)]
    [InlineData(112, FeedbackResolutionGroupStrategy.PerReport)]
    public void Document_playbooks_group_by_their_business_owner(int code, FeedbackResolutionGroupStrategy expected)
    {
        Assert.Equal(expected, FeedbackResolutionPlaybookCatalog.Get(code).GroupStrategy);
    }

    [Theory]
    [InlineData(45, FeedbackTreatmentTarget.Deposit)]
    [InlineData(50, FeedbackTreatmentTarget.Deposit)]
    [InlineData(56, FeedbackTreatmentTarget.Deposit)]
    [InlineData(79, FeedbackTreatmentTarget.Deposit)]
    [InlineData(83, FeedbackTreatmentTarget.Deposit)]
    [InlineData(84, FeedbackTreatmentTarget.Deposit)]
    [InlineData(85, FeedbackTreatmentTarget.Deposit)]
    [InlineData(18, FeedbackTreatmentTarget.Report)]
    [InlineData(100, FeedbackTreatmentTarget.Report)]
    [InlineData(102, FeedbackTreatmentTarget.Report)]
    [InlineData(4, FeedbackTreatmentTarget.Employee)]
    [InlineData(53, FeedbackTreatmentTarget.Deposit)]
    [InlineData(1, FeedbackTreatmentTarget.Informational)]
    [InlineData(31, FeedbackTreatmentTarget.Informational)]
    public void Treatment_target_is_independent_of_official_error_scope(
        int code, FeedbackTreatmentTarget target)
    {
        Assert.Equal(target, FeedbackTreatmentTargetCatalog.Resolve(code));
    }

    [Fact]
    public void Bank_account_code_requires_verified_employer_configuration_mismatch()
    {
        Assert.Equal(FeedbackTreatmentTarget.Deposit, FeedbackTreatmentTargetCatalog.Resolve(56));
        Assert.Equal(FeedbackTreatmentTarget.Employer,
            FeedbackTreatmentTargetCatalog.Resolve(56, verifiedEmployerAccountMismatch: true));
    }

    [Fact]
    public void Every_official_code_has_treatment_target()
    {
        foreach (var code in EmployerInterfaceLineFeedbackParser.OfficialErrorCodes)
            Assert.True(Enum.IsDefined(FeedbackTreatmentTargetCatalog.Resolve(code)));
    }

    [Fact]
    public void Employer_target_groups_require_employer_edit_permission()
    {
        var problem = new FeedbackResolutionProblemDto(
            ProblemId: "problem-56", Code: 56, Description: "Account feedback",
            Scope: "money", ResolutionType: "edit", Family: "payment",
            ResolverType: "payment", GroupStrategy: "perEmployer", GroupKey: "employer:1",
            CorrectionBehavior: "revalidateOnly", AvailableActions: ["editPayment"],
            CanEscalateExternally: false, FeedbackId: Guid.NewGuid(), ReportId: Guid.NewGuid(),
            ReportProductId: Guid.NewGuid(), ContributionId: Guid.NewGuid(),
            ReportEmployeeId: Guid.NewGuid(), EmploymentId: Guid.NewGuid(), PersonId: Guid.NewGuid(),
            EmployeeName: "", ProductName: "", FundCompanyName: "", PolicyNumber: "",
            ReportedValues: new Dictionary<string, string?>(),
            CurrentValues: new Dictionary<string, string?>(),
            FeedbackValues: new Dictionary<string, string?>(),
            ReceivedAt: DateTimeOffset.UtcNow, TargetScope: "employer");

        Assert.False(FeedbackResolutionWireProjection.BuildGroups([problem],
            canCreateReport: true, canEditEmployee: true, canEditEmployer: false)[0].CanExecute);
        Assert.True(FeedbackResolutionWireProjection.BuildGroups([problem],
            canCreateReport: false, canEditEmployee: false, canEditEmployer: true)[0].CanExecute);
    }

}
