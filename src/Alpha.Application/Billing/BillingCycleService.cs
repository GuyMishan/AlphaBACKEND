using Alpha.Application.Abstractions;
using Alpha.Domain.Billing;
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
    Task<int> RetryPastDueAsync(TimeSpan gracePeriod, TimeSpan retryDelay, int maxAttempts = 4,
        CancellationToken ct = default);
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

        var accountComponents = await db.BillingAccountPricingComponents.AsNoTracking()
            .Where(x => x.BillingAccountId == account.Id &&
                        x.EffectiveFrom < periodEnd &&
                        (!x.EffectiveTo.HasValue || x.EffectiveTo >= periodEnd) &&
                        x.IsEnabled)
            .ToListAsync(ct);

        var components = accountComponents;
        var tiers = await db.BillingAccountPricingTiers.AsNoTracking()
            .Where(x => accountComponents.Select(c => c.Id).Contains(x.ComponentId))
            .ToListAsync(ct);

        var period = await db.BillingPeriods.SingleOrDefaultAsync(x =>
            x.BillingAccountId == billingAccountId &&
            x.PeriodStart == periodStart &&
            x.PeriodEnd == periodEnd, ct);

        if (period is null)
        {
            period = new BillingPeriod(account.Id, periodStart, periodEnd, "ILS");
            db.BillingPeriods.Add(period);
            await db.SaveChangesAsync(ct);

            var rawUsage = await usageCollector.CollectAsync(account, periodStart, periodEnd, ct);
            var calculation = calculator.Calculate(components, tiers, rawUsage);
            period.SaveCalculation(calculation.Subtotal, calculation.Total, calculation.ToSnapshotJson());

            foreach (var line in calculation.Components)
                db.BillingUsages.Add(new BillingUsage(account.Id, period.Id, line.Metric,
                    line.Quantity, line.IncludedQuantity, line.BillableQuantity, line.UnitPrice, line.Amount));

            await db.SaveChangesAsync(ct);
        }

        var currentCalculation = await RehydrateCalculationAsync(period, ct);
        if (!charge || period.Status == BillingPeriodStatus.Charged)
            return new BillingRunResult(period.Id, account.Id, period.Status, period.Total,
                period.Currency, currentCalculation, null, null, null);

        if (period.Status == BillingPeriodStatus.ReconciliationRequired)
        {
            var unresolvedPayment = await db.Payments.AsNoTracking()
                .Where(x => x.BillingPeriodId == period.Id &&
                            x.Status == BillingPaymentStatus.ReconciliationRequired)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            return new BillingRunResult(period.Id, account.Id, period.Status, period.Total,
                period.Currency, currentCalculation, unresolvedPayment?.Id,
                unresolvedPayment?.Status, "payment_reconciliation_required");
        }

        if (account.Status is BillingAccountStatus.Suspended or BillingAccountStatus.Cancelled)
            return new BillingRunResult(period.Id, account.Id, period.Status, period.Total,
                period.Currency, currentCalculation, null, null, "billing_account_not_chargeable");

        if (period.Total == 0)
        {
            period.MarkCharged();
            await db.SaveChangesAsync(ct);
            return new BillingRunResult(period.Id, account.Id, period.Status, 0,
                period.Currency, currentCalculation, null, null, null);
        }

        var paymentMethod = account.DefaultPaymentMethodId.HasValue
            ? await db.PaymentMethods.SingleOrDefaultAsync(
                x => x.Id == account.DefaultPaymentMethodId.Value &&
                     x.BillingAccountId == account.Id &&
                     x.Status == BillingPaymentMethodStatus.Active, ct)
            : null;

        if (account.PaymentMethodStatus != BillingPaymentMethodStatus.Active ||
            paymentMethod is null ||
            string.IsNullOrWhiteSpace(paymentMethod.ProviderPaymentMethodId))
        {
            period.MarkPastDue();
            account.MarkStatus(BillingAccountStatus.PastDue);
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
        else if (payment.Status is BillingPaymentStatus.Succeeded
                 or BillingPaymentStatus.PartiallyRefunded
                 or BillingPaymentStatus.Refunded)
        {
            period.MarkCharged();
            if (account.Status == BillingAccountStatus.PastDue)
                account.MarkStatus(BillingAccountStatus.Active);
            await db.SaveChangesAsync(ct);
            return new BillingRunResult(period.Id, account.Id, period.Status, period.Total,
                period.Currency, currentCalculation, payment.Id, payment.Status, null);
        }

        var provider = providers.Resolve(paymentMethod.Provider);
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
                paymentMethod.ProviderCustomerId,
                paymentMethod.ProviderPaymentMethodId,
                payment.Amount,
                payment.Currency,
                $"ALPHA {period.PeriodStart:yyyy-MM-dd} - {period.PeriodEnd:yyyy-MM-dd}",
                payment.IdempotencyKey,
                true,
                paymentMethod.CardExpiryMonth,
                paymentMethod.CardExpiryYear), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            attempt.MarkReconciliationRequired("provider_result_unknown");
            payment.MarkReconciliationRequired("Charge request was cancelled after dispatch; provider result is unknown.");
            period.MarkReconciliationRequired();
            account.MarkStatus(BillingAccountStatus.PastDue);
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            attempt.MarkReconciliationRequired("provider_result_unknown");
            payment.MarkReconciliationRequired("Charge result is unknown and requires reconciliation.");
            period.MarkReconciliationRequired();
            account.MarkStatus(BillingAccountStatus.PastDue);
            await db.SaveChangesAsync(CancellationToken.None);
            return new BillingRunResult(period.Id, account.Id, period.Status, period.Total,
                period.Currency, currentCalculation, payment.Id, payment.Status, "provider_result_unknown");
        }

        if (!result.Success && result.ErrorCode == "transaction_id_missing")
        {
            attempt.MarkReconciliationRequired(result.ErrorMessage ?? "provider_result_unknown");
            payment.MarkReconciliationRequired("Provider approved the charge but did not return a transaction id.");
            period.MarkReconciliationRequired();
            account.MarkStatus(BillingAccountStatus.PastDue);
        }
        else
        {
            attempt.Complete(result.Success, result.TransactionId, result.ErrorCode, result.ErrorMessage);
            if (result.Success)
            {
                payment.Succeed(result.TransactionId, result.InvoiceReference);
                period.MarkCharged();
                account.MarkStatus(BillingAccountStatus.Active);
            }
            else
            {
                payment.Fail(result.ErrorCode, result.ErrorMessage);
                period.MarkPastDue();
                if (result.ErrorCode is "expiry_required" or "token_required")
                {
                    paymentMethod.MarkStatus(BillingPaymentMethodStatus.Failed);
                    account.UpdateProviderMetadata(
                        BillingPaymentMethodStatus.Failed,
                        paymentMethod.ProviderCustomerId,
                        paymentMethod.ProviderPaymentMethodId,
                        paymentMethod.CardBrand,
                        paymentMethod.CardLast4,
                        paymentMethod.CardExpiryMonth,
                        paymentMethod.CardExpiryYear,
                        paymentMethod.MandateReference);
                }
                else
                {
                    account.MarkStatus(BillingAccountStatus.PastDue);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return new BillingRunResult(period.Id, account.Id, period.Status, period.Total,
            period.Currency, currentCalculation, payment.Id, payment.Status, result.ErrorMessage);
    }

    public async Task<int> RetryPastDueAsync(TimeSpan gracePeriod, TimeSpan retryDelay, int maxAttempts = 4,
        CancellationToken ct = default)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        var now = DateTimeOffset.UtcNow;
        var retryBefore = now - retryDelay;

        var staleCharging = await db.BillingPeriods
            .Where(x => x.Status == BillingPeriodStatus.Charging && x.UpdatedAt <= retryBefore)
            .ToListAsync(ct);
        foreach (var period in staleCharging)
        {
            period.MarkReconciliationRequired();
            var payments = await db.Payments
                .Where(x => x.BillingPeriodId == period.Id && x.Status == BillingPaymentStatus.Processing)
                .ToListAsync(ct);
            foreach (var payment in payments)
            {
                payment.MarkReconciliationRequired("Billing worker stopped before the provider result was persisted.");
                var attempts = await db.PaymentAttempts
                    .Where(x => x.PaymentId == payment.Id && x.Status == BillingPaymentAttemptStatus.Pending)
                    .ToListAsync(ct);
                foreach (var attempt in attempts)
                    attempt.MarkReconciliationRequired("Billing worker stopped before the provider result was persisted.");
            }
            var account = await db.BillingAccounts.SingleAsync(x => x.Id == period.BillingAccountId, ct);
            account.MarkStatus(BillingAccountStatus.PastDue);
        }
        if (staleCharging.Count > 0)
            await db.SaveChangesAsync(ct);

        var due = await db.BillingPeriods.AsNoTracking()
            .Where(x => x.Status == BillingPeriodStatus.PastDue && x.UpdatedAt <= retryBefore)
            .OrderBy(x => x.PeriodEnd)
            .Select(x => new { x.BillingAccountId, x.PeriodStart, x.PeriodEnd })
            .ToListAsync(ct);

        var processed = 0;
        foreach (var item in due)
        {
            var periodId = await db.BillingPeriods.AsNoTracking()
                .Where(x => x.BillingAccountId == item.BillingAccountId &&
                            x.PeriodStart == item.PeriodStart && x.PeriodEnd == item.PeriodEnd)
                .Select(x => x.Id)
                .SingleAsync(ct);
            var attempts = await db.PaymentAttempts.AsNoTracking()
                .CountAsync(x => db.Payments.Any(p => p.Id == x.PaymentId && p.BillingPeriodId == periodId), ct);

            if (attempts < maxAttempts)
            {
                await RunPeriodAsync(item.BillingAccountId, item.PeriodStart, item.PeriodEnd, true, ct);
                processed++;
            }

            var current = await db.BillingPeriods.SingleAsync(x =>
                x.BillingAccountId == item.BillingAccountId &&
                x.PeriodStart == item.PeriodStart && x.PeriodEnd == item.PeriodEnd, ct);
            if (current.Status == BillingPeriodStatus.PastDue && now - current.PeriodEnd >= gracePeriod)
            {
                current.MarkSuspended();
                var account = await db.BillingAccounts.SingleAsync(x => x.Id == item.BillingAccountId, ct);
                account.MarkStatus(BillingAccountStatus.Suspended);
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
