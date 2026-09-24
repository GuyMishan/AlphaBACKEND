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
public sealed record EmployerPensionPaymentSettingsRequest(EmployerPensionPaymentMode Mode);
public sealed record EmployerReportingSettingsRequest(
    int? DefaultSalaryPaymentDay, int? DefaultPaymentMethodCode, int? DefaultEmployerAccountType,
    int? DefaultReceiverAccountType, string? ReportingNotes, int? DefaultDepositorTypeCode = null,
    int? DefaultEmployerIdentifierTypeCode = null);
public static class EmployerProfileCenterEndpoints
{
    public static IEndpointRouteBuilder MapEmployerProfileCenterEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/profile-center")
            .RequireAuthorization().WithTags("Employer Profile");

        group.MapGet("/settings", GetSettingsAsync);
        group.MapPut("/address", UpdateAddressAsync);
        group.MapPut("/billing", UpdateBillingAsync);
        group.MapPut("/pension-payment", UpdatePensionPaymentAsync);
        group.MapPut("/reporting", UpdateReportingAsync);

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
        var hasDirectPensionAccount = await db.EmployerPaymentAccounts.AsNoTracking()
            .AnyAsync(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.IsActive, ct);
        var organizationType = await db.Organizations.AsNoTracking()
            .Where(x => x.Id == organizationId).Select(x => x.Type).SingleAsync(ct);
        var defaultPensionMode = organizationType == Alpha.Domain.Organizations.OrganizationType.SelfService
            ? EmployerPensionPaymentMode.EmployerDirect
            : EmployerPensionPaymentMode.InheritOrganization;

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
                modeOverridden = settings?.BillingModeOverridden ?? false,
                status = settings?.BillingStatus ?? EmployerBillingStatus.NotConfigured,
                canChangeMode = await access.CanManageEmployerAsync(organizationId, employerId, ct)
            },
            pensionPayment = new
            {
                mode = settings?.PensionPaymentMode ?? (hasDirectPensionAccount ? EmployerPensionPaymentMode.EmployerDirect : defaultPensionMode),
                modeOverridden = settings?.PensionPaymentModeOverridden ?? false,
                canChangeMode = await access.CanManageEmployerAsync(organizationId, employerId, ct)
            },
            reporting = new
            {
                defaultSalaryPaymentDay = settings?.DefaultSalaryPaymentDay,
                defaultDepositorTypeCode = settings?.DefaultDepositorTypeCode ?? 1,
                defaultEmployerIdentifierTypeCode = settings?.DefaultEmployerIdentifierTypeCode ?? 1,
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

    private static async Task<IResult> UpdatePensionPaymentAsync(Guid organizationId, Guid employerId,
        EmployerPensionPaymentSettingsRequest request, IAlphaDbContext db, ICurrentUser currentUser,
        OrganizationAccessService access, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!Enum.IsDefined(request.Mode)) return Results.BadRequest(new { error = "invalid_pension_payment_mode" });

        var settings = await GetOrCreateSettingsAsync(organizationId, employerId, db, ct);
        if (settings is null) return Results.NotFound();

        if (request.Mode == EmployerPensionPaymentMode.EmployerDirect)
        {
            var directAccountExists = await db.EmployerPaymentAccounts.AsNoTracking()
                .AnyAsync(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.IsActive, ct);
            if (!directAccountExists)
                return Results.Conflict(new { error = "employer_payment_account_required" });
        }

        settings.UpdatePensionPaymentMode(request.Mode);
        AddAudit(db, currentUser, http, "employer.pension-payment-mode.updated", settings.Id,
            organizationId, employerId, new { request.Mode });
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { mode = settings.PensionPaymentMode, modeOverridden = settings.PensionPaymentModeOverridden });
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
            settings.UpdateReporting(request.DefaultSalaryPaymentDay,
                request.DefaultDepositorTypeCode ?? settings.DefaultDepositorTypeCode,
                request.DefaultEmployerIdentifierTypeCode ?? settings.DefaultEmployerIdentifierTypeCode,
                request.DefaultPaymentMethodCode, request.DefaultEmployerAccountType,
                request.DefaultReceiverAccountType, request.ReportingNotes);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }

        AddAudit(db, currentUser, http, "employer.reporting-settings.updated", settings.Id, organizationId, employerId, request);
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
        var organization = await db.Organizations.AsNoTracking()
            .SingleAsync(x => x.Id == organizationId, ct);
        var employerCount = await db.Employers.AsNoTracking()
            .CountAsync(x => x.OrganizationId == organizationId, ct);
        var defaultMode = organization.Type == Alpha.Domain.Organizations.OrganizationType.SelfService || employerCount <= 1
            ? EmployerBillingMode.EmployerDirect
            : EmployerBillingMode.InheritOrganization;
        settings.ApplyDefaultBillingMode(defaultMode);
        var hasDirectPensionAccount = await db.EmployerPaymentAccounts.AsNoTracking()
            .AnyAsync(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.IsActive, ct);
        settings.ApplyDefaultPensionPaymentMode(hasDirectPensionAccount || organization.Type == Alpha.Domain.Organizations.OrganizationType.SelfService
            ? EmployerPensionPaymentMode.EmployerDirect
            : EmployerPensionPaymentMode.InheritOrganization);
        db.EmployerProfileSettings.Add(settings);
        return settings;
    }

    private static void AddAudit(IAlphaDbContext db, ICurrentUser currentUser, HttpContext http,
        string action, Guid entityId, Guid organizationId, Guid employerId, object details)
    {
        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, action, "EmployerProfile", entityId,
            organizationId, employerId, JsonSerializer.Serialize(details), http.TraceIdentifier));
    }
}
