using Alpha.Application.Reporting;
using Alpha.Domain.Employees;
using Alpha.Domain.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class FeedbackResolutionContextMatcherTests
{
    [Fact]
    public void Product_match_prefers_unique_policy_number()
    {
        var employmentId = Guid.NewGuid();
        var reported = new ManualReportProduct(
            Guid.NewGuid(), PensionProductType.PensionFund, "POL-1",
            new DateOnly(2026, 9, 1), 10000m, "1", "1", false, null);

        var exact = Product(employmentId, PensionProductType.PensionFund, "POL-1", "F1", "001");
        var other = Product(employmentId, PensionProductType.PensionFund, "POL-2", "F2", "002");

        Assert.Same(exact, FeedbackResolutionContextMatcher.FindCurrentProduct(
            reported, employmentId, new[] { exact, other }));
    }

    [Fact]
    public void Product_match_does_not_fallback_to_type_when_report_has_stable_identifiers_that_do_not_match()
    {
        var employmentId = Guid.NewGuid();
        var reported = new ManualReportProduct(
            Guid.NewGuid(), PensionProductType.PensionFund, "OLD-POLICY",
            new DateOnly(2026, 9, 1), 10000m, "1", "1", false, null,
            fundExternalKey: "OLD-FUND", fundCode: "999");

        var onlySameType = Product(employmentId, PensionProductType.PensionFund, "NEW-POLICY", "NEW-FUND", "123");

        Assert.Null(FeedbackResolutionContextMatcher.FindCurrentProduct(
            reported, employmentId, new[] { onlySameType }));
    }

    [Fact]
    public void Product_match_may_use_type_only_when_snapshot_has_no_stable_product_reference()
    {
        var employmentId = Guid.NewGuid();
        var reported = new ManualReportProduct(
            Guid.NewGuid(), PensionProductType.StudyFund, "",
            new DateOnly(2026, 9, 1), 10000m, "1", "1", false, null);

        var only = Product(employmentId, PensionProductType.StudyFund, "", "", "");

        Assert.Same(only, FeedbackResolutionContextMatcher.FindCurrentProduct(
            reported, employmentId, new[] { only }));
    }

    [Fact]
    public void Contribution_match_fails_closed_when_current_master_contains_duplicates()
    {
        var employmentId = Guid.NewGuid();
        var product = Product(employmentId, PensionProductType.PensionFund, "POL-1", "F1", "001");
        var reported = new ManualContribution(
            Guid.NewGuid(), ContributionParty.Employee, ContributionComponent.Benefits, 600m, 6m, 0m);
        var first = new EmployeePensionContribution(
            product.Id, ContributionParty.Employee, ContributionComponent.Benefits, 6m);
        var second = new EmployeePensionContribution(
            product.Id, ContributionParty.Employee, ContributionComponent.Benefits, 6m);

        Assert.Null(FeedbackResolutionContextMatcher.FindCurrentContribution(
            reported, product, new[] { first, second }));
    }

    private static EmployeePensionProduct Product(
        Guid employmentId, PensionProductType type, string policy, string externalKey, string fundCode) =>
        new(employmentId, type, policy, 10000m, "1", "1", false, null,
            true, new DateOnly(2026, 1, 1), null, "body", "manufacturer",
            externalKey, fundCode, "fund", "company",
            SalaryAllocationType.Fixed, 10000m, 0);
}
