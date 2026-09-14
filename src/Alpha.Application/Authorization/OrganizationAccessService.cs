using Alpha.Application.Abstractions;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Application.Authorization;

public sealed class OrganizationAccessService(IAlphaDbContext db, ICurrentUser currentUser)
{
    public async Task<OrganizationMembership?> GetMembershipAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated) return null;
        return await db.OrganizationMemberships.AsNoTracking().SingleOrDefaultAsync(x =>
            x.UserId == currentUser.UserId && x.OrganizationId == organizationId && x.IsActive &&
            (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), cancellationToken);
    }

    public async Task<bool> CanViewOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
        currentUser.IsPlatformAdmin || await GetMembershipAsync(organizationId, cancellationToken) is not null;

    public async Task<bool> CanManageOrganizationAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (currentUser.IsPlatformAdmin) return true;
        return (await GetMembershipAsync(organizationId, cancellationToken))?.Role == OrganizationRole.Admin;
    }

    public async Task<bool> CanAccessEmployerAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken)
    {
        if (currentUser.IsPlatformAdmin) return true;
        var membership = await GetMembershipAsync(organizationId, cancellationToken);
        if (membership is null) return false;
        if (membership.EmployerAccessMode == EmployerAccessMode.AllEmployers) return true;
        return await db.EmployerUserAccesses.AsNoTracking().AnyAsync(x =>
            x.UserId == currentUser.UserId && x.OrganizationId == organizationId && x.EmployerId == employerId,
            cancellationToken);
    }

    public async Task<bool> CanCreateEmployerAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (currentUser.IsPlatformAdmin) return true;
        var membership = await GetMembershipAsync(organizationId, cancellationToken);
        return membership?.Role == OrganizationRole.Admin &&
               membership.EmployerAccessMode == EmployerAccessMode.AllEmployers;
    }

    public async Task<bool> CanEditEmployerAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken)
    {
        if (currentUser.IsPlatformAdmin) return true;
        var membership = await GetMembershipAsync(organizationId, cancellationToken);
        if (membership is null || membership.Role == OrganizationRole.Viewer) return false;
        return await CanAccessEmployerAsync(organizationId, employerId, cancellationToken);
    }

    public async Task<bool> CanCreateEmployeeAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken)
    {
        if (currentUser.IsPlatformAdmin) return true;
        var membership = await GetMembershipAsync(organizationId, cancellationToken);
        if (membership is null || membership.Role == OrganizationRole.Viewer) return false;
        return await CanAccessEmployerAsync(organizationId, employerId, cancellationToken);
    }

    public async Task<bool> CanEditEmployeeAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken)
    {
        if (currentUser.IsPlatformAdmin) return true;
        var membership = await GetMembershipAsync(organizationId, cancellationToken);
        if (membership is null || membership.Role == OrganizationRole.Viewer) return false;
        return await CanAccessEmployerAsync(organizationId, employerId, cancellationToken);
    }

    public Task<bool> CanManageEmployerAsync(Guid organizationId, Guid employerId, CancellationToken cancellationToken) =>
        CanEditEmployerAsync(organizationId, employerId, cancellationToken);
}
