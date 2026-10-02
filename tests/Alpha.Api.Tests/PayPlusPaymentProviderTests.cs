using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alpha.Api.Services;
using Alpha.Application.Billing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class PayPlusPaymentProviderTests
{
    [Fact]
    public async Task Charge_puts_idempotency_reference_in_searchable_more_info()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = await CloneRequestAsync(request);
            return Json(HttpStatusCode.OK, new
            {
                results = new { status = "success", code = 0 },
                data = new { transaction_uid = "tx-1" }
            });
        });

        var result = await provider.Charge(new PaymentChargeRequest(
            "customer-1", "token-1", 100m, "ILS", "monthly billing", "billing:key:1", true),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.EndsWith("/Transactions/Charge", captured!.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("billing:key:1", body.RootElement.GetProperty("more_info").GetString());
        Assert.False(body.RootElement.TryGetProperty("more_info_1", out _));
    }

    [Fact]
    public async Task Refund_uses_refund_by_original_transaction_uid()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = await CloneRequestAsync(request);
            return Json(HttpStatusCode.OK, new
            {
                results = new { status = "success", code = 0 },
                data = new { transaction_uid = "refund-1" }
            });
        });

        var result = await provider.Refund(new PaymentRefundRequest(
            "original-tx", "unused-token", 25m, "ILS", "refund:key:1"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.NotNull(captured);
        Assert.EndsWith("/Transactions/RefundByTransactionUID", captured!.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("original-tx", body.RootElement.GetProperty("transaction_uid").GetString());
        Assert.Equal(25m, body.RootElement.GetProperty("amount").GetDecimal());
        Assert.Equal("refund:key:1", body.RootElement.GetProperty("more_info").GetString());
        Assert.True(body.RootElement.GetProperty("initial_invoice").GetBoolean());
    }

    [Fact]
    public async Task Invalid_provider_json_is_normalized_to_provider_exception()
    {
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ definitely-not-json", Encoding.UTF8, "application/json")
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetPaymentMethodStatus("customer", "token", TestContext.Current.CancellationToken));

        Assert.Contains("invalid JSON", error.Message);
    }

    [Fact]
    public async Task Callback_rejects_invalid_signature_before_server_lookup()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP call must not happen"));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ResolvePaymentMethodFromCallback(
                """{"payment_request_uid":"req-1"}""",
                new Dictionary<string, string>
                {
                    ["user-agent"] = "PayPlus",
                    ["hash"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("signature", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Callback_with_valid_signature_is_verified_server_to_server()
    {
        var body = """{"payment_request_uid":"req-1"}""";
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = await CloneRequestAsync(request);
            return Json(HttpStatusCode.OK, new
            {
                results = new { status = "success", code = 0 },
                data = new
                {
                    status = "approved",
                    token_uid = "token-verified",
                    customer_uid = "customer-1",
                    more_info = "11111111-1111-1111-1111-111111111111:setup",
                    card_number_masked = "4580-****-****-4242",
                    card_date_mmyy = "12/30",
                    brand_name = "Visa"
                }
            });
        });
        var hash = Hmac(body, "secret");

        var result = await provider.ResolvePaymentMethodFromCallback(
            body,
            new Dictionary<string, string>
            {
                ["user-agent"] = "PayPlus Callback",
                ["hash"] = hash
            },
            TestContext.Current.CancellationToken);

        Assert.True(result.Active);
        Assert.Equal("token-verified", result.PaymentMethodId);
        Assert.Equal("4242", result.Last4);
        Assert.Equal("11111111-1111-1111-1111-111111111111:setup", result.ExternalReference);
        Assert.NotNull(captured);
        Assert.EndsWith("/PaymentPages/ipn-full", captured!.RequestUri!.AbsolutePath);
    }


    [Fact]
    public async Task Failed_setup_callback_cannot_activate_token()
    {
        var body = """{"payment_request_uid":"req-failed"}""";
        var provider = CreateProvider(_ => Task.FromResult(Json(HttpStatusCode.OK, new
        {
            results = new { status = "success", code = 0 },
            data = new
            {
                status = "rejected",
                token_uid = "token-that-must-not-activate",
                customer_uid = "customer-1",
                more_info = "11111111-1111-1111-1111-111111111111:setup"
            }
        })));
        var hash = Hmac(body, "secret");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ResolvePaymentMethodFromCallback(
                body,
                new Dictionary<string, string>
                {
                    ["user-agent"] = "PayPlus Callback",
                    ["hash"] = hash
                },
                TestContext.Current.CancellationToken));

        Assert.Contains("not approved", error.Message);
    }


    [Fact]
    public async Task Charge_approved_without_transaction_uid_is_not_treated_as_success()
    {
        var provider = CreateProvider(_ => Task.FromResult(Json(HttpStatusCode.OK, new
        {
            results = new { status = "success", code = 0 },
            data = new { }
        })));

        var result = await provider.Charge(new PaymentChargeRequest(
            "customer", "token", 100m, "ILS", "billing", "billing-key", true),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("transaction_id_missing", result.ErrorCode);
    }

    private static PayPlusPaymentProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:PayPlus:BaseUrl"] = "https://restapi.payplus.test/api/v1.0",
                ["Payments:PayPlus:ApiKey"] = "api",
                ["Payments:PayPlus:SecretKey"] = "secret",
                ["Payments:PayPlus:TerminalUid"] = "terminal",
                ["Payments:PayPlus:CashierUid"] = "cashier",
                ["Payments:PayPlus:PaymentPageUid"] = "page"
            })
            .Build();

        return new PayPlusPaymentProvider(
            new TestHttpClientFactory(new HttpClient(new DelegateHandler(handler))),
            configuration);
    }

    private static string Hmac(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
        new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        if (source.Content is not null)
            clone.Content = new StringContent(
                await source.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                Encoding.UTF8,
                source.Content.Headers.ContentType?.MediaType ?? "application/json");
        return clone;
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) => handler(request);
    }
}
