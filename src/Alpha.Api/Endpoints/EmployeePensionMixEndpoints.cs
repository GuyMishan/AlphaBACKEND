using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Employees;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class EmployeePensionMixEndpoints
{
    private const int MaxProducts = 20;

    public static IEndpointRouteBuilder MapEmployeePensionMixEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/employees/{employmentId:guid}/pension-mix")
            .RequireAuthorization().WithTags("Employee pension mix");

        group.MapGet("/", GetAsync);
        group.MapPut("/", SaveAsync);
        return endpoints;
    }

    private static async Task<IResult> GetAsync(Guid organizationId, Guid employerId, Guid employmentId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.Employments.AsNoTracking().AnyAsync(x => x.Id == employmentId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct))
            return Results.NotFound();

        var products = await db.EmployeePensionProducts.AsNoTracking()
            .Where(x => x.EmploymentId == employmentId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var ids = products.Select(x => x.Id).ToArray();
        var contributions = await db.EmployeePensionContributions.AsNoTracking()
            .Where(x => ids.Contains(x.EmployeePensionProductId)).ToListAsync(ct);

        return Results.Ok(products.Select(p => new
        {
            p.Id,
            p.ProductType,
            p.PolicyNumber,
            p.Salary,
            p.ReportingType,
            p.SalaryLayer,
            p.Section14,
            p.Section14StartDate,
            employerContributions = contributions.Where(c => c.EmployeePensionProductId == p.Id && c.Party == ContributionParty.Employer).OrderBy(c => c.Component),
            employeeContributions = contributions.Where(c => c.EmployeePensionProductId == p.Id && c.Party == ContributionParty.Employee).OrderBy(c => c.Component)
        }));
    }

    private static async Task<IResult> SaveAsync(Guid organizationId, Guid employerId, Guid employmentId,
        SaveEmployeePensionMixRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.Products.Count > MaxProducts) return Results.BadRequest(new { error = $"Employee mix is limited to {MaxProducts} products." });
        if (!await db.Employments.AsNoTracking().AnyAsync(x => x.Id == employmentId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct))
            return Results.NotFound();

        var existingIds = await db.EmployeePensionProducts.Where(x => x.EmploymentId == employmentId).Select(x => x.Id).ToArrayAsync(ct);
        if (existingIds.Length > 0)
        {
            await db.EmployeePensionContributions.Where(x => existingIds.Contains(x.EmployeePensionProductId)).ExecuteDeleteAsync(ct);
            await db.EmployeePensionProducts.Where(x => x.EmploymentId == employmentId).ExecuteDeleteAsync(ct);
        }

        foreach (var input in request.Products)
        {
            var product = new EmployeePensionProduct(employmentId, input.ProductType, input.PolicyNumber,
                input.Salary, input.ReportingType, input.SalaryLayer, input.Section14, input.Section14StartDate);
            db.EmployeePensionProducts.Add(product);
            AddContributions(db, product.Id, ContributionParty.Employer, input.EmployerContributions);
            AddContributions(db, product.Id, ContributionParty.Employee, input.EmployeeContributions);
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void AddContributions(IAlphaDbContext db, Guid productId, ContributionParty party,
        IReadOnlyCollection<EmployeePensionContributionInput> items)
    {
        if (items.GroupBy(x => x.Component).Any(g => g.Count() > 1))
            throw new ArgumentException("Each contribution component may appear only once per party.");
        foreach (var item in items)
            db.EmployeePensionContributions.Add(new EmployeePensionContribution(productId, party, item.Component, item.Percentage));
    }
}

public sealed record SaveEmployeePensionMixRequest(IReadOnlyCollection<EmployeePensionProductInput> Products);
public sealed record EmployeePensionProductInput(PensionProductType ProductType, string PolicyNumber, decimal Salary,
    string ReportingType, string SalaryLayer, bool Section14, DateOnly? Section14StartDate,
    IReadOnlyCollection<EmployeePensionContributionInput> EmployerContributions,
    IReadOnlyCollection<EmployeePensionContributionInput> EmployeeContributions);
public sealed record EmployeePensionContributionInput(ContributionComponent Component, decimal Percentage);
