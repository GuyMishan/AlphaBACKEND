using Alpha.Domain.Common;

namespace Alpha.Domain.Billing;

public enum BillingPaymentMethodType
{
    CreditCard = 1,
    BankDebit = 2
}

public enum BillingPaymentMethodStatus
{
    NotConfigured = 1,
    Pending = 2,
    Active = 3,
    Failed = 4,
    Suspended = 5,
    Cancelled = 6
}

public sealed class BillingAccount : Entity
{
    private BillingAccount() { }

    public BillingAccount(Guid? organizationId, Guid? employerId)
    {
        SetScope(organizationId, employerId);
    }

    public Guid? OrganizationId { get; private set; }
    public Guid? EmployerId { get; private set; }
    public string BillingName { get; private set; } = string.Empty;
    public string TaxId { get; private set; } = string.Empty;
    public string InvoiceEmail { get; private set; } = string.Empty;
    public string BillingAddress { get; private set; } = string.Empty;

    public BillingPaymentMethodType PaymentMethodType { get; private set; } = BillingPaymentMethodType.CreditCard;
    public BillingPaymentMethodStatus PaymentMethodStatus { get; private set; } = BillingPaymentMethodStatus.NotConfigured;

    public string ProviderCustomerId { get; private set; } = string.Empty;
    public string ProviderPaymentMethodId { get; private set; } = string.Empty;

    public string CardBrand { get; private set; } = string.Empty;
    public string CardLast4 { get; private set; } = string.Empty;
    public int? CardExpiryMonth { get; private set; }
    public int? CardExpiryYear { get; private set; }

    public string BankDebitMandateReference { get; private set; } = string.Empty;

    public void UpdateBillingDetails(string? billingName, string? taxId, string? invoiceEmail, string? billingAddress,
        BillingPaymentMethodType paymentMethodType)
    {
        BillingName = Clean(billingName, 200);
        TaxId = Clean(taxId, 30);
        InvoiceEmail = Clean(invoiceEmail, 320);
        BillingAddress = Clean(billingAddress, 500);
        PaymentMethodType = paymentMethodType;
        Touch();
    }

    public void UpdateProviderMetadata(BillingPaymentMethodStatus status, string? providerCustomerId,
        string? providerPaymentMethodId, string? cardBrand, string? cardLast4,
        int? cardExpiryMonth, int? cardExpiryYear, string? bankDebitMandateReference)
    {
        if (cardExpiryMonth is < 1 or > 12) throw new ArgumentOutOfRangeException(nameof(cardExpiryMonth));
        if (!string.IsNullOrWhiteSpace(cardLast4) && (cardLast4.Length != 4 || !cardLast4.All(char.IsDigit)))
            throw new ArgumentException("Card last4 must contain exactly 4 digits.", nameof(cardLast4));

        PaymentMethodStatus = status;
        ProviderCustomerId = Clean(providerCustomerId, 200);
        ProviderPaymentMethodId = Clean(providerPaymentMethodId, 200);
        CardBrand = Clean(cardBrand, 40);
        CardLast4 = Clean(cardLast4, 4);
        CardExpiryMonth = cardExpiryMonth;
        CardExpiryYear = cardExpiryYear;
        BankDebitMandateReference = Clean(bankDebitMandateReference, 200);
        Touch();
    }

    private void SetScope(Guid? organizationId, Guid? employerId)
    {
        var organizationScope = organizationId.HasValue && organizationId.Value != Guid.Empty;
        var employerScope = employerId.HasValue && employerId.Value != Guid.Empty;
        if (organizationScope == employerScope)
            throw new ArgumentException("Billing account must belong to exactly one scope: organization or employer.");

        OrganizationId = organizationScope ? organizationId : null;
        EmployerId = employerScope ? employerId : null;
    }

    private static string Clean(string? value, int max)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed[..Math.Min(trimmed.Length, max)];
    }
}
