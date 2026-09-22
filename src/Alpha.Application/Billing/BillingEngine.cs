using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Domain.Billing;
using Alpha.Domain.Reporting;
using Alpha.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Billing;

public sealed record BillingUsageSnapshot(
    decimal Employers,
    decimal Employees,
    decimal ReportRows,
    decimal Corrections,
    decimal CorrectedRows)
{
    public decimal QuantityFor(BillingMetricType metric) => metric switch
    {
        BillingMetricType.Base => 1,
        BillingMetricType.Employer => Employers,
        BillingMetricType.Employee => Employees,
        BillingMetricType.ReportRow => ReportRows,
        BillingMetricType.Correction => Corrections,
        _ => 0
    };
}

public sealed record BillingCalculationLine(
    BillingMetricType Metric,
    decimal Quantity,
    decimal IncludedQuantity,
    decimal BillableQuantity,
    decimal UnitPrice,
    decimal Amount);

public sealed record BillingCalculation(decimal Subtotal, decimal Total, IReadOnlyList<BillingCalculationLine> Components)
{
    public string ToSnapshotJson(Plan plan) => JsonSerializer.Serialize(new
    {
        plan = new
        {
            plan.Id,
            plan.Code,
            plan.Name,
            plan.Version,
            plan.Currency,
            plan.BillingInterval,
            plan.EffectiveFrom,
            plan.EffectiveTo,
            plan.CorrectionBillingMode
        },
        subtotal = Subtotal,
        total = Total,
        components = Components
    }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
}

public interface IBillingCalculator
{
    BillingCalculation Calculate(Plan plan, IReadOnlyCollection<PlanPricingComponent> components,
        IReadOnlyCollection<PlanPricingTier> tiers, BillingUsageSnapshot usage);
}

public sealed class BillingCalculator : IBillingCalculator
{
    public BillingCalculation Calculate(Plan plan, IReadOnlyCollection<PlanPricingComponent> components,
        IReadOnlyCollection<PlanPricingTier> tiers, BillingUsageSnapshot usage)
    {
        var lines = new List<BillingCalculationLine>();
        foreach (var component in components.Where(x => x.IsEnabled && x.MetricType != BillingMetricType.Correction)
                     .OrderBy(x => x.MetricType))
        {
            var quantity = usage.QuantityFor(component.MetricType);
            lines.Add(CalculateComponent(component, tiers.Where(x => x.ComponentId == component.Id).ToArray(), quantity));
        }

        lines.Add(CalculateCorrection(plan, components, tiers, usage));
        var normalized = lines.Where(x => x.Amount != 0 || x.Quantity != 0 || x.Metric == BillingMetricType.Base).ToArray();
        var subtotal = normalized.Sum(x => x.Amount);
        return new BillingCalculation(subtotal, subtotal, normalized);
    }

    private static BillingCalculationLine CalculateCorrection(Plan plan,
        IReadOnlyCollection<PlanPricingComponent> components, IReadOnlyCollection<PlanPricingTier> tiers,
        BillingUsageSnapshot usage)
    {
        var configured = components.FirstOrDefault(x => x.IsEnabled && x.MetricType == BillingMetricType.Correction);
        var correctionMode = configured?.CorrectionMode ?? plan.CorrectionBillingMode;
        if (correctionMode == CorrectionBillingMode.Free)
            return new BillingCalculationLine(BillingMetricType.Correction, usage.Corrections, 0, 0, 0, 0);

        var reportRows = components.FirstOrDefault(x => x.IsEnabled && x.MetricType == BillingMetricType.ReportRow);

        var quantity = correctionMode == CorrectionBillingMode.PerCorrection
            ? usage.Corrections
            : usage.CorrectedRows;
        var included = configured?.IncludedQuantity ??
            (correctionMode == CorrectionBillingMode.PerCorrection
                ? plan.IncludedCorrections
                : plan.IncludedCorrectionRows);
        var price = correctionMode == CorrectionBillingMode.SameAsRegularRows
            ? reportRows?.UnitPrice ?? 0
            : configured?.UnitPrice ?? plan.CorrectionUnitPrice ?? 0;

        var synthetic = configured is null
            ? new PricingDefinition(BillingPricingType.PerUnit, price, included, null, null)
            : new PricingDefinition(configured.PricingType, price, included, configured.MinimumCharge, configured.MaximumCharge);
        return CalculateDefinition(BillingMetricType.Correction, synthetic,
            configured is null ? [] : tiers.Where(x => x.ComponentId == configured.Id).ToArray(), quantity);
    }

    private static BillingCalculationLine CalculateComponent(PlanPricingComponent component,
        IReadOnlyCollection<PlanPricingTier> tiers, decimal quantity) =>
        CalculateDefinition(component.MetricType,
            new PricingDefinition(component.PricingType, component.UnitPrice, component.IncludedQuantity,
                component.MinimumCharge, component.MaximumCharge), tiers, quantity);

