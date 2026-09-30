using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/dashboard/stats", GetStatsAsync)
            .RequireAuthorization()
            .WithTags("Dashboard");
        return endpoints;
    }

    private static async Task<IResult> GetStatsAsync(
        Guid? organizationId,
        Guid? employerId,
        IAlphaDbContext db,
        ICurrentUser currentUser,
        OrganizationAccessService access,
        CancellationToken ct)
    {
        if (employerId.HasValue && !organizationId.HasValue)
            return Results.BadRequest(new { error = "organizationId is required when employerId is supplied." });

        IQueryable<Employer> employers = db.Employers.AsNoTracking();
        IQueryable<Employment> employments = db.Employments.AsNoTracking();
        int organizationCount;

        if (currentUser.IsPlatformAdmin)
        {
            if (organizationId.HasValue)
            {
                organizationCount = await db.Organizations.AsNoTracking()
                    .CountAsync(x => x.Id == organizationId.Value, ct);
                employers = employers.Where(x => x.OrganizationId == organizationId.Value);
                employments = employments.Where(x => x.OrganizationId == organizationId.Value);
            }
            else
            {
                organizationCount = await db.Organizations.AsNoTracking().CountAsync(ct);
            }

            if (employerId.HasValue)
            {
                employers = employers.Where(x => x.Id == employerId.Value);
                employments = employments.Where(x => x.EmployerId == employerId.Value);
            }
        }
        else if (organizationId.HasValue && employerId.HasValue)
        {
            if (!await access.CanAccessEmployerAsync(organizationId.Value, employerId.Value, ct))
                return Results.Forbid();

            organizationCount = 1;
            employers = employers.Where(x =>
                x.OrganizationId == organizationId.Value && x.Id == employerId.Value);
            employments = employments.Where(x =>
                x.OrganizationId == organizationId.Value && x.EmployerId == employerId.Value);
        }
        else if (organizationId.HasValue)
        {
            if (!await access.CanAccessOrganizationScopeAsync(organizationId.Value, ct))
                return Results.Forbid();

            organizationCount = 1;
            employers = employers.Where(x => x.OrganizationId == organizationId.Value);
            employments = employments.Where(x => x.OrganizationId == organizationId.Value);
            if (!await access.HasFullOrganizationEmployerScopeAsync(organizationId.Value, ct))
            {
                var directIds = db.EmployerUserAccesses.AsNoTracking()
                    .Where(x => x.OrganizationId == organizationId.Value &&
                        x.UserId == currentUser.UserId).Select(x => x.EmployerId);
                var referentIds = db.ReferentEmployerAssignments.AsNoTracking()
                    .Where(x => x.UserId == currentUser.UserId &&
                        db.Users.Any(u => u.Id == currentUser.UserId && u.IsReferent && u.IsActive))
                    .Select(x => x.EmployerId);
                employers = employers.Where(x => directIds.Contains(x.Id) || referentIds.Contains(x.Id));
                employments = employments.Where(x => directIds.Contains(x.EmployerId) ||
                    referentIds.Contains(x.EmployerId));
            }
        }
        else
        {
            return Results.BadRequest(new { error = "organizationId is required for non-platform users." });
        }

        var employerCount = await employers.CountAsync(ct);
        var activeEmployerCount = await employers.CountAsync(x => x.Status == EmployerStatus.Active, ct);
        var employeeCount = await employments.CountAsync(ct);
        var activeEmployeeCount = await employments.CountAsync(x => x.Status == EmploymentStatus.Active, ct);

        return Results.Ok(new
        {
            organizations = organizationCount,
            employers = employerCount,
            activeEmployers = activeEmployerCount,
            employees = employeeCount,
            activeEmployees = activeEmployeeCount,
            inactiveEmployees = employeeCount - activeEmployeeCount
        });
    }
}
