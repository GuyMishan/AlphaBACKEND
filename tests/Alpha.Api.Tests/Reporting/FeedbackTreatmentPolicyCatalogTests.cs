using Alpha.Modules.Reporting.Application;
using Xunit;

namespace Alpha.Api.Tests.Reporting;

public sealed class FeedbackTreatmentPolicyCatalogTests
{
    [Fact]
    public void Every_official_error_code_has_one_of_four_treatment_choices()
    {
        var official = EmployerInterfaceLineFeedbackParser.OfficialErrorCodes.OrderBy(x => x).ToArray();
        var policies = FeedbackTreatmentPolicyCatalog.All.OrderBy(x => x.Code).ToArray();
        Assert.Equal(official, policies.Select(x => x.Code).ToArray());
        Assert.Equal(101, policies.Length);
        Assert.Equal(101, policies.Select(x => x.Code).Distinct().Count());
        Assert.Equal(4, policies.Select(x => x.Choice).Distinct().Count());
    }

    [Theory]
    [InlineData(1, FeedbackTreatmentChoice.InformationOnly)]
    [InlineData(2, FeedbackTreatmentChoice.CorrectionOrExternalInquiry)]
    [InlineData(3, FeedbackTreatmentChoice.CorrectionOrExternalInquiry)]
    [InlineData(18, FeedbackTreatmentChoice.CorrectionOnly)]
    [InlineData(31, FeedbackTreatmentChoice.InformationOnly)]
    [InlineData(53, FeedbackTreatmentChoice.CorrectionOnly)]
    [InlineData(52, FeedbackTreatmentChoice.ExternalInquiryOnly)]
    [InlineData(100, FeedbackTreatmentChoice.CorrectionOnly)]
    [InlineData(103, FeedbackTreatmentChoice.CorrectionOrExternalInquiry)]
    public void Representative_codes_have_expected_choices(int code, FeedbackTreatmentChoice expected) =>
        Assert.Equal(expected, FeedbackTreatmentPolicyCatalog.Get(code).Choice);

    [Fact]
    public void External_inquiry_requires_playbook_support_and_correction_is_never_review_only()
    {
        foreach (var policy in FeedbackTreatmentPolicyCatalog.All)
        {
            var playbook = FeedbackResolutionPlaybookCatalog.Get(policy.Code);
            Assert.Equal(playbook.Actions.HasFlag(FeedbackResolutionAction.OpenExternalCase), policy.CanOpenExternalCase);
            Assert.Equal(playbook.Resolver, policy.Resolver);
            if (policy.Choice == FeedbackTreatmentChoice.InformationOnly)
                Assert.False(policy.CanCorrect || policy.CanOpenExternalCase);
        }
    }

    [Fact]
    public void Unknown_code_is_not_silently_assigned_an_action()
    {
        Assert.False(FeedbackTreatmentPolicyCatalog.TryGet(999, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => FeedbackTreatmentPolicyCatalog.Get(999));
    }
}
