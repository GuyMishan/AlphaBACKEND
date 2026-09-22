using Alpha.Domain.Common;

namespace Alpha.Domain.Billing;

public enum BillingOwnerType { Organization = 1, Employer = 2 }
public enum BillingMode { OrganizationBilling = 1, IndependentEmployerBilling = 2 }
public enum BillingAccountStatus { PendingSetup = 1, Active = 2, PastDue = 3, Suspended = 4, Cancelled = 5 }
public enum BillingMetricType { Base = 1, Employer = 2, Employee = 3, ReportRow = 4, Correction = 5 }
public enum BillingPricingType { Fixed = 1, PerUnit = 2, Tiered = 3 }
public enum CorrectionBillingMode { Free = 1, PerCorrection = 2, PerCorrectedRow = 3, SameAsRegularRows = 4 }
public enum BillingPeriodStatus { Open = 1, Calculated = 2, Charging = 3, Charged = 4, PastDue = 5, Suspended = 6, Cancelled = 7 }
public enum BillingPaymentStatus { Pending = 1, Processing = 2, Succeeded = 3, Failed = 4, Refunded = 5, PartiallyRefunded = 6, Cancelled = 7 }
public enum BillingPaymentAttemptStatus { Pending = 1, Succeeded = 2, Failed = 3 }
public enum BillingRefundStatus { Pending = 1, Succeeded = 2, Failed = 3 }
public enum ProviderWebhookStatus { Received = 1, Processed = 2, Ignored = 3, Failed = 4 }

public sealed class PlanPricingComponent : Entity
{
    private PlanPricingComponent() { }

    public PlanPricingComponent(Guid planId, BillingMetricType metricType, BillingPricingType pricingType,
        decimal unitPrice, decimal includedQuantity = 0, decimal? minimumCharge = null, decimal? maximumCharge = null,
        bool isEnabled = true, int version = 1, DateTimeOffset? effectiveFrom = null,
        CorrectionBillingMode? correctionMode = null)
    {
        if (planId == Guid.Empty) throw new ArgumentException("Plan is required.", nameof(planId));
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        PlanId = planId;
        Version = version;
        EffectiveFrom = effectiveFrom ?? DateTimeOffset.UtcNow;
        CorrectionMode = correctionMode;
        Update(metricType, pricingType, unitPrice, includedQuantity, minimumCharge, maximumCharge, isEnabled);
    }

    public Guid PlanId { get; private set; }
    public int Version { get; private set; } = 1;
    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset? EffectiveTo { get; private set; }
    public CorrectionBillingMode? CorrectionMode { get; private set; }
    public BillingMetricType MetricType { get; private set; }
    public BillingPricingType PricingType { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal IncludedQuantity { get; private set; }
    public decimal? MinimumCharge { get; private set; }
    public decimal? MaximumCharge { get; private set; }
    public bool IsEnabled { get; private set; }

    public void Close(DateTimeOffset effectiveTo)
    {
        if (effectiveTo <= EffectiveFrom) throw new ArgumentOutOfRangeException(nameof(effectiveTo));
        EffectiveTo = effectiveTo;
        Touch();
    }

    public void Update(BillingMetricType metricType, BillingPricingType pricingType, decimal unitPrice,
        decimal includedQuantity, decimal? minimumCharge, decimal? maximumCharge, bool isEnabled)
    {
        if (unitPrice < 0 || includedQuantity < 0) throw new ArgumentOutOfRangeException(nameof(unitPrice));
        if (minimumCharge < 0 || maximumCharge < 0 || minimumCharge > maximumCharge)
            throw new ArgumentOutOfRangeException(nameof(minimumCharge));
        MetricType = metricType;
        PricingType = pricingType;
        UnitPrice = unitPrice;
        IncludedQuantity = includedQuantity;
        MinimumCharge = minimumCharge;
        MaximumCharge = maximumCharge;
        IsEnabled = isEnabled;
        Touch();
    }

    private static string Truncate(string? value, int max)
    {
        var clean = value?.Trim() ?? string.Empty;
        return clean[..Math.Min(clean.Length, max)];
    }
}

public sealed class PlanPricingTier : Entity
{
    private PlanPricingTier() { }

