using Alpha.Domain.Billing;
using Alpha.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public static class BillingPlanSeedInitializer
{
    public static async Task EnsureSeededAsync(AlphaDbContext db, CancellationToken ct = default)
    {
        await EnsurePlanAsync(
            db,
            SubscriptionSchemaInitializer.FreePlanCode,
            "Free",
            "מסלול התנסות ללא חיוב.",
            maxEmployers: 1,
            maxEmployees: 3,
            maxUsers: 1,
            CorrectionBillingMode.Free,
            correctionUnitPrice: null,
            components:
            [
                new(BillingMetricType.Base, BillingPricingType.Fixed, 0m, 0m, false),
                new(BillingMetricType.Employer, BillingPricingType.PerUnit, 0m, 0m, false),
                new(BillingMetricType.Employee, BillingPricingType.PerUnit, 0m, 0m, false),
                new(BillingMetricType.ReportRow, BillingPricingType.PerUnit, 0m, 0m, false)
            ],
            ct);

        await EnsurePlanAsync(
            db,
            SubscriptionSchemaInitializer.BusinessPlanCode,
            "Business",
            "מסלול למעסיק יחיד: תשלום בסיס חודשי ותמחור לפי שימוש.",
            maxEmployers: 1,
            maxEmployees: 10000,
            maxUsers: 5,
            CorrectionBillingMode.Free,
            correctionUnitPrice: null,
            components:
            [
                new(BillingMetricType.Base, BillingPricingType.Fixed, 199m, 0m, true),
                new(BillingMetricType.Employer, BillingPricingType.PerUnit, 0m, 0m, false),
                new(BillingMetricType.Employee, BillingPricingType.PerUnit, 2m, 0m, true),
                new(BillingMetricType.ReportRow, BillingPricingType.PerUnit, 0.50m, 0m, true)
            ],
            ct);

        await EnsurePlanAsync(
            db,
            SubscriptionSchemaInitializer.MultiEmployerPlanCode,
            "Multi Employer",
            "מסלול מבוסס שימוש למספר מעסיקים, עם תמחור עובדים, שורות ותיקונים.",
            maxEmployers: 20,
            maxEmployees: 5000,
            maxUsers: 20,
            CorrectionBillingMode.PerCorrectedRow,
            correctionUnitPrice: 0.30m,
            components:
            [
                new(BillingMetricType.Base, BillingPricingType.Fixed, 0m, 0m, false),
                new(BillingMetricType.Employer, BillingPricingType.PerUnit, 0m, 0m, false),
                new(BillingMetricType.Employee, BillingPricingType.PerUnit, 1m, 0m, true),
                new(BillingMetricType.ReportRow, BillingPricingType.PerUnit, 0.70m, 0m, true)
            ],
            ct);

        await EnsurePlanAsync(
            db,
            SubscriptionSchemaInitializer.OrganizationPlanCode,
            "Organization",
            "מסלול חודשי קבוע לארגון, עד 5 מעסיקים ו-500 עובדים.",
            maxEmployers: 5,
            maxEmployees: 500,
            maxUsers: 20,
            CorrectionBillingMode.Free,
            correctionUnitPrice: null,
            components:
            [
                new(BillingMetricType.Base, BillingPricingType.Fixed, 999m, 0m, true),
                new(BillingMetricType.Employer, BillingPricingType.PerUnit, 0m, 5m, false),
                new(BillingMetricType.Employee, BillingPricingType.PerUnit, 0m, 500m, false),
                new(BillingMetricType.ReportRow, BillingPricingType.PerUnit, 0m, 5000m, false)
            ],
            ct);
    }

    private static async Task EnsurePlanAsync(
        AlphaDbContext db,
        string code,
        string name,
        string description,
        int maxEmployers,
        int maxEmployees,
        int maxUsers,
        CorrectionBillingMode correctionMode,
        decimal? correctionUnitPrice,
        IReadOnlyCollection<DefaultComponent> components,
        CancellationToken ct)
    {
        var plan = await db.Plans.SingleOrDefaultAsync(x => x.Code == code, ct);
        var isNew = plan is null;

        if (plan is null)
        {
            plan = new Plan(code, name, maxEmployers, maxEmployees, maxUsers);
            plan.UpdateBillingDefinition(
                description,
                "ILS",
                "Monthly",
                correctionMode,
                correctionUnitPrice,
                includedCorrections: 0,
                includedCorrectionRows: 0);
            db.Plans.Add(plan);
            await db.SaveChangesAsync(ct);
        }

        var hasPricing = await db.PlanPricingComponents.AsNoTracking()
            .AnyAsync(x => x.PlanId == plan.Id && x.EffectiveTo == null, ct);

        if (hasPricing)
            return;

        if (!isNew)
        {
            plan.UpdateDefinition(name, maxEmployers, maxEmployees, maxUsers, isActive: true);
            plan.UpdateBillingDefinition(
                description,
                "ILS",
                "Monthly",
                correctionMode,
                correctionUnitPrice,
                includedCorrections: 0,
                includedCorrectionRows: 0);
        }

        foreach (var component in components)
        {
            db.PlanPricingComponents.Add(new PlanPricingComponent(
                plan.Id,
                component.MetricType,
                component.PricingType,
                component.UnitPrice,
                component.IncludedQuantity,
                minimumCharge: null,
                maximumCharge: null,
                component.IsEnabled,
                plan.Version,
                plan.EffectiveFrom,
                component.MetricType == BillingMetricType.Correction ? correctionMode : null));
        }

        await db.SaveChangesAsync(ct);
    }

    private sealed record DefaultComponent(
        BillingMetricType MetricType,
        BillingPricingType PricingType,
        decimal UnitPrice,
        decimal IncludedQuantity,
        bool IsEnabled);
}
