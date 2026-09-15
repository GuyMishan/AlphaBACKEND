using Alpha.Api.Validation;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class ReportValidationEndpoints
{
    public static IEndpointRouteBuilder MapReportValidationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/manual-reports")
            .RequireAuthorization().WithTags("Manual reporting");
        group.MapGet("/{reportId:guid}/validate", ValidateAsync);
        group.MapGet("/contribution-limits", GetContributionLimitsAsync);
        return endpoints;
    }

    private static async Task<IResult> GetContributionLimitsAsync(Guid organizationId, Guid employerId, int year,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (year < 2000 || year > 2200) return Results.BadRequest(new { error = "שנת הגבולות אינה תקינה." });
        var items = await db.ContributionPercentageLimits.AsNoTracking()
            .Where(x => x.Year == year)
            .OrderBy(x => x.ProductType).ThenBy(x => x.Party).ThenBy(x => x.Component)
            .Select(x => new { x.Year, x.ProductType, x.Party, x.Component, x.MaxPercentage })
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> ValidateAsync(Guid organizationId, Guid employerId, Guid reportId,
        string? stage, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        var errors = new List<string>();
        if (report.ReportingMonth.Year < 2000 || report.ReportingMonth > new DateOnly(DateTime.UtcNow.Year + 1, 12, 1))
            errors.Add("חודש הדיווח אינו תקין.");
        if (report.SalaryPaymentDate is null)
            errors.Add("תאריך תשלום שכר הוא שדה חובה.");

        var employees = await db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == reportId).ToListAsync(ct);
        if (employees.Count == 0) errors.Add("יש לבחור לפחות עובד אחד לדיווח.");

        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking()
            .Where(x => employeeIds.Contains(x.ReportEmployeeId)).OrderBy(x => x.ReportEmployeeId).ThenBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var years = products.Select(x => x.SalaryMonth.Year).Distinct().ToArray();
        var limits = await db.ContributionPercentageLimits.AsNoTracking().Where(x => years.Contains(x.Year)).ToListAsync(ct);

        foreach (var employee in employees)
        {
            var employeeProducts = products.Where(x => x.ReportEmployeeId == employee.Id).ToList();
            if (employee.MonthlySalary <= 0)
                errors.Add($"לעובד {employee.FirstName} {employee.LastName} חסר שכר חודשי.");
            if (employeeProducts.Count == 0)
            {
                errors.Add($"לעובד {employee.FirstName} {employee.LastName} אין מוצר פנסיוני בדיווח.");
                continue;
            }
            if (employeeProducts.Count(x => x.SalaryAllocationType == SalaryAllocationType.Remainder) > 1)
                errors.Add($"לעובד {employee.FirstName} {employee.LastName} מוגדר יותר ממוצר אחד כיתרת שכר.");
            if (employee.MonthlySalary > 0 && employeeProducts.Sum(x => x.Salary) > employee.MonthlySalary + 0.01m)
                errors.Add($"סך השכר המבוטח של {employee.FirstName} {employee.LastName} גבוה מהשכר החודשי.");

            var inputs = employeeProducts.Select(product => new ManualProductInput(
                product.ProductType,
                product.PolicyNumber,
                product.SalaryMonth,
                product.Salary,
                product.ReportingType,
                product.SalaryLayer,
                product.Section14,
                product.Section14StartDate,
                product.FundExternalKey,
                product.FundCode,
                product.FundName,
                product.FundCompanyName,
                product.SalaryAllocationType,
                product.SalaryAllocationValue,
                product.AllocationOrder,
                contributions.Where(x => x.ReportProductId == product.Id && x.Party == ContributionParty.Employer)
                    .Select(x => new ManualContributionInput(x.Component, x.Amount, x.Percentage, x.ExemptPayments)).ToArray(),
                contributions.Where(x => x.ReportProductId == product.Id && x.Party == ContributionParty.Employee)
                    .Select(x => new ManualContributionInput(x.Component, x.Amount, x.Percentage, x.ExemptPayments)).ToArray()
            )).ToArray();

            foreach (var error in ApiInputValidation.Products(inputs, limits))
                errors.Add($"{employee.FirstName} {employee.LastName}: {error}");
        }

        var includePayments = string.Equals(stage, "deposits", StringComparison.OrdinalIgnoreCase)
            || string.Equals(stage, "final", StringComparison.OrdinalIgnoreCase);
        if (includePayments && products.Count > 0)
        {
            var payments = await db.ManualReportPayments.AsNoTracking()
                .Where(x => productIds.Contains(x.ReportProductId)).ToDictionaryAsync(x => x.ReportProductId, ct);
            foreach (var product in products)
            {
                var employee = employees.First(x => x.Id == product.ReportEmployeeId);
                if (!payments.TryGetValue(product.Id, out var payment))
                {
                    errors.Add($"חסרים פרטי אמצעי תשלום עבור {employee.FirstName} {employee.LastName}, פוליסה {product.PolicyNumber}.");
                    continue;
                }
                var request = new SaveManualReportPaymentRequest(payment.ProviderName, payment.ProviderAccount,
                    payment.PaymentMethod, payment.ValueDate, payment.ReferenceNumber, payment.EmployerBankName,
                    payment.EmployerBankCode, payment.EmployerBranch, payment.EmployerAccount, payment.ConfirmationFileName);
                foreach (var error in ApiInputValidation.Payment(request))
                    errors.Add($"{employee.FirstName} {employee.LastName}, פוליסה {product.PolicyNumber}: {error}");
            }
        }

        return Results.Ok(new { isValid = errors.Count == 0, errors = errors.Distinct().Take(100).ToArray() });
    }
}
