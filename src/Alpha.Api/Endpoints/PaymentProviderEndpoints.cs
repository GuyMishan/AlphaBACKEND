using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Billing;
using Alpha.Domain.Auditing;
using Alpha.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record BillingChargeRequest(decimal Amount, string? Description, bool CreateInvoice = true);
public sealed record PaymentMethodSetupApiRequest(string? ReturnPath);

public static class PaymentProviderEndpoints
{
    public static IEndpointRouteBuilder MapPaymentProviderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var org = endpoints.MapGroup("/api/organizations/{organizationId:guid}/billing-account/provider")
            .RequireAuthorization().WithTags("Alpha Billing Provider");
        org.MapPost("/setup", StartOrganizationSetupAsync);
        org.MapPost("/sync", SyncOrganizationAsync);
        org.MapPost("/cancel", CancelOrganizationAsync);

        var employer = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/billing-account/provider")
            .RequireAuthorization().WithTags("Alpha Billing Provider");
        employer.MapPost("/setup", StartEmployerSetupAsync);
        employer.MapPost("/sync", SyncEmployerAsync);
        employer.MapPost("/cancel", CancelEmployerAsync);

        endpoints.MapPost("/api/platform/billing-accounts/{billingAccountId:guid}/charge", ChargeAsync)
            .RequireAuthorization().WithTags("Alpha Billing Provider");

        endpoints.MapMethods("/api/billing/providers/{providerName}/callback", new[] { "GET", "POST" }, ProviderCallbackAsync)
            .AllowAnonymous().WithTags("Alpha Billing Provider");
        endpoints.MapPost("/api/billing/payplus/callback", LegacyPayPlusCallbackAsync)
            .AllowAnonymous().WithTags("Alpha Billing Provider");

