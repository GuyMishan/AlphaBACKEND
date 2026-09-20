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

    private static void AddAudit(IAlphaDbContext db, ICurrentUser currentUser, HttpContext http,
        string action, Guid entityId, Guid organizationId, Guid employerId, object details)
    {
        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, action, "EmployerProfile", entityId,
            organizationId, employerId, JsonSerializer.Serialize(details), http.TraceIdentifier));
    }
}
