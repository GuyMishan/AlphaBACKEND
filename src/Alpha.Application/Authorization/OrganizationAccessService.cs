using Alpha.Application.Abstractions;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Authorization;

public sealed class OrganizationAccessService(IAlphaDbContext db, ICurrentUser currentUser)
{
    private Task<bool>? referentEnabled;
    private Task<bool> IsReferentAsync(CancellationToken ct) =>
        referentEnabled ??= db.Users.AsNoTracking()
            .AnyAsync(x => x.Id == currentUser.UserId && x.IsActive && x.IsReferent, ct);

    public async Task<bool> HasReferentOrganizationAssignmentAsync(Guid organizationId, CancellationToken ct) =>
        currentUser.IsAuthenticated && await IsReferentAsync(ct) &&
        await db.ReferentOrganizationAssignments.AsNoTracking()
            .AnyAsync(x => x.UserId == currentUser.UserId && x.OrganizationId == organizationId, ct);

    public async Task<bool> HasReferentEmployerAssignmentAsync(Guid employerId, CancellationToken ct) =>
        currentUser.IsAuthenticated && await IsReferentAsync(ct) &&
        await db.ReferentEmployerAssignments.AsNoTracking()
            .AnyAsync(x => x.UserId == currentUser.UserId && x.EmployerId == employerId, ct);

    public async Task<bool> HasFullOrganizationEmployerScopeAsync(Guid organizationId, CancellationToken ct) =>
        currentUser.IsPlatformAdmin ||
        (await GetMembershipAsync(organizationId, ct))?.EmployerAccessMode == EmployerAccessMode.AllEmployers ||
        await HasReferentOrganizationAssignmentAsync(organizationId, ct);

    public async Task<bool> CanManageEmployerBillingAsync(Guid organizationId, Guid employerId, CancellationToken ct)
    {
        // Internal staff may operate pension reporting but must never gain
        // access to customer subscription/payment-provider administration.
        if (await IsReferentAsync(ct)) return false;
        return await CanManageEmployerAsync(organizationId, employerId, ct);
    }

    public async Task<bool> CanEditOrganizationGeneralAsync(Guid organizationId, CancellationToken ct) =>
        await CanManageOrganizationAsync(organizationId, ct) ||
        await HasReferentOrganizationAssignmentAsync(organizationId, ct);

