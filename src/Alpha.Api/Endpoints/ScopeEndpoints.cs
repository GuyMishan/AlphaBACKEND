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
                membershipOrgIds.Contains(x.Id) || employerOrgIds.Contains(x.Id));
        }

        var organizations = await organizationsQuery
            .OrderBy(x => x.Name)
            .Select(x => new { x.Id, x.Name, x.Type, x.Status })
            .ToListAsync(ct);

        var result = new List<object>(organizations.Count);

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

            var hasOrganizationScope = currentUser.IsPlatformAdmin || membership is not null;
            var canManageOrganization = currentUser.IsPlatformAdmin ||
                                        await access.CanManageOrganizationAsync(organization.Id, ct);

            var employersQuery = db.Employers.AsNoTracking()
                .Where(x => x.OrganizationId == organization.Id);

            if (!currentUser.IsPlatformAdmin &&
                membership?.EmployerAccessMode != EmployerAccessMode.AllEmployers)
            {
                var grantedEmployerIds = db.EmployerUserAccesses.AsNoTracking()
                    .Where(x => x.UserId == currentUser.UserId &&
                                x.OrganizationId == organization.Id)
                    .Select(x => x.EmployerId);
                employersQuery = employersQuery.Where(x => grantedEmployerIds.Contains(x.Id));
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

        var employerCount = 0;
        foreach (var organization in organizations)
        {
            if (currentUser.IsPlatformAdmin)
            {
                employerCount += await db.Employers.AsNoTracking().CountAsync(x => x.OrganizationId == organization.Id, ct);
                continue;
            }

            var membership = await db.OrganizationMemberships.AsNoTracking()
                .SingleOrDefaultAsync(x => x.UserId == currentUser.UserId &&
                    x.OrganizationId == organization.Id && x.IsActive &&
                    (x.ExpiresAt == null || x.ExpiresAt > DateTimeOffset.UtcNow), ct);

            if (membership?.EmployerAccessMode == EmployerAccessMode.AllEmployers)
                employerCount += await db.Employers.AsNoTracking().CountAsync(x => x.OrganizationId == organization.Id, ct);
            else
                employerCount += await db.EmployerUserAccesses.AsNoTracking()
                    .Where(x => x.UserId == currentUser.UserId && x.OrganizationId == organization.Id)
                    .Select(x => x.EmployerId).Distinct().CountAsync(ct);
        }

        return Results.Ok(new { organizations = result, organizationCount = organizations.Count, employerCount });
    }
}
