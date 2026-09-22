using Alpha.Application.Abstractions;
using Alpha.Domain.Billing;
using Alpha.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Billing;

public sealed record BillingRunResult(
    Guid BillingPeriodId,
    Guid BillingAccountId,
    BillingPeriodStatus Status,
    decimal Total,
    string Currency,
    BillingCalculation Calculation,
    Guid? PaymentId,
    BillingPaymentStatus? PaymentStatus,
    string? Error);

public interface IBillingCycleService
{
    Task<BillingRunResult> RunPeriodAsync(Guid billingAccountId, DateTimeOffset periodStart,
        DateTimeOffset periodEnd, bool charge, CancellationToken ct = default);
    Task<int> RetryPastDueAsync(TimeSpan gracePeriod, TimeSpan retryDelay, CancellationToken ct = default);
}

public sealed class BillingCycleService(
    IAlphaDbContext db,
    IBillingUsageCollector usageCollector,
    IBillingCalculator calculator,
    IPaymentProviderResolver providers) : IBillingCycleService
{
    public async Task<BillingRunResult> RunPeriodAsync(Guid billingAccountId, DateTimeOffset periodStart,
        DateTimeOffset periodEnd, bool charge, CancellationToken ct = default)
    {
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x => x.Id == billingAccountId, ct)
            ?? throw new InvalidOperationException("Billing account was not found.");

        var organizationId = account.OrganizationId ??
            await db.Employers.AsNoTracking()
                .Where(x => x.Id == account.EmployerId)
                .Select(x => x.OrganizationId)
                .SingleAsync(ct);

        var subscription = await db.Subscriptions.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.Status != SubscriptionStatus.Cancelled, ct)
            ?? throw new InvalidOperationException("No active subscription was found for the billing account.");

        var plan = await db.Plans.AsNoTracking().SingleAsync(x => x.Id == subscription.PlanId, ct);
        var components = await db.PlanPricingComponents.AsNoTracking()
            .Where(x => x.PlanId == plan.Id && x.IsEnabled &&
                        x.EffectiveFrom <= periodStart &&
                        (!x.EffectiveTo.HasValue || x.EffectiveTo > periodStart))
            .ToListAsync(ct);
        var componentIds = components.Select(x => x.Id).ToArray();
        var tiers = await db.PlanPricingTiers.AsNoTracking()
            .Where(x => componentIds.Contains(x.ComponentId))
            .ToListAsync(ct);

        var period = await db.BillingPeriods.SingleOrDefaultAsync(x =>
            x.BillingAccountId == billingAccountId &&
            x.PeriodStart == periodStart &&
            x.PeriodEnd == periodEnd, ct);

        if (period is null)
        {
            period = new BillingPeriod(account.Id, plan.Id, periodStart, periodEnd, plan.Currency);
            db.BillingPeriods.Add(period);
            await db.SaveChangesAsync(ct);

            var rawUsage = await usageCollector.CollectAsync(account, periodStart, periodEnd, ct);
            var calculation = calculator.Calculate(plan, components, tiers, rawUsage);
            period.SaveCalculation(calculation.Subtotal, calculation.Total, calculation.ToSnapshotJson(plan));

            foreach (var line in calculation.Components)
                db.BillingUsages.Add(new BillingUsage(account.Id, period.Id, line.Metric,
                    line.Quantity, line.IncludedQuantity, line.BillableQuantity, line.UnitPrice, line.Amount));

            await db.SaveChangesAsync(ct);
        }

        var currentCalculation = await RehydrateCalculationAsync(period, ct);
        if (!charge || period.Status == BillingPeriodStatus.Charged)
            return new BillingRunResult(period.Id, account.Id, period.Status, period.Total,
                period.Currency, currentCalculation, null, null, null);

        if (period.Total == 0)
        {
            period.MarkCharged();
            account.MarkStatus(BillingAccountStatus.Active);
            if (subscription.Status == SubscriptionStatus.PastDue)
                subscription.ChangeStatus(SubscriptionStatus.Active);
            await db.SaveChangesAsync(ct);
            return new BillingRunResult(period.Id, account.Id, period.Status, 0,
                period.Currency, currentCalculation, null, null, null);
        }

        if (account.PaymentMethodStatus != BillingPaymentMethodStatus.Active ||
            string.IsNullOrWhiteSpace(account.ProviderPaymentMethodId))
        {
            period.MarkPastDue();
            account.MarkStatus(BillingAccountStatus.PastDue);
            subscription.ChangeStatus(SubscriptionStatus.PastDue);
            await db.SaveChangesAsync(ct);
            return new BillingRunResult(period.Id, account.Id, period.Status, period.Total,
                period.Currency, currentCalculation, null, null, "payment_method_not_active");
        }

        var paymentKey = $"billing:{account.Id:N}:{periodStart:yyyyMMddHHmmss}:{periodEnd:yyyyMMddHHmmss}";
        var payment = await db.Payments.SingleOrDefaultAsync(x => x.IdempotencyKey == paymentKey, ct);
        if (payment is null)
        {
            payment = new Payment(account.Id, period.Id, period.Total, period.Currency, paymentKey);
            db.Payments.Add(payment);
            await db.SaveChangesAsync(ct);
        }
        else if (payment.Status == BillingPaymentStatus.Succeeded)
        {
            period.MarkCharged();
            account.MarkStatus(BillingAccountStatus.Active);
            await db.SaveChangesAsync(ct);
            return new BillingRunResult(period.Id, account.Id, period.Status, period.Total,
                period.Currency, currentCalculation, payment.Id, payment.Status, null);
        }

        var provider = providers.Resolve();
        var attemptNumber = (await db.PaymentAttempts
            .Where(x => x.PaymentId == payment.Id)
            .MaxAsync(x => (int?)x.AttemptNumber, ct) ?? 0) + 1;
        var attempt = new PaymentAttempt(payment.Id, attemptNumber, provider.Name,
            $"{paymentKey}:attempt:{attemptNumber}");
        db.PaymentAttempts.Add(attempt);
        payment.MarkProcessing(provider.Name);
        period.MarkCharging();
        await db.SaveChangesAsync(ct);

        PaymentChargeResult result;
        try
        {
            result = await provider.Charge(new PaymentChargeRequest(
                account.ProviderCustomerId,
                account.ProviderPaymentMethodId,
                payment.Amount,
                payment.Currency,
                $"ALPHA {period.PeriodStart:yyyy-MM-dd} - {period.PeriodEnd:yyyy-MM-dd}",
                payment.IdempotencyKey,
                true,
                account.CardExpiryMonth,
                account.CardExpiryYear), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            result = new PaymentChargeResult(false, string.Empty, null, "provider_exception", ex.Message);
        }

        attempt.Complete(result.Success, result.TransactionId, result.ErrorCode, result.ErrorMessage);
        if (result.Success)
        {
            payment.Succeed(result.TransactionId, result.InvoiceReference);
            period.MarkCharged();
            account.MarkStatus(BillingAccountStatus.Active);
            if (subscription.Status == SubscriptionStatus.PastDue)
                subscription.ChangeStatus(SubscriptionStatus.Active);
        }
        else
        {
            payment.Fail(result.ErrorCode, result.ErrorMessage);
            period.MarkPastDue();
            account.MarkStatus(BillingAccountStatus.PastDue);
            subscription.ChangeStatus(SubscriptionStatus.PastDue);
        }

        await db.SaveChangesAsync(ct);
        return new BillingRunResult(period.Id, account.Id, period.Status, period.Total,
            period.Currency, currentCalculation, payment.Id, payment.Status, result.ErrorMessage);
    }

    public async Task<int> RetryPastDueAsync(TimeSpan gracePeriod, TimeSpan retryDelay, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var retryBefore = now - retryDelay;
        var due = await db.BillingPeriods.AsNoTracking()
            .Where(x => x.Status == BillingPeriodStatus.PastDue && x.UpdatedAt <= retryBefore)
            .OrderBy(x => x.PeriodEnd)
            .Select(x => new { x.BillingAccountId, x.PeriodStart, x.PeriodEnd })
            .ToListAsync(ct);

        var processed = 0;
        foreach (var item in due)
        {
            await RunPeriodAsync(item.BillingAccountId, item.PeriodStart, item.PeriodEnd, true, ct);
            processed++;

            var current = await db.BillingPeriods.SingleAsync(x =>
                x.BillingAccountId == item.BillingAccountId &&
                x.PeriodStart == item.PeriodStart && x.PeriodEnd == item.PeriodEnd, ct);
            if (current.Status == BillingPeriodStatus.PastDue && now - current.PeriodEnd >= gracePeriod)
            {
                current.MarkSuspended();
                var account = await db.BillingAccounts.SingleAsync(x => x.Id == item.BillingAccountId, ct);
                account.MarkStatus(BillingAccountStatus.Suspended);
                var organizationId = account.OrganizationId ??
                    await db.Employers.AsNoTracking().Where(x => x.Id == account.EmployerId)
                        .Select(x => x.OrganizationId).SingleAsync(ct);
                var subscription = await db.Subscriptions.SingleOrDefaultAsync(
                    x => x.OrganizationId == organizationId && x.Status != SubscriptionStatus.Cancelled, ct);
                subscription?.ChangeStatus(SubscriptionStatus.Suspended);
                await db.SaveChangesAsync(ct);
            }
        }
        return processed;
    }

    private async Task<BillingCalculation> RehydrateCalculationAsync(BillingPeriod period, CancellationToken ct)
    {
        var lines = await db.BillingUsages.AsNoTracking()
            .Where(x => x.BillingPeriodId == period.Id)
            .OrderBy(x => x.MetricType)
            .Select(x => new BillingCalculationLine(x.MetricType, x.Quantity, x.IncludedQuantity,
                x.BillableQuantity, x.UnitPrice, x.Amount))
            .ToListAsync(ct);
        return new BillingCalculation(period.Subtotal, period.Total, lines);
    }
}
