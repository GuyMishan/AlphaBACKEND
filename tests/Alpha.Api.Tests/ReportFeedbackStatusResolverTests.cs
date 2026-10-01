using Alpha.Api.Services;
using Alpha.Domain.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class ReportFeedbackStatusResolverTests
{
    [Fact]
    public void No_transmission_and_no_feedback_is_not_sent()
    {
        Assert.Equal("not-sent", ReportFeedbackStatusResolver.ResolveReportState(null, 0, 3, 0, 0));
    }

    [Fact]
    public void Sent_transmission_without_feedback_is_pending()
    {
        Assert.Equal("pending", ReportFeedbackStatusResolver.ResolveReportState(
            ReportTransmissionStatus.Sent, 0, 3, 0, 0));
    }

    [Fact]
    public void Official_feedback_with_partial_contribution_coverage_is_partial()
    {
        Assert.Equal("partial", ReportFeedbackStatusResolver.ResolveReportState(
            ReportTransmissionStatus.Accepted, 1, 3, 1, 0));
    }

    [Fact]
    public void Official_feedback_with_full_contribution_coverage_is_completed()
    {
        Assert.Equal("completed", ReportFeedbackStatusResolver.ResolveReportState(
            ReportTransmissionStatus.Accepted, 1, 3, 3, 0));
    }

    [Fact]
    public void Imported_feedback_can_complete_without_local_transmission()
    {
        Assert.Equal("completed", ReportFeedbackStatusResolver.ResolveReportState(
            null, 1, 2, 2, 0));
    }

    [Fact]
    public void Contribution_error_requires_attention()
    {
        Assert.Equal("attention", ReportFeedbackStatusResolver.ResolveReportState(
            ReportTransmissionStatus.Accepted, 1, 2, 2, 1));
    }

    [Fact]
    public void Rejected_transmission_requires_attention()
    {
        Assert.Equal("attention", ReportFeedbackStatusResolver.ResolveReportState(
            ReportTransmissionStatus.Rejected, 0, 2, 0, 0));
    }

    [Theory]
    [InlineData(1000, 1000, 900, 100, "in-transit")]
    [InlineData(1000, 1000, 1000, 0, "allocated")]
    [InlineData(1000, 500, 500, 0, "received-partial")]
    [InlineData(1000, 0, 0, 0, "unresolved")]
    public void Money_state_uses_transfer_level_reported_amount(
        decimal reported, decimal received, decimal allocated, decimal inTransit, string expected)
    {
        Assert.Equal(expected, ReportFeedbackStatusResolver.ResolveMoneyState(
            reported, received, allocated, inTransit));
    }

    [Fact]
    public void Empty_placeholder_contribution_is_not_effective()
    {
        var contribution = new ManualContribution(
            Guid.NewGuid(), ContributionParty.Employee, ContributionComponent.Benefits, 0, 0, 0);

        Assert.False(ReportFeedbackStatusResolver.IsEffectiveContribution(contribution));
    }

    [Fact]
    public void Non_empty_contribution_is_effective()
    {
        var contribution = new ManualContribution(
            Guid.NewGuid(), ContributionParty.Employee, ContributionComponent.Benefits, 600, 6, 0);

        Assert.True(ReportFeedbackStatusResolver.IsEffectiveContribution(contribution));
    }
}

public sealed class ReportCsvFormatterTests
{
    [Fact]
    public void Csv_escapes_quotes_and_commas()
    {
        Assert.Equal("\"a,b \"\"quoted\"\"\"", ReportCsvFormatter.Escape("a,b \"quoted\""));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"https://example.com\")")]
    [InlineData("+SUM(1,2)")]
    [InlineData("-1+2")]
    [InlineData("@cmd")]
    [InlineData("   =1+1")]
    public void Csv_neutralizes_formula_like_strings(string value)
    {
        var escaped = ReportCsvFormatter.Escape(value);
        Assert.StartsWith("\"'", escaped, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_does_not_rewrite_numeric_negative_values()
    {
        Assert.Equal("\"-10\"", ReportCsvFormatter.Escape(-10m));
    }

    [Fact]
    public void Csv_output_has_utf8_bom()
    {
        var bytes = ReportCsvFormatter.Utf8WithBom(["a,b"]);
        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xEF, bytes[0]);
        Assert.Equal(0xBB, bytes[1]);
        Assert.Equal(0xBF, bytes[2]);
    }
}
