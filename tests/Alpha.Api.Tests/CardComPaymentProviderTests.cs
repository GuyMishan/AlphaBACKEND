using System.Net;
using Alpha.Api.Services;
using Alpha.Application.Billing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class CardComPaymentProviderTests
{
    [Fact]
    public async Task Setup_creates_operation_3_low_profile_request()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(async request =>
        {
            captured = await CloneRequestAsync(request);
            return Text(HttpStatusCode.OK,
                "ResponseCode=0&Description=OK&LowProfileCode=LP-123&url=https%3A%2F%2Ftest.cardcom.solutions%2Fpay%2FLP-123");
        });

        var result = await provider.CreatePaymentMethod(new PaymentMethodSetupRequest(
            "customer", "https://alpha.test/success", "https://alpha.test/fail",
            "https://alpha.test/cancel", "https://api.alpha.test/callback", "account-id"));

        Assert.Equal("LP-123", result.SetupRequestId);
        Assert.Equal("https://test.cardcom.solutions/pay/LP-123", result.RedirectUrl);
        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.EndsWith("/Interface/LowProfile.aspx", captured.RequestUri!.AbsolutePath);

        var form = CardComPaymentProvider.ParseNameValue(await captured.Content!.ReadAsStringAsync());
        Assert.Equal("3", form["Operation"]);
        Assert.Equal("account-id", form["ReturnValue"]);
        Assert.Equal("https://api.alpha.test/callback", form["IndicatorUrl"]);
    }

    [Fact]
    public async Task Callback_is_verified_server_to_server_before_token_is_accepted()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Contains("BillGoldGetLowProfileIndicator.aspx", request.RequestUri!.AbsoluteUri);
            Assert.Contains("LowProfileCode=LP-123", request.RequestUri.AbsoluteUri);
            return Task.FromResult(Text(HttpStatusCode.OK,
                "ResponseCode=0&Description=Low+Profile+Code+Found&LowProfileCode=LP-123&Operation=3&" +
                "OperationResponse=0&TokenResponse=0&Token=tok-123&TokenExDate=20301201&" +
                "CardValidityYear=2030&CardValidityMonth=12&ExtShvaParams.CardNumber5=4242&" +
                "ExtShvaParams.CardName=VISA&ReturnValue=11111111-1111-1111-1111-111111111111"));
        });

        var result = await provider.ResolvePaymentMethodFromCallback(
            "LowProfileCode=LP-123", new Dictionary<string, string>());

        Assert.True(result.Active);
        Assert.Equal("tok-123", result.PaymentMethodId);
        Assert.Equal("4242", result.Last4);
        Assert.Equal(12, result.ExpiryMonth);
        Assert.Equal(2030, result.ExpiryYear);
        Assert.Equal("11111111-1111-1111-1111-111111111111", result.ExternalReference);
    }

    [Fact]
    public async Task Callback_rejects_failed_token_creation()
    {
        var provider = CreateProvider(_ => Task.FromResult(Text(HttpStatusCode.OK,
            "ResponseCode=0&LowProfileCode=LP-123&Operation=3&OperationResponse=0&TokenResponse=5&" +
            "ReturnValue=11111111-1111-1111-1111-111111111111")));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ResolvePaymentMethodFromCallback(
                "LowProfileCode=LP-123", new Dictionary<string, string>()));

        Assert.Contains("TokenResponse=5", error.Message);
    }

    [Fact]
    public async Task Charge_posts_token_and_returns_decline_without_throwing()
    {
        string? posted = null;
        var provider = CreateProvider(async request =>
        {
            posted = await request.Content!.ReadAsStringAsync();
            return Text(HttpStatusCode.OK, "ResponseCode=4&Description=Declined");
        });

        var result = await provider.Charge(new PaymentChargeRequest(
            "customer", "tok-123", 99.90m, "ILS", "ALPHA monthly charge",
            "billing:key", true, 12, 2030));

        Assert.False(result.Success);
        Assert.Equal("4", result.ErrorCode);
        Assert.Equal("Declined", result.ErrorMessage);

        var form = CardComPaymentProvider.ParseNameValue(posted!);
        Assert.Equal("tok-123", form["TokenToCharge.Token"]);
        Assert.Equal("99.90", form["TokenToCharge.SumToBill"]);
        Assert.Equal("12", form["TokenToCharge.CardValidityMonth"]);
        Assert.Equal("2030", form["TokenToCharge.CardValidityYear"]);
        Assert.Equal("False", form["TokenToCharge.RefundInsteadOfCharge"]);
    }

    [Fact]
    public async Task Refund_supports_partial_amount_and_refund_password()
    {
        string? posted = null;
        var provider = CreateProvider(async request =>
        {
            posted = await request.Content!.ReadAsStringAsync();
            return Text(HttpStatusCode.OK, "ResponseCode=0&Description=OK&Uid=refund-uid");
        }, refundPassword: "refund-secret");

        var result = await provider.Refund(new PaymentRefundRequest(
            "charge-uid", "tok-123", 25.50m, "ILS", "refund:key", 12, 2030));

        Assert.True(result.Success);
        Assert.Equal("refund-uid", result.RefundId);

        var form = CardComPaymentProvider.ParseNameValue(posted!);
        Assert.Equal("25.50", form["TokenToCharge.SumToBill"]);
        Assert.Equal("True", form["TokenToCharge.RefundInsteadOfCharge"]);
        Assert.Equal("refund-secret", form["TokenToCharge.UserPassword"]);
    }

    private static CardComPaymentProvider CreateProvider(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler,
        string refundPassword = "")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Payments:CardCom:BaseUrl"] = "https://test.cardcom.solutions",
                ["Payments:CardCom:TerminalNumber"] = "131719",
                ["Payments:CardCom:UserName"] = "test-user",
                ["Payments:CardCom:RefundPassword"] = refundPassword
            })
            .Build();

        return new CardComPaymentProvider(
            new TestHttpClientFactory(new HttpClient(new DelegateHandler(handler))),
            configuration);
    }

    private static HttpResponseMessage Text(HttpStatusCode status, string value) =>
        new(status) { Content = new StringContent(value) };

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri);
        if (source.Content is not null)
            clone.Content = new StringContent(await source.Content.ReadAsStringAsync());
        return clone;
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request);
    }
}