        return endpoints;
    }

    private static async Task<IResult> StartOrganizationSetupAsync(Guid organizationId, PaymentMethodSetupApiRequest request,
        IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        IPaymentProvider provider, IConfiguration config, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.EmployerId == null, ct);
        if (account is null) return Results.Conflict(new { error = "billing_account_required" });
        return await StartSetupAsync(account, organizationId, null, request.ReturnPath, db, currentUser, provider, config, http, ct);
    }

    private static async Task<IResult> StartEmployerSetupAsync(Guid organizationId, Guid employerId, PaymentMethodSetupApiRequest request,
        IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        IPaymentProvider provider, IConfiguration config, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.EmployerId == employerId && x.OrganizationId == null, ct);
        if (account is null) return Results.Conflict(new { error = "billing_account_required" });
        return await StartSetupAsync(account, organizationId, employerId, request.ReturnPath, db, currentUser, provider, config, http, ct);
    }

    private static async Task<IResult> StartSetupAsync(BillingAccount account, Guid organizationId, Guid? employerId,
        string? returnPath, IAlphaDbContext db, ICurrentUser currentUser, IPaymentProvider provider,
        IConfiguration config, HttpContext http, CancellationToken ct)
    {
        if (account.PaymentMethodType != BillingPaymentMethodType.CreditCard)
            return Results.Conflict(new { error = "provider_setup_not_supported_for_bank_debit" });
        if (string.IsNullOrWhiteSpace(account.BillingName) || string.IsNullOrWhiteSpace(account.InvoiceEmail))
            return Results.Conflict(new { error = "billing_details_incomplete" });

        try
        {
            var customerId = account.ProviderCustomerId;
            if (string.IsNullOrWhiteSpace(customerId))
            {
                var customer = await provider.CreateCustomer(new PaymentProviderCustomerRequest(
                    account.BillingName,
                    account.InvoiceEmail,
                    account.TaxId,
                    account.BillingAddress,
                    account.Id.ToString()), ct);
                customerId = customer.CustomerId;
            }

            var frontend = config["Frontend:BaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(frontend))
                frontend = $"{http.Request.Scheme}://{http.Request.Host}";

            var safeReturnPath = !string.IsNullOrWhiteSpace(returnPath) && returnPath.StartsWith('/') && !returnPath.StartsWith("//")
                ? returnPath
                : "/settings";
            var returnSeparator = safeReturnPath.Contains('?') ? "&" : "?";

            var callbackBase = config["Payments:PublicApiBaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(callbackBase))
                callbackBase = $"{http.Request.Scheme}://{http.Request.Host}";

            var setup = await provider.CreatePaymentMethod(new PaymentMethodSetupRequest(
                customerId,
                $"{frontend}{safeReturnPath}{returnSeparator}payment=success",
                $"{frontend}{safeReturnPath}{returnSeparator}payment=failed",
                $"{frontend}{safeReturnPath}{returnSeparator}payment=cancelled",
                $"{callbackBase}/api/billing/providers/{Uri.EscapeDataString(provider.Name)}/callback",
                account.Id.ToString()), ct);

            account.UpdateProviderMetadata(
                BillingPaymentMethodStatus.Pending,
                customerId,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                null,
                string.Empty);

            db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "billing.payment-method.setup-started",
                nameof(BillingAccount), account.Id, organizationId, employerId,
                JsonSerializer.Serialize(new { provider = provider.Name, setup.SetupRequestId }),
                http.TraceIdentifier));
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                provider = provider.Name,
                setupRequestId = setup.SetupRequestId,
                redirectUrl = setup.RedirectUrl
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = "payment_provider_unavailable", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> SyncOrganizationAsync(Guid organizationId, IAlphaDbContext db,
        OrganizationAccessService access, IPaymentProvider provider, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return await SyncAsync(account, db, provider, ct);
    }

    private static async Task<IResult> SyncEmployerAsync(Guid organizationId, Guid employerId, IAlphaDbContext db,
        OrganizationAccessService access, IPaymentProvider provider, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.EmployerId == employerId && x.OrganizationId == null, ct);
        return await SyncAsync(account, db, provider, ct);
    }

    private static async Task<IResult> SyncAsync(BillingAccount? account, IAlphaDbContext db,
        IPaymentProvider provider, CancellationToken ct)
    {
        if (account is null) return Results.Conflict(new { error = "billing_account_required" });
        if (string.IsNullOrWhiteSpace(account.ProviderCustomerId) ||
            string.IsNullOrWhiteSpace(account.ProviderPaymentMethodId))
            return Results.Conflict(new { error = "payment_method_not_configured" });

        try
        {
            var status = await provider.GetPaymentMethodStatus(
                account.ProviderCustomerId, account.ProviderPaymentMethodId, ct);

            account.UpdateProviderMetadata(
                status.Active ? BillingPaymentMethodStatus.Active : BillingPaymentMethodStatus.Failed,
                account.ProviderCustomerId,
                status.PaymentMethodId,
                string.IsNullOrWhiteSpace(status.Brand) ? account.CardBrand : status.Brand,
                string.IsNullOrWhiteSpace(status.Last4) ? account.CardLast4 : status.Last4,
                status.ExpiryMonth ?? account.CardExpiryMonth,
                status.ExpiryYear ?? account.CardExpiryYear,
                string.IsNullOrWhiteSpace(status.BankDebitMandateReference)
                    ? account.BankDebitMandateReference
                    : status.BankDebitMandateReference);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                provider = provider.Name,
                status = account.PaymentMethodStatus,
                account.CardBrand,
                account.CardLast4,
                account.CardExpiryMonth,
                account.CardExpiryYear
            });
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = "payment_provider_unavailable", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> CancelOrganizationAsync(Guid organizationId, IAlphaDbContext db,
        OrganizationAccessService access, IPaymentProvider provider, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return await CancelAsync(account, db, provider, ct);
    }

    private static async Task<IResult> CancelEmployerAsync(Guid organizationId, Guid employerId, IAlphaDbContext db,
        OrganizationAccessService access, IPaymentProvider provider, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.EmployerId == employerId && x.OrganizationId == null, ct);
        return await CancelAsync(account, db, provider, ct);
    }

    private static async Task<IResult> CancelAsync(BillingAccount? account, IAlphaDbContext db,
        IPaymentProvider provider, CancellationToken ct)
    {
        if (account is null) return Results.Conflict(new { error = "billing_account_required" });
        if (string.IsNullOrWhiteSpace(account.ProviderPaymentMethodId))
            return Results.NoContent();

        try
        {
            await provider.CancelPaymentMethod(
                account.ProviderCustomerId, account.ProviderPaymentMethodId, ct);
            account.UpdateProviderMetadata(
                BillingPaymentMethodStatus.Cancelled,
                account.ProviderCustomerId,
                string.Empty,
                string.Empty,
                string.Empty,
                null,
                null,
                string.Empty);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = "payment_provider_unavailable", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ChargeAsync(Guid billingAccountId, BillingChargeRequest request,
        IAlphaDbContext db, ICurrentUser currentUser, IPaymentProvider provider, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        if (request.Amount <= 0) return Results.BadRequest(new { error = "invalid_amount" });

        var account = await db.BillingAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == billingAccountId, ct);
        if (account is null) return Results.NotFound();
        if (account.PaymentMethodStatus != BillingPaymentMethodStatus.Active ||
            string.IsNullOrWhiteSpace(account.ProviderCustomerId) ||
            string.IsNullOrWhiteSpace(account.ProviderPaymentMethodId))
            return Results.Conflict(new { error = "payment_method_not_active" });

        try
        {
            var charge = await provider.Charge(new PaymentChargeRequest(
                account.ProviderCustomerId,
                account.ProviderPaymentMethodId,
                request.Amount,
                "ILS",
                request.Description?.Trim() ?? "Alpha subscription charge",
                $"alpha:{account.Id}:{Guid.NewGuid():N}",
                request.CreateInvoice,
                account.CardExpiryMonth,
                account.CardExpiryYear), ct);

            return charge.Success
                ? Results.Ok(charge)
                : Results.Json(charge, statusCode: StatusCodes.Status402PaymentRequired);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Json(new { error = "payment_provider_unavailable", detail = ex.Message },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static Task<IResult> LegacyPayPlusCallbackAsync(
        HttpContext http, IAlphaDbContext db, IPaymentProviderResolver resolver, CancellationToken ct) =>
        ProcessProviderCallbackAsync("PayPlus", http, db, resolver, ct);

    private static Task<IResult> ProviderCallbackAsync(
        string providerName, HttpContext http, IAlphaDbContext db,
        IPaymentProviderResolver resolver, CancellationToken ct) =>
        ProcessProviderCallbackAsync(providerName, http, db, resolver, ct);

    private static async Task<IResult> ProcessProviderCallbackAsync(
        string providerName, HttpContext http, IAlphaDbContext db,
        IPaymentProviderResolver resolver, CancellationToken ct)
    {
        string rawBody;
        if (HttpMethods.IsGet(http.Request.Method))
        {
            rawBody = http.Request.QueryString.HasValue
                ? http.Request.QueryString.Value!.TrimStart('?')
                : string.Empty;
        }
        else
        {
            using var reader = new StreamReader(http.Request.Body);
            rawBody = await reader.ReadToEndAsync(ct);
            if (string.IsNullOrWhiteSpace(rawBody) && http.Request.QueryString.HasValue)
                rawBody = http.Request.QueryString.Value!.TrimStart('?');
        }
        var headers = http.Request.Headers.ToDictionary(
            x => x.Key.ToLowerInvariant(),
            x => x.Value.ToString(),
            StringComparer.OrdinalIgnoreCase);

        IPaymentProvider provider;
        try
        {
            provider = resolver.Resolve(providerName);
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound();
        }

        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        var eventKey = $"{provider.Name}:{payloadHash}";
        if (await db.ProviderWebhookEvents.AsNoTracking()
            .AnyAsync(x => x.Provider == provider.Name && x.EventKey == eventKey, ct))
            return Results.Ok(new { ok = true, duplicate = true });

        var webhook = new ProviderWebhookEvent(provider.Name, eventKey, payloadHash, rawBody);
        db.ProviderWebhookEvents.Add(webhook);
        await db.SaveChangesAsync(ct);

        PaymentMethodStatusResult result;
        try
        {
            result = await provider.ResolvePaymentMethodFromCallback(rawBody, headers, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            webhook.Complete(ProviderWebhookStatus.Failed, ex.Message);
            await db.SaveChangesAsync(ct);
            return Results.Unauthorized();
        }

        if (!Guid.TryParse(result.ExternalReference, out var billingAccountId))
        {
            webhook.Complete(ProviderWebhookStatus.Failed, "billing_account_reference_missing");
            await db.SaveChangesAsync(ct);
            return Results.BadRequest(new { error = "billing_account_reference_missing" });
        }

        var account = await db.BillingAccounts.SingleOrDefaultAsync(x => x.Id == billingAccountId, ct);
        if (account is null)
        {
            webhook.Complete(ProviderWebhookStatus.Failed, "billing_account_not_found");
            await db.SaveChangesAsync(ct);
            return Results.NotFound();
        }

        var customerId = !string.IsNullOrWhiteSpace(result.CustomerId)
            ? result.CustomerId
            : account.ProviderCustomerId;

        account.UpdateProviderMetadata(
            BillingPaymentMethodStatus.Active,
            customerId,
            result.PaymentMethodId,
            result.Brand,
            result.Last4,
            result.ExpiryMonth,
            result.ExpiryYear,
            result.BankDebitMandateReference);

        var method = await db.PaymentMethods.SingleOrDefaultAsync(x =>
            x.BillingAccountId == account.Id &&
            x.Provider == provider.Name &&
            x.ProviderPaymentMethodId == result.PaymentMethodId, ct);

        if (method is null)
        {
            method = new PaymentMethod(account.Id, provider.Name, account.PaymentMethodType);
            db.PaymentMethods.Add(method);
        }

        method.Activate(customerId, result.PaymentMethodId, result.Brand, result.Last4,
            result.ExpiryMonth, result.ExpiryYear, result.BankDebitMandateReference);
        account.SetDefaultPaymentMethod(method.Id);
        webhook.Complete(ProviderWebhookStatus.Processed);

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { ok = true });
    }
}