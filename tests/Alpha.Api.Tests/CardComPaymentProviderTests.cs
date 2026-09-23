using System.Net;
using System.Text.Json;
using Alpha.Api.Services;
using Alpha.Application.Billing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class CardComPaymentProviderTests
{
    [Fact]
    public async Task Setup_uses_api_11_json_create_token_only()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = await CloneRequestAsync(request);
            return Json(HttpStatusCode.OK, new
            {
                ResponseCode = 0,
                Description = "OK",
                LowProfileId = "LP-123",
                Url = "https://secure.cardcom.solutions/pay/LP-123"
            });
        });

        var result = await provider.CreatePaymentMethod(new PaymentMethodSetupRequest(
            "customer", "https://alpha.test/success", "https://alpha.test/fail",
            "https://alpha.test/cancel", "https://api.alpha.test/callback", "account-id",
            "Acme Ltd", "515151515", "billing@acme.test", "1 Test Street"),
            TestContext.Current.CancellationToken);

        Assert.Equal("LP-123", result.SetupRequestId);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.EndsWith("/api/v11/LowProfile/Create", captured.RequestUri!.AbsolutePath);
        Assert.Equal("application/json", captured.Content!.Headers.ContentType!.MediaType);

        using var json = JsonDocument.Parse(await captured.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("cardtest26", json.RootElement.GetProperty("ApiName").GetString());
        Assert.Equal("CreateTokenOnly", json.RootElement.GetProperty("Operation").GetString());
        Assert.Equal("account-id", json.RootElement.GetProperty("ReturnValue").GetString());
        Assert.Equal("https://api.alpha.test/callback", json.RootElement.GetProperty("WebHookUrl").GetString());
        Assert.Equal("ALPHA – Acme Ltd", json.RootElement.GetProperty("ProductName").GetString());
        var document = json.RootElement.GetProperty("Document");
        Assert.Equal("Acme Ltd", document.GetProperty("Name").GetString());
        Assert.Equal("515151515", document.GetProperty("TaxId").GetString());
        Assert.Equal("billing@acme.test", document.GetProperty("Email").GetString());
        Assert.Equal("1 Test Street", document.GetProperty("AddressLine1").GetString());
        Assert.True(document.GetProperty("IsShowOnlyDocument").GetBoolean());
        Assert.False(document.GetProperty("IsSendByEmail").GetBoolean());
    }

    [Fact]
    public async Task Webhook_is_verified_with_api_11_get_lp_result_before_token_is_accepted()
    {
        var provider = CreateProvider(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.EndsWith("/api/v11/LowProfile/GetLpResult", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
            Assert.Equal("LP-123", body.RootElement.GetProperty("LowProfileId").GetString());

            return Json(HttpStatusCode.OK, new
            {
                ResponseCode = 0,
                Description = "OK",
                LowProfileId = "LP-123",
                ReturnValue = "11111111-1111-1111-1111-111111111111",
                Operation = "CreateTokenOnly",
                TokenInfo = new
                {
                    Token = "tok-123",
                    TokenExDate = "20301201",
                    CardYear = 2030,
                    CardMonth = 12
                },
                TranzactionInfo = new
                {
                    ResponseCode = 0,
                    Last4CardDigitsString = "4242",
                    Brand = "Visa"
                }
            });
        });

        var result = await provider.ResolvePaymentMethodFromCallback(
            """{"LowProfileId":"LP-123"}""", new Dictionary<string, string>(),
            TestContext.Current.CancellationToken);

        Assert.True(result.Active);
        Assert.Equal("tok-123", result.PaymentMethodId);
        Assert.Equal("4242", result.Last4);
        Assert.Equal("Visa", result.Brand);
        Assert.Equal(12, result.ExpiryMonth);
        Assert.Equal(2030, result.ExpiryYear);
    }

    [Fact]
    public async Task Webhook_rejects_unverified_or_mismatched_low_profile_id()
    {
        var provider = CreateProvider(_ => Task.FromResult(Json(HttpStatusCode.OK, new
        {
            ResponseCode = 0,
            LowProfileId = "OTHER",
            ReturnValue = "11111111-1111-1111-1111-111111111111",
            Operation = "CreateTokenOnly",
            TokenInfo = new { Token = "tok", CardYear = 2030, CardMonth = 12 }
        })));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ResolvePaymentMethodFromCallback(
                """{"LowProfileId":"LP-123"}""", new Dictionary<string, string>(),
                TestContext.Current.CancellationToken));

        Assert.Contains("verification mismatch", error.Message);
    }

    [Fact]
    public async Task Charge_uses_api_11_transaction_and_maps_decline()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = await CloneRequestAsync(request);
            return Json(HttpStatusCode.OK, new { ResponseCode = 4, Description = "Declined" });
        });

        var result = await provider.Charge(new PaymentChargeRequest(
            "customer", "tok-123", 99.90m, "ILS", "ALPHA monthly charge",
            "billing:key:that-is-longer-than-cardcom-limit", true, 12, 2030),
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("4", result.ErrorCode);
        Assert.Equal("Declined", result.ErrorMessage);
        Assert.EndsWith("/api/v11/Transactions/Transaction", captured!.RequestUri!.AbsolutePath);

        using var json = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("tok-123", json.RootElement.GetProperty("Token").GetString());
        Assert.Equal(99.90m, json.RootElement.GetProperty("Amount").GetDecimal());
        Assert.Equal("1230", json.RootElement.GetProperty("CardExpirationMMYY").GetString());
        Assert.True(json.RootElement.GetProperty("ExternalUniqTranId").GetString()!.Length <= 25);
    }

    [Fact]
    public async Task Refund_uses_api_11_refund_by_transaction_id_for_partial_refund()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = await CloneRequestAsync(request);
            return Json(HttpStatusCode.OK, new
            {
                ResponseCode = 0,
                Description = "Refunded",
                NewTranzactionId = 204972703
            });
        }, apiPassword: "refund-secret");

        var result = await provider.Refund(new PaymentRefundRequest(
            "204966999", "tok-123", 25.50m, "ILS", "refund:key", 12, 2030),
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal("204972703", result.RefundId);
        Assert.EndsWith("/api/v11/Transactions/RefundByTransactionId", captured!.RequestUri!.AbsolutePath);

        using var json = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("refund-secret", json.RootElement.GetProperty("ApiPassword").GetString());
        Assert.Equal(204966999, json.RootElement.GetProperty("TransactionId").GetInt64());
        Assert.Equal(25.50m, json.RootElement.GetProperty("PartialSum").GetDecimal());
        Assert.False(json.RootElement.GetProperty("CancelOnly").GetBoolean());
        Assert.True(json.RootElement.GetProperty("AllowMultipleRefunds").GetBoolean());
    }

    private static CardComPaymentProvider CreateProvider(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        string apiPassword = "")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:CardCom:BaseUrl"] = "https://secure.cardcom.solutions",
                ["Payments:CardCom:TerminalNumber"] = "1000",
                ["Payments:CardCom:InterfaceApiName"] = "cardtest26",
                ["Payments:CardCom:ApiName"] = "pGpgO8S1VFLyBpVhi1Xp",
                ["Payments:CardCom:ApiPassword"] = apiPassword
            })
            .Build();

        return new CardComPaymentProvider(
            new TestHttpClientFactory(new HttpClient(new DelegateHandler(handler))),
            configuration);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object value) =>
        new(status)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(value),
                System.Text.Encoding.UTF8,
                "application/json")
        };

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        if (source.Content is not null)
            clone.Content = new StringContent(
                await source.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
                System.Text.Encoding.UTF8,
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
