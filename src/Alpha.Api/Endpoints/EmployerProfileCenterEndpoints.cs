using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employers;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record EmployerAddressRequest(
    string? City, string? Street, string? HouseNumber, string? Apartment, string? PostalCode, string? PostOfficeBox);
public sealed record EmployerBillingSettingsRequest(EmployerBillingMode BillingMode, EmployerBillingStatus? BillingStatus = null);
public sealed record EmployerReportingSettingsRequest(
    int? DefaultSalaryPaymentDay, int? DefaultPaymentMethodCode, int? DefaultEmployerAccountType,
    int? DefaultReceiverAccountType, string? ReportingNotes);
public sealed record EmployerPensionPaymentAccountRequest(
    string AccountName, int BankCode, int BranchCode, string AccountNumber, string AccountHolderName,
    bool IsDefault, DebitAuthorizationStatus DebitAuthorizationStatus);

public static class EmployerProfileCenterEndpoints
{
    public static IEndpointRouteBuilder MapEmployerProfileCenterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/profile-center")
            .RequireAuthorization().WithTags("Employer Profile");

        group.MapGet("/settings", GetSettingsAsync);
        group.MapPut("/address", UpdateAddressAsync);
        group.MapPut("/billing", UpdateBillingAsync);
        group.MapPut("/reporting", UpdateReportingAsync);

        group.MapGet("/pension-payment-accounts", GetPaymentAccountsAsync);
        group.MapPost("/pension-payment-accounts", CreatePaymentAccountAsync);
        group.MapPut("/pension-payment-accounts/{accountId:guid}", UpdatePaymentAccountAsync);
        group.MapDelete("/pension-payment-accounts/{accountId:guid}", DeletePaymentAccountAsync);

