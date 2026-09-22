using Alpha.Domain.Billing;

namespace Alpha.Application.Billing;

public sealed record PaymentProviderCustomerRequest(
    string Name,
    string Email,
    string TaxId,
    string BillingAddress,
    string ExternalReference);

public sealed record PaymentProviderCustomerResult(string CustomerId);

public sealed record PaymentMethodSetupRequest(
    string CustomerId,
    string SuccessUrl,
    string FailureUrl,
    string CancelUrl,
    string CallbackUrl,
    string ExternalReference);

public sealed record PaymentMethodSetupResult(
    string SetupRequestId,
    string RedirectUrl);

public sealed record PaymentMethodStatusResult(
    bool Active,
    string PaymentMethodId,
    string Brand,
    string Last4,
    int? ExpiryMonth,
    int? ExpiryYear,
    string? BankDebitMandateReference,
    string? CustomerId = null,
    string? ExternalReference = null);

public sealed record PaymentChargeRequest(
    string CustomerId,
    string PaymentMethodId,
    decimal Amount,
    string Currency,
    string Description,
    string ExternalReference,
    bool CreateInvoice,
    int? ExpiryMonth = null,
    int? ExpiryYear = null);

public sealed record PaymentChargeResult(
    bool Success,
    string TransactionId,
    string? InvoiceReference,
    string? ErrorCode,
    string? ErrorMessage);

public sealed record PaymentRefundRequest(
    string TransactionId,
    string PaymentMethodId,
    decimal Amount,
    string Currency,
    string ExternalReference,
    int? ExpiryMonth = null,
    int? ExpiryYear = null);

public sealed record PaymentRefundResult(
    bool Success,
    string RefundId,
    string? ErrorCode,
    string? ErrorMessage);

public interface IPaymentProvider
{
    string Name { get; }
    Task<PaymentProviderCustomerResult> CreateCustomer(PaymentProviderCustomerRequest request, CancellationToken ct = default);
    Task<PaymentMethodSetupResult> CreatePaymentMethod(PaymentMethodSetupRequest request, CancellationToken ct = default);
    Task<PaymentChargeResult> Charge(PaymentChargeRequest request, CancellationToken ct = default);
    Task<PaymentRefundResult> Refund(PaymentRefundRequest request, CancellationToken ct = default);
    Task<PaymentMethodStatusResult> GetPaymentMethodStatus(string customerId, string paymentMethodId, CancellationToken ct = default);
    Task CancelPaymentMethod(string customerId, string paymentMethodId, CancellationToken ct = default);
    Task<PaymentMethodStatusResult> ResolvePaymentMethodFromCallback(string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);
}

public interface IPaymentProviderResolver
{
    IPaymentProvider Resolve(string? providerName = null);
}
