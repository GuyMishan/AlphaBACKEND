using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alpha.Application.Billing;

namespace Alpha.Api.Services;

public sealed class PayPlusPaymentProvider(IHttpClientFactory httpClients, IConfiguration configuration) : IPaymentProvider
{
    public string Name => "PayPlus";

    private string BaseUrl => configuration["Payments:PayPlus:BaseUrl"]?.TrimEnd('/')
        ?? "https://restapidev.payplus.co.il/api/v1.0";
    private string ApiKey => Required("Payments:PayPlus:ApiKey");
    private string SecretKey => Required("Payments:PayPlus:SecretKey");
    private string TerminalUid => Required("Payments:PayPlus:TerminalUid");
    private string CashierUid => Required("Payments:PayPlus:CashierUid");
    private string PaymentPageUid => Required("Payments:PayPlus:PaymentPageUid");

    public async Task<PaymentProviderCustomerResult> CreateCustomer(PaymentProviderCustomerRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            email = request.Email,
            customer_name = request.Name,
            paying_vat = true,
            vat_number = request.TaxId,
            customer_number = request.ExternalReference,
            business_address = request.BillingAddress,
            business_country_iso = "IL",
            communication_email = request.Email
        };
        using var response = await SendAsync(HttpMethod.Post, "/Customers/Add", payload, ct);
        var root = await ReadJsonAsync(response, ct);
        var id = FirstString(root, "customer_uid", "uid", "customer_id")
            ?? throw new InvalidOperationException("PayPlus customer response did not include a customer id.");
        return new PaymentProviderCustomerResult(id);
    }

    public async Task<PaymentMethodSetupResult> CreatePaymentMethod(PaymentMethodSetupRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            payment_page_uid = PaymentPageUid,
            charge_method = 5,
            charge_default = "credit-card",
            hide_other_charge_methods = true,
            language_code = "he",
            amount = 0,
            currency_code = "ILS",
            sendEmailApproval = false,
            sendEmailFailure = false,
            refURL_success = request.SuccessUrl,
            refURL_failure = request.FailureUrl,
            refURL_cancel = request.CancelUrl,
            refURL_callback = request.CallbackUrl,
            send_failure_callback = true,
            create_token = true,
            initial_invoice = false,
            customer = new { customer_uid = request.CustomerId },
            more_info = request.ExternalReference,
            cashier_uid = CashierUid
        };

        using var response = await SendAsync(HttpMethod.Post, "/PaymentPages/generateLink", payload, ct);
        var root = await ReadJsonAsync(response, ct);
        var setupId = FirstString(root, "page_request_uid", "payment_request_uid")
            ?? throw new InvalidOperationException("PayPlus payment page response did not include page_request_uid.");
        var url = FirstString(root, "payment_page_link", "url", "link")
            ?? throw new InvalidOperationException("PayPlus payment page response did not include payment_page_link.");
        return new PaymentMethodSetupResult(setupId, url);
    }

    public async Task<PaymentChargeResult> Charge(PaymentChargeRequest request, CancellationToken ct = default)
    {
        var payload = new
        {
            terminal_uid = TerminalUid,
            cashier_uid = CashierUid,
            amount = request.Amount,
            currency_code = request.Currency,
            credit_terms = 1,
            use_token = true,
            token = request.PaymentMethodId,
            customer_uid = request.CustomerId,
            initial_invoice = request.CreateInvoice,
            extra_info = request.Description,
            more_info_1 = request.ExternalReference
        };

        using var response = await SendAsync(HttpMethod.Post, "/Transactions/Charge", payload, ct, throwOnFailure: false);
        var root = await ReadJsonAsync(response, ct, allowFailureStatus: true);
        var success = response.IsSuccessStatusCode && IsSuccess(root);
        return new PaymentChargeResult(
            success,
            FirstString(root, "transaction_uid", "uid") ?? string.Empty,
            FirstString(root, "document_uid", "invoice_uid", "invoice_number"),
            success ? null : FirstString(root, "code"),
            success ? null : FirstString(root, "description", "message") ?? "PayPlus charge failed.");
    }

    public async Task<PaymentMethodStatusResult> GetPaymentMethodStatus(string customerId, string paymentMethodId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/Token/View/{Uri.EscapeDataString(paymentMethodId)}?mask=true", null, ct);
        var root = await ReadJsonAsync(response, ct);
        var masked = FirstString(root, "card_number", "card_number_masked", "masked_card_number") ?? string.Empty;
        var last4 = new string(masked.Where(char.IsDigit).TakeLast(4).ToArray());
        var expiry = FirstString(root, "card_date_mmyy", "expiry", "expiration");
        ParseExpiry(expiry, out var month, out var year);

        return new PaymentMethodStatusResult(
            IsSuccess(root),
            paymentMethodId,
            FirstString(root, "brand_name", "brand", "card_brand") ?? string.Empty,
            last4,
            month,
            year,
            null,
            customerId,
            null);
    }

    public async Task CancelPaymentMethod(string customerId, string paymentMethodId, CancellationToken ct = default)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/Token/Remove/{Uri.EscapeDataString(paymentMethodId)}", new { }, ct);
        _ = await ReadJsonAsync(response, ct);
    }

    public async Task<PaymentMethodStatusResult> ResolvePaymentMethodFromCallback(string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default)
    {
        ValidateCallback(rawBody, headers);
        using var callback = JsonDocument.Parse(rawBody);
        var requestUid = FirstString(callback.RootElement, "payment_request_uid", "page_request_uid");
        var transactionUid = FirstString(callback.RootElement, "transaction_uid");

        object lookup = !string.IsNullOrWhiteSpace(requestUid)
            ? new { payment_request_uid = requestUid, related_transaction = true }
            : !string.IsNullOrWhiteSpace(transactionUid)
                ? new { transaction_uid = transactionUid, related_transaction = true }
                : throw new InvalidOperationException("PayPlus callback did not include a payment or transaction uid.");

        using var response = await SendAsync(HttpMethod.Post, "/PaymentPages/ipn-full", lookup, ct);
        var root = await ReadJsonAsync(response, ct);
        var token = FirstString(root, "token_uid", "card_token", "token")
            ?? throw new InvalidOperationException("PayPlus callback lookup did not include a card token.");
        var masked = FirstString(root, "card_number_masked", "card_number", "masked_card_number") ?? string.Empty;
        var last4 = new string(masked.Where(char.IsDigit).TakeLast(4).ToArray());
        var expiry = FirstString(root, "card_date_mmyy", "expiry", "expiration");
        ParseExpiry(expiry, out var month, out var year);

        return new PaymentMethodStatusResult(
            true,
            token,
            FirstString(root, "brand_name", "brand", "card_brand") ?? string.Empty,
            last4,
            month,
            year,
            null,
            FirstString(root, "customer_uid", "customer_id"),
            FirstString(root, "more_info", "external_reference"));
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? payload, CancellationToken ct, bool throwOnFailure = true)
    {
        var client = httpClients.CreateClient("payplus");
        using var request = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        request.Headers.Add("api-key", ApiKey);
        request.Headers.Add("secret-key", SecretKey);
        request.Headers.Accept.ParseAdd("application/json");
        if (payload is not null) request.Content = JsonContent.Create(payload);

        var response = await client.SendAsync(request, ct);
        if (throwOnFailure && !response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            throw new InvalidOperationException($"PayPlus request failed ({(int)response.StatusCode}): {Safe(body)}");
        }
        return response;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct, bool allowFailureStatus = false)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!allowFailureStatus && !response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Payment provider request failed ({(int)response.StatusCode}).");
        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        return document.RootElement.Clone();
    }

    private void ValidateCallback(string rawBody, IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("user-agent", out var userAgent) ||
            !userAgent.Contains("PayPlus", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Invalid PayPlus callback user-agent.");
        if (!headers.TryGetValue("hash", out var suppliedHash) || string.IsNullOrWhiteSpace(suppliedHash))
            throw new InvalidOperationException("Missing PayPlus callback hash.");

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SecretKey));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody)));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(suppliedHash.Trim())))
            throw new InvalidOperationException("Invalid PayPlus callback signature.");
    }

    private static bool IsSuccess(JsonElement root)
    {
        var status = FirstString(root, "status");
        var code = FirstString(root, "code");
        return string.Equals(status, "success", StringComparison.OrdinalIgnoreCase) || code == "0";
    }

    private static string? FirstString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            var found = Find(root, name);
            if (found.HasValue)
            {
                var value = found.Value;
                if (value.ValueKind == JsonValueKind.String) return value.GetString();
                if (value.ValueKind == JsonValueKind.Number) return value.GetRawText();
            }
        }
        return null;
    }

    private static JsonElement? Find(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                    return property.Value;
                var nested = Find(property.Value, name);
                if (nested.HasValue) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = Find(item, name);
                if (nested.HasValue) return nested;
            }
        }
        return null;
    }

    private static void ParseExpiry(string? value, out int? month, out int? year)
    {
        month = null; year = null;
        if (string.IsNullOrWhiteSpace(value)) return;
        var parts = value.Replace("-", "/").Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var m) || !int.TryParse(parts[1], out var y)) return;
        month = m;
        year = y < 100 ? 2000 + y : y;
    }

    private string Required(string key) =>
        configuration[key]?.Trim() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"Payment provider configuration '{key}' is missing.");

    private static string Safe(string value) => value.Length <= 500 ? value : value[..500];
}
