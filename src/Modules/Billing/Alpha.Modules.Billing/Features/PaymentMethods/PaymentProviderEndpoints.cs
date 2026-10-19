using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Auditing;
using Alpha.Modules.Billing.Contracts;
using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Domain.Billing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Alpha.Modules.Billing.Features.PaymentMethods;

public sealed record BillingChargeRequest(decimal Amount, string? Description, bool CreateInvoice = true);
public sealed record PaymentMethodSetupApiRequest(string? ReturnPath, string? BillingName = null, string? TaxId = null, string? InvoiceEmail = null, string? BillingAddress = null);
public sealed record PaymentMethodAbandonApiRequest(string SetupReference);

public static class PaymentProviderEndpoints
{
    public static IEndpointRouteBuilder MapPaymentProviderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var org = endpoints.MapGroup("/api/organizations/{organizationId:guid}/billing-account/provider")
            .RequireAuthorization().WithTags("Alpha Billing Provider");
        org.MapPost("/setup", StartOrganizationSetupAsync);
        org.MapPost("/abandon", AbandonOrganizationSetupAsync);
        org.MapPost("/sync", SyncOrganizationAsync);
        org.MapPost("/cancel", CancelOrganizationAsync);

        var employer = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/billing-account/provider")
            .RequireAuthorization().WithTags("Alpha Billing Provider");
        employer.MapPost("/setup", StartEmployerSetupAsync);
        employer.MapPost("/abandon", AbandonEmployerSetupAsync);
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