    public PlanPricingTier(Guid componentId, decimal fromQuantity, decimal? toQuantity, decimal unitPrice)
    {
        if (componentId == Guid.Empty) throw new ArgumentException("Component is required.", nameof(componentId));
        if (fromQuantity < 0 || toQuantity < 0 || unitPrice < 0 || (toQuantity.HasValue && toQuantity < fromQuantity))
            throw new ArgumentOutOfRangeException(nameof(fromQuantity));
        ComponentId = componentId;
        FromQuantity = fromQuantity;
        ToQuantity = toQuantity;
        UnitPrice = unitPrice;
    }

    public Guid ComponentId { get; private set; }
    public decimal FromQuantity { get; private set; }
    public decimal? ToQuantity { get; private set; }
    public decimal UnitPrice { get; private set; }
}

public sealed class BillingAccountPricingComponent : Entity
{
    private BillingAccountPricingComponent() { }

    public BillingAccountPricingComponent(Guid billingAccountId, BillingMetricType metricType, BillingPricingType pricingType,
        decimal unitPrice, decimal includedQuantity = 0, decimal? minimumCharge = null, decimal? maximumCharge = null,
        bool isEnabled = true, int version = 1, DateTimeOffset? effectiveFrom = null,
        CorrectionBillingMode? correctionMode = null)
    {
        if (billingAccountId == Guid.Empty) throw new ArgumentException("Billing account is required.", nameof(billingAccountId));
        if (version <= 0) throw new ArgumentOutOfRangeException(nameof(version));
        BillingAccountId = billingAccountId;
        Version = version;
        EffectiveFrom = effectiveFrom ?? DateTimeOffset.UtcNow;
        CorrectionMode = correctionMode;
        Update(metricType, pricingType, unitPrice, includedQuantity, minimumCharge, maximumCharge, isEnabled);
    }

    public Guid BillingAccountId { get; private set; }
    public int Version { get; private set; } = 1;
    public DateTimeOffset EffectiveFrom { get; private set; }
    public DateTimeOffset? EffectiveTo { get; private set; }
    public CorrectionBillingMode? CorrectionMode { get; private set; }
    public BillingMetricType MetricType { get; private set; }
    public BillingPricingType PricingType { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal IncludedQuantity { get; private set; }
    public decimal? MinimumCharge { get; private set; }
    public decimal? MaximumCharge { get; private set; }
    public bool IsEnabled { get; private set; }

    public void Close(DateTimeOffset effectiveTo)
    {
        if (effectiveTo <= EffectiveFrom) throw new ArgumentOutOfRangeException(nameof(effectiveTo));
        EffectiveTo = effectiveTo;
        Touch();
    }

    public void Update(BillingMetricType metricType, BillingPricingType pricingType, decimal unitPrice,
        decimal includedQuantity, decimal? minimumCharge, decimal? maximumCharge, bool isEnabled)
    {
        if (unitPrice < 0 || includedQuantity < 0) throw new ArgumentOutOfRangeException(nameof(unitPrice));
        if (minimumCharge < 0 || maximumCharge < 0 || minimumCharge > maximumCharge)
            throw new ArgumentOutOfRangeException(nameof(minimumCharge));
        MetricType = metricType;
        PricingType = pricingType;
        UnitPrice = unitPrice;
        IncludedQuantity = includedQuantity;
        MinimumCharge = minimumCharge;
        MaximumCharge = maximumCharge;
        IsEnabled = isEnabled;
        Touch();
    }
}

public sealed class BillingAccountPricingTier : Entity
{
    private BillingAccountPricingTier() { }

    public BillingAccountPricingTier(Guid componentId, decimal fromQuantity, decimal? toQuantity, decimal unitPrice)
    {
        if (componentId == Guid.Empty) throw new ArgumentException("Component is required.", nameof(componentId));
        if (fromQuantity < 0 || toQuantity < 0 || unitPrice < 0 || (toQuantity.HasValue && toQuantity < fromQuantity))
            throw new ArgumentOutOfRangeException(nameof(fromQuantity));
        ComponentId = componentId;
        FromQuantity = fromQuantity;
        ToQuantity = toQuantity;
        UnitPrice = unitPrice;
    }