    public async Task<OrganizationMembership?> GetMembershipAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated) return null;
        return await db.OrganizationMemberships.AsNoTracking().SingleOrDefaultAsync(x =>
            x.UserId == currentUser.UserId && x.OrganizationId == organizationId && x.IsActive &&
            (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task<EmployerUserAccess?> GetEmployerAccessAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated) return null;
        return await db.EmployerUserAccesses.AsNoTracking().SingleOrDefaultAsync(x =>
            x.UserId == currentUser.UserId && x.OrganizationId == organizationId && x.EmployerId == employerId,
            cancellationToken);
    }

    public async Task<bool> CanAccessOrganizationScopeAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (currentUser.IsPlatformAdmin) return true;
        if (await GetMembershipAsync(organizationId, cancellationToken) is not null) return true;
        if (await db.EmployerUserAccesses.AsNoTracking().AnyAsync(x =>
            x.UserId == currentUser.UserId && x.OrganizationId == organizationId, cancellationToken)) return true;
        if (!await IsReferentAsync(cancellationToken)) return false;
        if (await db.ReferentOrganizationAssignments.AsNoTracking().AnyAsync(x =>
            x.UserId == currentUser.UserId && x.OrganizationId == organizationId, cancellationToken)) return true;
        return await (from assignment in db.ReferentEmployerAssignments.AsNoTracking()
            join employer in db.Employers.AsNoTracking() on assignment.EmployerId equals employer.Id
            where assignment.UserId == currentUser.UserId && employer.OrganizationId == organizationId
            select assignment.Id).AnyAsync(cancellationToken);
    }

    public async Task<bool> CanViewOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
        currentUser.IsPlatformAdmin || await GetMembershipAsync(organizationId, cancellationToken) is not null ||
        await HasReferentOrganizationAssignmentAsync(organizationId, cancellationToken);

    public async Task<bool> CanManageOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (currentUser.IsPlatformAdmin) return true;
        return (await GetMembershipAsync(organizationId, cancellationToken))?.Role == OrganizationRole.Admin;
    }

    public async Task<bool> CanAccessEmployerAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken)
    {
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, cancellationToken)) return false;
        if (currentUser.IsPlatformAdmin) return true;
        if (await HasReferentOrganizationAssignmentAsync(organizationId, cancellationToken) ||
            await HasReferentEmployerAssignmentAsync(employerId, cancellationToken)) return true;

        var directAccess = await GetEmployerAccessAsync(organizationId, employerId, cancellationToken);
        if (directAccess is not null) return true;

        var membership = await GetMembershipAsync(organizationId, cancellationToken);
        return membership?.EmployerAccessMode == EmployerAccessMode.AllEmployers;
    }

    public async Task<bool> CanCreateEmployerAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (currentUser.IsPlatformAdmin ||
            await HasReferentOrganizationAssignmentAsync(organizationId, cancellationToken)) return true;
        var membership = await GetMembershipAsync(organizationId, cancellationToken);
        return membership?.CanCreateEmployer == true &&
               membership.EmployerAccessMode == EmployerAccessMode.AllEmployers;
    }

    public async Task<bool> CanManageEmployerAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken)
    {
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, cancellationToken)) return false;
        if (currentUser.IsPlatformAdmin) return true;
        if (await HasReferentOrganizationAssignmentAsync(organizationId, cancellationToken) ||
            await HasReferentEmployerAssignmentAsync(employerId, cancellationToken)) return true;

        var membership = await GetMembershipAsync(organizationId, cancellationToken);
        var directRole = (await GetEmployerAccessAsync(organizationId, employerId, cancellationToken))?.Role;

        if (membership is not null)
        {
            if (membership.Role != OrganizationRole.Admin || !membership.CanEditEmployer) return false;
            if (membership.EmployerAccessMode == EmployerAccessMode.AllEmployers) return true;
            return directRole is EmployerRole.Owner or EmployerRole.Admin;
        }

        return directRole is EmployerRole.Owner or EmployerRole.Admin;
    }

    public async Task<bool> CanEditEmployerAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken)
    {
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, cancellationToken)) return false;
        if (currentUser.IsPlatformAdmin) return true;
        if (await HasReferentOrganizationAssignmentAsync(organizationId, cancellationToken) ||
            await HasReferentEmployerAssignmentAsync(employerId, cancellationToken)) return true;

        var membership = await GetMembershipAsync(organizationId, cancellationToken);
        var directRole = (await GetEmployerAccessAsync(organizationId, employerId, cancellationToken))?.Role;

        if (membership is not null)
        {
            if (!membership.CanEditEmployer) return false;
            if (membership.EmployerAccessMode == EmployerAccessMode.AllEmployers) return true;
            return directRole is EmployerRole.Owner or EmployerRole.Admin or EmployerRole.User;
        }

        return directRole is EmployerRole.Owner or EmployerRole.Admin;
    }

    public async Task<bool> CanCreateEmployeeAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken) =>
        await CanOperateEmployerAsync(organizationId, employerId, cancellationToken, m => m.CanCreateEmployee);

    public async Task<bool> CanEditEmployeeAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken) =>
        await CanOperateEmployerAsync(organizationId, employerId, cancellationToken, m => m.CanEditEmployee);

    public async Task<bool> CanCreateReportAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken) =>
        await CanOperateEmployerAsync(organizationId, employerId, cancellationToken, m => m.CanCreateReport);

    public async Task<bool> CanTransmitReportAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken) =>
        await CanOperateEmployerAsync(organizationId, employerId, cancellationToken, m => m.CanTransmitReport);

    private async Task<bool> CanOperateEmployerAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken,
        Func<OrganizationMembership, bool> permission)
    {
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, cancellationToken)) return false;
        if (currentUser.IsPlatformAdmin) return true;
        if (await HasReferentOrganizationAssignmentAsync(organizationId, cancellationToken) ||
            await HasReferentEmployerAssignmentAsync(employerId, cancellationToken)) return true;

        var membership = await GetMembershipAsync(organizationId, cancellationToken);
        var directRole = (await GetEmployerAccessAsync(organizationId, employerId, cancellationToken))?.Role;

        if (membership is not null)
        {
            if (!permission(membership)) return false;
            if (membership.EmployerAccessMode == EmployerAccessMode.AllEmployers) return true;
            return directRole is EmployerRole.Owner or EmployerRole.Admin or EmployerRole.User;
        }

        return directRole is EmployerRole.Owner or EmployerRole.Admin or EmployerRole.User;
    }
}