        return endpoints;
    }

    private static async Task<IResult> GetSettingsAsync(Guid organizationId, Guid employerId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();

        var employerExists = await db.Employers.AsNoTracking()
            .AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (!employerExists) return Results.NotFound();

        var settings = await db.EmployerProfileSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerId == employerId, ct);

        return Results.Ok(new
        {
            address = new
            {
                city = settings?.City ?? string.Empty,
                street = settings?.Street ?? string.Empty,
                houseNumber = settings?.HouseNumber ?? string.Empty,
                apartment = settings?.Apartment ?? string.Empty,
                postalCode = settings?.PostalCode ?? string.Empty,
                postOfficeBox = settings?.PostOfficeBox ?? string.Empty
            },
            billing = new
            {
                mode = settings?.BillingMode ?? EmployerBillingMode.EmployerDirect,
                status = settings?.BillingStatus ?? EmployerBillingStatus.NotConfigured
            },
            reporting = new
            {
                defaultSalaryPaymentDay = settings?.DefaultSalaryPaymentDay,
                defaultPaymentMethodCode = settings?.DefaultPaymentMethodCode,
                defaultEmployerAccountType = settings?.DefaultEmployerAccountType,
                defaultReceiverAccountType = settings?.DefaultReceiverAccountType,
                reportingNotes = settings?.ReportingNotes ?? string.Empty
            }
        });
    }

    private static async Task<IResult> UpdateAddressAsync(Guid organizationId, Guid employerId,
        EmployerAddressRequest request, IAlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanEditEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var settings = await GetOrCreateSettingsAsync(organizationId, employerId, db, ct);
        if (settings is null) return Results.NotFound();

        settings.UpdateAddress(request.City, request.Street, request.HouseNumber, request.Apartment,
            request.PostalCode, request.PostOfficeBox);
        AddAudit(db, currentUser, http, "employer.address.updated", settings.Id, organizationId, employerId, request);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateBillingAsync(Guid organizationId, Guid employerId,
        EmployerBillingSettingsRequest request, IAlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!Enum.IsDefined(request.BillingMode)) return Results.BadRequest(new { error = "Invalid billing mode." });
        if (request.BillingStatus.HasValue && !Enum.IsDefined(request.BillingStatus.Value))
            return Results.BadRequest(new { error = "Invalid billing status." });

        var settings = await GetOrCreateSettingsAsync(organizationId, employerId, db, ct);
        if (settings is null) return Results.NotFound();

        settings.UpdateBilling(request.BillingMode, currentUser.IsPlatformAdmin ? request.BillingStatus : null);
        AddAudit(db, currentUser, http, "employer.billing.updated", settings.Id, organizationId, employerId,
            new { request.BillingMode, BillingStatus = currentUser.IsPlatformAdmin ? request.BillingStatus : null });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { settings.BillingMode, settings.BillingStatus });
    }

    private static async Task<IResult> UpdateReportingAsync(Guid organizationId, Guid employerId,
        EmployerReportingSettingsRequest request, IAlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanEditEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var settings = await GetOrCreateSettingsAsync(organizationId, employerId, db, ct);
        if (settings is null) return Results.NotFound();

        try
        {
            settings.UpdateReporting(request.DefaultSalaryPaymentDay, request.DefaultPaymentMethodCode,
                request.DefaultEmployerAccountType, request.DefaultReceiverAccountType, request.ReportingNotes);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        AddAudit(db, currentUser, http, "employer.reporting-settings.updated", settings.Id, organizationId, employerId, request);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPaymentAccountsAsync(Guid organizationId, Guid employerId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        return Results.Ok(await db.EmployerPensionPaymentAccounts.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId)
            .OrderByDescending(x => x.IsDefault).ThenBy(x => x.AccountName)
            .ToListAsync(ct));
    }

    private static async Task<IResult> CreatePaymentAccountAsync(Guid organizationId, Guid employerId,
        EmployerPensionPaymentAccountRequest request, IAlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.Employers.AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();

        EmployerPensionPaymentAccount item;
        try
        {
            item = new EmployerPensionPaymentAccount(organizationId, employerId, request.AccountName,
                request.BankCode, request.BranchCode, request.AccountNumber, request.AccountHolderName);
            item.SetDebitAuthorizationStatus(request.DebitAuthorizationStatus);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (request.IsDefault || !await db.EmployerPensionPaymentAccounts.AnyAsync(x => x.EmployerId == employerId, ct))
        {
            await ClearDefaultAsync(db, employerId, null, ct);
            item.SetDefault(true);
        }

        db.EmployerPensionPaymentAccounts.Add(item);
        AddAudit(db, currentUser, http, "employer.pension-payment-account.created", item.Id, organizationId, employerId,
            new { item.AccountName, item.BankCode, item.BranchCode, item.IsDefault, item.DebitAuthorizationStatus });
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/organizations/{organizationId}/employers/{employerId}/profile-center/pension-payment-accounts/{item.Id}", item);
    }

    private static async Task<IResult> UpdatePaymentAccountAsync(Guid organizationId, Guid employerId, Guid accountId,
        EmployerPensionPaymentAccountRequest request, IAlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var item = await db.EmployerPensionPaymentAccounts.SingleOrDefaultAsync(x =>
            x.Id == accountId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (item is null) return Results.NotFound();

        try
        {
            item.Update(request.AccountName, request.BankCode, request.BranchCode, request.AccountNumber, request.AccountHolderName);
            item.SetDebitAuthorizationStatus(request.DebitAuthorizationStatus);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        if (request.IsDefault)
        {
            await ClearDefaultAsync(db, employerId, item.Id, ct);
            item.SetDefault(true);
        }
        else if (item.IsDefault)
        {
            var hasOther = await db.EmployerPensionPaymentAccounts.AnyAsync(x => x.EmployerId == employerId && x.Id != item.Id, ct);
            if (!hasOther) item.SetDefault(true);
        }

        AddAudit(db, currentUser, http, "employer.pension-payment-account.updated", item.Id, organizationId, employerId,
            new { item.AccountName, item.BankCode, item.BranchCode, request.IsDefault, item.DebitAuthorizationStatus });
        await db.SaveChangesAsync(ct);
        return Results.Ok(item);
    }

    private static async Task<IResult> DeletePaymentAccountAsync(Guid organizationId, Guid employerId, Guid accountId,
        IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var item = await db.EmployerPensionPaymentAccounts.SingleOrDefaultAsync(x =>
            x.Id == accountId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (item is null) return Results.NoContent();

        var wasDefault = item.IsDefault;
        db.EmployerPensionPaymentAccounts.Remove(item);
        AddAudit(db, currentUser, http, "employer.pension-payment-account.deleted", item.Id, organizationId, employerId,
            new { item.AccountName, item.BankCode, item.BranchCode });

        if (wasDefault)
        {
            var replacement = await db.EmployerPensionPaymentAccounts
                .Where(x => x.EmployerId == employerId && x.Id != accountId)
                .OrderBy(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            replacement?.SetDefault(true);
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<EmployerProfileSettings?> GetOrCreateSettingsAsync(Guid organizationId, Guid employerId,
        IAlphaDbContext db, CancellationToken ct)
    {
        if (!await db.Employers.AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return null;

        var settings = await db.EmployerProfileSettings.SingleOrDefaultAsync(x => x.EmployerId == employerId, ct);
        if (settings is not null) return settings;

        settings = new EmployerProfileSettings(employerId);
        db.EmployerProfileSettings.Add(settings);
        return settings;
    }

    private static async Task ClearDefaultAsync(IAlphaDbContext db, Guid employerId, Guid? exceptId, CancellationToken ct)
    {
        var defaults = await db.EmployerPensionPaymentAccounts
            .Where(x => x.EmployerId == employerId && x.IsDefault && (!exceptId.HasValue || x.Id != exceptId.Value))
            .ToListAsync(ct);
        foreach (var item in defaults) item.SetDefault(false);
    }

    private static void AddAudit(IAlphaDbContext db, ICurrentUser currentUser, HttpContext http,
        string action, Guid entityId, Guid organizationId, Guid employerId, object details)
    {
        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, action, "EmployerProfile", entityId,
            organizationId, employerId, JsonSerializer.Serialize(details), http.TraceIdentifier));
    }
}
