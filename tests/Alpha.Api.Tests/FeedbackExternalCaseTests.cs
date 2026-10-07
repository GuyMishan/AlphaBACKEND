using Alpha.Application.Reporting;
using Alpha.Domain.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class FeedbackExternalCaseTests
{
    [Fact]
    public void Payment_external_codes_83_84_85_share_the_same_transfer_case_key()
    {
        var employerId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var contributionId = Guid.NewGuid();
        var employmentId = Guid.NewGuid();
        var feedbackId = Guid.NewGuid();

        string KeyFor(int code)
        {
            var playbook = FeedbackResolutionPlaybookCatalog.Get(code);
            return FeedbackResolutionWireProjection.BuildResolutionGroupKey(
                playbook,
                employerId,
                reportId,
                productId,
                contributionId,
                employmentId,
                "transfer-abc",
                null,
                feedbackId,
                1);
        }

        var key83 = KeyFor(83);
        Assert.Equal(key83, KeyFor(84));
        Assert.Equal(key83, KeyFor(85));
        Assert.StartsWith("externalCase:transfer:", key83);
    }

    [Fact]
    public void External_case_status_can_reopen_after_resolution()
    {
        var externalCase = new FeedbackExternalCase(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "externalCase:transfer:report:transfer",
            "מסלקה / גוף מוסדי",
            "בירור",
            "תוכן",
            Guid.NewGuid());

        Assert.Equal("open", externalCase.Status);

        externalCase.SetStatus("waiting");
        Assert.Equal("waiting", externalCase.Status);
        Assert.Null(externalCase.ClosedAt);

        externalCase.SetStatus("resolved");
        Assert.Equal("resolved", externalCase.Status);
        Assert.NotNull(externalCase.ClosedAt);

        externalCase.SetStatus("open");
        Assert.Equal("open", externalCase.Status);
        Assert.Null(externalCase.ClosedAt);
    }

    [Fact]
    public void Every_external_playbook_can_open_an_external_case()
    {
        var external = FeedbackResolutionPlaybookCatalog.All
            .Where(item => item.ResolutionType == FeedbackResolutionType.External)
            .ToArray();

        Assert.NotEmpty(external);
        Assert.All(external, item =>
            Assert.True(item.Actions.HasFlag(FeedbackResolutionAction.OpenExternalCase)));
    }
}
