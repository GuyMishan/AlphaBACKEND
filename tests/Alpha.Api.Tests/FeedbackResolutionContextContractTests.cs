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
