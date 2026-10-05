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
    public void Stress_scenario_covers_every_official_failure_code_and_repeats_scoped_errors_correctly()
    {
        var employeeA = Guid.NewGuid();
        var employeeB = Guid.NewGuid();
        var productA = Guid.NewGuid();
        var productB = Guid.NewGuid();
        var productC = Guid.NewGuid();
        var rows = new[]
        {
            new SimulatedStressContribution(Guid.NewGuid(), productA, employeeA),
            new SimulatedStressContribution(Guid.NewGuid(), productA, employeeA),
            new SimulatedStressContribution(Guid.NewGuid(), productA, employeeA),
            new SimulatedStressContribution(Guid.NewGuid(), productB, employeeA),
            new SimulatedStressContribution(Guid.NewGuid(), productB, employeeA),
            new SimulatedStressContribution(Guid.NewGuid(), productC, employeeB),
            new SimulatedStressContribution(Guid.NewGuid(), productC, employeeB),
        };

        var outcomes = SimulatedClearinghouseResponder.BuildStressContributionOutcomes(rows);

        var emittedCodes = outcomes.Select(x => x.ErrorCode).Distinct().ToHashSet();
        var missingCodes = EmployerInterfaceLineFeedbackParser.OfficialFailureCodes
            .Where(code => !emittedCodes.Contains(code))
            .ToArray();

        Assert.All(missingCodes, code =>
        {
            var group = SimulatedClearinghouseResponder.StressExclusiveGroup(code);
            Assert.False(string.IsNullOrWhiteSpace(group));
            Assert.Contains(emittedCodes, emitted =>
                SimulatedClearinghouseResponder.StressExclusiveGroup(emitted) == group);
        });

        var code15 = outcomes.Where(x => x.ErrorCode == 15).ToArray();
        Assert.NotEmpty(code15);
        var productFor15 = rows.Single(x => x.ContributionId == code15[0].ContributionId).ProductId;
        Assert.Equal(
            rows.Where(x => x.ProductId == productFor15).Select(x => x.ContributionId).OrderBy(x => x),
            code15.Select(x => x.ContributionId).OrderBy(x => x));

        var code4 = outcomes.Where(x => x.ErrorCode == 4).ToArray();
        Assert.NotEmpty(code4);
        var employeeFor4 = rows.Single(x => x.ContributionId == code4[0].ContributionId).EmployeeId;
        Assert.Equal(
            rows.Where(x => x.EmployeeId == employeeFor4).Select(x => x.ContributionId).OrderBy(x => x),
            code4.Select(x => x.ContributionId).OrderBy(x => x));

        var code53 = outcomes.Where(x => x.ErrorCode == 53).ToArray();
        Assert.Single(code53);

        var code27 = outcomes.Where(x => x.ErrorCode == 27).ToArray();
        Assert.Equal(rows.Length, code27.Length);
    }

    [Theory]
    [InlineData(new int[] { }, "success")]
    [InlineData(new int[] { 31 }, "success")]
    [InlineData(new int[] { 53 }, "partial")]
    [InlineData(new int[] { 15, 53 }, "partial")]
    [InlineData(new int[] { 45 }, "error")]
    [InlineData(new int[] { 53, 45 }, "error")]
    public void Stress_transfer_mode_is_not_fully_allocated_when_actionable_errors_exist(
        int[] codes,
        string expected)
    {
        Assert.Equal(expected, SimulatedClearinghouseResponder.ResolveStressTransferMode(codes));
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
    [Theory]
    [InlineData(111, 112)]
    [InlineData(109, 110)]
    [InlineData(102, 103)]
    [InlineData(105, 106)]
    [InlineData(107, 108)]
    [InlineData(113, 114)]
    [InlineData(6, 7)]
    [InlineData(81, 82)]
    [InlineData(83, 84)]
    [InlineData(84, 85)]
    [InlineData(94, 95)]
    [InlineData(95, 96)]
    [InlineData(100, 101)]
    public void Stress_exclusive_codes_share_the_same_exclusion_group(int left, int right)
    {
        Assert.Equal(
            SimulatedClearinghouseResponder.StressExclusiveGroup(left),
            SimulatedClearinghouseResponder.StressExclusiveGroup(right));
    }

    [Fact]
    public void Stress_affidavit_family_is_globally_exclusive_within_one_report()
    {
        var employeeA = Guid.NewGuid();
        var employeeB = Guid.NewGuid();
        var productA = Guid.NewGuid();
        var productB = Guid.NewGuid();
        var rows = new[]
        {
            new SimulatedStressContribution(Guid.NewGuid(), productA, employeeA),
            new SimulatedStressContribution(Guid.NewGuid(), productA, employeeA),
            new SimulatedStressContribution(Guid.NewGuid(), productB, employeeB),
            new SimulatedStressContribution(Guid.NewGuid(), productB, employeeB)
        };

        var outcomes = SimulatedClearinghouseResponder.BuildStressContributionOutcomes(rows);
        var affidavitCodes = new HashSet<int> { 102, 103, 109, 110, 111, 112 };
        var emitted = outcomes.Select(x => x.ErrorCode).Where(affidavitCodes.Contains).Distinct().ToArray();

        Assert.Single(emitted);
    }


}
