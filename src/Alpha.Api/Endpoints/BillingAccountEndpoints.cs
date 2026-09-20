using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Auditing;
using Alpha.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record BillingAccountDetailsRequest(
    string? BillingName,
    string? TaxId,
    string? InvoiceEmail,
    string? BillingAddress,
    BillingPaymentMethodType PaymentMethodType);

public sealed record BillingProviderMetadataRequest(
    BillingPaymentMethodStatus PaymentMethodStatus,
    string? ProviderCustomerId,
    string? ProviderPaymentMethodId,
    string? CardBrand,
    string? CardLast4,
    int? CardExpiryMonth,
    int? CardExpiryYear,
    string? BankDebitMandateReference);

public static class BillingAccountEndpoints
{
    public static IEndpointRouteBuilder MapBillingAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var org = endpoints.MapGroup("/api/organizations/{organizationId:guid}/billing-account")
            .RequireAuthorization().WithTags("Alpha Billing");
        org.MapGet("/", GetOrganizationBillingAsync);
        org.MapPut("/", UpsertOrganizationBillingAsync);
        org.MapPut("/provider-metadata", UpdateOrganizationProviderMetadataAsync);

        var employer = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/billing-account")
            .RequireAuthorization().WithTags("Alpha Billing");
        employer.MapGet("/", GetEmployerBillingAsync);
        employer.MapPut("/", UpsertEmployerBillingAsync);
        employer.MapPut("/provider-metadata", UpdateEmployerProviderMetadataAsync);

