using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Billing;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public sealed record OrganizationGeneralProfileRequest(
    string Name, OrganizationType Type, string? RegistrationNumber, string? City, string? Street,
    string? HouseNumber, string? Apartment, string? PostalCode, string? PostOfficeBox,
    string? ContactName, string? ContactEmail, string? ContactPhone);

public sealed record OrganizationBillingProfileRequest(
    string? InvoiceName, string? InvoiceRegistrationNumber, string? InvoiceEmail,
    string? BillingContactName, string? BillingContactPhone, OrganizationBillingStatus? BillingStatus = null);

public static class OrganizationProfileEndpoints
{
    public static IEndpointRouteBuilder MapOrganizationProfileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/profile-center")
            .RequireAuthorization().WithTags("Organization Profile");

        group.MapGet("/", GetProfileAsync);
        group.MapPut("/general", UpdateGeneralAsync);
        group.MapPut("/billing", UpdateBillingAsync);
        group.MapGet("/members", GetMembersAsync);
        group.MapGet("/employer-billing", GetEmployerBillingAsync);

        return endpoints;
    }

    private static async Task<IResult> GetProfileAsync(Guid organizationId, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();

        var organization = await db.Organizations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == organizationId, ct);
        if (organization is null) return Results.NotFound();

        var settings = await db.OrganizationProfileSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct);

        return Results.Ok(new
        {
            organization.Id,
            organization.Name,
            organization.Type,
            organization.Status,
            canManageOrganization = await access.CanManageOrganizationAsync(organizationId, ct),
            general = new
            {
                registrationNumber = settings?.RegistrationNumber ?? string.Empty,
                city = settings?.City ?? string.Empty,
                street = settings?.Street ?? string.Empty,
                houseNumber = settings?.HouseNumber ?? string.Empty,
                apartment = settings?.Apartment ?? string.Empty,
                postalCode = settings?.PostalCode ?? string.Empty,
                postOfficeBox = settings?.PostOfficeBox ?? string.Empty,
                contactName = settings?.ContactName ?? string.Empty,
                contactEmail = settings?.ContactEmail ?? string.Empty,
                contactPhone = settings?.ContactPhone ?? string.Empty
            },
            billing = new
            {
                status = settings?.BillingStatus ?? OrganizationBillingStatus.NotConfigured,
                invoiceName = settings?.InvoiceName ?? string.Empty,
                invoiceRegistrationNumber = settings?.InvoiceRegistrationNumber ?? string.Empty,
                invoiceEmail = settings?.InvoiceEmail ?? string.Empty,
                billingContactName = settings?.BillingContactName ?? string.Empty,
                billingContactPhone = settings?.BillingContactPhone ?? string.Empty
            }
        });
    }

    private static async Task<IResult> UpdateGeneralAsync(Guid organizationId, OrganizationGeneralProfileRequest request,
        IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        BillingInheritanceService billingInheritance, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        if (!Enum.IsDefined(request.Type) || request.Type == OrganizationType.SelfService)
            return Results.BadRequest(new { error = "Invalid organization type." });

        var organization = await db.Organizations.SingleOrDefaultAsync(x => x.Id == organizationId, ct);
        if (organization is null) return Results.NotFound();
        var settings = await GetOrCreateSettingsAsync(organizationId, db, ct);

        organization.Update(request.Name, request.Type);
        settings.UpdateGeneral(request.RegistrationNumber, request.City, request.Street, request.HouseNumber,
            request.Apartment, request.PostalCode, request.PostOfficeBox, request.ContactName,
            request.ContactEmail, request.ContactPhone);

        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "organization.profile.updated", nameof(Organization),
            organization.Id, organizationId, null, JsonSerializer.Serialize(request), http.TraceIdentifier));
        await db.SaveChangesAsync(ct);
        await billingInheritance.NormalizeDefaultsAsync(organizationId, ct);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateBillingAsync(Guid organizationId, OrganizationBillingProfileRequest request,
        IAlphaDbContext db, ICurrentUser currentUser, OrganizationAccessService access,
        HttpContext http, CancellationToken ct)
    {
        if (!await access.CanManageOrganizationAsync(organizationId, ct)) return Results.Forbid();
        if (request.BillingStatus.HasValue && !Enum.IsDefined(request.BillingStatus.Value))
            return Results.BadRequest(new { error = "Invalid billing status." });

        var settings = await GetOrCreateSettingsAsync(organizationId, db, ct);
        settings.UpdateBilling(request.InvoiceName, request.InvoiceRegistrationNumber, request.InvoiceEmail,
            request.BillingContactName, request.BillingContactPhone,
            currentUser.IsPlatformAdmin ? request.BillingStatus : null);

        db.AuditEvents.Add(new AuditEvent(currentUser.UserId, "organization.billing.updated", nameof(OrganizationProfileSettings),
            settings.Id, organizationId, null,
            JsonSerializer.Serialize(new
            {
                request.InvoiceName,
                request.InvoiceRegistrationNumber,
                request.InvoiceEmail,
                request.BillingContactName,
                request.BillingContactPhone,
                BillingStatus = currentUser.IsPlatformAdmin ? request.BillingStatus : null
            }), http.TraceIdentifier));
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMembersAsync(Guid organizationId, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();

        var items = await (
            from membership in db.OrganizationMemberships.AsNoTracking()
            join user in db.Users.AsNoTracking() on membership.UserId equals user.Id
            where membership.OrganizationId == organizationId && membership.IsActive &&
                  (membership.ExpiresAt == null || membership.ExpiresAt > DateTimeOffset.UtcNow)
            orderby user.DisplayName
            select new
            {
                userId = user.Id,
                user.DisplayName,
                user.Email,
                membership.Role,
                membership.EmployerAccessMode
            }).ToListAsync(ct);

        return Results.Ok(items);
    }

    private static async Task<IResult> GetEmployerBillingAsync(Guid organizationId, IAlphaDbContext db,
        OrganizationAccessService access, BillingInheritanceService billingInheritance, CancellationToken ct)
    {
        if (!await access.CanViewOrganizationAsync(organizationId, ct)) return Results.Forbid();

        var employers = await db.Employers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .OrderBy(x => x.LegalName)
            .Select(x => new { x.Id, x.LegalName })
            .ToListAsync(ct);

        var items = new List<object>(employers.Count);
        foreach (var employer in employers)
        {
            var resolution = await billingInheritance.ResolveBillingAccountAsync(employer.Id, ct);
            var settings = await db.EmployerProfileSettings.AsNoTracking()
                .SingleOrDefaultAsync(x => x.EmployerId == employer.Id, ct);
            items.Add(new
            {
                employerId = employer.Id,
                employerName = employer.LegalName,
                billingMode = resolution?.BillingMode ?? EmployerBillingMode.EmployerDirect,
                billingStatus = settings?.BillingStatus ?? EmployerBillingStatus.NotConfigured,
                billingModeOverridden = settings?.BillingModeOverridden ?? false,
                billedThroughName = resolution?.BilledThroughName ?? employer.LegalName,
                billingSource = resolution?.Source ?? "Employer",
                effectiveBillingConfigured = resolution?.Account is not null,
                effectivePaymentMethodStatus = resolution?.Account?.PaymentMethodStatus
            });
        }

        return Results.Ok(items);
    }

    private static async Task<OrganizationProfileSettings> GetOrCreateSettingsAsync(Guid organizationId,
        IAlphaDbContext db, CancellationToken ct)
    {
        var settings = await db.OrganizationProfileSettings.SingleOrDefaultAsync(x => x.OrganizationId == organizationId, ct);
        if (settings is not null) return settings;

        settings = new OrganizationProfileSettings(organizationId);
        db.OrganizationProfileSettings.Add(settings);
        return settings;
    }
}
