using Alpha.Application.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class FeedbackResolutionEscalationTests
{
    [Fact]
    public void Every_external_playbook_is_marked_as_escalatable()
    {
        var external = FeedbackResolutionPlaybookCatalog.All
            .Where(item => item.ResolutionType == FeedbackResolutionType.External)
            .ToArray();

        Assert.NotEmpty(external);
        Assert.All(external, item =>
        {
            Assert.True(item.CanEscalateExternally);
            Assert.True(item.Actions.HasFlag(FeedbackResolutionAction.OpenExternalCase));
        });
    }
}
