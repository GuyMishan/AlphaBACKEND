using System.Globalization;
using System.Net;
using Alpha.Application.Billing;

namespace Alpha.Api.Services;

public sealed class CardComPaymentProvider(IHttpClientFactory clients, IConfiguration configuration) : IPaymentProvider
{
    public string Name => "CardCom";

    private string BaseUrl => (configuration["Payments:CardCom:BaseUrl"] ?? "https://test.cardcom.solutions").TrimEnd('/');
    private string TerminalNumber => Required("Payments:CardCom:TerminalNumber");
    private string UserName => Required("Payments:CardCom:UserName");
    private string? RefundPassword => configuration["Payments:CardCom:RefundPassword"]?.Trim();

    public Task<PaymentProviderCustomerResult> CreateCustomer(
        PaymentProviderCustomerRequest request, CancellationToken ct = default) =>
        Task.FromResult(new PaymentProviderCustomerResult(request.ExternalReference));

    public async Task<PaymentMethodSetupResult> CreatePaymentMethod(
        PaymentMethodSetupRequest request, CancellationToken ct = default)
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
        EnsureHttpAndResponseSuccess(response, parsed, "CardCom low profile setup");

        var code = RequiredValue(parsed, "LowProfileCode", "CardCom response did not include LowProfileCode.");
        var url = Get(parsed, "url") ?? Get(parsed, "Url");
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException("CardCom response did not include redirect url.");

