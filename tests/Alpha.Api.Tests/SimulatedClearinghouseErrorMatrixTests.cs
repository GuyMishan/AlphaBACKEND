using System.Xml.Linq;
using Alpha.Api.Services;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class SimulatedClearinghouseErrorMatrixTests
{
    [Fact]
    public void Official_error_catalog_matches_summary_feedback_xsd_exactly()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "docs", "specifications", "employer-interface", "006",
            "mimshak_maasikim_mesakem_xsd_schema_006.xsd.xml");
        var doc = XDocument.Load(path);
        XNamespace xsd = "http://www.w3.org/2001/XMLSchema";

        var element = doc.Descendants(xsd + "element")
            .First(x => string.Equals((string?)x.Attribute("name"), "SUG-SHGIHA", StringComparison.Ordinal));
        var official = element.Descendants(xsd + "enumeration")
            .Select(x => int.Parse((string)x.Attribute("value")!))
            .Distinct()
            .OrderBy(x => x)
            .ToArray();

        Assert.Equal(official, EmployerInterfaceLineFeedbackParser.OfficialErrorCodes.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Every_official_failure_code_has_a_real_description_and_can_be_simulated()
    {
        foreach (var code in EmployerInterfaceLineFeedbackParser.OfficialFailureCodes)
        {
            var scenario = SimulatedClearinghouseResponder.ParseScenario($"error:{code}");

            Assert.Equal("error", scenario.Mode);
            Assert.Equal(code, scenario.ErrorCode);
            Assert.DoesNotContain("ראו פירוט במשוב הרשמי",
                EmployerInterfaceLineFeedbackParser.Description(code), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void All_errors_expands_to_every_official_failure_code_exactly_once()
    {
        var expanded = SimulatedClearinghouseResponder.ExpandScenario(
            SimulatedClearinghouseResponder.ParseScenario("all-errors"));

        Assert.Equal(EmployerInterfaceLineFeedbackParser.OfficialFailureCodes.Count, expanded.Count);
        Assert.Equal(
            EmployerInterfaceLineFeedbackParser.OfficialFailureCodes.OrderBy(x => x),
            expanded.Select(x => x.ErrorCode!.Value).OrderBy(x => x));
        Assert.All(expanded, x => Assert.Equal("error", x.Mode));
    }

    [Fact]
    public void Mixed_scenario_is_preserved_for_auto_responder()
    {
        var scenario = SimulatedClearinghouseResponder.ParseScenario("mixed");

        Assert.Equal("mixed", scenario.Mode);
        Assert.Equal("mixed", scenario.CanonicalName);
        Assert.Null(scenario.ErrorCode);
        Assert.Equal("EMPFED", scenario.FeedbackInterface);
    }

    [Fact]
    public void Stress_scenario_distributes_every_official_failure_code_across_available_contributions()
    {
        var contributionIds = Enumerable.Range(0, 43).Select(_ => Guid.NewGuid()).ToArray();
        var outcomes = SimulatedClearinghouseResponder.BuildStressContributionOutcomes(contributionIds);

        Assert.Equal(EmployerInterfaceLineFeedbackParser.OfficialFailureCodes.Count, outcomes.Count);
        Assert.Equal(
            EmployerInterfaceLineFeedbackParser.OfficialFailureCodes.OrderBy(x => x),
            outcomes.Select(x => x.ErrorCode).OrderBy(x => x));
        Assert.All(outcomes, outcome => Assert.Contains(outcome.ContributionId, contributionIds));
        Assert.True(outcomes.GroupBy(x => x.ContributionId).Max(group => group.Count()) >= 2);
    }

    [Theory]
    [InlineData("success", "success", null)]
    [InlineData("partial", "partial", null)]
    [InlineData("error", "error", null)]
    [InlineData("in-transit", "in-transit", null)]
    [InlineData("stress", "stress", null)]
    [InlineData("error:53", "error", 53)]
    [InlineData("partial:116", "partial", 116)]
    [InlineData("error:999", "success", null)]
    public void Scenario_parser_supports_full_feedback_matrix(
        string input, string expectedMode, int? expectedErrorCode)
    {
        var scenario = SimulatedClearinghouseResponder.ParseScenario(input);

        Assert.Equal(expectedMode, scenario.Mode);
        Assert.Equal(expectedErrorCode, scenario.ErrorCode);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