    public Guid ComponentId { get; private set; }
    public decimal FromQuantity { get; private set; }
    public decimal? ToQuantity { get; private set; }
    public decimal UnitPrice { get; private set; }
}

public sealed class BillingPeriod : Entity
{
    private BillingPeriod() { }

    public BillingPeriod(Guid billingAccountId, Guid planId, DateTimeOffset periodStart, DateTimeOffset periodEnd, string currency = "ILS")
    {
        if (billingAccountId == Guid.Empty || planId == Guid.Empty) throw new ArgumentException("Billing account and plan are required.");
        if (periodEnd <= periodStart) throw new ArgumentException("Period end must be after period start.");
        BillingAccountId = billingAccountId;
        PlanId = planId;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Currency = string.IsNullOrWhiteSpace(currency) ? "ILS" : currency.Trim().ToUpperInvariant();
    }

    public Guid BillingAccountId { get; private set; }
    public Guid PlanId { get; private set; }
    public DateTimeOffset PeriodStart { get; private set; }
    public DateTimeOffset PeriodEnd { get; private set; }
    public BillingPeriodStatus Status { get; private set; } = BillingPeriodStatus.Open;
    public string Currency { get; private set; } = "ILS";
    public decimal Subtotal { get; private set; }
    public decimal Total { get; private set; }
    public string CalculationSnapshotJson { get; private set; } = "{}";
    public DateTimeOffset? CalculatedAt { get; private set; }
    public DateTimeOffset? ChargedAt { get; private set; }

    public void SaveCalculation(decimal subtotal, decimal total, string snapshotJson)
    {
        if (subtotal < 0 || total < 0) throw new ArgumentOutOfRangeException(nameof(total));
        Subtotal = subtotal;
        Total = total;
        CalculationSnapshotJson = string.IsNullOrWhiteSpace(snapshotJson) ? "{}" : snapshotJson;
        CalculatedAt = DateTimeOffset.UtcNow;
        Status = BillingPeriodStatus.Calculated;
        Touch();
    }

    public void MarkCharging() { Status = BillingPeriodStatus.Charging; Touch(); }
    public void MarkCharged() { Status = BillingPeriodStatus.Charged; ChargedAt = DateTimeOffset.UtcNow; Touch(); }
    public void MarkPastDue() { Status = BillingPeriodStatus.PastDue; Touch(); }
    public void MarkSuspended() { Status = BillingPeriodStatus.Suspended; Touch(); }
}

public sealed class BillingUsage : Entity
{
    private BillingUsage() { }

    public BillingUsage(Guid billingAccountId, Guid billingPeriodId, BillingMetricType metricType, decimal quantity,
        decimal includedQuantity, decimal billableQuantity, decimal unitPrice, decimal amount,
        Guid? employerId = null, string? sourceType = null, string? sourceId = null)
    {
        if (billingAccountId == Guid.Empty || billingPeriodId == Guid.Empty) throw new ArgumentException("Billing account and period are required.");
        if (quantity < 0 || includedQuantity < 0 || billableQuantity < 0 || unitPrice < 0 || amount < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity));
        BillingAccountId = billingAccountId;
        BillingPeriodId = billingPeriodId;
        EmployerId = employerId;
        MetricType = metricType;
        Quantity = quantity;
        IncludedQuantity = includedQuantity;
        BillableQuantity = billableQuantity;
        UnitPrice = unitPrice;
        Amount = amount;
        SourceType = sourceType?.Trim() ?? string.Empty;
        SourceId = sourceId?.Trim() ?? string.Empty;
    }

    public Guid BillingAccountId { get; private set; }
    public Guid BillingPeriodId { get; private set; }
    public Guid? EmployerId { get; private set; }
    public BillingMetricType MetricType { get; private set; }
    public decimal Quantity { get; private set; }
    public decimal IncludedQuantity { get; private set; }
    public decimal BillableQuantity { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal Amount { get; private set; }
    public string SourceType { get; private set; } = string.Empty;
    public string SourceId { get; private set; } = string.Empty;
}

public sealed class PaymentMethod : Entity
{
    private PaymentMethod() { }

