using Alpha.Application.Abstractions;
using Alpha.Domain.Billing;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Billing;

public sealed record BillingResolution(
    Guid EmployerId,
    Guid OrganizationId,
    EmployerBillingMode BillingMode,
    string Source,
    string BilledThroughName,
    BillingAccount? Account);

public sealed class BillingInheritanceService(IAlphaDbContext db)
{
    public async Task<BillingResolution?> ResolveBillingAccountAsync(Guid employerId, CancellationToken ct = default)
    {
        var employer = await db.Employers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == employerId, ct);
        if (employer is null) return null;

        var organization = await db.Organizations.AsNoTracking()
            .SingleAsync(x => x.Id == employer.OrganizationId, ct);
        var settings = await db.EmployerProfileSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerId == employerId, ct);

        var mode = settings?.BillingMode ?? await DefaultModeAsync(organization.Id, organization.Type, ct);
        if (mode == EmployerBillingMode.InheritOrganization)
        {
            var account = await db.BillingAccounts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.OrganizationId == organization.Id && x.EmployerId == null, ct);
            return new BillingResolution(employer.Id, organization.Id, mode, "Organization", organization.Name, account);
        }

        var employerAccount = await db.BillingAccounts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerId == employer.Id && x.OrganizationId == null, ct);
        return new BillingResolution(employer.Id, organization.Id, mode, "Employer", employer.LegalName, employerAccount);
    }

    public async Task ApplyDefaultsForNewEmployerAsync(Guid organizationId, Guid employerId, CancellationToken ct = default)
    {
        var organization = await db.Organizations.AsNoTracking().SingleAsync(x => x.Id == organizationId, ct);
        var existingCount = await db.Employers.AsNoTracking().CountAsync(x => x.OrganizationId == organizationId, ct);
        var mode = organization.Type == OrganizationType.SelfService
            ? EmployerBillingMode.EmployerDirect
            : existingCount >= 1
                ? EmployerBillingMode.InheritOrganization
                : EmployerBillingMode.EmployerDirect;

        var newSettings = new EmployerProfileSettings(employerId);
        newSettings.ApplyDefaultBillingMode(mode);
        db.EmployerProfileSettings.Add(newSettings);

        if (organization.Type != OrganizationType.SelfService && existingCount >= 1)
        {
            var existingEmployerIds = await db.Employers.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId)
                .Select(x => x.Id)
                .ToListAsync(ct);
            var existingSettings = await db.EmployerProfileSettings
                .Where(x => existingEmployerIds.Contains(x.EmployerId) && !x.BillingModeOverridden)
                .ToListAsync(ct);
            foreach (var settings in existingSettings)
                settings.ApplyDefaultBillingMode(EmployerBillingMode.InheritOrganization);
        }
    }

    public async Task NormalizeDefaultsAsync(Guid organizationId, CancellationToken ct = default)
    {
        var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == organizationId, ct);
        if (organization is null) return;

        var employerIds = await db.Employers.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId)
            .Select(x => x.Id)
            .ToListAsync(ct);
        var mode = organization.Type == OrganizationType.SelfService || employerIds.Count <= 1
            ? EmployerBillingMode.EmployerDirect
            : EmployerBillingMode.InheritOrganization;

        var settings = await db.EmployerProfileSettings
            .Where(x => employerIds.Contains(x.EmployerId) && !x.BillingModeOverridden)
            .ToListAsync(ct);
        foreach (var item in settings)
            item.ApplyDefaultBillingMode(mode);
    }

    private async Task<EmployerBillingMode> DefaultModeAsync(Guid organizationId, OrganizationType type, CancellationToken ct)
    {
        if (type == OrganizationType.SelfService) return EmployerBillingMode.EmployerDirect;
        var count = await db.Employers.AsNoTracking().CountAsync(x => x.OrganizationId == organizationId, ct);
        return count > 1 ? EmployerBillingMode.InheritOrganization : EmployerBillingMode.EmployerDirect;
    }
}
