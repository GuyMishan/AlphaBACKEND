using Alpha.Domain.Billing;

namespace Alpha.Application.Billing;

public sealed record BillingGateDecision(
    bool Allowed,
    string? Error,
    string? BillingMode,
    string? Source,
    string? BilledThroughName,
    BillingPaymentMethodType? PaymentMethodType,
    BillingPaymentMethodStatus? PaymentMethodStatus,
    bool Configured)
{
    public static BillingGateDecision Deny(string error, BillingResolution? resolution = null) =>
        new(
            false,
            error,
            resolution?.BillingMode.ToString(),
            resolution?.Source,
            resolution?.BilledThroughName,
            resolution?.Account?.PaymentMethodType,
            resolution?.Account?.PaymentMethodStatus,
            resolution?.Account is not null);

    public static BillingGateDecision Allow(BillingResolution resolution) =>
        new(
            true,
            null,
            resolution.BillingMode.ToString(),
            resolution.Source,
            resolution.BilledThroughName,
            resolution.Account?.PaymentMethodType,
            resolution.Account?.PaymentMethodStatus,
            resolution.Account is not null);
}

public sealed class BillingGateService(BillingInheritanceService inheritance)
{
    public const string BillingAccountRequired = "billing_account_required";
    public const string BillingPaymentMethodNotActive = "billing_payment_method_not_active";
    public const string BillingPaymentMethodReferenceRequired = "billing_payment_method_reference_required";
    public const string BillingAccountSuspended = "billing_account_suspended";

    public async Task<BillingGateDecision> CanTransmitAsync(Guid employerId, CancellationToken ct = default)
    {
        var resolution = await inheritance.ResolveBillingAccountAsync(employerId, ct);
        if (resolution is null || resolution.Account is null)
            return BillingGateDecision.Deny(BillingAccountRequired, resolution);

        var account = resolution.Account;
        if (account.Status is BillingAccountStatus.Suspended or BillingAccountStatus.Cancelled)
            return BillingGateDecision.Deny(BillingAccountSuspended, resolution);

        if (account.PaymentMethodStatus != BillingPaymentMethodStatus.Active)
            return BillingGateDecision.Deny(BillingPaymentMethodNotActive, resolution);

        var hasProviderReference = account.PaymentMethodType switch
        {
            BillingPaymentMethodType.CreditCard =>
                !string.IsNullOrWhiteSpace(account.ProviderPaymentMethodId),

            BillingPaymentMethodType.BankDebit =>
                !string.IsNullOrWhiteSpace(account.BankDebitMandateReference)
                || !string.IsNullOrWhiteSpace(account.ProviderPaymentMethodId),

            _ => false
        };

        return hasProviderReference
            ? BillingGateDecision.Allow(resolution)
            : BillingGateDecision.Deny(BillingPaymentMethodReferenceRequired, resolution);
    }
}