    public PaymentMethod(Guid billingAccountId, string provider, BillingPaymentMethodType type)
    {
        if (billingAccountId == Guid.Empty) throw new ArgumentException("Billing account is required.", nameof(billingAccountId));
        BillingAccountId = billingAccountId;
        Provider = Require(provider);
        Type = type;
    }

    public Guid BillingAccountId { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public BillingPaymentMethodType Type { get; private set; }
    public BillingPaymentMethodStatus Status { get; private set; } = BillingPaymentMethodStatus.NotConfigured;
    public string ProviderCustomerId { get; private set; } = string.Empty;
    public string ProviderPaymentMethodId { get; private set; } = string.Empty;
    public string CardBrand { get; private set; } = string.Empty;
    public string CardLast4 { get; private set; } = string.Empty;
    public int? CardExpiryMonth { get; private set; }
    public int? CardExpiryYear { get; private set; }
    public string MandateReference { get; private set; } = string.Empty;

    public void Activate(string? customerId, string paymentMethodId, string? cardBrand, string? cardLast4,
        int? expiryMonth, int? expiryYear, string? mandateReference)
    {
        ProviderCustomerId = Truncate(customerId, 200);
        ProviderPaymentMethodId = Truncate(Require(paymentMethodId), 200);
        CardBrand = Truncate(cardBrand, 40);
        CardLast4 = Truncate(cardLast4, 4);
        CardExpiryMonth = expiryMonth;
        CardExpiryYear = expiryYear;
        MandateReference = Truncate(mandateReference, 200);
        Status = BillingPaymentMethodStatus.Active;
        Touch();
    }

    public void MarkStatus(BillingPaymentMethodStatus status) { Status = status; Touch(); }
    private static string Require(string? value) => string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Value is required.") : value.Trim();
    private static string Truncate(string? value, int max)
    {
        var clean = value?.Trim() ?? string.Empty;
        return clean[..Math.Min(clean.Length, max)];
    }
}

public sealed class Payment : Entity
{
    private Payment() { }

    public Payment(Guid billingAccountId, Guid billingPeriodId, decimal amount, string currency, string idempotencyKey)
    {
        if (billingAccountId == Guid.Empty || billingPeriodId == Guid.Empty) throw new ArgumentException("Billing account and period are required.");
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        BillingAccountId = billingAccountId;
        BillingPeriodId = billingPeriodId;
        Amount = amount;
        Currency = string.IsNullOrWhiteSpace(currency) ? "ILS" : currency.Trim().ToUpperInvariant();
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? throw new ArgumentException("Idempotency key is required.") : idempotencyKey.Trim();
    }

    public Guid BillingAccountId { get; private set; }
    public Guid BillingPeriodId { get; private set; }
    public decimal Amount { get; private set; }
    public string Currency { get; private set; } = "ILS";
    public BillingPaymentStatus Status { get; private set; } = BillingPaymentStatus.Pending;
    public string Provider { get; private set; } = string.Empty;
    public string ProviderTransactionId { get; private set; } = string.Empty;
    public string InvoiceReference { get; private set; } = string.Empty;
    public string FailureCode { get; private set; } = string.Empty;
    public string FailureMessage { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTimeOffset? PaidAt { get; private set; }

    public void MarkProcessing(string provider) { Provider = Truncate(provider, 40); Status = BillingPaymentStatus.Processing; Touch(); }
    public void Succeed(string transactionId, string? invoiceReference) { ProviderTransactionId = Truncate(transactionId, 200); InvoiceReference = Truncate(invoiceReference, 200); Status = BillingPaymentStatus.Succeeded; PaidAt = DateTimeOffset.UtcNow; FailureCode = FailureMessage = string.Empty; Touch(); }
    public void Fail(string? code, string? message) { Status = BillingPaymentStatus.Failed; FailureCode = Truncate(code, 120); FailureMessage = Truncate(message, 1000); Touch(); }
    public void MarkRefunded(bool partial) { Status = partial ? BillingPaymentStatus.PartiallyRefunded : BillingPaymentStatus.Refunded; Touch(); }

    private static string Truncate(string? value, int max)
    {
        var clean = value?.Trim() ?? string.Empty;
        return clean[..Math.Min(clean.Length, max)];
    }
}

public sealed class PaymentAttempt : Entity
{
    private PaymentAttempt() { }

