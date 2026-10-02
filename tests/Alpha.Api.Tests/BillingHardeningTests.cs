using Alpha.Domain.Billing;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class BillingHardeningTests
{
    [Fact]
    public void Provider_error_text_is_bounded_to_database_limits()
    {
        var payment = new Payment(Guid.NewGuid(), Guid.NewGuid(), 10m, "ILS", "payment-key");
        var attempt = new PaymentAttempt(payment.Id, 1, "CardCom", "attempt-key");
        var refund = new Refund(payment.Id, 1m, "reason", "refund-key");
        var webhook = new ProviderWebhookEvent("CardCom", "event", "hash", "{}");
        var huge = new string('x', 5000);

        payment.Fail(huge, huge);
        attempt.Complete(false, huge, huge, huge);
        refund.Complete(false, huge, huge);
        webhook.Complete(ProviderWebhookStatus.Failed, huge);

        Assert.Equal(120, payment.FailureCode.Length);
        Assert.Equal(1000, payment.FailureMessage.Length);
        Assert.Equal(200, attempt.ProviderTransactionId.Length);
        Assert.Equal(120, attempt.ErrorCode.Length);
        Assert.Equal(1000, attempt.ErrorMessage.Length);
        Assert.Equal(200, refund.ProviderRefundId.Length);
        Assert.Equal(1000, refund.ErrorMessage.Length);
        Assert.Equal(1000, webhook.ErrorMessage.Length);
    }

    [Fact]
    public void Payment_method_provider_metadata_is_bounded()
    {
        var method = new PaymentMethod(Guid.NewGuid(), "CardCom", BillingPaymentMethodType.CreditCard);
        var huge = new string('9', 500);

        method.Activate(huge, huge, huge, "12345678", 12, 2030, huge);

        Assert.Equal(200, method.ProviderCustomerId.Length);
        Assert.Equal(200, method.ProviderPaymentMethodId.Length);
        Assert.Equal(40, method.CardBrand.Length);
        Assert.Equal(4, method.CardLast4.Length);
        Assert.Equal(200, method.MandateReference.Length);
    }

    [Fact]
    public void Ambiguous_charge_states_are_explicit_and_not_retryable_failures()
    {
        var accountId = Guid.NewGuid();
        var period = new BillingPeriod(accountId, DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow);
        var payment = new Payment(accountId, period.Id, 100m, "ILS", "billing-key");
        var attempt = new PaymentAttempt(payment.Id, 1, "PayPlus", "attempt-key");

        period.MarkCharging();
        payment.MarkProcessing("PayPlus");
        attempt.MarkReconciliationRequired("provider_result_unknown");
        payment.MarkReconciliationRequired("provider_result_unknown");
        period.MarkReconciliationRequired();

        Assert.Equal(BillingPaymentAttemptStatus.ReconciliationRequired, attempt.Status);
        Assert.Equal(BillingPaymentStatus.ReconciliationRequired, payment.Status);
        Assert.Equal(BillingPeriodStatus.ReconciliationRequired, period.Status);
        Assert.Equal("provider_result_unknown", payment.FailureCode);
    }

    [Fact]
    public void Ambiguous_refund_stays_reserved_as_pending()
    {
        var refund = new Refund(Guid.NewGuid(), 25m, new string('r', 800), "refund-key");

        refund.RecordPendingError("provider_result_unknown");

        Assert.Equal(BillingRefundStatus.Pending, refund.Status);
        Assert.Equal("provider_result_unknown", refund.ErrorMessage);
        Assert.Equal(500, refund.Reason.Length);
    }

    [Fact]
    public void Refund_rejects_oversized_idempotency_key()
    {
        Assert.Throws<ArgumentException>(() =>
            new Refund(Guid.NewGuid(), 25m, "reason", new string('k', 161)));
    }

    [Fact]
    public void Billing_account_preserves_active_method_while_replacement_setup_is_pending()
    {
        var account = new BillingAccount(Guid.NewGuid(), null);
        var methodId = Guid.NewGuid();
        account.UpdateProviderMetadata(BillingPaymentMethodStatus.Active, "customer", "old-token",
            "Visa", "4242", 12, 2030, "");
        account.SetDefaultPaymentMethod(methodId);

        account.BeginProviderSetup("PayPlus", "customer-2", $"{account.Id:D}:setup");

        Assert.Equal(BillingPaymentMethodStatus.Active, account.PaymentMethodStatus);
        Assert.Equal("old-token", account.ProviderPaymentMethodId);
        Assert.Equal(methodId, account.DefaultPaymentMethodId);
        Assert.Equal("PayPlus", account.PendingProvider);
        Assert.Equal("customer-2", account.PendingProviderCustomerId);
        Assert.NotEmpty(account.PendingSetupReference);
    }
}