    private static BillingCalculationLine CalculateDefinition(BillingMetricType metric, PricingDefinition pricing,
        IReadOnlyCollection<PlanPricingTier> tiers, decimal quantity)
    {
        var billable = Math.Max(0, quantity - pricing.IncludedQuantity);
        var raw = pricing.PricingType switch
        {
            BillingPricingType.Fixed => quantity > 0 ? pricing.UnitPrice : 0,
            BillingPricingType.PerUnit => billable * pricing.UnitPrice,
            BillingPricingType.Tiered => CalculateTiered(billable, tiers),
            _ => 0
        };

        var amount = raw;
        if (pricing.MinimumCharge.HasValue && quantity > 0) amount = Math.Max(amount, pricing.MinimumCharge.Value);
        if (pricing.MaximumCharge.HasValue) amount = Math.Min(amount, pricing.MaximumCharge.Value);

        return new BillingCalculationLine(metric, quantity, pricing.IncludedQuantity, billable,
            pricing.UnitPrice, decimal.Round(amount, 2, MidpointRounding.AwayFromZero));
    }

    private static decimal CalculateTiered(decimal billableQuantity, IReadOnlyCollection<PlanPricingTier> tiers)
    {
        if (billableQuantity <= 0) return 0;
        decimal amount = 0;
        foreach (var tier in tiers.OrderBy(x => x.FromQuantity))
        {
            var from = Math.Max(0, tier.FromQuantity);
            if (billableQuantity <= from) continue;
            var upper = tier.ToQuantity ?? billableQuantity;
            var units = Math.Max(0, Math.Min(billableQuantity, upper) - from);
            amount += units * tier.UnitPrice;
        }
        return amount;
    }

    private sealed record PricingDefinition(BillingPricingType PricingType, decimal UnitPrice,
        decimal IncludedQuantity, decimal? MinimumCharge, decimal? MaximumCharge);
}

public interface IBillingUsageCollector
{
    Task<BillingUsageSnapshot> CollectAsync(BillingAccount account, DateTimeOffset periodStart,
        DateTimeOffset periodEnd, CancellationToken ct = default);
}

public sealed class BillingUsageCollector(IAlphaDbContext db, BillingInheritanceService inheritance) : IBillingUsageCollector
{
    public async Task<BillingUsageSnapshot> CollectAsync(BillingAccount account, DateTimeOffset periodStart,
        DateTimeOffset periodEnd, CancellationToken ct = default)
    {
        if (periodEnd <= periodStart) throw new ArgumentException("Billing period end must be after start.");

        var accepted = db.ReportTransmissions.AsNoTracking()
            .Where(x => (x.Status == ReportTransmissionStatus.Sent || x.Status == ReportTransmissionStatus.Accepted) &&
                        ((x.SentAt ?? x.CompletedAt) >= periodStart) &&
                        ((x.SentAt ?? x.CompletedAt) < periodEnd));

        accepted = account.EmployerId.HasValue
            ? accepted.Where(x => x.EmployerId == account.EmployerId.Value)
            : accepted.Where(x => x.OrganizationId == account.OrganizationId);

        // A report is billable once, even if a provider callback/retry created more than one accepted transmission.
        var reportIds = await accepted.Select(x => x.ReportId).Distinct().ToListAsync(ct);
        if (reportIds.Count == 0) return new BillingUsageSnapshot(0, 0, 0, 0, 0);

        var reports = await db.ManualReports.AsNoTracking()
            .Where(x => reportIds.Contains(x.Id))
            .Select(x => new { x.Id, x.EmployerId, x.ReportKind })
            .ToListAsync(ct);

        if (account.OrganizationId.HasValue)
        {
            var employerIds = reports.Select(x => x.EmployerId).Distinct().ToArray();
            var billedThroughAccount = new HashSet<Guid>();
            foreach (var employerId in employerIds)
            {
                var resolution = await inheritance.ResolveBillingAccountAsync(employerId, ct);
                if (resolution?.Account?.Id == account.Id)
                    billedThroughAccount.Add(employerId);
            }
            reports = reports.Where(x => billedThroughAccount.Contains(x.EmployerId)).ToList();
        }

        var actualReportIds = reports.Select(x => x.Id).ToArray();
        if (actualReportIds.Length == 0) return new BillingUsageSnapshot(0, 0, 0, 0, 0);
        var correctionReportIds = reports.Where(x => x.ReportKind != ManualReportKind.Current).Select(x => x.Id).ToArray();

        var employers = reports.Select(x => x.EmployerId).Distinct().Count();
        var employees = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => actualReportIds.Contains(x.ReportId))
            .Select(x => x.EmploymentId)
            .Distinct()
            .CountAsync(ct);

        var reportEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => actualReportIds.Contains(x.ReportId))
            .Select(x => x.Id)
            .ToListAsync(ct);
        var reportRows = await db.ManualReportProducts.AsNoTracking()
            .CountAsync(x => reportEmployeeIds.Contains(x.ReportEmployeeId), ct);

        var correctedRows = 0;
        if (correctionReportIds.Length > 0)
        {
            var correctionEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
                .Where(x => correctionReportIds.Contains(x.ReportId))
                .Select(x => x.Id)
                .ToListAsync(ct);
            correctedRows = await db.ManualReportProducts.AsNoTracking()
                .CountAsync(x => correctionEmployeeIds.Contains(x.ReportEmployeeId), ct);
        }

        return new BillingUsageSnapshot(employers, employees, reportRows,
            correctionReportIds.Length, correctedRows);
    }
}
