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
}