    public PaymentAttempt(Guid paymentId, int attemptNumber, string provider, string idempotencyKey)
    {
        if (paymentId == Guid.Empty || attemptNumber <= 0) throw new ArgumentException("Valid payment and attempt number are required.");
        PaymentId = paymentId;
        AttemptNumber = attemptNumber;
        Provider = provider?.Trim() ?? string.Empty;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? throw new ArgumentException("Idempotency key is required.") : idempotencyKey.Trim();
    }

    public Guid PaymentId { get; private set; }
    public int AttemptNumber { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public BillingPaymentAttemptStatus Status { get; private set; } = BillingPaymentAttemptStatus.Pending;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string ProviderTransactionId { get; private set; } = string.Empty;
    public string ErrorCode { get; private set; } = string.Empty;
    public string ErrorMessage { get; private set; } = string.Empty;
    public DateTimeOffset? CompletedAt { get; private set; }

    public void Complete(bool success, string? transactionId, string? errorCode, string? errorMessage)
    {
        Status = success ? BillingPaymentAttemptStatus.Succeeded : BillingPaymentAttemptStatus.Failed;
        ProviderTransactionId = Truncate(transactionId, 200);
        ErrorCode = Truncate(errorCode, 120);
        ErrorMessage = Truncate(errorMessage, 1000);
        CompletedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    private static string Truncate(string? value, int max)
    {
        var clean = value?.Trim() ?? string.Empty;
        return clean[..Math.Min(clean.Length, max)];
    }
}

public sealed class Refund : Entity
{
    private Refund() { }

    public Refund(Guid paymentId, decimal amount, string reason, string idempotencyKey)
    {
        if (paymentId == Guid.Empty || amount <= 0) throw new ArgumentException("Payment and positive amount are required.");
        PaymentId = paymentId;
        Amount = amount;
        Reason = reason?.Trim() ?? string.Empty;
        IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? throw new ArgumentException("Idempotency key is required.") : idempotencyKey.Trim();
    }

    public Guid PaymentId { get; private set; }
    public decimal Amount { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public BillingRefundStatus Status { get; private set; } = BillingRefundStatus.Pending;
    public string ProviderRefundId { get; private set; } = string.Empty;
    public string ErrorMessage { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;

    public void Complete(bool success, string? providerRefundId, string? errorMessage)
    {
        Status = success ? BillingRefundStatus.Succeeded : BillingRefundStatus.Failed;
        ProviderRefundId = Truncate(providerRefundId, 200);
        ErrorMessage = Truncate(errorMessage, 1000);
        Touch();
    }

    private static string Truncate(string? value, int max)
    {
        var clean = value?.Trim() ?? string.Empty;
        return clean[..Math.Min(clean.Length, max)];
    }
}

public sealed class ProviderWebhookEvent : Entity
{
    private ProviderWebhookEvent() { }

    public ProviderWebhookEvent(string provider, string eventKey, string payloadHash, string payload)
    {
        Provider = string.IsNullOrWhiteSpace(provider) ? throw new ArgumentException("Provider is required.") : provider.Trim();
        EventKey = string.IsNullOrWhiteSpace(eventKey) ? throw new ArgumentException("Event key is required.") : eventKey.Trim();
        PayloadHash = payloadHash?.Trim() ?? string.Empty;
        Payload = payload ?? string.Empty;
    }

    public string Provider { get; private set; } = string.Empty;
    public string EventKey { get; private set; } = string.Empty;
    public string PayloadHash { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public ProviderWebhookStatus Status { get; private set; } = ProviderWebhookStatus.Received;
    public string ErrorMessage { get; private set; } = string.Empty;
    public DateTimeOffset? ProcessedAt { get; private set; }

    public void Complete(ProviderWebhookStatus status, string? error = null)
    {
        Status = status;
        ErrorMessage = Truncate(error, 1000);
        ProcessedAt = DateTimeOffset.UtcNow;
        Touch();
    }

    private static string Truncate(string? value, int max)
    {
        var clean = value?.Trim() ?? string.Empty;
        return clean[..Math.Min(clean.Length, max)];
    }
}
