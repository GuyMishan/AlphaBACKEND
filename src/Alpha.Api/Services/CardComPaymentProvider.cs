using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alpha.Application.Billing;

namespace Alpha.Api.Services;

public sealed class CardComPaymentProvider(IHttpClientFactory clients, IConfiguration configuration) : IPaymentProvider
{
    public string Name => "CardCom";

    private string BaseUrl => (configuration["Payments:CardCom:BaseUrl"] ?? "https://secure.cardcom.solutions").TrimEnd('/');
    private int TerminalNumber => int.TryParse(Required("Payments:CardCom:TerminalNumber"), out var value)
        ? value
        : throw new InvalidOperationException("CardCom TerminalNumber must be numeric.");
    private string InterfaceApiName => Required("Payments:CardCom:InterfaceApiName");
    private string ApiName => Required("Payments:CardCom:ApiName");
    private string? ApiPassword => configuration["Payments:CardCom:ApiPassword"]?.Trim();

    public Task<PaymentProviderCustomerResult> CreateCustomer(
        PaymentProviderCustomerRequest request, CancellationToken ct = default) =>
        // API 11 tokenization does not require a separate CardCom customer entity.
        Task.FromResult(new PaymentProviderCustomerResult(request.ExternalReference));

    public async Task<PaymentMethodSetupResult> CreatePaymentMethod(
        PaymentMethodSetupRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            TerminalNumber,
            ApiName = InterfaceApiName,
            Amount = 1m,
            Operation = "CreateTokenOnly",
            ReturnValue = request.ExternalReference,
            SuccessRedirectUrl = request.SuccessUrl,
            FailedRedirectUrl = request.FailureUrl,
            WebHookUrl = request.CallbackUrl,
            ProductName = "ALPHA payment method",
            Language = "he",
            ISOCoinId = 1
        };

        using var response = await PostJson("/api/v11/LowProfile/Create", payload, ct);
        using var json = await ReadJson(response, "CardCom API 11 LowProfile/Create", ct);
        EnsureSuccess(response, json.RootElement, "CardCom API 11 LowProfile/Create");

        var id = StringValue(json.RootElement, "LowProfileId")
            ?? throw new InvalidOperationException("CardCom API 11 response did not include LowProfileId.");
        var url = StringValue(json.RootElement, "Url")
            ?? throw new InvalidOperationException("CardCom API 11 response did not include Url.");

