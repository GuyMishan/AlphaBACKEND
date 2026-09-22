using System.Security.Cryptography;
using System.Text;
using Alpha.Application.Billing;

namespace Alpha.Api.Services;

public sealed class PaymentProviderResolver(
    FakePaymentProvider fake,
    CardComPaymentProvider cardCom,
    PayPlusPaymentProvider payPlus,
    IConfiguration configuration) : IPaymentProviderResolver, IPaymentProvider
{
    private IReadOnlyDictionary<string, IPaymentProvider> Providers => new Dictionary<string, IPaymentProvider>(StringComparer.OrdinalIgnoreCase)
    {
        [fake.Name] = fake,
        [cardCom.Name] = cardCom,
        [payPlus.Name] = payPlus
    };

    public string Name => Resolve().Name;

    public IPaymentProvider Resolve(string? providerName = null)
    {
        var selected = string.IsNullOrWhiteSpace(providerName)
            ? configuration["Payments:DefaultProvider"]
                ?? configuration["Payments:Provider"]
                ?? "Fake"
            : providerName;
        return Providers.TryGetValue(selected, out var provider)
            ? provider
            : throw new InvalidOperationException($"Payment provider '{selected}' is not registered.");
    }

    public Task<PaymentProviderCustomerResult> CreateCustomer(PaymentProviderCustomerRequest request, CancellationToken ct = default) =>
        Resolve().CreateCustomer(request, ct);
    public Task<PaymentMethodSetupResult> CreatePaymentMethod(PaymentMethodSetupRequest request, CancellationToken ct = default) =>
        Resolve().CreatePaymentMethod(request, ct);
    public Task<PaymentChargeResult> Charge(PaymentChargeRequest request, CancellationToken ct = default) =>
        Resolve().Charge(request, ct);
    public Task<PaymentRefundResult> Refund(PaymentRefundRequest request, CancellationToken ct = default) =>
        Resolve().Refund(request, ct);
    public Task<PaymentMethodStatusResult> GetPaymentMethodStatus(string customerId, string paymentMethodId, CancellationToken ct = default) =>
        Resolve().GetPaymentMethodStatus(customerId, paymentMethodId, ct);
    public Task CancelPaymentMethod(string customerId, string paymentMethodId, CancellationToken ct = default) =>
        Resolve().CancelPaymentMethod(customerId, paymentMethodId, ct);
    public Task<PaymentMethodStatusResult> ResolvePaymentMethodFromCallback(string rawBody,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct = default) =>
        Resolve().ResolvePaymentMethodFromCallback(rawBody, headers, ct);
}

public sealed class FakePaymentProvider : IPaymentProvider
{
    public string Name => "Fake";

    public Task<PaymentProviderCustomerResult> CreateCustomer(PaymentProviderCustomerRequest request, CancellationToken ct = default) =>
        Task.FromResult(new PaymentProviderCustomerResult($"fake-customer-{request.ExternalReference}"));

    public Task<PaymentMethodSetupResult> CreatePaymentMethod(PaymentMethodSetupRequest request, CancellationToken ct = default) =>
        Task.FromResult(new PaymentMethodSetupResult(
            $"fake-setup-{Guid.NewGuid():N}",
            $"{request.SuccessUrl}{(request.SuccessUrl.Contains('?') ? '&' : '?')}fakePaymentMethod=fake-token"));

    public Task<PaymentChargeResult> Charge(PaymentChargeRequest request, CancellationToken ct = default) =>
        Task.FromResult(new PaymentChargeResult(true, $"fake-charge-{StableId(request.ExternalReference)}",
            request.CreateInvoice ? $"fake-invoice-{StableId(request.ExternalReference)}" : null, null, null));

    public Task<PaymentRefundResult> Refund(PaymentRefundRequest request, CancellationToken ct = default) =>
        Task.FromResult(new PaymentRefundResult(true, $"fake-refund-{StableId(request.ExternalReference)}", null, null));

    public Task<PaymentMethodStatusResult> GetPaymentMethodStatus(string customerId, string paymentMethodId, CancellationToken ct = default) =>
        Task.FromResult(new PaymentMethodStatusResult(true, paymentMethodId, "FAKE", "4242", 12, 2030, null, customerId));

    public Task CancelPaymentMethod(string customerId, string paymentMethodId, CancellationToken ct = default) => Task.CompletedTask;

    public Task<PaymentMethodStatusResult> ResolvePaymentMethodFromCallback(string rawBody,
        IReadOnlyDictionary<string, string> headers, CancellationToken ct = default) =>
        Task.FromResult(new PaymentMethodStatusResult(true, "fake-token", "FAKE", "4242", 12, 2030, null,
            "fake-customer", null));

    private static string StableId(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
