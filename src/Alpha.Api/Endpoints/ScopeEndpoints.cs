using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class ScopeEndpoints
{
    public static IEndpointRouteBuilder MapScopeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/scope", GetScopeAsync)
            .RequireAuthorization()
            .WithTags("Scope");
        return endpoints;
    }

    private static async Task<IResult> GetScopeAsync(
        IAlphaDbContext db,
        ICurrentUser currentUser,
        OrganizationAccessService access,
        CancellationToken ct)
    {
        if (!currentUser.IsAuthenticated) return Results.Unauthorized();

        var isReferent = await db.Users.AsNoTracking().AnyAsync(x =>
            x.Id == currentUser.UserId && x.IsActive && x.IsReferent, ct);
        var referentOrgIds = db.ReferentOrganizationAssignments.AsNoTracking()
            .Where(x => x.UserId == currentUser.UserId).Select(x => x.OrganizationId);
        var referentEmployerIds = db.ReferentEmployerAssignments.AsNoTracking()
            .Where(x => x.UserId == currentUser.UserId).Select(x => x.EmployerId);
        var referentEmployerOrgIds = db.Employers.AsNoTracking()
            .Where(x => referentEmployerIds.Contains(x.Id)).Select(x => x.OrganizationId);
        var organizationsQuery = db.Organizations.AsNoTracking();

        if (!currentUser.IsPlatformAdmin)
        {
            var membershipOrgIds = db.OrganizationMemberships.AsNoTracking()
                .Where(x => x.UserId == currentUser.UserId && x.IsActive &&
                            (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow))
                .Select(x => x.OrganizationId);

            var employerOrgIds = db.EmployerUserAccesses.AsNoTracking()
                .Where(x => x.UserId == currentUser.UserId)
                .Select(x => x.OrganizationId);

            organizationsQuery = organizationsQuery.Where(x =>
                membershipOrgIds.Contains(x.Id) || employerOrgIds.Contains(x.Id) ||
                (isReferent && (referentOrgIds.Contains(x.Id) || referentEmployerOrgIds.Contains(x.Id))));
        }

        var organizations = await organizationsQuery
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.Type, x.Status })
            .ToListAsync(ct);

        var result = new List<object>(organizations.Count);
        var employerCount = 0;

        foreach (var organization in organizations)
        {
            OrganizationMembership? membership = null;
            if (!currentUser.IsPlatformAdmin)
            {
                membership = await db.OrganizationMemberships.AsNoTracking()
                    .SingleOrDefaultAsync(x =>
                        x.UserId == currentUser.UserId &&
                        x.OrganizationId == organization.Id &&
                        x.IsActive &&
                        (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), ct);
            }

            var hasReferentOrganizationScope = isReferent && await db.ReferentOrganizationAssignments
                .AsNoTracking().AnyAsync(x => x.UserId == currentUser.UserId &&
                    x.OrganizationId == organization.Id, ct);
            var hasOrganizationScope = currentUser.IsPlatformAdmin || membership is not null ||
                hasReferentOrganizationScope;
            var canManageOrganization = currentUser.IsPlatformAdmin ||
                                        await access.CanManageOrganizationAsync(organization.Id, ct);

            var employersQuery = db.Employers.AsNoTracking()
                .Where(x => x.OrganizationId == organization.Id);

            if (!currentUser.IsPlatformAdmin &&
                membership?.EmployerAccessMode != EmployerAccessMode.AllEmployers && !hasReferentOrganizationScope)
            {
                var grantedEmployerIds = db.EmployerUserAccesses.AsNoTracking()
                    .Where(x => x.UserId == currentUser.UserId &&
                                x.OrganizationId == organization.Id)
                    .Select(x => x.EmployerId);
                employersQuery = employersQuery.Where(x => grantedEmployerIds.Contains(x.Id) ||
                    (isReferent && referentEmployerIds.Contains(x.Id)));
            }

            var employers = await employersQuery
                .OrderBy(x => x.LegalName)
                .Select(x => new
                {
                    x.Id,
                    x.OrganizationId,
                    x.LegalName,
                    x.RegistrationNumber,
                    x.WithholdingFileNumber,
                    x.Status
                })
                .ToListAsync(ct);

            employerCount += employers.Count;
            result.Add(new
            {
                organization.Id,
                organization.Name,
                organization.Type,
                organization.Status,
                hasOrganizationScope,
                canManageOrganization,
                employers
            });
        }

        return Results.Ok(new { organizations = result, organizationCount = organizations.Count, employerCount });
    }
}
