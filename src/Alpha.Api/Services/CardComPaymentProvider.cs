using System.Net;
using Alpha.Application.Billing;

namespace Alpha.Api.Services;

public sealed class CardComPaymentProvider(IHttpClientFactory clients, IConfiguration configuration) : IPaymentProvider
{
    public string Name => "CardCom";

    private string BaseUrl => configuration["Payments:CardCom:BaseUrl"]?.TrimEnd('/')
        ?? "https://secure.cardcom.solutions";
    private string TerminalNumber => Required("Payments:CardCom:TerminalNumber");
    private string UserName => Required("Payments:CardCom:UserName");
    private string? RefundPassword => configuration["Payments:CardCom:RefundPassword"]?.Trim();

    public Task<PaymentProviderCustomerResult> CreateCustomer(PaymentProviderCustomerRequest request, CancellationToken ct = default) =>
        Task.FromResult(new PaymentProviderCustomerResult(request.ExternalReference));

    public async Task<PaymentMethodSetupResult> CreatePaymentMethod(PaymentMethodSetupRequest request, CancellationToken ct = default)
    {
        var values = new Dictionary<string, string>
        {
            ["Operation"] = "3",
            ["TerminalNumber"] = TerminalNumber,
            ["UserName"] = UserName,
            ["SumToBill"] = "1",
            ["CoinID"] = "1",
            ["Language"] = "he",
            ["ProductName"] = "ALPHA payment method",
            ["APILevel"] = "10",
            ["codepage"] = "65001",
            ["SuccessRedirectUrl"] = request.SuccessUrl,
            ["ErrorRedirectUrl"] = request.FailureUrl,
            ["IndicatorUrl"] = request.CallbackUrl,
            ["ReturnValue"] = request.ExternalReference,
            ["AutoRedirect"] = "true"
        };

        using var response = await PostForm("/Interface/LowProfile.aspx", values, ct);
        var parsed = ParseNameValue(await response.Content.ReadAsStringAsync(ct));
        EnsureSuccess(parsed, "CardCom low profile setup");

        var code = Get(parsed, "LowProfileCode")
            ?? throw new InvalidOperationException("CardCom response did not include LowProfileCode.");
        var url = Get(parsed, "url")
            ?? throw new InvalidOperationException("CardCom response did not include redirect url.");
        return new PaymentMethodSetupResult(code, url);
    }

    public async Task<PaymentChargeResult> Charge(PaymentChargeRequest request, CancellationToken ct = default)
    {
        if (!request.ExpiryMonth.HasValue || !request.ExpiryYear.HasValue)
            return new PaymentChargeResult(false, string.Empty, null, "expiry_required",
                "CardCom token charge requires token expiry.");

        var values = TokenOperationValues(request.PaymentMethodId, request.Amount,
            request.ExpiryMonth.Value, request.ExpiryYear.Value, false);

        using var response = await PostForm("/interface/ChargeToken.aspx", values, ct);
        var parsed = ParseNameValue(await response.Content.ReadAsStringAsync(ct));
        var ok = response.IsSuccessStatusCode && Get(parsed, "ResponseCode") == "0";

        return new PaymentChargeResult(
            ok,
            Get(parsed, "Uid") ?? Get(parsed, "InternalDealNumber") ?? string.Empty,
            null,
            ok ? null : Get(parsed, "ResponseCode"),
            ok ? null : Get(parsed, "Description") ?? "CardCom charge failed.");
    }

    public async Task<PaymentRefundResult> Refund(PaymentRefundRequest request, CancellationToken ct = default)
    {
        if (!request.ExpiryMonth.HasValue || !request.ExpiryYear.HasValue)
            return new PaymentRefundResult(false, string.Empty, "expiry_required",
                "CardCom token refund requires token expiry.");
        if (string.IsNullOrWhiteSpace(RefundPassword))
            return new PaymentRefundResult(false, string.Empty, "refund_password_missing",
                "CardCom refund password is not configured.");

        var values = TokenOperationValues(request.PaymentMethodId, request.Amount,
            request.ExpiryMonth.Value, request.ExpiryYear.Value, true);
        values["TokenToCharge.UserPassword"] = RefundPassword!;

        using var response = await PostForm("/interface/ChargeToken.aspx", values, ct);
        var parsed = ParseNameValue(await response.Content.ReadAsStringAsync(ct));
        var ok = response.IsSuccessStatusCode && Get(parsed, "ResponseCode") == "0";

        return new PaymentRefundResult(
            ok,
            Get(parsed, "Uid") ?? Get(parsed, "InternalDealNumber") ?? string.Empty,
            ok ? null : Get(parsed, "ResponseCode"),
            ok ? null : Get(parsed, "Description") ?? "CardCom refund failed.");
    }