        return new PaymentMethodSetupResult(code, url);
    }

    public async Task<PaymentChargeResult> Charge(PaymentChargeRequest request, CancellationToken ct = default)
    {
        if (!ValidExpiry(request.ExpiryMonth, request.ExpiryYear))
            return new PaymentChargeResult(false, string.Empty, null, "expiry_required",
                "CardCom token charge requires a valid token expiry.");

        var values = TokenOperationValues(
            request.PaymentMethodId,
            request.Amount,
            request.ExpiryMonth!.Value,
            request.ExpiryYear!.Value,
            refund: false,
            request.Description);

        using var response = await PostForm("/interface/ChargeToken.aspx", values, ct);
        var parsed = ParseNameValue(await response.Content.ReadAsStringAsync(ct));
        var responseCode = Get(parsed, "ResponseCode");
        var ok = response.IsSuccessStatusCode && responseCode == "0";

        return new PaymentChargeResult(
            ok,
            Get(parsed, "Uid") ?? Get(parsed, "InternalDealNumber") ?? string.Empty,
            Get(parsed, "InvoiceNumber") ?? Get(parsed, "InvoiceDocumentNumber"),
            ok ? null : responseCode ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
            ok ? null : Get(parsed, "Description") ?? $"CardCom charge failed ({(int)response.StatusCode}).");
    }

    public async Task<PaymentRefundResult> Refund(PaymentRefundRequest request, CancellationToken ct = default)
    {
        if (!ValidExpiry(request.ExpiryMonth, request.ExpiryYear))
            return new PaymentRefundResult(false, string.Empty, "expiry_required",
                "CardCom token refund requires a valid token expiry.");
        if (string.IsNullOrWhiteSpace(RefundPassword))
            return new PaymentRefundResult(false, string.Empty, "refund_password_missing",
                "CardCom refund password is not configured.");

        var values = TokenOperationValues(
            request.PaymentMethodId,
            request.Amount,
            request.ExpiryMonth!.Value,
            request.ExpiryYear!.Value,
            refund: true,
            description: $"ALPHA refund {request.ExternalReference}");
        values["TokenToCharge.UserPassword"] = RefundPassword!;

        using var response = await PostForm("/interface/ChargeToken.aspx", values, ct);
        var parsed = ParseNameValue(await response.Content.ReadAsStringAsync(ct));
        var responseCode = Get(parsed, "ResponseCode");
        var ok = response.IsSuccessStatusCode && responseCode == "0";

        return new PaymentRefundResult(
            ok,
            Get(parsed, "Uid") ?? Get(parsed, "InternalDealNumber") ?? string.Empty,
            ok ? null : responseCode ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
            ok ? null : Get(parsed, "Description") ?? $"CardCom refund failed ({(int)response.StatusCode}).");
    }

    public Task<PaymentMethodStatusResult> GetPaymentMethodStatus(
        string customerId, string paymentMethodId, CancellationToken ct = default)
    {
        // CardCom API 10 tokenization does not expose a harmless "ping token" endpoint.
        // A token that was server-verified through GetLowProfileIndicator remains active
        // locally until it is replaced/cancelled or its stored expiry passes.
        if (string.IsNullOrWhiteSpace(paymentMethodId))
            return Task.FromResult(new PaymentMethodStatusResult(
                false, string.Empty, string.Empty, string.Empty, null, null, null, customerId));

        return Task.FromResult(new PaymentMethodStatusResult(
            true, paymentMethodId, string.Empty, string.Empty, null, null, null, customerId));
    }

    public Task CancelPaymentMethod(string customerId, string paymentMethodId, CancellationToken ct = default)
    {
        // CardCom tokens are not a recurring instruction owned by CardCom in this integration.
        // ALPHA stops using the token by clearing it locally; no remote cancellation is required.
        return Task.CompletedTask;
    }

    public async Task<PaymentMethodStatusResult> ResolvePaymentMethodFromCallback(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        var callback = ParseNameValue(rawBody);
        var lowProfileCode = Get(callback, "LowProfileCode")
            ?? Get(callback, "lowprofilecode");
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

        EnsureHttpAndResponseSuccess(response, parsed, "CardCom low profile indicator");
        EnsureCode(parsed, "OperationResponse", "0", "CardCom low profile operation was not successful.");

        var operation = Get(parsed, "Operation");
        if (!string.IsNullOrWhiteSpace(operation) && operation != "3")
            throw new InvalidOperationException($"Unexpected CardCom low profile operation '{operation}'.");

        EnsureCode(parsed, "TokenResponse", "0", "CardCom token creation failed.");

        var token = Get(parsed, "Token") ?? Get(parsed, "ExtShvaParams.CardToken");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("CardCom indicator did not include a token.");

        var externalReference = Get(parsed, "ReturnValue");
        if (string.IsNullOrWhiteSpace(externalReference))
            throw new InvalidOperationException("CardCom indicator did not include ReturnValue.");

        var returnedLowProfileCode = Get(parsed, "LowProfileCode") ?? Get(parsed, "lowprofilecode");
        if (!string.IsNullOrWhiteSpace(returnedLowProfileCode) &&
            !string.Equals(returnedLowProfileCode, lowProfileCode, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CardCom LowProfileCode verification mismatch.");

        var last4 = Get(parsed, "ExtShvaParams.CardNumber5") ?? Get(parsed, "CardNumEnd") ?? string.Empty;
        last4 = new string(last4.Where(char.IsDigit).TakeLast(4).ToArray());

        var expiryMonth = ParseInt(Get(parsed, "CardValidityMonth"));
        var expiryYear = NormalizeYear(ParseInt(Get(parsed, "CardValidityYear")));
        if ((!expiryMonth.HasValue || !expiryYear.HasValue) && TryParseTokenExpiry(Get(parsed, "TokenExDate"), out var tokenMonth, out var tokenYear))
        {
            expiryMonth ??= tokenMonth;
            expiryYear ??= tokenYear;
        }

        if (!ValidExpiry(expiryMonth, expiryYear))
            throw new InvalidOperationException("CardCom indicator returned an invalid token expiry.");

        return new PaymentMethodStatusResult(
            true,
            token,
            Get(parsed, "ExtShvaParams.CardName") ?? Get(parsed, "CardName104") ?? string.Empty,
            last4,
            expiryMonth,
            expiryYear,
            null,
            null,
            externalReference);
    }

    private Dictionary<string, string> TokenOperationValues(
        string token, decimal amount, int month, int year, bool refund, string? description)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("CardCom token is required.");
        if (amount <= 0)
            throw new InvalidOperationException("CardCom amount must be positive.");

        var values = new Dictionary<string, string>
        {
            ["terminalnumber"] = TerminalNumber,
            ["username"] = UserName,
            ["codepage"] = "65001",
            ["TokenToCharge.Token"] = token,
            ["TokenToCharge.CardValidityMonth"] = month.ToString("00", CultureInfo.InvariantCulture),
            ["TokenToCharge.CardValidityYear"] = NormalizeYear(year)!.Value.ToString("0000", CultureInfo.InvariantCulture),
            ["TokenToCharge.SumToBill"] = amount.ToString("0.00", CultureInfo.InvariantCulture),
            ["TokenToCharge.CoinID"] = "1",
            ["TokenToCharge.APILevel"] = "10",
            ["TokenToCharge.RefundInsteadOfCharge"] = refund ? "True" : "False"
        };

        if (!string.IsNullOrWhiteSpace(description))
            values["TokenToCharge.DealDescription"] = description.Trim()[..Math.Min(description.Trim().Length, 200)];

        return values;
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
            .ToDictionary(
                x => x.Key,
                x => WebUtility.UrlDecode(x.Last()[1].Replace("+", " ")),
                StringComparer.OrdinalIgnoreCase);

    private static string ToQuery(IReadOnlyDictionary<string, string> values) =>
        string.Join("&", values.Select(x =>
            $"{WebUtility.UrlEncode(x.Key)}={WebUtility.UrlEncode(x.Value)}"));

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static string RequiredValue(IReadOnlyDictionary<string, string> values, string key, string message) =>
        Get(values, key) is { Length: > 0 } value ? value : throw new InvalidOperationException(message);

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static int? NormalizeYear(int? year)
    {
        if (!year.HasValue) return null;
        return year.Value is >= 0 and <= 99 ? 2000 + year.Value : year.Value;
    }

    private static bool ValidExpiry(int? month, int? year)
    {
        year = NormalizeYear(year);
        if (month is < 1 or > 12 || year is < 2000 or > 2200) return false;
        var now = DateTimeOffset.UtcNow;
        return year > now.Year || (year == now.Year && month >= now.Month);
    }

    private static bool TryParseTokenExpiry(string? value, out int month, out int year)
    {
        month = 0;
        year = 0;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length < 6) return false;

        // CardCom examples use yyyyMMdd (e.g. 20241101). Month/year are all ALPHA needs.
        if (digits.Length >= 8 &&
            int.TryParse(digits[..4], out year) &&
            int.TryParse(digits.Substring(4, 2), out month))
            return month is >= 1 and <= 12;

        return false;
    }

    private static void EnsureHttpAndResponseSuccess(
        HttpResponseMessage response, IReadOnlyDictionary<string, string> values, string operation)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{operation} failed with HTTP {(int)response.StatusCode}.");

        EnsureCode(values, "ResponseCode", "0",
            $"{operation} failed: {Get(values, "Description") ?? "unknown CardCom error"}.");
    }

    private static void EnsureCode(
        IReadOnlyDictionary<string, string> values, string key, string expected, string message)
    {
        var actual = Get(values, key);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{message} {key}={actual ?? "<missing>"}");
    }

    private string Required(string key) =>
        configuration[key]?.Trim() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"Payment provider configuration '{key}' is missing.");
}
