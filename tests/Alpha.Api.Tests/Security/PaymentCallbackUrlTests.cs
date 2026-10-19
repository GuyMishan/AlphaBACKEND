using System.Net;
using System.Text.Json;
using Alpha.Domain.Billing;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.Billing.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;
using static Alpha.Api.Tests.TestInfrastructure.TestData;

namespace Alpha.Api.Tests.Security;

/// <summary>
/// The payment provider calls back the URL Alpha gives it during setup, so that URL must come from trusted
/// configuration. A caller-controlled <c>Host</c> header must never reach it.
/// </summary>
public sealed class PaymentCallbackUrlTests
{
    [Fact]
    public async Task Missing_cardcom_setup_configuration_returns_specific_503_without_creating_pending_setup()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var (organizationId, request) = await SetupRequestAsync(factory);
            using var response = await client.SendAsync(request, ct);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("PAYMENT_PROVIDER_NOT_CONFIGURED", body.RootElement.GetProperty("errorCode").GetString());
            await factory.WithDbAsync(async db => Assert.Equal(string.Empty,
                await db.BillingAccounts.Where(x => x.OrganizationId == organizationId)
                    .Select(x => x.PendingProvider).SingleAsync(ct)));
        }, new Dictionary<string, string?>
        {
            ["Payments:DefaultProvider"] = "CardCom",
            ["Payments:CardCom:TerminalNumber"] = "",
            ["Payments:CardCom:InterfaceApiName"] = "",
            ["Payments:PublicApiBaseUrl"] = "https://api.alpha.test"
        });
    }

    [Fact]
    public async Task Provider_setup_stages_new_details_without_changing_existing_account()
    {
        var provider = new RecordingPaymentProvider();
        await RunAsync(async (factory, client, ct) =>
        {
            var (organizationId, request) = await SetupRequestAsync(factory);
            using (request)
            {
                // Replace only the body; retain the seeded authenticated identity.
                request.Content = System.Net.Http.Json.JsonContent.Create(new {
                    returnPath = "/settings",
                    billingName = "New Pending Name",
                    taxId = "123456789",
                    invoiceEmail = "new@test.invalid",
                    billingAddress = "New Address"
                });
                using var response = await client.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            await factory.WithDbAsync(async db =>
            {
                var account = await db.BillingAccounts.SingleAsync(x => x.OrganizationId == organizationId, ct);
                Assert.Equal("Callback Test", account.BillingName);
                Assert.Equal("515151515", account.TaxId);
                Assert.Equal("billing@test.invalid", account.InvoiceEmail);
                Assert.Equal("1 Test Street", account.BillingAddress);
                Assert.Null(account.DefaultPaymentMethodId);
                var staged = await db.Database.SqlQueryRaw<string>(
                    "SELECT details_json::text AS \"Value\" FROM billing.pending_payment_setup_details WHERE billing_account_id = {0}",
                    account.Id).ToListAsync(ct);
                Assert.Single(staged);
                Assert.Contains("New Pending Name", staged[0]);
            });
        }, new Dictionary<string, string?> { ["Payments:PublicApiBaseUrl"] = "https://api.alpha.test" }, provider.Install);
    }

    [Fact]
    public async Task Verified_payment_callback_applies_pending_details_and_card_together()
    {
        var provider = new RecordingPaymentProvider();
        await RunAsync(async (factory, client, ct) =>
        {
            var (organizationId, request) = await SetupRequestAsync(factory);
            using (request)
            {
                request.Content = System.Net.Http.Json.JsonContent.Create(new { returnPath = "/settings",
                    billingName = "Approved Name", taxId = "123456789",
                    invoiceEmail = "approved@test.invalid", billingAddress = "Approved Address" });
                using var response = await client.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            using var callback = new HttpRequestMessage(HttpMethod.Post, "/api/billing/providers/Recording/callback")
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { event_id = "success-1" })
            };
            using var callbackResponse = await client.SendAsync(callback, ct);
            Assert.Equal(HttpStatusCode.OK, callbackResponse.StatusCode);
            await factory.WithDbAsync(async db =>
            {
                var account = await db.BillingAccounts.SingleAsync(x => x.OrganizationId == organizationId, ct);
                Assert.Equal("Approved Name", account.BillingName);
                Assert.Equal("123456789", account.TaxId);
                Assert.Equal("approved@test.invalid", account.InvoiceEmail);
                Assert.Equal("Approved Address", account.BillingAddress);
                Assert.Equal(BillingPaymentMethodStatus.Active, account.PaymentMethodStatus);
                Assert.NotNull(account.DefaultPaymentMethodId);
                Assert.Equal("token-1", account.ProviderPaymentMethodId);
            });
        }, new Dictionary<string, string?> { ["Payments:PublicApiBaseUrl"] = "https://api.alpha.test" }, provider.Install);
    }

    [Fact]
    public async Task Callback_url_uses_the_configured_api_base_and_ignores_the_host_header()
    {
        var provider = new RecordingPaymentProvider();
        await RunAsync(async (factory, client, ct) =>
        {
            var (organizationId, request) = await SetupRequestAsync(factory);

            using var response = await client.SendAsync(request, ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var callback = Assert.Single(provider.CallbackUrls);
            Assert.Equal("https://api.alpha.test/api/billing/providers/Recording/callback", callback);
        }, new Dictionary<string, string?> { ["Payments:PublicApiBaseUrl"] = "https://api.alpha.test/" }, provider.Install);
    }

    [Fact]
    public async Task Without_a_configured_api_base_the_platform_provided_render_url_is_used_and_the_host_header_ignored()
    {
        var provider = new RecordingPaymentProvider();
        await RunAsync(async (factory, client, ct) =>
        {
            var (_, request) = await SetupRequestAsync(factory);

            using var response = await client.SendAsync(request, ct);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("https://alpha-api.onrender.com/api/billing/providers/Recording/callback", Assert.Single(provider.CallbackUrls));
        }, new Dictionary<string, string?> { ["RENDER_EXTERNAL_URL"] = "https://alpha-api.onrender.com" }, provider.Install);
    }

    [Fact]
    public async Task An_invalid_configured_api_base_is_never_replaced_by_the_render_url()
    {
        var provider = new RecordingPaymentProvider();
        await RunAsync(async (factory, client, ct) =>
        {
            var (_, request) = await SetupRequestAsync(factory);

            using var response = await client.SendAsync(request, ct);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Empty(provider.CallbackUrls);
        }, new Dictionary<string, string?>
        {
            ["Payments:PublicApiBaseUrl"] = "http://api.alpha.test",
            ["RENDER_EXTERNAL_URL"] = "https://alpha-api.onrender.com"
        }, provider.Install);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://api.alpha.test")]
    [InlineData("https://user:secret@api.alpha.test")]
    [InlineData("https://api.alpha.test/?next=https://attacker.example")]
    [InlineData("javascript:alert(1)")]
    public async Task Missing_or_untrusted_api_base_fails_closed_before_any_provider_call(string? configured)
    {
        var provider = new RecordingPaymentProvider();
        await RunAsync(async (factory, client, ct) =>
        {
            var (organizationId, request) = await SetupRequestAsync(factory);

            using var response = await client.SendAsync(request, ct);

            Assert.DoesNotContain(provider.CallbackUrls, url => url.Contains("attacker.example", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            Assert.Equal("PAYMENT_CALLBACK_NOT_CONFIGURED", body.RootElement.GetProperty("errorCode").GetString());
            Assert.Empty(provider.CallbackUrls);
            Assert.Equal(0, provider.CustomersCreated);
            await factory.WithDbAsync(async db => Assert.Empty(
                await db.BillingAccounts.Where(x => x.OrganizationId == organizationId).Select(x => x.PendingProvider).SingleAsync(ct)));
        }, new Dictionary<string, string?> { ["Payments:PublicApiBaseUrl"] = configured }, provider.Install);
    }

    /// <summary>An organization with a credit-card billing account, and a setup request carrying a forged Host header.</summary>
    private static async Task<(Guid OrganizationId, HttpRequestMessage Request)> SetupRequestAsync(Factory factory)
    {
        var tenant = await SeedTenantAsync(factory, "pay-callback");
        await factory.WithDbAsync(async db =>
        {
            var account = new BillingAccount(tenant.Organization.Id, null);
            account.UpdateBillingDetails("Callback Test", "515151515", "billing@test.invalid", "1 Test Street", BillingPaymentMethodType.CreditCard);
            db.Add(account);
            await db.SaveChangesAsync();
        });
        var request = Request(HttpMethod.Post, $"/api/organizations/{tenant.Organization.Id}/billing-account/provider/setup",
            tenant.Admin.Id, tenant.Session.Id, json: new { returnPath = "/settings" });
        request.Headers.Host = "attacker.example";
        return (tenant.Organization.Id, request);
    }

    private sealed class RecordingPaymentProvider : IPaymentProvider, IPaymentProviderResolver
    {
        private int customersCreated;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> callbackUrls = new();
        private string? externalReference;

        public string Name => "Recording";
        public int CustomersCreated => Volatile.Read(ref customersCreated);
        public IReadOnlyCollection<string> CallbackUrls => callbackUrls.ToArray();

        public void Install(IServiceCollection services)
        {
            services.RemoveAll<IPaymentProviderResolver>();
            services.AddSingleton<IPaymentProviderResolver>(this);
        }

        public IPaymentProvider Resolve(string? providerName = null) => this;

        public Task<PaymentProviderCustomerResult> CreateCustomer(PaymentProviderCustomerRequest request, CancellationToken ct = default)
        {
            Interlocked.Increment(ref customersCreated);
            return Task.FromResult(new PaymentProviderCustomerResult("customer-1"));
        }

        public Task<PaymentMethodSetupResult> CreatePaymentMethod(PaymentMethodSetupRequest request, CancellationToken ct = default)
        {
            externalReference = request.ExternalReference;
            callbackUrls.Enqueue(request.CallbackUrl);
            return Task.FromResult(new PaymentMethodSetupResult("setup-1", "https://provider.test/setup"));
        }

        public Task<PaymentChargeResult> Charge(PaymentChargeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PaymentRefundResult> Refund(PaymentRefundRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PaymentMethodStatusResult> GetPaymentMethodStatus(string customerId, string paymentMethodId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CancelPaymentMethod(string customerId, string paymentMethodId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PaymentMethodStatusResult> ResolvePaymentMethodFromCallback(string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default) =>
            Task.FromResult(new PaymentMethodStatusResult(true, "token-1", "Visa", "4242", 12, 2030, null, "customer-1", externalReference));
    }
}