    public Task<PaymentMethodStatusResult> GetPaymentMethodStatus(
        string customerId, string paymentMethodId, CancellationToken ct = default) =>
        Task.FromResult(new PaymentMethodStatusResult(
            true, paymentMethodId, string.Empty, string.Empty, null, null, null, customerId));

    public Task CancelPaymentMethod(string customerId, string paymentMethodId, CancellationToken ct = default) =>
        Task.CompletedTask;

    public async Task<PaymentMethodStatusResult> ResolvePaymentMethodFromCallback(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        var callback = ParseNameValue(rawBody);
        var lowProfileCode = Get(callback, "LowProfileCode");
        if (string.IsNullOrWhiteSpace(lowProfileCode))
            throw new InvalidOperationException("CardCom callback did not include LowProfileCode.");

        var query = new Dictionary<string, string>
        {
            ["TerminalNumber"] = TerminalNumber,
            ["UserName"] = UserName,
            ["codepage"] = "65001",
            ["LowProfileCode"] = lowProfileCode
        };

        var client = clients.CreateClient("cardcom");
        using var response = await client.GetAsync(
            $"{BaseUrl}/Interface/BillGoldGetLowProfileIndicator.aspx?{ToQuery(query)}", ct);
        var parsed = ParseNameValue(await response.Content.ReadAsStringAsync(ct));
        EnsureSuccess(parsed, "CardCom low profile indicator");

        var token = Get(parsed, "Token") ?? Get(parsed, "ExtShvaParams.CardToken")
            ?? throw new InvalidOperationException("CardCom indicator did not include a token.");
        var last4 = Get(parsed, "ExtShvaParams.CardNumber5") ?? Get(parsed, "CardNumEnd") ?? string.Empty;
        last4 = new string(last4.Where(char.IsDigit).TakeLast(4).ToArray());

        return new PaymentMethodStatusResult(
            true,
            token,
            Get(parsed, "ExtShvaParams.CardName") ?? Get(parsed, "CardName104") ?? string.Empty,
            last4,
            ParseInt(Get(parsed, "CardValidityMonth")),
            ParseInt(Get(parsed, "CardValidityYear")),
            null,
            null,
            Get(parsed, "ReturnValue"));
    }

    private Dictionary<string, string> TokenOperationValues(
        string token, decimal amount, int month, int year, bool refund)
    {
        var twoDigitYear = year >= 2000 ? year % 100 : year;
        return new Dictionary<string, string>
        {
            ["terminalnumber"] = TerminalNumber,
            ["username"] = UserName,
            ["codepage"] = "65001",
            ["TokenToCharge.Token"] = token,
            ["TokenToCharge.CardValidityMonth"] = month.ToString("00"),
            ["TokenToCharge.CardValidityYear"] = twoDigitYear.ToString("00"),
            ["TokenToCharge.SumToBill"] = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
            ["TokenToCharge.CoinID"] = "1",
            ["TokenToCharge.APILevel"] = "10",
            ["TokenToCharge.RefundInsteadOfCharge"] = refund ? "True" : "False"
        };
    }

    private async Task<HttpResponseMessage> PostForm(
        string path, IReadOnlyDictionary<string, string> values, CancellationToken ct)
    {
        var client = clients.CreateClient("cardcom");
        using var content = new FormUrlEncodedContent(values);
        return await client.PostAsync($"{BaseUrl}{path}", content, ct);
    }

    private static Dictionary<string, string> ParseNameValue(string raw) =>
        raw.Trim().TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Split('=', 2))
            .Where(x => x.Length == 2)
            .GroupBy(x => WebUtility.UrlDecode(x[0]), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => WebUtility.UrlDecode(x.Last()[1]), StringComparer.OrdinalIgnoreCase);

    private static string ToQuery(IReadOnlyDictionary<string, string> values) =>
        string.Join("&", values.Select(x =>
            $"{WebUtility.UrlEncode(x.Key)}={WebUtility.UrlEncode(x.Value)}"));

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var parsed) ? parsed : null;

    private static void EnsureSuccess(IReadOnlyDictionary<string, string> values, string operation)
    {
        if (Get(values, "ResponseCode") != "0")
            throw new InvalidOperationException(
                $"{operation} failed: {Get(values, "Description") ?? "unknown CardCom error"}.");
    }

    private string Required(string key) =>
        configuration[key]?.Trim() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Payment provider configuration '{key}' is missing.");
}