        return new PaymentMethodSetupResult(id, url);
    }

    public async Task<PaymentChargeResult> Charge(PaymentChargeRequest request, CancellationToken ct = default)
    {
        if (!ValidExpiry(request.ExpiryMonth, request.ExpiryYear))
            return new PaymentChargeResult(false, string.Empty, null, "expiry_required",
                "CardCom API 11 token charge requires a valid token expiry.");
        if (string.IsNullOrWhiteSpace(request.PaymentMethodId))
            return new PaymentChargeResult(false, string.Empty, null, "token_required",
                "CardCom API 11 token charge requires a token.");

        var payload = new
        {
            TerminalNumber,
            ApiName = InterfaceApiName,
            Amount = request.Amount,
            Token = request.PaymentMethodId,
            CardExpirationMMYY = ExpiryMmYy(request.ExpiryMonth!.Value, request.ExpiryYear!.Value),
            ExternalUniqTranId = ExternalTransactionId(request.ExternalReference),
            ExternalUniqUniqTranIdResponse = false,
            NumOfPayments = 1
        };

        using var response = await PostJson("/api/v11/Transactions/Transaction", payload, ct);
        using var json = await ReadJson(response, "CardCom API 11 Transactions/Transaction", ct);
        var root = json.RootElement;
        var responseCode = IntValue(root, "ResponseCode");
        var success = response.IsSuccessStatusCode && responseCode == 0;

        return new PaymentChargeResult(
            success,
            NumberOrStringValue(root, "TranzactionId") ?? NumberOrStringValue(root, "TransactionId") ?? string.Empty,
            null,
            success ? null : responseCode?.ToString(CultureInfo.InvariantCulture) ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
            success ? null : StringValue(root, "Description") ?? $"CardCom API 11 charge failed ({(int)response.StatusCode}).");
    }

    public async Task<PaymentRefundResult> Refund(PaymentRefundRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ApiPassword))
            return new PaymentRefundResult(false, string.Empty, "api_password_missing",
                "CardCom API 11 ApiPassword is not configured.");
        if (!long.TryParse(request.TransactionId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var transactionId))
            return new PaymentRefundResult(false, string.Empty, "invalid_transaction_id",
                "CardCom API 11 refund requires the original CardCom transaction id.");

        var payload = new
        {
            ApiName,
            ApiPassword,
            TransactionId = transactionId,
            PartialSum = request.Amount,
            CancelOnly = false,
            AllowMultipleRefunds = true
        };

        using var response = await PostJson("/api/v11/Transactions/RefundByTransactionId", payload, ct);
        using var json = await ReadJson(response, "CardCom API 11 RefundByTransactionId", ct);
        var root = json.RootElement;
        var responseCode = IntValue(root, "ResponseCode");
        var success = response.IsSuccessStatusCode && responseCode == 0;

        return new PaymentRefundResult(
            success,
            NumberOrStringValue(root, "NewTranzactionId") ?? string.Empty,
            success ? null : responseCode?.ToString(CultureInfo.InvariantCulture) ?? ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture),
            success ? null : StringValue(root, "Description") ?? $"CardCom API 11 refund failed ({(int)response.StatusCode}).");
    }

    public Task<PaymentMethodStatusResult> GetPaymentMethodStatus(
        string customerId, string paymentMethodId, CancellationToken ct = default)
    {
        // API 11 has no harmless token-status request. The token was verified by GetLpResult
        // before being persisted; ALPHA treats it as active until locally cancelled/replaced.
        return Task.FromResult(new PaymentMethodStatusResult(
            !string.IsNullOrWhiteSpace(paymentMethodId),
            paymentMethodId,
            string.Empty,
            string.Empty,
            null,
            null,
            null,
            customerId));
    }

    public Task CancelPaymentMethod(string customerId, string paymentMethodId, CancellationToken ct = default) =>
        // This integration stores a reusable token, not a CardCom standing-order object.
        // Cancellation means ALPHA stops using and clears the token locally.
        Task.CompletedTask;

    public async Task<PaymentMethodStatusResult> ResolvePaymentMethodFromCallback(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        var lowProfileId = ExtractLowProfileId(rawBody);
        if (string.IsNullOrWhiteSpace(lowProfileId))
            throw new InvalidOperationException("CardCom API 11 webhook did not include LowProfileId.");

        var payload = new
        {
            TerminalNumber,
            ApiName = InterfaceApiName,
            LowProfileId = lowProfileId
        };

        // CardCom explicitly requires server-to-server verification after receiving WebHookUrl.
        using var response = await PostJson("/api/v11/LowProfile/GetLpResult", payload, ct);
        using var json = await ReadJson(response, "CardCom API 11 LowProfile/GetLpResult", ct);
        var root = json.RootElement;
        EnsureSuccess(response, root, "CardCom API 11 LowProfile/GetLpResult");

        var verifiedId = StringValue(root, "LowProfileId");
        if (!string.Equals(lowProfileId, verifiedId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("CardCom API 11 LowProfileId verification mismatch.");

        var operation = StringValue(root, "Operation");
        if (!string.Equals(operation, "CreateTokenOnly", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(operation, "ChargeAndCreateToken", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unexpected CardCom API 11 operation '{operation}'.");

        var returnValue = StringValue(root, "ReturnValue");
        if (string.IsNullOrWhiteSpace(returnValue))
            throw new InvalidOperationException("CardCom API 11 result did not include ReturnValue.");

        var tokenInfo = ObjectValue(root, "TokenInfo")
            ?? throw new InvalidOperationException("CardCom API 11 result did not include TokenInfo.");
        var token = StringValue(tokenInfo, "Token");
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("CardCom API 11 TokenInfo did not include Token.");

        var month = IntValue(tokenInfo, "CardMonth");
        var year = NormalizeYear(IntValue(tokenInfo, "CardYear"));
        if (!ValidExpiry(month, year))
            throw new InvalidOperationException("CardCom API 11 returned an invalid token expiry.");

        var transactionInfo = ObjectValue(root, "TranzactionInfo");
        if (transactionInfo.HasValue)
        {
            var transactionResponse = IntValue(transactionInfo.Value, "ResponseCode");
            if (transactionResponse.HasValue && transactionResponse is not 0 and not 700 and not 701)
                throw new InvalidOperationException(
                    $"CardCom API 11 token verification transaction failed. ResponseCode={transactionResponse.Value}");
        }

        var last4 = transactionInfo.HasValue
            ? StringValue(transactionInfo.Value, "Last4CardDigitsString") ?? string.Empty
            : string.Empty;
        var brand = transactionInfo.HasValue
            ? StringValue(transactionInfo.Value, "Brand") ?? StringValue(transactionInfo.Value, "CardName") ?? string.Empty
            : string.Empty;

        return new PaymentMethodStatusResult(
            true,
            token,
            brand,
            last4,
            month,
            year,
            null,
            null,
            returnValue);
    }

    private static readonly JsonSerializerOptions CardComJson = new()
    {
        PropertyNamingPolicy = null
    };

    private async Task<HttpResponseMessage> PostJson(string path, object payload, CancellationToken ct)
    {
        var client = clients.CreateClient("cardcom");
        using var content = JsonContent.Create(payload, options: CardComJson);
        return await client.PostAsync($"{BaseUrl}{path}", content, ct);
    }

    private static async Task<JsonDocument> ReadJson(HttpResponseMessage response, string operation, CancellationToken ct)
    {
        var raw = await response.Content.ReadAsStringAsync(ct);
        try
        {
            return JsonDocument.Parse(raw);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{operation} returned invalid JSON.", ex);
        }
    }

    private static void EnsureSuccess(HttpResponseMessage response, JsonElement root, string operation)
    {
        var code = IntValue(root, "ResponseCode");
        if (!response.IsSuccessStatusCode || code != 0)
            throw new InvalidOperationException(
                $"{operation} failed: {StringValue(root, "Description") ?? $"HTTP {(int)response.StatusCode}"} (ResponseCode={code?.ToString() ?? "<missing>"}).");
    }

    private static string? ExtractLowProfileId(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return null;
        try
        {
            using var json = JsonDocument.Parse(rawBody);
            return StringValue(json.RootElement, "LowProfileId");
        }
        catch (JsonException)
        {
            // Defensive compatibility for providers/proxies that append the id as a query/form value.
            var pairs = rawBody.Trim().TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Split('=', 2))
                .Where(x => x.Length == 2);
            foreach (var pair in pairs)
                if (string.Equals(Uri.UnescapeDataString(pair[0]), "LowProfileId", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(pair[1].Replace("+", " "));
            return null;
        }
    }

    private static JsonElement? ObjectValue(JsonElement root, string name) =>
        TryProperty(root, name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? StringValue(JsonElement root, string name)
    {
        if (!TryProperty(root, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
    }

    private static string? NumberOrStringValue(JsonElement root, string name) =>
        StringValue(root, name);

    private static int? IntValue(JsonElement root, string name)
    {
        if (!TryProperty(root, name, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }

    private static bool TryProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

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

    private static string ExpiryMmYy(int month, int year)
    {
        year = NormalizeYear(year) ?? year;
        return $"{month:00}{year % 100:00}";
    }

    private static string ExternalTransactionId(string source)
    {
        // CardCom documents ExternalUniqTranId as max 25 characters.
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(source ?? string.Empty));
        return Convert.ToHexString(bytes)[..25].ToLowerInvariant();
    }

    private string Required(string key) =>
        configuration[key]?.Trim() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Payment provider configuration '{key}' is missing.");
}
