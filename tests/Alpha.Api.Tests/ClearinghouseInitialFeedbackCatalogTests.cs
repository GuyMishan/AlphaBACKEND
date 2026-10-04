using Alpha.Api.Services;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class ClearinghouseInitialFeedbackCatalogTests
{
    [Fact]
    public void Fedbka_file_level_catalog_contains_only_documented_codes()
    {
        Assert.Equal(
            [1, 2, 3, 4, 11],
            ClearinghouseInitialFeedbackCatalog.StageAFileErrors.Select(x => x.Code).ToArray());
    }

    [Theory]
    [InlineData(1, "שם קובץ לא תקין")]
    [InlineData(2, "קובץ לא קריא")]
    [InlineData(3, "מבנה XML לא חוקי")]
    [InlineData(4, "היררכיה ראשית בקובץ לא תקינה")]
    [InlineData(11, "תאריך קובץ עתידי")]
    public void Fedbka_documented_code_can_be_simulated(int code, string description)
    {
        var scenario = SimulatedClearinghouseResponder.ParseScenario($"fedbka:{code}");

        Assert.Equal(ClearinghouseInitialFeedbackCatalog.TechnicalInterface, scenario.FeedbackInterface);
        Assert.Equal("error", scenario.Mode);
        Assert.Equal(code, scenario.ErrorCode);
        Assert.Equal(description, scenario.ErrorDetail);
    }

    [Fact]
    public void Fedbka_all_errors_expands_to_every_documented_file_error()
    {
        var expanded = SimulatedClearinghouseResponder.ExpandScenario(
            SimulatedClearinghouseResponder.ParseScenario("fedbka:all-errors"));

        Assert.Equal([1, 2, 3, 4, 11], expanded.Select(x => x.ErrorCode!.Value).ToArray());
        Assert.All(expanded, x =>
        {
            Assert.Equal(ClearinghouseInitialFeedbackCatalog.TechnicalInterface, x.FeedbackInterface);
            Assert.Equal("error", x.Mode);
        });
    }

    [Fact]
    public void Fedbka_duplicate_uses_official_invalid_filename_code_with_duplicate_detail()
    {
        var scenario = SimulatedClearinghouseResponder.ParseScenario("fedbka:duplicate");

        Assert.Equal(ClearinghouseInitialFeedbackCatalog.TechnicalInterface, scenario.FeedbackInterface);
        Assert.Equal(1, scenario.ErrorCode);
        Assert.Equal("duplicate", scenario.Mode);
        Assert.Contains("קובץ בשם TEST.DAT התקבל כקובץ כפול",
            ClearinghouseInitialFeedbackCatalog.DuplicateFileDetail("TEST.DAT"), StringComparison.Ordinal);
    }

    [Fact]
    public void Fedbkb_stage_is_known_but_arbitrary_error_codes_fail_closed()
    {
        var accepted = SimulatedClearinghouseResponder.ParseScenario("fedbkb:accepted");
        var unsupported = SimulatedClearinghouseResponder.ParseScenario("fedbkb:1234");

        Assert.Equal(ClearinghouseInitialFeedbackCatalog.ContentInterface, accepted.FeedbackInterface);
        Assert.Equal("accepted", accepted.Mode);
        Assert.Equal(ClearinghouseInitialFeedbackCatalog.ContentInterface, unsupported.FeedbackInterface);
        Assert.Equal("unsupported", unsupported.Mode);
        Assert.Contains("authoritative", unsupported.ErrorDetail, StringComparison.OrdinalIgnoreCase);
    }
}
