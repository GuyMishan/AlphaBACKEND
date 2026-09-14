using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class ManualReportEndpoints
{
    private const int MaxEmployeesPerDraft = 5000;
    private const int MaxProductsPerEmployee = 20;

    public static IEndpointRouteBuilder MapManualReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/manual-reports")
            .RequireAuthorization().WithTags("Manual reporting");

        group.MapPost("/", CreateDraftAsync);
        group.MapGet("/{reportId:guid}", GetReportAsync);
        group.MapPut("/{reportId:guid}/details", UpdateDetailsAsync);
        group.MapPut("/{reportId:guid}/selection", SyncSelectionAsync);
        group.MapGet("/{reportId:guid}/employees", GetEmployeesAsync);
        group.MapGet("/{reportId:guid}/employees/{reportEmployeeId:guid}", GetEmployeeAsync);
        group.MapPut("/{reportId:guid}/employees/{reportEmployeeId:guid}", SaveEmployeeAsync);
        group.MapGet("/{reportId:guid}/deposits", GetDepositsAsync);
        return endpoints;
    }

    private static async Task<IResult> CreateDraftAsync(Guid organizationId, Guid employerId,
        CreateManualReportRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.EmploymentIds.Count > MaxEmployeesPerDraft)
            return Results.BadRequest(new { error = $"Manual reports are limited to {MaxEmployeesPerDraft} employees per draft." });
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();

        var report = new ManualReport(organizationId, employerId, request.ReportingMonth, request.SalaryPaymentDate);
        db.ManualReports.Add(report);
        var selectedIds = request.EmploymentIds.Distinct().ToArray();
        if (selectedIds.Length > 0)
        {
            var employees = await (from employment in db.Employments.AsNoTracking()
                                   join person in db.People.AsNoTracking() on employment.PersonId equals person.Id
                                   where employment.OrganizationId == organizationId && employment.EmployerId == employerId && selectedIds.Contains(employment.Id)
                                   select new { Employment = employment, Person = person }).ToListAsync(ct);
            if (employees.Count != selectedIds.Length)
                return Results.BadRequest(new { error = "One or more selected employees do not belong to this employer." });
            foreach (var item in employees)
                db.ManualReportEmployees.Add(new ManualReportEmployee(report.Id, organizationId, employerId,
                    item.Employment.Id, item.Person.Id, item.Person.NationalId, item.Person.FirstName,
                    item.Person.LastName, item.Employment.EmployeeNumber));
        }
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/organizations/{organizationId}/employers/{employerId}/manual-reports/{report.Id}",
            new { report.Id, report.ReportingMonth, report.SalaryPaymentDate, report.Status, employeeCount = selectedIds.Length });
    }

    private static async Task<IResult> GetReportAsync(Guid organizationId, Guid employerId, Guid reportId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        var employeeCount = await db.ManualReportEmployees.AsNoTracking().CountAsync(x => x.ReportId == reportId, ct);
        return Results.Ok(new { report.Id, report.ReportingMonth, report.SalaryPaymentDate, report.Status, employeeCount });
    }

    private static async Task<IResult> UpdateDetailsAsync(Guid organizationId, Guid employerId, Guid reportId,
        UpdateManualReportDetailsRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        report.UpdateDetails(request.ReportingMonth, request.SalaryPaymentDate);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SyncSelectionAsync(Guid organizationId, Guid employerId, Guid reportId,
        UpdateManualReportSelectionRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.EmploymentIds.Count > MaxEmployeesPerDraft)
            return Results.BadRequest(new { error = $"Manual reports are limited to {MaxEmployeesPerDraft} employees per draft." });
        if (!await db.ManualReports.AsNoTracking().AnyAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct))
            return Results.NotFound();

        var requested = request.EmploymentIds.Distinct().ToHashSet();
        var existing = await db.ManualReportEmployees.Where(x => x.ReportId == reportId).ToListAsync(ct);
        var toRemove = existing.Where(x => !requested.Contains(x.EmploymentId)).ToList();
        if (toRemove.Count > 0) db.ManualReportEmployees.RemoveRange(toRemove);
        var existingIds = existing.Select(x => x.EmploymentId).ToHashSet();
        var toAddIds = requested.Where(x => !existingIds.Contains(x)).ToArray();
        if (toAddIds.Length > 0)
        {
            var employees = await (from employment in db.Employments.AsNoTracking()
                                   join person in db.People.AsNoTracking() on employment.PersonId equals person.Id
                                   where employment.OrganizationId == organizationId && employment.EmployerId == employerId && toAddIds.Contains(employment.Id)
                                   select new { Employment = employment, Person = person }).ToListAsync(ct);
            if (employees.Count != toAddIds.Length)
                return Results.BadRequest(new { error = "One or more selected employees do not belong to this employer." });
            foreach (var item in employees)
                db.ManualReportEmployees.Add(new ManualReportEmployee(reportId, organizationId, employerId,
                    item.Employment.Id, item.Person.Id, item.Person.NationalId, item.Person.FirstName,
                    item.Person.LastName, item.Employment.EmployeeNumber));
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetEmployeesAsync(Guid organizationId, Guid employerId, Guid reportId,
        string? search, int skip, int take, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.ManualReports.AsNoTracking().AnyAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct))
            return Results.NotFound();
        skip = Math.Max(0, skip);
        take = Math.Clamp(take == 0 ? 50 : take, 1, 100);
        var query = db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == reportId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.FirstName.ToLower().Contains(term) || x.LastName.ToLower().Contains(term) || x.NationalId.Contains(term) || x.EmployeeNumber.Contains(term));
        }
        var page = await query.OrderBy(x => x.LastName).ThenBy(x => x.FirstName).Skip(skip).Take(take + 1).ToListAsync(ct);
        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var ids = page.Select(x => x.Id).ToArray();
        var productCounts = await db.ManualReportProducts.AsNoTracking().Where(x => ids.Contains(x.ReportEmployeeId))
            .GroupBy(x => x.ReportEmployeeId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var items = page.Select(x => new
        {
            x.Id, x.EmploymentId, x.PersonId, x.NationalId, x.FirstName, x.LastName, x.EmployeeNumber,
            productCount = productCounts.GetValueOrDefault(x.Id),
            validationStatus = productCounts.GetValueOrDefault(x.Id) > 0 ? "ready" : "missing-products"
        });
        return Results.Ok(new { items, hasMore });
    }

    private static async Task<IResult> GetDepositsAsync(Guid organizationId, Guid employerId, Guid reportId,
        string? search, int skip, int take, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.ManualReports.AsNoTracking().AnyAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct))
            return Results.NotFound();

        skip = Math.Max(0, skip);
        take = Math.Clamp(take == 0 ? 50 : take, 1, 100);
        var query = from product in db.ManualReportProducts.AsNoTracking()
                    join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                    where employee.ReportId == reportId
                    select new { Product = product, Employee = employee };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Employee.FirstName.ToLower().Contains(term)
                || x.Employee.LastName.ToLower().Contains(term)
                || x.Employee.NationalId.Contains(term)
                || x.Product.PolicyNumber.ToLower().Contains(term));
        }

        var page = await query.OrderBy(x => x.Employee.LastName).ThenBy(x => x.Employee.FirstName)
            .ThenBy(x => x.Product.CreatedAt).Skip(skip).Take(take + 1).ToListAsync(ct);
        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var productIds = page.Select(x => x.Product.Id).ToArray();
        var totals = await db.ManualContributions.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .GroupBy(x => x.ReportProductId)
            .Select(g => new { Id = g.Key, Amount = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.Id, x => x.Amount, ct);

        var items = page.Select(x => new
        {
            id = x.Product.Id,
            reportEmployeeId = x.Employee.Id,
            x.Employee.EmploymentId,
            employeeName = x.Employee.FirstName + " " + x.Employee.LastName,
            x.Employee.NationalId,
            x.Product.ProductType,
            x.Product.PolicyNumber,
            x.Product.SalaryMonth,
            x.Product.Salary,
            x.Product.ReportingType,
            x.Product.SalaryLayer,
            x.Product.Section14,
            x.Product.Section14StartDate,
            totalDeposit = totals.GetValueOrDefault(x.Product.Id)
        });
        return Results.Ok(new { items, hasMore });
    }

    private static async Task<IResult> GetEmployeeAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportEmployeeId, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employee = await db.ManualReportEmployees.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportEmployeeId && x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (employee is null) return Results.NotFound();
        var products = await db.ManualReportProducts.AsNoTracking().Where(x => x.ReportEmployeeId == reportEmployeeId).OrderBy(x => x.CreatedAt).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        return Results.Ok(new
        {
            employee.Id, employee.EmploymentId, employee.PersonId, employee.NationalId, employee.FirstName, employee.LastName, employee.EmployeeNumber,
            products = products.Select(p => new
            {
                p.Id, p.ProductType, p.PolicyNumber, p.SalaryMonth, p.Salary, p.ReportingType, p.SalaryLayer, p.Section14, p.Section14StartDate,
                employerContributions = contributions.Where(c => c.ReportProductId == p.Id && c.Party == ContributionParty.Employer).OrderBy(c => c.Component),
                employeeContributions = contributions.Where(c => c.ReportProductId == p.Id && c.Party == ContributionParty.Employee).OrderBy(c => c.Component)
            })
        });
    }

    private static async Task<IResult> SaveEmployeeAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportEmployeeId, SaveManualReportEmployeeRequest request, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.Products.Count > MaxProductsPerEmployee)
            return Results.BadRequest(new { error = $"An employee can have up to {MaxProductsPerEmployee} products in a report." });
        if (!await db.ManualReportEmployees.AsNoTracking().AnyAsync(x => x.Id == reportEmployeeId && x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct))
            return Results.NotFound();

        var existingProductIds = await db.ManualReportProducts.Where(x => x.ReportEmployeeId == reportEmployeeId).Select(x => x.Id).ToArrayAsync(ct);
        if (existingProductIds.Length > 0)
        {
            await db.ManualContributions.Where(x => existingProductIds.Contains(x.ReportProductId)).ExecuteDeleteAsync(ct);
            await db.ManualReportProducts.Where(x => x.ReportEmployeeId == reportEmployeeId).ExecuteDeleteAsync(ct);
        }
        foreach (var input in request.Products)
        {
            var product = new ManualReportProduct(reportEmployeeId, input.ProductType, input.PolicyNumber,
                input.SalaryMonth, input.Salary, input.ReportingType, input.SalaryLayer, input.Section14, input.Section14StartDate);
            db.ManualReportProducts.Add(product);
            AddContributions(db, product.Id, ContributionParty.Employer, input.EmployerContributions);
            AddContributions(db, product.Id, ContributionParty.Employee, input.EmployeeContributions);
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static void AddContributions(IAlphaDbContext db, Guid productId, ContributionParty party, IReadOnlyCollection<ManualContributionInput> items)
    {
        if (items.GroupBy(x => x.Component).Any(g => g.Count() > 1))
            throw new ArgumentException("Each contribution component may appear only once per party.");
        foreach (var item in items)
            db.ManualContributions.Add(new ManualContribution(productId, party, item.Component, item.Amount, item.Percentage, item.ExemptPayments));
    }
}

public sealed record CreateManualReportRequest(DateOnly ReportingMonth, DateOnly? SalaryPaymentDate, IReadOnlyCollection<Guid> EmploymentIds);
public sealed record UpdateManualReportDetailsRequest(DateOnly ReportingMonth, DateOnly? SalaryPaymentDate);
public sealed record UpdateManualReportSelectionRequest(IReadOnlyCollection<Guid> EmploymentIds);
public sealed record SaveManualReportEmployeeRequest(IReadOnlyCollection<ManualProductInput> Products);
public sealed record ManualProductInput(PensionProductType ProductType, string PolicyNumber, DateOnly SalaryMonth,
    decimal Salary, string ReportingType, string SalaryLayer, bool Section14, DateOnly? Section14StartDate,
    IReadOnlyCollection<ManualContributionInput> EmployerContributions, IReadOnlyCollection<ManualContributionInput> EmployeeContributions);
public sealed record ManualContributionInput(ContributionComponent Component, decimal Amount, decimal Percentage, decimal ExemptPayments);