        return endpoints;
    }

    private static async Task<IResult> GetOrganizationBillingAsync(Guid organizationId, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();
        var exists = await db.Organizations.AsNoTracking().AnyAsync(x => x.Id == organizationId, ct);
        if (!exists) return Results.NotFound();

        var account = await db.BillingAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == null, ct);
        return Results.Ok(ToResponse(account, organizationId, null));
    }

    private static async Task<IResult> UpsertOrganizationBillingAsync(Guid organizationId, BillingAccountDetailsRequest request,
        IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        if (!Enum.IsDefined(request.PaymentMethodType))
            return Results.BadRequest(new { error = "invalid_payment_method_type" });
        if (!await db.Organizations.AnyAsync(x => x.Id == organizationId, ct)) return Results.NotFound();

        var account = await db.BillingAccounts
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == null, ct);
        if (account is null)
        {
            account = new BillingAccount(organizationId, null);
            db.BillingAccounts.Add(account);
        }

        account.UpdateBillingDetails(request.BillingName, request.TaxId, request.InvoiceEmail,
            request.BillingAddress, request.PaymentMethodType);
        AddAudit(db, currentUser, http, "billing.organization.updated", account, organizationId, null);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(account, organizationId, null));
    }

    private static async Task<IResult> GetEmployerBillingAsync(Guid organizationId, Guid employerId, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var exists = await db.Employers.AsNoTracking()
            .AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (!exists) return Results.NotFound();

        var account = await db.BillingAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerId == employerId && x.OrganizationId == null, ct);
        return Results.Ok(ToResponse(account, null, employerId));
    }

    private static async Task<IResult> UpsertEmployerBillingAsync(Guid organizationId, Guid employerId,
        BillingAccountDetailsRequest request, IAlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!Enum.IsDefined(request.PaymentMethodType))
            return Results.BadRequest(new { error = "invalid_payment_method_type" });
        if (!await db.Employers.AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();

        var account = await db.BillingAccounts
            .SingleOrDefaultAsync(x => x.EmployerId == employerId && x.OrganizationId == null, ct);
        if (account is null)
        {
            account = new BillingAccount(null, employerId);
            db.BillingAccounts.Add(account);
        }

        account.UpdateBillingDetails(request.BillingName, request.TaxId, request.InvoiceEmail,
            request.BillingAddress, request.PaymentMethodType);
        AddAudit(db, currentUser, http, "billing.employer.updated", account, organizationId, employerId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(account, null, employerId));
    }

    private static async Task<IResult> UpdateOrganizationProviderMetadataAsync(Guid organizationId,
        BillingProviderMetadataRequest request, IAlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();

        var account = await db.BillingAccounts
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId && x.EmployerId == null, ct);
        if (account is null) return Results.NotFound();

        var error = ApplyProviderMetadata(account, request);
        if (error is not null) return Results.BadRequest(new { error });
        AddAudit(db, currentUser, http, "billing.organization.provider-metadata.updated", account, organizationId, null);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(account, organizationId, null));
    }

    private static async Task<IResult> UpdateEmployerProviderMetadataAsync(Guid organizationId, Guid employerId,
        BillingProviderMetadataRequest request, IAlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!currentUser.IsPlatformAdmin) return Results.Forbid();
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();

        var account = await db.BillingAccounts
            .SingleOrDefaultAsync(x => x.EmployerId == employerId && x.OrganizationId == null, ct);
        if (account is null) return Results.NotFound();

        var error = ApplyProviderMetadata(account, request);
        if (error is not null) return Results.BadRequest(new { error });
        AddAudit(db, currentUser, http, "billing.employer.provider-metadata.updated", account, organizationId, employerId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(account, null, employerId));
    }

    private static string? ApplyProviderMetadata(BillingAccount account, BillingProviderMetadataRequest request)
    {
        if (!Enum.IsDefined(request.PaymentMethodStatus)) return "invalid_payment_method_status";

        if (account.PaymentMethodType == BillingPaymentMethodType.CreditCard &&
            !string.IsNullOrWhiteSpace(request.BankDebitMandateReference))
            return "bank_debit_reference_not_allowed_for_card";

        if (account.PaymentMethodType == BillingPaymentMethodType.BankDebit &&
            (!string.IsNullOrWhiteSpace(request.CardBrand) || !string.IsNullOrWhiteSpace(request.CardLast4) ||
             request.CardExpiryMonth.HasValue || request.CardExpiryYear.HasValue))
            return "card_metadata_not_allowed_for_bank_debit";

        try
        {
            account.UpdateProviderMetadata(request.PaymentMethodStatus, request.ProviderCustomerId,
                request.ProviderPaymentMethodId, request.CardBrand, request.CardLast4,
                request.CardExpiryMonth, request.CardExpiryYear, request.BankDebitMandateReference);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static object ToResponse(BillingAccount? account, Guid? organizationId, Guid? employerId)
    {
        if (account is null)
        {
            return new
            {
                id = (Guid?)null,
                organizationId,
                employerId,
                billingName = string.Empty,
                taxId = string.Empty,
                invoiceEmail = string.Empty,
                billingAddress = string.Empty,
                paymentMethodType = BillingPaymentMethodType.CreditCard,
                paymentMethodStatus = BillingPaymentMethodStatus.NotConfigured,
                providerCustomerId = string.Empty,
                providerPaymentMethodId = string.Empty,
                cardBrand = string.Empty,
                cardLast4 = string.Empty,
                cardExpiryMonth = (int?)null,
                cardExpiryYear = (int?)null,
                bankDebitMandateReference = string.Empty,
                configured = false
            };
        }

        return new
        {
            account.Id,
            account.OrganizationId,
            account.EmployerId,
            account.BillingName,
            account.TaxId,
            account.InvoiceEmail,
            account.BillingAddress,
            account.PaymentMethodType,
            account.PaymentMethodStatus,
            account.ProviderCustomerId,
            account.ProviderPaymentMethodId,
            account.CardBrand,
            account.CardLast4,
            account.CardExpiryMonth,
            account.CardExpiryYear,
            account.BankDebitMandateReference,
            configured = true,
            account.CreatedAt,
            account.UpdatedAt
        };
    }

    private static void AddAudit(IAlphaDbContext db, ICurrentUser currentUser, HttpContext http,
        string action, BillingAccount account, Guid organizationId, Guid? employerId)
    {
        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, action, nameof(BillingAccount), account.Id,
            organizationId, employerId,
            JsonSerializer.Serialize(new
            {
                account.BillingName,
                account.TaxId,
                account.InvoiceEmail,
                account.PaymentMethodType,
                account.PaymentMethodStatus,
                hasProviderCustomerId = !string.IsNullOrWhiteSpace(account.ProviderCustomerId),
                hasProviderPaymentMethodId = !string.IsNullOrWhiteSpace(account.ProviderPaymentMethodId),
                account.CardBrand,
                account.CardLast4,
                account.CardExpiryMonth,
                account.CardExpiryYear,
                hasBankDebitMandateReference = !string.IsNullOrWhiteSpace(account.BankDebitMandateReference)
            }), http.TraceIdentifier));
    }
}
