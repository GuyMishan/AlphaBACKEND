using Alpha.Application.Billing;
using Alpha.Domain.Billing;
using Alpha.Domain.Subscriptions;

namespace Alpha.Api.Tests;

public sealed class BillingCalculatorTests
{
    private readonly BillingCalculator _calculator = new();

    [Fact]
    public void Business_example_totals_999()
    {
        var plan = PlanWithCorrections(CorrectionBillingMode.PerCorrectedRow, 0.30m);
        var components = new[]
        {
            C(plan, BillingMetricType.Base, BillingPricingType.Fixed, 199m),
            C(plan, BillingMetricType.Employer, BillingPricingType.PerUnit, 40m, 1),
            C(plan, BillingMetricType.Employee, BillingPricingType.PerUnit, 1.5m, 100),
            C(plan, BillingMetricType.ReportRow, BillingPricingType.PerUnit, 0.60m, 500),
            C(plan, BillingMetricType.Correction, BillingPricingType.PerUnit, 0.30m, 0,
                CorrectionBillingMode.PerCorrectedRow)
        };

        var result = _calculator.Calculate(plan, components, [],
            new BillingUsageSnapshot(3, 180, 1400, 1, 200));

        Assert.Equal(999m, result.Total);
    }

    [Fact]
    public void Included_quantities_never_produce_negative_billable_usage()
    {
        var plan = PlanWithCorrections(CorrectionBillingMode.Free, null);
        var components = new[]
        {
            C(plan, BillingMetricType.Base, BillingPricingType.Fixed, 199m),
            C(plan, BillingMetricType.Employee, BillingPricingType.PerUnit, 2m, 50),
            C(plan, BillingMetricType.ReportRow, BillingPricingType.PerUnit, 0.5m, 500)
        };

        var result = _calculator.Calculate(plan, components, [],
            new BillingUsageSnapshot(0, 10, 100, 0, 0));

        Assert.Equal(199m, result.Total);
        Assert.All(result.Components, line => Assert.True(line.BillableQuantity >= 0));
    }

    [Fact]
    public void Corrections_can_be_free()
    {
        var plan = PlanWithCorrections(CorrectionBillingMode.Free, null);
        var result = _calculator.Calculate(plan,
            [C(plan, BillingMetricType.Correction, BillingPricingType.PerUnit, 100m, 0, CorrectionBillingMode.Free)],
            [], new BillingUsageSnapshot(0, 0, 0, 20, 400));

        Assert.Equal(0m, result.Total);
    }

    [Fact]
    public void Corrections_can_bill_per_correction_with_included_quantity()
    {
        var plan = PlanWithCorrections(CorrectionBillingMode.PerCorrection, 5m, includedCorrections: 10);
        var result = _calculator.Calculate(plan,
            [C(plan, BillingMetricType.Correction, BillingPricingType.PerUnit, 5m, 10, CorrectionBillingMode.PerCorrection)],
            [], new BillingUsageSnapshot(0, 0, 0, 14, 200));

        Assert.Equal(20m, result.Total);
    }

    [Fact]
    public void Corrections_can_bill_per_corrected_row()
    {
        var plan = PlanWithCorrections(CorrectionBillingMode.PerCorrectedRow, 0.3m, includedRows: 100);
        var result = _calculator.Calculate(plan,
            [C(plan, BillingMetricType.Correction, BillingPricingType.PerUnit, 0.3m, 100, CorrectionBillingMode.PerCorrectedRow)],
            [], new BillingUsageSnapshot(0, 0, 0, 2, 180));

        Assert.Equal(24m, result.Total);
    }

    [Fact]
    public void Corrections_can_use_regular_row_price()
    {
        var plan = PlanWithCorrections(CorrectionBillingMode.SameAsRegularRows, null);
        var components = new[]
        {
            C(plan, BillingMetricType.ReportRow, BillingPricingType.PerUnit, 0.8m, 0),
            C(plan, BillingMetricType.Correction, BillingPricingType.PerUnit, 0m, 0, CorrectionBillingMode.SameAsRegularRows)
        };

        var result = _calculator.Calculate(plan, components, [],
            new BillingUsageSnapshot(0, 0, 0, 1, 8));

        Assert.Equal(6.4m, result.Total);
    }

    [Fact]
    public void Tiered_pricing_uses_progressive_tiers()
    {
        var plan = PlanWithCorrections(CorrectionBillingMode.Free, null);
        var component = C(plan, BillingMetricType.Employee, BillingPricingType.Tiered, 0m);
        var tiers = new[]
        {
            new PlanPricingTier(component.Id, 0, 100, 2m),
            new PlanPricingTier(component.Id, 100, 500, 1.5m),
            new PlanPricingTier(component.Id, 500, null, 1m)
        };

        var result = _calculator.Calculate(plan, [component], tiers,
            new BillingUsageSnapshot(0, 600, 0, 0, 0));

        Assert.Equal(900m, result.Total);
    }

    private static Plan PlanWithCorrections(
        CorrectionBillingMode mode, decimal? unitPrice,
        decimal includedCorrections = 0, decimal includedRows = 0)
    {
        var plan = new Plan("TEST", "Test", 100, 10000, 100);
        plan.UpdateBillingDefinition("", "ILS", "Monthly", mode, unitPrice,
            includedCorrections, includedRows);
        return plan;
    }

    private static PlanPricingComponent C(
        Plan plan, BillingMetricType metric, BillingPricingType pricing,
        decimal price, decimal included = 0, CorrectionBillingMode? correctionMode = null) =>
        new(plan.Id, metric, pricing, price, included, null, null, true,
            plan.Version, DateTimeOffset.UtcNow.AddMinutes(-1), correctionMode);
}
