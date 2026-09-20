using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Reporting;
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
        group.MapGet("/", GetOpenReportsAsync);
        group.MapGet("/{reportId:guid}", GetReportAsync);
        group.MapPut("/{reportId:guid}/details", UpdateDetailsAsync);
        group.MapPut("/{reportId:guid}/selection", SyncSelectionAsync);
        group.MapGet("/{reportId:guid}/employees", GetEmployeesAsync);
        group.MapGet("/{reportId:guid}/employees/{reportEmployeeId:guid}", GetEmployeeAsync);
        group.MapPut("/{reportId:guid}/employees/{reportEmployeeId:guid}", SaveEmployeeAsync);
        group.MapGet("/{reportId:guid}/deposits", GetDepositsAsync);
        group.MapPut("/{reportId:guid}/deposits/{reportProductId:guid}", SaveDepositPaymentAsync);
        return endpoints;
    }

    private static async Task<IResult> GetOpenReportsAsync(Guid organizationId, Guid employerId,
        string? search, int skip, int take, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        skip = Math.Max(0, skip);
        take = Math.Clamp(take == 0 ? 30 : take, 1, 100);

        var query = db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                && x.Status != ManualReportStatus.Submitted && x.Status != ManualReportStatus.Cancelled);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (DateOnly.TryParse(term, out var parsed))
            {
                var month = new DateOnly(parsed.Year, parsed.Month, 1);
                query = query.Where(x => x.ReportingMonth == month);
            }
        }

        var page = await query.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.CreatedAt)
            .Skip(skip).Take(take + 1).ToListAsync(ct);
        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);

        var ids = page.Select(x => x.Id).ToArray();
        var employeeCounts = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => ids.Contains(x.ReportId))
            .GroupBy(x => x.ReportId)
            .Select(g => new { ReportId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ReportId, x => x.Count, ct);

        var reportEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => ids.Contains(x.ReportId))
            .Select(x => new { x.Id, x.ReportId })
            .ToListAsync(ct);
        var employeeReportById = reportEmployeeIds.ToDictionary(x => x.Id, x => x.ReportId);
        var reportEmployeeIdValues = employeeReportById.Keys.ToArray();
        var productCounts = await db.ManualReportProducts.AsNoTracking()
            .Where(x => reportEmployeeIdValues.Contains(x.ReportEmployeeId))
            .GroupBy(x => x.ReportEmployeeId)
            .Select(g => new { EmployeeId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var productCountByReport = productCounts
            .GroupBy(x => employeeReportById[x.EmployeeId])
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Count));

        return Results.Ok(new
        {
            items = page.Select(x => new
            {
                x.Id,
                x.ReportingMonth,
                x.SalaryPaymentDate,
                x.Status,
                x.CreatedAt,
                x.UpdatedAt,
                employeeCount = employeeCounts.GetValueOrDefault(x.Id),
                productCount = productCountByReport.GetValueOrDefault(x.Id)
            }),
            hasMore
        });
    }

    private static async Task<IResult> CreateDraftAsync(Guid organizationId, Guid employerId,
        CreateManualReportRequest request, IAlphaDbContext db, OrganizationAccessService access,
        ReportPaymentAccountService paymentAccounts, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.EmploymentIds.Count > MaxEmployeesPerDraft)
            return Results.BadRequest(new { error = $"Manual reports are limited to {MaxEmployeesPerDraft} employees per draft." });
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();

        var report = new ManualReport(organizationId, employerId, request.ReportingMonth, request.SalaryPaymentDate);
        var paymentAccount = await paymentAccounts.ResolveForReportAsync(employerId, request.PaymentAccountId, ct);
        if (paymentAccount is null) return Results.Conflict(new { error = "payment_account_required" });
        await paymentAccounts.ApplySnapshotAsync(report, paymentAccount, ct);
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
            {
                var reportEmployee = new ManualReportEmployee(report.Id, organizationId, employerId,
                    item.Employment.Id, item.Person.Id, item.Person.NationalId, item.Person.FirstName,
                    item.Person.LastName, item.Employment.EmployeeNumber, item.Employment.MonthlySalary);
                db.ManualReportEmployees.Add(reportEmployee);
                await SeedProductsFromMixAsync(db, reportEmployee, report.ReportingMonth, ct);
            }
        }
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/organizations/{organizationId}/employers/{employerId}/manual-reports/{report.Id}",
            new
            {
                report.Id, report.ReportingMonth, report.SalaryPaymentDate, report.Status,
                report.PaymentAccountId, report.PaymentBankId, report.PaymentBranchId,
                report.PaymentAccountNumberMasked, report.PaymentMandateReference,
                employeeCount = selectedIds.Length
            });
    }

    private static async Task<IResult> GetReportAsync(Guid organizationId, Guid employerId, Guid reportId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        var employeeCount = await db.ManualReportEmployees.AsNoTracking().CountAsync(x => x.ReportId == reportId, ct);
        return Results.Ok(new
        {
            report.Id, report.ReportingMonth, report.SalaryPaymentDate, report.Status,
            report.PaymentAccountId, report.PaymentBankId, report.PaymentBranchId,
            report.PaymentAccountNumberMasked, report.PaymentMandateReference,
            employeeCount
        });
    }

    private static async Task<IResult> UpdateDetailsAsync(Guid organizationId, Guid employerId, Guid reportId,
        UpdateManualReportDetailsRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        report.UpdateDetails(request.ReportingMonth, request.SalaryPaymentDate);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SyncSelectionAsync(Guid organizationId, Guid employerId, Guid reportId,
        UpdateManualReportSelectionRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.EmploymentIds.Count > MaxEmployeesPerDraft)
            return Results.BadRequest(new { error = $"Manual reports are limited to {MaxEmployeesPerDraft} employees per draft." });
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

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
            {
                var reportEmployee = new ManualReportEmployee(reportId, organizationId, employerId,
                    item.Employment.Id, item.Person.Id, item.Person.NationalId, item.Person.FirstName,
                    item.Person.LastName, item.Employment.EmployeeNumber, item.Employment.MonthlySalary);
                db.ManualReportEmployees.Add(reportEmployee);
                await SeedProductsFromMixAsync(db, reportEmployee, report.ReportingMonth, ct);
            }
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
            x.Id, x.EmploymentId, x.PersonId, x.NationalId, x.FirstName, x.LastName, x.EmployeeNumber, x.MonthlySalary,
            productCount = productCounts.GetValueOrDefault(x.Id),
            validationStatus = productCounts.GetValueOrDefault(x.Id) > 0 && x.MonthlySalary > 0 ? "ready" : "missing-products"
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
                || x.Product.PolicyNumber.ToLower().Contains(term)
                || x.Product.FundName.ToLower().Contains(term));
        }

        var page = await query.OrderBy(x => x.Employee.LastName).ThenBy(x => x.Employee.FirstName)
            .ThenBy(x => x.Product.AllocationOrder).ThenBy(x => x.Product.CreatedAt).Skip(skip).Take(take + 1).ToListAsync(ct);
        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var productIds = page.Select(x => x.Product.Id).ToArray();
        var totals = await db.ManualContributions.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .GroupBy(x => x.ReportProductId)
            .Select(g => new { Id = g.Key, Amount = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.Id, x => x.Amount, ct);
        var payments = await db.ManualReportPayments.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);

        var items = page.Select(x =>
        {
            payments.TryGetValue(x.Product.Id, out var payment);
            return new
            {
                id = x.Product.Id,
                reportEmployeeId = x.Employee.Id,
                x.Employee.EmploymentId,
                employeeName = x.Employee.FirstName + " " + x.Employee.LastName,
                x.Employee.NationalId,
                x.Employee.MonthlySalary,
                x.Product.ProductType,
                x.Product.PolicyNumber,
                x.Product.FundExternalKey,
                x.Product.FundCode,
                x.Product.FundName,
                x.Product.FundCompanyName,
                x.Product.SalaryMonth,
                x.Product.Salary,
                x.Product.SalaryAllocationType,
                x.Product.SalaryAllocationValue,
                x.Product.AllocationOrder,
                x.Product.ReportingType,
                x.Product.SalaryLayer,
                x.Product.Section14,
                x.Product.Section14StartDate,
                totalDeposit = totals.GetValueOrDefault(x.Product.Id),
                providerName = payment?.ProviderName ?? string.Empty,
                providerAccount = payment?.ProviderAccount ?? string.Empty,
                paymentMethod = payment?.PaymentMethod ?? "העברה בנקאית",
                valueDate = payment?.ValueDate,
                referenceNumber = payment?.ReferenceNumber ?? string.Empty,
                employerBankName = payment?.EmployerBankName ?? string.Empty,
                employerBankCode = payment?.EmployerBankCode ?? string.Empty,
                employerBranch = payment?.EmployerBranch ?? string.Empty,
                employerAccount = payment?.EmployerAccount ?? string.Empty,
                confirmationFileName = payment?.ConfirmationFileName ?? string.Empty
            };
        });
        return Results.Ok(new { items, hasMore });
    }

    private static async Task<IResult> SaveDepositPaymentAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportProductId, SaveManualReportPaymentRequest request, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var exists = await (from product in db.ManualReportProducts.AsNoTracking()
                            join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                            where product.Id == reportProductId && employee.ReportId == reportId
                                && employee.OrganizationId == organizationId && employee.EmployerId == employerId
                            select product.Id).AnyAsync(ct);
        if (!exists) return Results.NotFound();

        var payment = await db.ManualReportPayments.SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        if (payment is null)
        {
            payment = new ManualReportPayment(reportProductId);
            db.ManualReportPayments.Add(payment);
        }
        payment.Update(request.ProviderName, request.ProviderAccount, request.PaymentMethod, request.ValueDate,
            request.ReferenceNumber, request.EmployerBankName, request.EmployerBankCode, request.EmployerBranch,
            request.EmployerAccount, request.ConfirmationFileName);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetEmployeeAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportEmployeeId, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employee = await db.ManualReportEmployees.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportEmployeeId && x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (employee is null) return Results.NotFound();
        var products = await db.ManualReportProducts.AsNoTracking().Where(x => x.ReportEmployeeId == reportEmployeeId)
            .OrderBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        return Results.Ok(new
        {
            employee.Id, employee.EmploymentId, employee.PersonId, employee.NationalId, employee.FirstName, employee.LastName, employee.EmployeeNumber, employee.MonthlySalary,
            products = products.Select(p => new
            {
                p.Id, p.ProductType, p.PolicyNumber, p.FundExternalKey, p.FundCode, p.FundName, p.FundCompanyName,
                p.SalaryMonth, p.Salary, p.SalaryAllocationType, p.SalaryAllocationValue, p.AllocationOrder,
                p.ReportingType, p.SalaryLayer, p.Section14, p.Section14StartDate,
                employerContributions = contributions.Where(c => c.ReportProductId == p.Id && c.Party == ContributionParty.Employer).OrderBy(c => c.Component),
                employeeContributions = contributions.Where(c => c.ReportProductId == p.Id && c.Party == ContributionParty.Employee).OrderBy(c => c.Component)
            })
        });
    }

    private static async Task<IResult> SaveEmployeeAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportEmployeeId, SaveManualReportEmployeeRequest request, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.Products.Count > MaxProductsPerEmployee)
            return Results.BadRequest(new { error = $"An employee can have up to {MaxProductsPerEmployee} products in a report." });

        var employee = await db.ManualReportEmployees.SingleOrDefaultAsync(x => x.Id == reportEmployeeId && x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (employee is null) return Results.NotFound();

        if (request.Products.Any(x => x.ProductType != PensionProductType.Other && string.IsNullOrWhiteSpace(x.FundExternalKey)))
            return Results.BadRequest(new { error = "A fund must be selected for every pension product." });

        var monthlySalary = request.MonthlySalary > 0
            ? request.MonthlySalary
            : request.Products.Select(x => x.Salary).DefaultIfEmpty(0).Max();
        if (request.Products.Count > 0 && monthlySalary <= 0)
            return Results.BadRequest(new { error = "Employee monthly salary is required before salary allocations can be calculated." });
        if (request.Products.Count(x => (x.SalaryAllocationType ?? SalaryAllocationType.Fixed) == SalaryAllocationType.Remainder) > 1)
            return Results.BadRequest(new { error = "Only one product may use remainder salary allocation." });

        var resolvedProducts = ResolveSalaryAllocations(monthlySalary, request.Products);
        if (resolvedProducts.Error is not null) return Results.BadRequest(new { error = resolvedProducts.Error });

        employee.UpdateMonthlySalary(monthlySalary);
        var existingProductIds = await db.ManualReportProducts.Where(x => x.ReportEmployeeId == reportEmployeeId).Select(x => x.Id).ToArrayAsync(ct);
        if (existingProductIds.Length > 0)
        {
            await db.ManualContributions.Where(x => existingProductIds.Contains(x.ReportProductId)).ExecuteDeleteAsync(ct);
            await db.ManualReportProducts.Where(x => x.ReportEmployeeId == reportEmployeeId).ExecuteDeleteAsync(ct);
        }

        foreach (var item in resolvedProducts.Items)
        {
            var input = item.Input;
            var product = new ManualReportProduct(reportEmployeeId, input.ProductType, input.PolicyNumber,
                input.SalaryMonth, item.InsuredSalary, input.ReportingType, input.SalaryLayer, input.Section14,
                input.Section14StartDate, input.FundExternalKey, input.FundCode, input.FundName, input.FundCompanyName,
                item.AllocationType, item.AllocationValue, item.AllocationOrder);
            db.ManualReportProducts.Add(product);
            AddContributions(db, product.Id, ContributionParty.Employer, item.InsuredSalary, input.EmployerContributions);
            AddContributions(db, product.Id, ContributionParty.Employee, item.InsuredSalary, input.EmployeeContributions);
        }
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static (List<ResolvedReportProduct> Items, string? Error) ResolveSalaryAllocations(decimal monthlySalary,
        IReadOnlyCollection<ManualProductInput> products)
    {
        var ordered = products.Select((input, index) => new
        {
            Input = input,
            AllocationType = input.SalaryAllocationType ?? SalaryAllocationType.Fixed,
            AllocationValue = input.SalaryAllocationValue ?? (input.Salary > 0 ? input.Salary : null),
            AllocationOrder = input.AllocationOrder ?? index
        }).OrderBy(x => x.AllocationOrder).ThenBy(x => x.Input.PolicyNumber).ToList();

        decimal allocated = 0;
        var resolved = new List<ResolvedReportProduct>(ordered.Count);
        foreach (var item in ordered)
        {
            if (item.AllocationOrder < 0) return (resolved, "Salary allocation order cannot be negative.");
            if (item.AllocationType != SalaryAllocationType.Remainder && (!item.AllocationValue.HasValue || item.AllocationValue.Value <= 0))
                return (resolved, "Fixed, percentage and cap allocations require a positive value.");
            if (item.AllocationType == SalaryAllocationType.Percentage && item.AllocationValue > 100)
                return (resolved, "Salary allocation percentage cannot exceed 100%.");

            var insuredSalary = item.AllocationType switch
            {
                SalaryAllocationType.Fixed => item.AllocationValue!.Value,
                SalaryAllocationType.Percentage => Math.Round(monthlySalary * item.AllocationValue!.Value / 100m, 2, MidpointRounding.AwayFromZero),
                SalaryAllocationType.Cap => Math.Min(monthlySalary, item.AllocationValue!.Value),
                SalaryAllocationType.Remainder => Math.Max(monthlySalary - allocated, 0),
                _ => 0
            };

            if (item.AllocationType != SalaryAllocationType.Remainder && allocated + insuredSalary > monthlySalary + 0.01m)
                return (resolved, "Salary allocations exceed the employee monthly salary.");
            allocated += insuredSalary;
            resolved.Add(new ResolvedReportProduct(item.Input, item.AllocationType,
                item.AllocationType == SalaryAllocationType.Remainder ? null : item.AllocationValue,
                item.AllocationOrder, insuredSalary));
        }
        return (resolved, null);
    }

    private static async Task SeedProductsFromMixAsync(IAlphaDbContext db, ManualReportEmployee reportEmployee,
        DateOnly reportingMonth, CancellationToken ct)
    {
        var mixProducts = await db.EmployeePensionProducts.AsNoTracking()
            .Where(x => x.EmploymentId == reportEmployee.EmploymentId && x.IsActive
                && x.EffectiveFrom <= reportingMonth
                && (x.EffectiveTo == null || x.EffectiveTo >= reportingMonth))
            .OrderBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);
        if (mixProducts.Count == 0) return;

        var mixIds = mixProducts.Select(x => x.Id).ToArray();
        var mixContributions = await db.EmployeePensionContributions.AsNoTracking()
            .Where(x => mixIds.Contains(x.EmployeePensionProductId)).ToListAsync(ct);

        foreach (var mix in mixProducts)
        {
            var product = new ManualReportProduct(reportEmployee.Id, mix.ProductType, mix.PolicyNumber,
                reportingMonth, mix.Salary, mix.ReportingType, mix.SalaryLayer, mix.Section14, mix.Section14StartDate,
                mix.FundExternalKey, mix.FundCode, mix.FundName, mix.FundCompanyName,
                mix.SalaryAllocationType, mix.SalaryAllocationValue, mix.AllocationOrder);
            db.ManualReportProducts.Add(product);

            foreach (var contribution in mixContributions.Where(x => x.EmployeePensionProductId == mix.Id))
            {
                var amount = Math.Round(mix.Salary * contribution.Percentage / 100m, 2, MidpointRounding.AwayFromZero);
                db.ManualContributions.Add(new ManualContribution(product.Id, contribution.Party, contribution.Component,
                    amount, contribution.Percentage, 0));
            }
        }
    }

    private static void AddContributions(IAlphaDbContext db, Guid productId, ContributionParty party, decimal insuredSalary,
        IReadOnlyCollection<ManualContributionInput> items)
    {
        if (items.GroupBy(x => x.Component).Any(g => g.Count() > 1))
            throw new ArgumentException("Each contribution component may appear only once per party.");
        foreach (var item in items)
        {
            var amount = Math.Round(insuredSalary * item.Percentage / 100m, 2, MidpointRounding.AwayFromZero);
            db.ManualContributions.Add(new ManualContribution(productId, party, item.Component, amount, item.Percentage, item.ExemptPayments));
        }
    }

    private sealed record ResolvedReportProduct(ManualProductInput Input, SalaryAllocationType AllocationType,
        decimal? AllocationValue, int AllocationOrder, decimal InsuredSalary);
}

