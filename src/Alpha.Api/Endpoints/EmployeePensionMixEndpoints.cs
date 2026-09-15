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
            .Where(x => x.EmploymentId == employmentId).OrderBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);
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
            p.IsActive,
            p.EffectiveFrom,
            p.EffectiveTo,
            p.InstitutionalBody,
            p.Manufacturer,
            p.SalaryAllocationType,
            p.SalaryAllocationValue,
            p.AllocationOrder,
            missingDetails = GetMissingDetails(p, contributions.Where(c => c.EmployeePensionProductId == p.Id)),
            isComplete = GetMissingDetails(p, contributions.Where(c => c.EmployeePensionProductId == p.Id)).Count == 0,
            employerContributions = contributions.Where(c => c.EmployeePensionProductId == p.Id && c.Party == ContributionParty.Employer).OrderBy(c => c.Component),
            employeeContributions = contributions.Where(c => c.EmployeePensionProductId == p.Id && c.Party == ContributionParty.Employee).OrderBy(c => c.Component)
        }));
    }

    private static async Task<IResult> SaveAsync(Guid organizationId, Guid employerId, Guid employmentId,
        SaveEmployeePensionMixRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.Products.Count > MaxProducts) return Results.BadRequest(new { error = $"Employee mix is limited to {MaxProducts} products." });

        var employment = await db.Employments.SingleOrDefaultAsync(x =>
            x.Id == employmentId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (employment is null) return Results.NotFound();

        var monthlySalary = request.MonthlySalary ?? employment.MonthlySalary;
        if (monthlySalary < 0) return Results.BadRequest(new { error = "Employee monthly salary cannot be negative." });

        var existingProducts = await db.EmployeePensionProducts.AsNoTracking()
            .Where(x => x.EmploymentId == employmentId).ToListAsync(ct);
        var resolved = new List<ResolvedProductInput>(request.Products.Count);

        foreach (var input in request.Products)
        {
            var existing = existingProducts.FirstOrDefault(x =>
                x.ProductType == input.ProductType &&
                string.Equals(x.PolicyNumber.Trim(), input.PolicyNumber.Trim(), StringComparison.OrdinalIgnoreCase));
            var lifecycleProvided = input.IsActive.HasValue || input.EffectiveFrom.HasValue || input.InstitutionalBody is not null || input.Manufacturer is not null;

            var isActive = lifecycleProvided ? input.IsActive ?? true : existing?.IsActive ?? true;
            var effectiveFrom = lifecycleProvided ? input.EffectiveFrom ?? DateOnly.FromDateTime(DateTime.UtcNow) : existing?.EffectiveFrom ?? DateOnly.FromDateTime(DateTime.UtcNow);
            var effectiveTo = lifecycleProvided ? input.EffectiveTo : existing?.EffectiveTo;
            var institutionalBody = lifecycleProvided ? input.InstitutionalBody ?? string.Empty : existing?.InstitutionalBody ?? string.Empty;
            var manufacturer = lifecycleProvided ? input.Manufacturer ?? string.Empty : existing?.Manufacturer ?? string.Empty;
            var allocationType = input.SalaryAllocationType ?? existing?.SalaryAllocationType ?? SalaryAllocationType.Fixed;
            var allocationValue = input.SalaryAllocationType.HasValue || input.SalaryAllocationValue.HasValue
                ? input.SalaryAllocationValue
                : existing?.SalaryAllocationValue ?? (input.Salary > 0 ? input.Salary : null);
            var allocationOrder = input.AllocationOrder ?? existing?.AllocationOrder ?? resolved.Count;

            if (effectiveTo is not null && effectiveTo.Value < effectiveFrom)
                return Results.BadRequest(new { error = "Product effective end date cannot be earlier than the start date." });
            if (isActive && string.IsNullOrWhiteSpace(input.PolicyNumber))
                return Results.BadRequest(new { error = "Active pension products must have a policy number." });
            if (allocationType != SalaryAllocationType.Remainder && (!allocationValue.HasValue || allocationValue.Value <= 0))
                return Results.BadRequest(new { error = "Fixed, percentage and cap salary allocations require a positive value." });
            if (allocationType == SalaryAllocationType.Percentage && allocationValue > 100)
                return Results.BadRequest(new { error = "Salary allocation percentage cannot exceed 100%." });
            if (allocationOrder < 0)
                return Results.BadRequest(new { error = "Salary allocation order cannot be negative." });

            resolved.Add(new ResolvedProductInput(input, isActive, effectiveFrom, effectiveTo, institutionalBody,
                manufacturer, allocationType, allocationValue, allocationOrder));
        }

        var active = resolved.Where(x => x.IsActive).ToList();
        if (active.Count > 0 && monthlySalary <= 0)
            return Results.BadRequest(new { error = "Employee monthly salary must be greater than zero when active pension products are configured." });
        if (active.Count(x => x.SalaryAllocationType == SalaryAllocationType.Remainder) > 1)
            return Results.BadRequest(new { error = "Only one active product may use remainder salary allocation." });
        if (active.GroupBy(x => x.AllocationOrder).Any(g => g.Count() > 1))
            return Results.BadRequest(new { error = "Active pension products must have a unique salary allocation order." });

        var allocationError = ValidateSalaryAllocations(monthlySalary, active);
        if (allocationError is not null) return Results.BadRequest(new { error = allocationError });

        employment.UpdateMonthlySalary(monthlySalary);
        var existingIds = existingProducts.Select(x => x.Id).ToArray();
        if (existingIds.Length > 0)
        {
            await db.EmployeePensionContributions.Where(x => existingIds.Contains(x.EmployeePensionProductId)).ExecuteDeleteAsync(ct);
            await db.EmployeePensionProducts.Where(x => x.EmploymentId == employmentId).ExecuteDeleteAsync(ct);
        }

        foreach (var item in resolved)
        {
            var input = item.Input;
            var legacySalary = item.SalaryAllocationType == SalaryAllocationType.Fixed
                ? item.SalaryAllocationValue ?? input.Salary
                : input.Salary;
            var product = new EmployeePensionProduct(employmentId, input.ProductType, input.PolicyNumber,
                Math.Max(legacySalary, 0), input.ReportingType, input.SalaryLayer, input.Section14, input.Section14StartDate,
                item.IsActive, item.EffectiveFrom, item.EffectiveTo, item.InstitutionalBody, item.Manufacturer,
                item.SalaryAllocationType, item.SalaryAllocationValue, item.AllocationOrder);
            db.EmployeePensionProducts.Add(product);
            AddContributions(db, product.Id, ContributionParty.Employer, input.EmployerContributions);
            AddContributions(db, product.Id, ContributionParty.Employee, input.EmployeeContributions);
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static string? ValidateSalaryAllocations(decimal monthlySalary, IReadOnlyCollection<ResolvedProductInput> products)
    {
        decimal allocated = 0;
        foreach (var item in products.OrderBy(x => x.AllocationOrder))
        {
            var insuredSalary = item.SalaryAllocationType switch
            {
                SalaryAllocationType.Fixed => item.SalaryAllocationValue!.Value,
                SalaryAllocationType.Percentage => Math.Round(monthlySalary * item.SalaryAllocationValue!.Value / 100m, 2, MidpointRounding.AwayFromZero),
                SalaryAllocationType.Cap => Math.Min(monthlySalary, item.SalaryAllocationValue!.Value),
                SalaryAllocationType.Remainder => Math.Max(monthlySalary - allocated, 0),
                _ => 0
            };

            if (allocated + insuredSalary > monthlySalary + 0.01m)
                return "Salary allocations exceed the employee monthly salary.";
            allocated += insuredSalary;
        }
        return null;
    }

    private static IReadOnlyCollection<string> GetMissingDetails(EmployeePensionProduct product,
        IEnumerable<EmployeePensionContribution> contributions)
    {
        var missing = product.MissingDetails().ToList();
        if (!contributions.Any(c => c.Percentage > 0)) missing.Add("contributions");
        return missing;
    }

    private static void AddContributions(IAlphaDbContext db, Guid productId, ContributionParty party,
        IReadOnlyCollection<EmployeePensionContributionInput> items)
    {
        if (items.GroupBy(x => x.Component).Any(g => g.Count() > 1))
            throw new ArgumentException("Each contribution component may appear only once per party.");
        foreach (var item in items)
            db.EmployeePensionContributions.Add(new EmployeePensionContribution(productId, party, item.Component, item.Percentage));
    }

    private sealed record ResolvedProductInput(EmployeePensionProductInput Input, bool IsActive, DateOnly EffectiveFrom,
        DateOnly? EffectiveTo, string InstitutionalBody, string Manufacturer, SalaryAllocationType SalaryAllocationType,
        decimal? SalaryAllocationValue, int AllocationOrder);
}

public sealed record SaveEmployeePensionMixRequest(decimal? MonthlySalary, IReadOnlyCollection<EmployeePensionProductInput> Products);
public sealed record EmployeePensionProductInput(PensionProductType ProductType, string PolicyNumber, decimal Salary,
    string ReportingType, string SalaryLayer, bool Section14, DateOnly? Section14StartDate,
    bool? IsActive, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, string? InstitutionalBody, string? Manufacturer,
    SalaryAllocationType? SalaryAllocationType, decimal? SalaryAllocationValue, int? AllocationOrder,
    IReadOnlyCollection<EmployeePensionContributionInput> EmployerContributions,
    IReadOnlyCollection<EmployeePensionContributionInput> EmployeeContributions);
public sealed record EmployeePensionContributionInput(ContributionComponent Component, decimal Percentage);
