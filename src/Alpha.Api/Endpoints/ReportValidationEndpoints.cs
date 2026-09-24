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
        group.MapGet("/{reportId:guid}/validate", PreviewValidationAsync);
        group.MapPost("/{reportId:guid}/validate", CommitValidationAsync);
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

    private static async Task<IResult> PreviewValidationAsync(Guid organizationId, Guid employerId, Guid reportId,
        string? stage, IAlphaDbContext db, OrganizationAccessService access,
        EmployerInterface006ExportService employerInterfaceExporter, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var normalizedStage = NormalizeStage(stage);
        var result = await ValidateReportAsync(organizationId, employerId, reportId, normalizedStage, db, false, ct);
        if (result is null) return Results.NotFound();
        await AppendEmployerInterfacePreflightAsync(result, normalizedStage, employerInterfaceExporter, ct);
        return Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> CommitValidationAsync(Guid organizationId, Guid employerId, Guid reportId,
        string? stage, IAlphaDbContext db, OrganizationAccessService access,
        EmployerInterface006ExportService employerInterfaceExporter, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var normalizedStage = NormalizeStage(stage);
        var result = await ValidateReportAsync(organizationId, employerId, reportId, normalizedStage, db, true, ct);
        if (result is null) return Results.NotFound();
        await AppendEmployerInterfacePreflightAsync(result, normalizedStage, employerInterfaceExporter, ct);

        if (result.Report.Status is ManualReportStatus.Submitted or ManualReportStatus.Sent
            or ManualReportStatus.Processing or ManualReportStatus.Completed or ManualReportStatus.Cancelled)
            return Results.Conflict(new { error = "לא ניתן לבצע validation מחדש לדיווח שכבר נשלח או הושלם." });

        result.Report.MarkReadyForValidation();
        foreach (var employee in result.Employees) employee.MarkReadyForValidation();
        foreach (var product in result.Products) product.MarkReadyForValidation();

        foreach (var employee in result.Employees)
        {
            var employeeIssues = result.Issues.Where(x => x.EmployeeId == employee.Id).ToArray();
            employee.SetValidationResult(employeeIssues.Length == 0,
                employeeIssues.Length == 0 ? null : string.Join(" ", employeeIssues.Take(5).Select(x => x.Message)));
        }

        foreach (var product in result.Products)
        {
            var productIssues = result.Issues.Where(x => x.ProductId == product.Id).ToList();
            if (productIssues.Count == 0)
            {
                var employeeId = result.ProductsByEmployee.First(x => x.Value.Any(p => p.Id == product.Id)).Key;
                productIssues.AddRange(result.Issues.Where(x => x.EmployeeId == employeeId && x.ProductId is null
                    && x.Scope is ValidationScope.Product or ValidationScope.Contribution));
            }
            product.SetValidationResult(productIssues.Count == 0,
                productIssues.Count == 0 ? null : string.Join(" ", productIssues.Take(5).Select(x => x.Message)));
        }

        if (result.Issues.Count > 0)
            result.Report.MarkValidationError(string.Join(" ", result.Issues.Take(8).Select(x => x.Message)));
        else if (normalizedStage == ValidationStage.Final)
            result.Report.MarkValidated();

        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(result));
    }

    private static async Task<ValidationContext?> ValidateReportAsync(Guid organizationId, Guid employerId, Guid reportId,
        ValidationStage stage, IAlphaDbContext db, bool tracked, CancellationToken ct)
    {
        var reportQuery = db.ManualReports.Where(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId);
        var report = tracked
            ? await reportQuery.SingleOrDefaultAsync(ct)
            : await reportQuery.AsNoTracking().SingleOrDefaultAsync(ct);
        if (report is null) return null;

        var employeeQuery = db.ManualReportEmployees.Where(x => x.ReportId == reportId);
        var employees = tracked
            ? await employeeQuery.ToListAsync(ct)
            : await employeeQuery.AsNoTracking().ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();

        var productQuery = db.ManualReportProducts.Where(x => employeeIds.Contains(x.ReportEmployeeId))
            .OrderBy(x => x.ReportEmployeeId).ThenBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt);
        var products = tracked
            ? await productQuery.ToListAsync(ct)
            : await productQuery.AsNoTracking().ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();

        var contributions = await db.ManualContributions.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var years = products.Select(x => x.SalaryMonth.Year).Distinct().ToArray();
        var limits = await db.ContributionPercentageLimits.AsNoTracking()
            .Where(x => years.Contains(x.Year)).ToListAsync(ct);
        var issues = new List<ValidationIssue>();

        if (report.ReportingMonth.Year < 2000 || report.ReportingMonth > new DateOnly(DateTime.UtcNow.Year + 1, 12, 1))
            issues.Add(new("REPORTING_MONTH_INVALID", "חודש הדיווח אינו תקין.", ValidationScope.Report));
        if (report.SalaryPaymentDate is null)
            issues.Add(new("SALARY_PAYMENT_DATE_REQUIRED", "תאריך תשלום שכר הוא שדה חובה.", ValidationScope.Report));
        if (employees.Count == 0)
            issues.Add(new("EMPLOYEE_REQUIRED", "יש לבחור לפחות עובד אחד לדיווח.", ValidationScope.Report));

        var productsByEmployee = products.GroupBy(x => x.ReportEmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var employee in employees)
        {
            var employeeProducts = productsByEmployee.GetValueOrDefault(employee.Id) ?? [];
            var employeeName = $"{employee.FirstName} {employee.LastName}".Trim();

            if (employee.MonthlySalary <= 0)
                issues.Add(new("MONTHLY_SALARY_REQUIRED", $"לעובד {employeeName} חסר שכר חודשי.", ValidationScope.Employee, employee.Id));
            if (string.IsNullOrWhiteSpace(employee.NationalId))
                issues.Add(new("NATIONAL_ID_REQUIRED", $"לעובד {employeeName} חסרה תעודת זהות.", ValidationScope.Employee, employee.Id));
            if (employeeProducts.Count == 0)
            {
                issues.Add(new("PENSION_PRODUCT_REQUIRED", $"לעובד {employeeName} אין מוצר פנסיוני בדיווח.", ValidationScope.Product, employee.Id));
                continue;
            }
            if (employeeProducts.Count(x => x.SalaryAllocationType == SalaryAllocationType.Remainder) > 1)
                issues.Add(new("MULTIPLE_REMAINDER_PRODUCTS", $"לעובד {employeeName} מוגדר יותר ממוצר אחד כיתרת שכר.", ValidationScope.Product, employee.Id));
            if (employee.MonthlySalary > 0 && employeeProducts.Sum(x => x.Salary) > employee.MonthlySalary + 0.01m)
                issues.Add(new("INSURED_SALARY_EXCEEDS_MONTHLY", $"סך השכר המבוטח של {employeeName} גבוה מהשכר החודשי.", ValidationScope.Product, employee.Id));

            foreach (var product in employeeProducts)
            {
                if (product.Salary <= 0)
                    issues.Add(new("INSURED_SALARY_REQUIRED", $"למוצר {ProductLabel(product)} של {employeeName} חסר שכר מבוטח.", ValidationScope.Product, employee.Id, product.Id));
                if (product.ProductType != PensionProductType.Other && string.IsNullOrWhiteSpace(product.FundExternalKey))
                    issues.Add(new("FUND_REQUIRED", $"למוצר {ProductLabel(product)} של {employeeName} לא נבחרה קופה.", ValidationScope.Product, employee.Id, product.Id));
                if (string.IsNullOrWhiteSpace(product.PolicyNumber))
                    issues.Add(new("POLICY_NUMBER_REQUIRED", $"למוצר של {employeeName} חסר מספר פוליסה/חשבון.", ValidationScope.Product, employee.Id, product.Id));
            }

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
                issues.Add(new("PRODUCT_VALIDATION", $"{employeeName}: {error}", ValidationScope.Contribution, employee.Id));
        }

        if ((stage is ValidationStage.Deposits or ValidationStage.Final) && products.Count > 0)
        {
            var payments = await db.ManualReportPayments.AsNoTracking()
                .Where(x => productIds.Contains(x.ReportProductId)).ToDictionaryAsync(x => x.ReportProductId, ct);
            var employeeById = employees.ToDictionary(x => x.Id);
            foreach (var product in products)
            {
                var employee = employeeById[product.ReportEmployeeId];
                var employeeName = $"{employee.FirstName} {employee.LastName}".Trim();
                if (!payments.TryGetValue(product.Id, out var payment))
                {
                    issues.Add(new("PAYMENT_REQUIRED", $"חסרים פרטי אמצעי תשלום עבור {employeeName}, פוליסה {product.PolicyNumber}.", ValidationScope.Payment, employee.Id, product.Id));
                    continue;
                }
                var request = new SaveManualReportPaymentRequest(payment.ProviderName, payment.ProviderAccount,
                    payment.PaymentMethod, payment.ValueDate, payment.ReferenceNumber, payment.EmployerBankName,
                    payment.EmployerBankCode, payment.EmployerBranch, payment.EmployerAccount, payment.ConfirmationFileName);
                foreach (var error in ApiInputValidation.Payment(request))
                    issues.Add(new("PAYMENT_VALIDATION", $"{employeeName}, פוליסה {product.PolicyNumber}: {error}", ValidationScope.Payment, employee.Id, product.Id));
            }
        }

        return new ValidationContext(report, employees, products, productsByEmployee, issues);
    }

    private static async Task AppendEmployerInterfacePreflightAsync(
        ValidationContext result,
        ValidationStage stage,
        EmployerInterface006ExportService exporter,
        CancellationToken ct)
    {
        if (stage != ValidationStage.Final || result.Issues.Count > 0 || result.Report.ReportKind == ManualReportKind.Differences)
            return;

        var generated = await exporter.ExportAsync(result.Report, ct);
        if (generated.Validation.IsValid) return;

        foreach (var issue in generated.Validation.Issues.Distinct(StringComparer.Ordinal).Take(100))
            result.Issues.Add(new("EMPLOYER_INTERFACE_006", issue, ValidationScope.Report));
    }

    private static object ToResponse(ValidationContext result) => new
    {
        isValid = result.Issues.Count == 0,
        status = result.Report.Status,
        snapshotTakenAt = result.Report.SnapshotTakenAt,
        validatedAt = result.Report.ValidatedAt,
        errors = result.Issues.Select(x => x.Message).Distinct().Take(100).ToArray(),
        issues = result.Issues.Take(100).Select(x => new
        {
            x.Code,
            x.Message,
            scope = x.Scope.ToString(),
            x.EmployeeId,
            x.ProductId
        }).ToArray()
    };

    private static ValidationStage NormalizeStage(string? stage) => stage?.Trim().ToLowerInvariant() switch
    {
        "employees" => ValidationStage.Employees,
        "deposits" => ValidationStage.Deposits,
        _ => ValidationStage.Final
    };

    private static string ProductLabel(ManualReportProduct product) =>
        !string.IsNullOrWhiteSpace(product.FundName) ? product.FundName :
        !string.IsNullOrWhiteSpace(product.PolicyNumber) ? product.PolicyNumber : product.ProductType.ToString();

    private enum ValidationStage { Employees, Deposits, Final }
    private enum ValidationScope { Report, Employee, Product, Contribution, Payment }
    private sealed record ValidationIssue(string Code, string Message, ValidationScope Scope, Guid? EmployeeId = null, Guid? ProductId = null);
    private sealed record ValidationContext(ManualReport Report, List<ManualReportEmployee> Employees,
        List<ManualReportProduct> Products, Dictionary<Guid, List<ManualReportProduct>> ProductsByEmployee,
        List<ValidationIssue> Issues);
}