public sealed record CreateManualReportRequest(DateOnly ReportingMonth, DateOnly? SalaryPaymentDate,
    IReadOnlyCollection<Guid> EmploymentIds, Guid? PaymentAccountId = null);
public sealed record UpdateManualReportDetailsRequest(DateOnly ReportingMonth, DateOnly? SalaryPaymentDate);
public sealed record UpdateManualReportSelectionRequest(IReadOnlyCollection<Guid> EmploymentIds);
public sealed record SaveManualReportEmployeeRequest(decimal MonthlySalary, IReadOnlyCollection<ManualProductInput> Products);
public sealed record ManualProductInput(PensionProductType ProductType, string PolicyNumber, DateOnly SalaryMonth,
    decimal Salary, string ReportingType, string SalaryLayer, bool Section14, DateOnly? Section14StartDate,
    string? FundExternalKey, string? FundCode, string? FundName, string? FundCompanyName,
    SalaryAllocationType? SalaryAllocationType, decimal? SalaryAllocationValue, int? AllocationOrder,
    IReadOnlyCollection<ManualContributionInput> EmployerContributions, IReadOnlyCollection<ManualContributionInput> EmployeeContributions);
public sealed record ManualContributionInput(ContributionComponent Component, decimal Amount, decimal Percentage, decimal ExemptPayments);
public sealed record SaveManualReportPaymentRequest(string ProviderName, string ProviderAccount, string PaymentMethod,
    DateOnly? ValueDate, string ReferenceNumber, string EmployerBankName, string EmployerBankCode,
    string EmployerBranch, string EmployerAccount, string ConfirmationFileName);