    private static async Task<IResult> AbandonOrganizationSetupAsync(Guid organizationId,
        PaymentMethodAbandonApiRequest request, AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(
            x => x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return await AbandonSetupAsync(account, request.SetupReference, db, ct);
    }

    private static async Task<IResult> AbandonEmployerSetupAsync(Guid organizationId, Guid employerId,
        PaymentMethodAbandonApiRequest request, AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanManageEmployerBillingAsync(organizationId, employerId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(
            x => x.EmployerId == employerId && x.OrganizationId == null, ct);
        return await AbandonSetupAsync(account, request.SetupReference, db, ct);
    }

    private static async Task<IResult> AbandonSetupAsync(BillingAccount? account, string setupReference,
        AlphaDbContext db, CancellationToken ct)
    {
        if (account is null || !account.AbandonProviderSetup(setupReference))
            return Results.Conflict(new { error = "payment_setup_not_pending" });

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM billing.pending_payment_setup_details
            WHERE billing_account_id = {account.Id} AND setup_reference = {setupReference}
            """, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> StartOrganizationSetupAsync(Guid organizationId, PaymentMethodSetupApiRequest request,
        AlphaDbContext db, ICurrentUser currentUser, IOrganizationAuthorization access,
        IPaymentProviderResolver resolver, IConfiguration config, ILoggerFactory loggerFactory, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.EmployerId == null, ct);
        if (account is null) { account = new BillingAccount(organizationId, null); db.BillingAccounts.Add(account); }
        return await StartSetupAsync(account, organizationId, null, request, db, currentUser, resolver, config, loggerFactory.CreateLogger("Alpha.Billing.PaymentSetup"), http, ct);
    }

    private static async Task<IResult> StartEmployerSetupAsync(Guid organizationId, Guid employerId, PaymentMethodSetupApiRequest request,
        AlphaDbContext db, ICurrentUser currentUser, IOrganizationAuthorization access,
        IPaymentProviderResolver resolver, IConfiguration config, ILoggerFactory loggerFactory, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerBillingAsync(organizationId, employerId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.EmployerId == employerId && x.OrganizationId == null, ct);
        if (account is null) { account = new BillingAccount(null, employerId); db.BillingAccounts.Add(account); }
        return await StartSetupAsync(account, organizationId, employerId, request, db, currentUser, resolver, config, loggerFactory.CreateLogger("Alpha.Billing.PaymentSetup"), http, ct);
    }

    private static async Task<IResult> StartSetupAsync(BillingAccount account, Guid organizationId, Guid? employerId,
        PaymentMethodSetupApiRequest request, AlphaDbContext db, ICurrentUser currentUser, IPaymentProviderResolver resolver,
        IConfiguration config, ILogger logger, HttpContext http, CancellationToken ct)
    {
        if (account.PaymentMethodType != BillingPaymentMethodType.CreditCard)
            return Results.Conflict(new { error = "provider_setup_not_supported_for_bank_debit" });
        var draftProvided = request.BillingName is not null || request.TaxId is not null || request.InvoiceEmail is not null || request.BillingAddress is not null;
        var billingName = draftProvided ? request.BillingName?.Trim() ?? "" : account.BillingName;
        var taxId = draftProvided ? request.TaxId?.Trim() ?? "" : account.TaxId;
        var invoiceEmail = draftProvided ? request.InvoiceEmail?.Trim() ?? "" : account.InvoiceEmail;
        var billingAddress = draftProvided ? request.BillingAddress?.Trim() ?? "" : account.BillingAddress;
        if (string.IsNullOrWhiteSpace(billingName) || billingName.Length > 200 ||
            string.IsNullOrWhiteSpace(taxId) || taxId.Length > 30 ||
            string.IsNullOrWhiteSpace(invoiceEmail) || invoiceEmail.Length > 320 ||
            !System.Net.Mail.MailAddress.TryCreate(invoiceEmail, out _) ||
            string.IsNullOrWhiteSpace(billingAddress) || billingAddress.Length > 500)
            return Results.BadRequest(new { error = "billing_details_incomplete" });
        // The provider calls this URL back, so it comes only from configuration; Host is caller-controlled.
        // Without an explicit setting, Render's own RENDER_EXTERNAL_URL (set by the platform for every web service)
        // is used; an explicit but invalid setting is never replaced. Checked before any provider call or state change.
        var configuredApiBase = config["Payments:PublicApiBaseUrl"];
        if (TrustedApiBaseUrl(string.IsNullOrWhiteSpace(configuredApiBase) ? config["RENDER_EXTERNAL_URL"] : configuredApiBase)
            is not { } callbackBase)
            return Results.Json(new { error = "payment_callback_not_configured" }, statusCode: StatusCodes.Status503ServiceUnavailable);

        var stage = "resolve_provider";
        string? providerName = null;
        try
        {
            var provider = resolver.Resolve();
            providerName = provider.Name;
            var requiredSettings = provider.Name switch
            {
                "CardCom" => new[] { "Payments:CardCom:TerminalNumber", "Payments:CardCom:InterfaceApiName" },
                "PayPlus" => new[] { "Payments:PayPlus:BaseUrl", "Payments:PayPlus:ApiKey", "Payments:PayPlus:SecretKey",
                    "Payments:PayPlus:CashierUid", "Payments:PayPlus:PaymentPageUid" },
                _ => Array.Empty<string>()
            };
            var missing = requiredSettings.Where(key => string.IsNullOrWhiteSpace(config[key])).ToArray();
            if (missing.Length > 0)
            {
                logger.LogError("Payment setup missing configuration: {SettingNames}; provider={Provider}; traceId={TraceId}",
                    string.Join(", ", missing), provider.Name, http.TraceIdentifier);
                return Results.Json(new { error = "payment_provider_not_configured" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            stage = "load_payment_method";
            var currentMethod = account.DefaultPaymentMethodId.HasValue
                ? await db.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(
                    x => x.Id == account.DefaultPaymentMethodId.Value && x.BillingAccountId == account.Id, ct)
                : null;

            var customerId = string.Equals(account.PendingProvider, provider.Name, StringComparison.OrdinalIgnoreCase)
                ? account.PendingProviderCustomerId
                : string.Equals(currentMethod?.Provider, provider.Name, StringComparison.OrdinalIgnoreCase)
                    ? currentMethod?.ProviderCustomerId ?? string.Empty
                    : string.Empty;

            if (string.IsNullOrWhiteSpace(customerId))
            {
                stage = "create_customer";
                var customer = await provider.CreateCustomer(new PaymentProviderCustomerRequest(
                    billingName,
                    invoiceEmail,
                    taxId,
                    billingAddress,
                    account.Id.ToString()), ct);
                customerId = customer.CustomerId;
            }

            stage = "persist_pending_setup";
            var setupReference = $"{account.Id:D}:{Guid.NewGuid():N}";
            // Persist setup only once the external payment page was successfully created.

            var frontend = config["Frontend:BaseUrl"]?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(frontend))
                frontend = "https://alpha-ochre-ten.vercel.app";

            var safeReturnPath = !string.IsNullOrWhiteSpace(request.ReturnPath) && request.ReturnPath.StartsWith('/') && !request.ReturnPath.StartsWith("//")
                ? request.ReturnPath
                : "/settings";
            var returnSeparator = safeReturnPath.Contains('?') ? "&" : "?";

            stage = "create_payment_page";
            var setup = await provider.CreatePaymentMethod(new PaymentMethodSetupRequest(
                customerId,
                $"{frontend}{safeReturnPath}{returnSeparator}payment=success&setup_reference={Uri.EscapeDataString(setupReference)}",
                $"{frontend}{safeReturnPath}{returnSeparator}payment=failed&setup_reference={Uri.EscapeDataString(setupReference)}",
                $"{frontend}{safeReturnPath}{returnSeparator}payment=cancelled&setup_reference={Uri.EscapeDataString(setupReference)}",
                $"{callbackBase}/api/billing/providers/{Uri.EscapeDataString(provider.Name)}/callback",
                setupReference,
                billingName,
                taxId,
                invoiceEmail,
                billingAddress), ct);

            stage = "persist_setup_audit";
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            account.BeginProviderSetup(provider.Name, customerId, setupReference);
            await db.SaveChangesAsync(ct);
            var stagedDetails = JsonSerializer.Serialize(new { billingName, taxId, invoiceEmail, billingAddress });
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO billing.pending_payment_setup_details (billing_account_id, setup_reference, details_json)
                VALUES ({account.Id}, {setupReference}, {stagedDetails}::jsonb)
                ON CONFLICT (billing_account_id) DO UPDATE
                SET setup_reference = EXCLUDED.setup_reference, details_json = EXCLUDED.details_json, created_at = now()
                """, ct);
            db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "billing.payment-method.setup-started",
                nameof(BillingAccount), account.Id, organizationId, employerId,
                JsonSerializer.Serialize(new { provider = provider.Name, setup.SetupRequestId }),
                http.TraceIdentifier));
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return Results.Ok(new
            {
                provider = provider.Name,
                setupRequestId = setup.SetupRequestId,
                redirectUrl = setup.RedirectUrl
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never log ex.Message: upstream errors may contain provider payloads or personal information.
            logger.LogError("Payment setup failed at {Stage}; provider={Provider}; exceptionType={ExceptionType}; traceId={TraceId}",
                stage, providerName ?? "unresolved", ex.GetType().Name, http.TraceIdentifier);
            return Results.Json(new { error = "payment_provider_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> SyncOrganizationAsync(Guid organizationId, AlphaDbContext db,
        IOrganizationAuthorization access, IPaymentProviderResolver resolver, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return await SyncAsync(account, db, resolver, ct);
    }

    private static async Task<IResult> SyncEmployerAsync(Guid organizationId, Guid employerId, AlphaDbContext db,
        IOrganizationAuthorization access, IPaymentProviderResolver resolver, CancellationToken ct)
    {
        if (!await access.CanManageEmployerBillingAsync(organizationId, employerId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.EmployerId == employerId && x.OrganizationId == null, ct);
        return await SyncAsync(account, db, resolver, ct);
    }

    private static async Task<IResult> SyncAsync(BillingAccount? account, AlphaDbContext db,
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Results.Json(new { error = "payment_provider_unavailable" },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> CancelOrganizationAsync(Guid organizationId, AlphaDbContext db,
        IOrganizationAuthorization access, IPaymentProviderResolver resolver, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return await CancelAsync(account, db, resolver, ct);
    }

    private static async Task<IResult> CancelEmployerAsync(Guid organizationId, Guid employerId, AlphaDbContext db,
        IOrganizationAuthorization access, IPaymentProviderResolver resolver, CancellationToken ct)
    {
        if (!await access.CanManageEmployerBillingAsync(organizationId, employerId, ct)) return Results.Forbid();
        var account = await db.BillingAccounts.SingleOrDefaultAsync(x =>
            x.EmployerId == employerId && x.OrganizationId == null, ct);
        return await CancelAsync(account, db, resolver, ct);
    }

    private static async Task<IResult> CancelAsync(BillingAccount? account, AlphaDbContext db,
        IPaymentProviderResolver resolver, CancellationToken ct)
    {
        if (account is null) return Results.Conflict(new { error = "billing_account_required" });

        var method = account.DefaultPaymentMethodId.HasValue
            ? await db.PaymentMethods.SingleOrDefaultAsync(
                x => x.Id == account.DefaultPaymentMethodId.Value &&
                     x.BillingAccountId == account.Id, ct)
            : null;

        string? providerName = null;
        string? customerId = null;
        string? paymentMethodId = null;
        if (method is not null)
        {
            providerName = method.Provider;
            customerId = method.ProviderCustomerId;
            paymentMethodId = method.ProviderPaymentMethodId;
            method.MarkStatus(BillingPaymentMethodStatus.Cancelled);
        }

        // Local cancellation is authoritative for ALPHA charging. Persist it before any
        // provider-side cleanup so an external outage can never leave the account chargeable.
        account.ResetBillingSetup();
        await db.SaveChangesAsync(ct);

        if (!string.IsNullOrWhiteSpace(providerName) &&
            !string.IsNullOrWhiteSpace(paymentMethodId))
        {
            try
            {
                var provider = resolver.Resolve(providerName);
                await provider.CancelPaymentMethod(customerId ?? string.Empty, paymentMethodId, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The token may remain at the provider, but ALPHA has already stopped using it.
                // A later provider-side cleanup can be performed without reopening local billing.
            }
        }

        return Results.NoContent();
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
        HttpContext http, AlphaDbContext db, IPaymentProviderResolver resolver, CancellationToken ct) =>
        ProcessProviderCallbackAsync("PayPlus", http, db, resolver, ct);

    private static Task<IResult> ProviderCallbackAsync(
        string providerName, HttpContext http, AlphaDbContext db,
        IPaymentProviderResolver resolver, CancellationToken ct) =>
        ProcessProviderCallbackAsync(providerName, http, db, resolver, ct);

    private static async Task<IResult> ProcessProviderCallbackAsync(
        string providerName, HttpContext http, AlphaDbContext db,
        IPaymentProviderResolver resolver, CancellationToken ct)
    {
        const int maxWebhookBytes = 64 * 1024;
        if (http.Request.ContentLength is > maxWebhookBytes)
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);

        using var reader = new StreamReader(http.Request.Body);
        var body = new StringBuilder(Math.Min(maxWebhookBytes, 4096));
        var buffer = new char[4096];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (read == 0) break;
            body.Append(buffer, 0, read);
            if (body.Length > maxWebhookBytes)
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }
        var rawBody = body.ToString();
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

        var tokenOwner = await db.PaymentMethods.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Provider == provider.Name &&
            x.ProviderPaymentMethodId == result.PaymentMethodId, ct);
        if (tokenOwner is not null && tokenOwner.BillingAccountId != account.Id)
        {
            webhook.Complete(ProviderWebhookStatus.Failed, "provider_payment_method_already_assigned");
            await db.SaveChangesAsync(ct);
            return Results.Conflict(new { error = "provider_payment_method_already_assigned" });
        }

        var stagedJson = await db.Database.SqlQueryRaw<string>(
            "SELECT details_json::text AS \"Value\" FROM billing.pending_payment_setup_details WHERE billing_account_id = {0} AND setup_reference = {1}",
            account.Id, externalReference).ToListAsync(ct);
        if (stagedJson.Count > 0)
        {
            using var staged = JsonDocument.Parse(stagedJson[0]);
            var d = staged.RootElement;
            account.UpdateBillingDetails(d.GetProperty("billingName").GetString(),
                d.GetProperty("taxId").GetString(), d.GetProperty("invoiceEmail").GetString(),
                d.GetProperty("billingAddress").GetString(), BillingPaymentMethodType.CreditCard);
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

        var method = tokenOwner is not null
            ? await db.PaymentMethods.SingleAsync(x => x.Id == tokenOwner.Id, ct)
            : null;

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

        await using var commit = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM billing.pending_payment_setup_details
            WHERE billing_account_id = {account.Id} AND setup_reference = {externalReference}
            """, ct);
        await commit.CommitAsync(ct);
        return Results.Ok(new { ok = true });
    }

    /// <summary>
    /// The configured public API base URL when it is an absolute https URL without credentials, query or fragment
    /// (http only for a loopback host in local development); otherwise null.
    /// </summary>
    internal static string? TrustedApiBaseUrl(string? configured)
    {
        if (!Uri.TryCreate(configured?.Trim(), UriKind.Absolute, out var uri)) return null;
        var schemeAllowed = uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);
        if (!schemeAllowed || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
