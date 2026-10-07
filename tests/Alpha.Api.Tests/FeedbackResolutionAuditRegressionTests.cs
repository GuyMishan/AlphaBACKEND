using Alpha.Application.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class FeedbackResolutionAuditRegressionTests
{
    [Fact]
    public void Resolver_is_part_of_resolution_group_key()
    {
        var employerId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var employmentId = Guid.NewGuid();
        var feedbackId = Guid.NewGuid();

        var identity = FeedbackResolutionWireProjection.BuildResolutionGroupKey(
            FeedbackResolutionPlaybookCatalog.Get(4),
            employerId, reportId, productId, contributionId, employmentId,
            "transfer", null, feedbackId, 0);

        var employment = FeedbackResolutionWireProjection.BuildResolutionGroupKey(
            FeedbackResolutionPlaybookCatalog.Get(33),
            employerId, reportId, productId, contributionId, employmentId,
            "transfer", null, feedbackId, 0);

        Assert.NotEqual(identity, employment);
        Assert.StartsWith("employee:", identity);
        Assert.StartsWith("employmentStatus:", employment);
    }

    [Fact]
    public void Identity_errors_for_same_employee_share_one_resolution_group()
    {
        var employerId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var employmentId = Guid.NewGuid();
        var feedbackId = Guid.NewGuid();

        var code4 = FeedbackResolutionWireProjection.BuildResolutionGroupKey(
            FeedbackResolutionPlaybookCatalog.Get(4),
            employerId, reportId, productId, contributionId, employmentId,
            null, null, feedbackId, 0);
        var code11 = FeedbackResolutionWireProjection.BuildResolutionGroupKey(
            FeedbackResolutionPlaybookCatalog.Get(11),
            employerId, reportId, productId, contributionId, employmentId,
            null, null, feedbackId, 1);

        Assert.Equal(code4, code11);
    }

    [Fact]
    public void Wrong_employer_bank_account_groups_at_employer_level()
    {
        var playbook = FeedbackResolutionPlaybookCatalog.Get(56);
        Assert.Equal(FeedbackResolutionGroupStrategy.PerEmployer, playbook.GroupStrategy);

        var employerId = Guid.NewGuid();
        var first = FeedbackResolutionWireProjection.BuildResolutionGroupKey(
            playbook, employerId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "transfer-a", null, Guid.NewGuid(), 0);
        var second = FeedbackResolutionWireProjection.BuildResolutionGroupKey(
            playbook, employerId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            "transfer-b", null, Guid.NewGuid(), 0);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Report_level_document_errors_share_report_resolver_group()
    {
        var employerId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var playbook111 = FeedbackResolutionPlaybookCatalog.Get(111);
        var playbook112 = FeedbackResolutionPlaybookCatalog.Get(112);

        var first = FeedbackResolutionWireProjection.BuildResolutionGroupKey(
            playbook111, employerId, reportId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, null, Guid.NewGuid(), 0);
        var second = FeedbackResolutionWireProjection.BuildResolutionGroupKey(
            playbook112, employerId, reportId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            null, null, Guid.NewGuid(), 0);

        Assert.Equal(first, second);
        Assert.StartsWith("documents:report:", first);
    }
}
