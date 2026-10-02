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

        endpoints.MapPost("/api/billing/providers/{providerName}/callback", ProviderCallbackAsync)
            .AllowAnonymous().WithTags("Alpha Billing Provider");
        endpoints.MapPost("/api/billing/payplus/callback", LegacyPayPlusCallbackAsync)
            .AllowAnonymous().WithTags("Alpha Billing Provider");

        return endpoints;
    }

    private static async Task<IResult> StartOrganizationSetupAsync(Guid organizationId, PaymentMethodSetupApiRequest request,
        IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        IPaymentProviderResolver resolver, IConfiguration config, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.EmployerId == null, ct);
        if (account is null) return Results.Conflict(new { error = "billing_account_required" });
        return await StartSetupAsync(account, organizationId, null, request.ReturnPath, db, currentUser, resolver.Resolve(), config, http, ct);
    }

    private static async Task<IResult> StartEmployerSetupAsync(Guid organizationId, Guid employerId, PaymentMethodSetupApiRequest request,
        IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        IPaymentProviderResolver resolver, IConfiguration config, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerBillingAsync(organizationId, employerId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.EmployerId == employerId && x.OrganizationId == null, ct);
        if (account is null) return Results.Conflict(new { error = "billing_account_required" });
        return await StartSetupAsync(account, organizationId, employerId, request.ReturnPath, db, currentUser, resolver.Resolve(), config, http, ct);
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
            var currentMethod = account.DefaultPaymentMethodId.HasValue
                ? await db.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(
                    x => x.Id == account.DefaultPaymentMethodId.Value && x.BillingAccountId == account.Id, ct)
                : null;

            var customerId = string.Equals(account.PendingProvider, provider.Name, StringComparison.OrdinalIgnoreCase)
                ? account.PendingProviderCustomerId
                : string.Equals(currentMethod?.Provider, provider.Name, StringComparison.OrdinalIgnoreCase)
                    ? currentMethod.ProviderCustomerId
                    : string.Empty;

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

            var setupReference = $"{account.Id:D}:{Guid.NewGuid():N}";
            account.BeginProviderSetup(provider.Name, customerId, setupReference);
            await db.SaveChangesAsync(ct);

            var frontend = config["Frontend:BaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(frontend))
                frontend = "https://alpha-ochre-ten.vercel.app";

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
                setupReference,
                account.BillingName,
                account.TaxId,
                account.InvoiceEmail,
                account.BillingAddress), ct);

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
        catch (InvalidOperationException)
        {
            return Results.Json(new { error = "payment_provider_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> SyncOrganizationAsync(Guid organizationId, IAlphaDbContext db,
        OrganizationAccessService access, IPaymentProviderResolver resolver, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return await SyncAsync(account, db, resolver, ct);
    }

    private static async Task<IResult> SyncEmployerAsync(Guid organizationId, Guid employerId, IAlphaDbContext db,
        OrganizationAccessService access, IPaymentProviderResolver resolver, CancellationToken ct)
    {
        if (!await access.CanManageEmployerBillingAsync(organizationId, employerId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.EmployerId == employerId && x.OrganizationId == null, ct);
        return await SyncAsync(account, db, resolver, ct);
    }

    private static async Task<IResult> SyncAsync(BillingAccount? account, IAlphaDbContext db,
        IPaymentProviderResolver resolver, CancellationToken ct)
    {
        if (account is null) return Results.Conflict(new { error = "billing_account_required" });
        if (!string.IsNullOrWhiteSpace(account.PendingSetupReference))
            return Results.Conflict(new { error = "payment_method_setup_pending" });
        if (string.IsNullOrWhiteSpace(account.ProviderCustomerId) ||
            string.IsNullOrWhiteSpace(account.ProviderPaymentMethodId))
            return Results.Conflict(new { error = "payment_method_not_configured" });

        try
        {
            var method = account.DefaultPaymentMethodId.HasValue
                ? await db.PaymentMethods.SingleOrDefaultAsync(
                    x => x.Id == account.DefaultPaymentMethodId.Value &&
                         x.BillingAccountId == account.Id, ct)
                : null;
            if (method is null)
                return Results.Conflict(new { error = "default_payment_method_not_found" });

            var provider = resolver.Resolve(method.Provider);
            var status = await provider.GetPaymentMethodStatus(
                method.ProviderCustomerId, method.ProviderPaymentMethodId, ct);

            method.MarkStatus(status.Active ? BillingPaymentMethodStatus.Active : BillingPaymentMethodStatus.Failed);
            account.UpdateProviderMetadata(
                status.Active ? BillingPaymentMethodStatus.Active : BillingPaymentMethodStatus.Failed,
                method.ProviderCustomerId,
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
        catch (InvalidOperationException)
        {
            return Results.Json(new { error = "payment_provider_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> CancelOrganizationAsync(Guid organizationId, IAlphaDbContext db,
        OrganizationAccessService access, IPaymentProviderResolver resolver, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return await CancelAsync(account, db, resolver, ct);
    }

    private static async Task<IResult> CancelEmployerAsync(Guid organizationId, Guid employerId, IAlphaDbContext db,
        OrganizationAccessService access, IPaymentProviderResolver resolver, CancellationToken ct)
    {
        if (!await access.CanManageEmployerBillingAsync(organizationId, employerId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.EmployerId == employerId && x.OrganizationId == null, ct);
        return await CancelAsync(account, db, resolver, ct);
    }

    private static async Task<IResult> CancelAsync(BillingAccount? account, IAlphaDbContext db,
        IPaymentProviderResolver resolver, CancellationToken ct)
    {
        if (account is null) return Results.Conflict(new { error = "billing_account_required" });

        try
        {
            var method = account.DefaultPaymentMethodId.HasValue
                ? await db.PaymentMethods.SingleOrDefaultAsync(
                    x => x.Id == account.DefaultPaymentMethodId.Value &&
                         x.BillingAccountId == account.Id, ct)
                : null;

            if (method is not null)
            {
                if (!string.IsNullOrWhiteSpace(method.ProviderPaymentMethodId))
                {
                    var provider = resolver.Resolve(method.Provider);
                    await provider.CancelPaymentMethod(
                        method.ProviderCustomerId, method.ProviderPaymentMethodId, ct);
                }

                method.MarkStatus(BillingPaymentMethodStatus.Cancelled);
            }

            account.ResetBillingSetup();
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException)
        {
            return Results.Json(new { error = "payment_provider_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static Task<IResult> ChargeAsync(Guid billingAccountId, BillingChargeRequest request,
        ICurrentUser currentUser)
    {
        if (!currentUser.IsPlatformAdmin) return Task.FromResult<IResult>(Results.Forbid());
        if (request.Amount <= 0)
            return Task.FromResult<IResult>(Results.BadRequest(new { error = "invalid_amount" }));

        // One-off provider charges bypass the billing ledger and cannot be reconciled safely after
        // network ambiguity. All ALPHA subscription charges must go through a billing period so the
        // deterministic payment idempotency key, attempts and reconciliation state are persisted.
        return Task.FromResult<IResult>(Results.Conflict(new
        {
            error = "direct_provider_charge_disabled",
            billingAccountId,
            use = "POST /api/platform/billing/accounts/{billingAccountId}/run"
        }));
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
        const int maxWebhookBytes = 64 * 1024;
        if (http.Request.ContentLength is > maxWebhookBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        using var reader = new StreamReader(http.Request.Body);
        var rawBody = await reader.ReadToEndAsync(ct);
        if (Encoding.UTF8.GetByteCount(rawBody) > maxWebhookBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
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
        var webhook = await db.ProviderWebhookEvents.SingleOrDefaultAsync(
            x => x.Provider == provider.Name && x.EventKey == eventKey, ct);

        if (webhook is not null)
        {
            if (webhook.Status is ProviderWebhookStatus.Processed or ProviderWebhookStatus.Ignored)
                return Results.Ok(new { ok = true, duplicate = true });

            if (webhook.Status == ProviderWebhookStatus.Received &&
                DateTimeOffset.UtcNow - webhook.UpdatedAt < TimeSpan.FromMinutes(2))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            webhook.Retry();
            await db.SaveChangesAsync(ct);
        }
        else
        {
            webhook = new ProviderWebhookEvent(provider.Name, eventKey, payloadHash, string.Empty);
            db.ProviderWebhookEvents.Add(webhook);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Never acknowledge a concurrent duplicate before the winning handler has finished.
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        }

        PaymentMethodStatusResult result;
        try
        {
            result = await provider.ResolvePaymentMethodFromCallback(rawBody, headers, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            webhook.Complete(ProviderWebhookStatus.Failed, "provider_callback_validation_failed");
            await db.SaveChangesAsync(ct);
            return Results.Unauthorized();
        }

        var externalReference = result.ExternalReference?.Trim() ?? string.Empty;
        var accountIdPart = externalReference.Split(':', 2)[0];
        if (!Guid.TryParse(accountIdPart, out var billingAccountId))
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

        if (!result.Active)
        {
            webhook.Complete(ProviderWebhookStatus.Failed, "provider_payment_method_inactive");
            await db.SaveChangesAsync(ct);
            return Results.BadRequest(new { error = "provider_payment_method_inactive" });
        }

        if (!string.IsNullOrWhiteSpace(account.PendingSetupReference))
        {
            if (!string.Equals(account.PendingProvider, provider.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(account.PendingSetupReference, externalReference, StringComparison.Ordinal))
            {
                webhook.Complete(ProviderWebhookStatus.Ignored, "stale_payment_setup_callback");
                await db.SaveChangesAsync(ct);
                return Results.Ok(new { ok = true, stale = true });
            }
        }
        else if (!string.Equals(externalReference, account.Id.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            webhook.Complete(ProviderWebhookStatus.Ignored, "stale_payment_setup_callback");
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { ok = true, stale = true });
        }

        var expectedCustomerId = !string.IsNullOrWhiteSpace(account.PendingProviderCustomerId)
            ? account.PendingProviderCustomerId
            : account.ProviderCustomerId;
        if (!string.IsNullOrWhiteSpace(expectedCustomerId) && !string.IsNullOrWhiteSpace(result.CustomerId)
            && !string.Equals(expectedCustomerId, result.CustomerId, StringComparison.Ordinal))
        {
            webhook.Complete(ProviderWebhookStatus.Failed, "provider_customer_mismatch");
            await db.SaveChangesAsync(ct);
            return Results.Unauthorized();
        }

        var customerId = !string.IsNullOrWhiteSpace(result.CustomerId)
            ? result.CustomerId
            : expectedCustomerId;
        if (string.IsNullOrWhiteSpace(customerId) || string.IsNullOrWhiteSpace(result.PaymentMethodId))
        {
            webhook.Complete(ProviderWebhookStatus.Failed, "provider_payment_method_incomplete");
            await db.SaveChangesAsync(ct);
            return Results.BadRequest(new { error = "provider_payment_method_incomplete" });
        }

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

        var previouslyActive = await db.PaymentMethods
            .Where(x => x.BillingAccountId == account.Id &&
                        x.Id != method.Id &&
                        x.Status == BillingPaymentMethodStatus.Active)
            .ToListAsync(ct);
        foreach (var oldMethod in previouslyActive)
            oldMethod.MarkStatus(BillingPaymentMethodStatus.Cancelled);

        method.Activate(customerId, result.PaymentMethodId, result.Brand, result.Last4,
            result.ExpiryMonth, result.ExpiryYear, result.BankDebitMandateReference);
        account.SetDefaultPaymentMethod(method.Id);
        account.CompleteProviderSetup();
        webhook.Complete(ProviderWebhookStatus.Processed);

        await db.SaveChangesAsync(ct);
        return Results.Ok(new { ok = true });
    }
}