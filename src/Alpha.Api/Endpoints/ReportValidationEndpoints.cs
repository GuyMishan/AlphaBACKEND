using Alpha.Api.Security;
using Alpha.Api.Services;
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
        EmployerInterface006ExportService employerInterfaceExporter, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var normalizedStage = NormalizeStage(stage);
        var result = await ValidateReportAsync(organizationId, employerId, reportId, normalizedStage, db, protector, false, ct);
        if (result is null) return Results.NotFound();
        await AppendEmployerInterfacePreflightAsync(result, normalizedStage, employerInterfaceExporter, ct);
        return Results.Ok(ToResponse(result));
    }

    private static async Task<IResult> CommitValidationAsync(Guid organizationId, Guid employerId, Guid reportId,
        string? stage, IAlphaDbContext db, OrganizationAccessService access,
        EmployerInterface006ExportService employerInterfaceExporter, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var normalizedStage = NormalizeStage(stage);
        if (normalizedStage == ValidationStage.Final)
            await CorrectionWorkflowService.SyncCurrentCorrectionReferencesAsync(reportId, db, ct);
        var result = await ValidateReportAsync(organizationId, employerId, reportId, normalizedStage, db, protector, true, ct);
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

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new
            {
                error = "report_changed_during_validation",
                detail = "The report changed while validation was running. Reload the latest draft and validate again."
            });
        }
        return Results.Ok(ToResponse(result));
    }

    private static async Task<ValidationContext?> ValidateReportAsync(Guid organizationId, Guid employerId, Guid reportId,
        ValidationStage stage, IAlphaDbContext db, IDataProtectionService protector, bool tracked, CancellationToken ct)
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

        if (stage == ValidationStage.Final
            && report.ReportKind == ManualReportKind.Current
            && report.SourceReportId.HasValue)
        {
            var correctionSource = await db.ManualReports.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == report.SourceReportId.Value, ct);
            if (correctionSource?.ReportKind == ManualReportKind.Negative)
            {
                if (correctionSource.Status is not (ManualReportStatus.Sent or ManualReportStatus.Completed))
                {
                    issues.Add(new("CORRECTION_NEGATIVE_NOT_SENT",
                        "הדיווח השלילי המקדים עדיין לא נשלח. יש להשלים קודם את שלב הביטול (פעולה 6).",
                        ValidationScope.Report));
                }
                else
                {
                    var correctionEmployeeIds = employees.Select(x => x.Id).ToArray();
                    var correctionProductIds = await db.ManualReportProducts.AsNoTracking()
                        .Where(x => correctionEmployeeIds.Contains(x.ReportEmployeeId))
                        .Select(x => x.Id)
                        .ToArrayAsync(ct);
                    var waitingForPreviousReference = await db.EmployerInterfaceReportProductData.AsNoTracking()
                        .AnyAsync(x => correctionProductIds.Contains(x.ReportProductId)
                            && (x.OperationCode == 2 || x.OperationCode == 3)
                            && string.IsNullOrEmpty(x.PreviousIdentifier)
                            && string.IsNullOrEmpty(x.PreviousClearingIdentifier)
                            && !x.PreviousReferenceExceptionCode.HasValue, ct);
                    if (waitingForPreviousReference)
                    {
                        issues.Add(new("CORRECTION_PREVIOUS_REFERENCE_PENDING",
                            "הדיווח השוטף המתקן ממתין למזהה דיווח קודם, מזהה מסלקה קודם או חריג רשמי לפי ממשק מעסיקים 006.",
                            ValidationScope.Report));
                    }
                }
            }
        }

        var productsByEmployee = products.GroupBy(x => x.ReportEmployeeId).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var employee in employees)
        {
            var employeeProducts = productsByEmployee.GetValueOrDefault(employee.Id) ?? [];
            var employeeName = $"{employee.FirstName} {employee.LastName}".Trim();

            if (string.IsNullOrWhiteSpace(employee.NationalId))
                issues.Add(new("NATIONAL_ID_REQUIRED", $"לעובד {employeeName} חסרה תעודת זהות.", ValidationScope.Employee, employee.Id));
            if (employee.InterfaceIdentifierType == 1)
            {
                var interfaceIdentifier = protector.Unprotect(employee.InterfaceIdentifier, $"report-employee-interface-id:{employee.Id}");
                if (!ApiInputValidation.IsIsraeliId(interfaceIdentifier))
                    issues.Add(new("SUG_SHGIHA_62", $"קוד שגיאה 62: תעודת הזהות של {employeeName} אינה עוברת בדיקת ספרת ביקורת.", ValidationScope.Employee, employee.Id));
            }
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
                if (product.ProductType != PensionProductType.Other && string.IsNullOrWhiteSpace(product.FundExternalKey))
                    issues.Add(new("FUND_REQUIRED", $"למוצר {ProductLabel(product)} של {employeeName} לא נבחרה קופה.", ValidationScope.Product, employee.Id, product.Id));
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
                product.Section14Code,
                product.FundExternalKey,
                product.FundCode,
                product.FundName,
                product.FundCompanyName,
                product.FundClassification,
                product.SalaryAllocationType,
                product.SalaryAllocationValue,
                product.AllocationOrder,
                contributions.Where(x => x.ReportProductId == product.Id && x.Party == ContributionParty.Employer)
                    .Select(x => new ManualContributionInput(x.Component, x.Amount, x.Percentage, x.ExemptPayments)).ToArray(),
                contributions.Where(x => x.ReportProductId == product.Id && x.Party == ContributionParty.Employee)
                    .Select(x => new ManualContributionInput(x.Component, x.Amount, x.Percentage, x.ExemptPayments)).ToArray()
            )).ToArray();

            foreach (var error in ApiInputValidation.Products(inputs, limits, enforcePolicyPercentageLimits: true))
                issues.Add(new("PRODUCT_VALIDATION", $"{employeeName}: {error}", ValidationScope.Contribution, employee.Id));
        }

        if (stage == ValidationStage.Final && products.Count > 0)
            await AppendPreventableHistoryAndCorrectionIssuesAsync(report, employees, products, contributions, db, issues, ct);

        if ((stage is ValidationStage.Deposits or ValidationStage.Final) && products.Count > 0)
        {
            var payments = await db.ManualReportPayments.AsNoTracking()
                .Where(x => productIds.Contains(x.ReportProductId)).ToDictionaryAsync(x => x.ReportProductId, ct);
            var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
                .Where(x => productIds.Contains(x.ReportProductId)).ToDictionaryAsync(x => x.ReportProductId, ct);
            var employeeById = employees.ToDictionary(x => x.Id);
            foreach (var product in products)
            {
                var employee = employeeById[product.ReportEmployeeId];
                var employeeName = $"{employee.FirstName} {employee.LastName}".Trim();
                metadata.TryGetValue(product.Id, out var productMetadata);
                var negativeCancellationWithoutRefund = report.ReportKind == ManualReportKind.Negative
                    && productMetadata?.OperationCode == 6;

                if (!payments.TryGetValue(product.Id, out var payment))
                {
                    // In the normal managed-debit flow there is intentionally no per-product payment row.
                    // The report-level pension payment account + active mandate are the source of truth and
                    // Employer Interface 006 emits method 6 with its prescribed zero/reference defaults.
                    var paymentMandate = report.PaymentAccountId.HasValue
                        ? await db.BankDebitMandates.AsNoTracking()
                            .FirstOrDefaultAsync(x => x.EmployerPaymentAccountId == report.PaymentAccountId.Value, ct)
                        : null;
                    var hasActiveMandate = paymentMandate?.IsActive == true;
                    var managedDebit = report.ReportKind == ManualReportKind.Current
                        && report.PaymentAccountId.HasValue
                        && hasActiveMandate;
                    if (!negativeCancellationWithoutRefund && !managedDebit)
                        issues.Add(new("PAYMENT_REQUIRED", $"חסרים פרטי אמצעי תשלום עבור {employeeName}, פוליסה {product.PolicyNumber}.", ValidationScope.Payment, employee.Id, product.Id));
                    continue;
                }

                if (negativeCancellationWithoutRefund)
                    continue;

                var request = new SaveManualReportPaymentRequest(payment.ProviderName, payment.ProviderAccount,
                    payment.PaymentMethod, payment.ValueDate, payment.ReferenceNumber, payment.EmployerBankName,
                    payment.EmployerBankCode, payment.EmployerBranch, payment.EmployerAccount, payment.ConfirmationFileName,
                    payment.TrustAccountValueDate, payment.ActualDepositAmount, payment.MasavSenderCode);
                var totalDeposit = contributions.Where(x => x.ReportProductId == product.Id).Sum(x => x.Amount);
                foreach (var error in ApiInputValidation.Payment(request, totalDeposit,
                    productMetadata?.OperationCode, productMetadata?.EmployerAccountType, productMetadata?.ReceiverAccountType))
                    issues.Add(new("PAYMENT_VALIDATION", $"{employeeName}, פוליסה {product.PolicyNumber}: {error}", ValidationScope.Payment, employee.Id, product.Id));
            }
        }

        return new ValidationContext(report, employees, products, productsByEmployee, issues);
    }

    private static async Task AppendPreventableHistoryAndCorrectionIssuesAsync(
        ManualReport report,
        IReadOnlyCollection<ManualReportEmployee> employees,
        IReadOnlyCollection<ManualReportProduct> products,
        IReadOnlyCollection<ManualContribution> contributions,
        IAlphaDbContext db,
        List<ValidationIssue> issues,
        CancellationToken ct)
    {
        var immutableStatuses = new[]
        {
            ManualReportStatus.Submitted, ManualReportStatus.Processing,
            ManualReportStatus.Sent, ManualReportStatus.Completed
        };

        var employmentIds = employees.Select(x => x.EmploymentId).Distinct().ToArray();
        var historicalRows = await (
            from historicalReport in db.ManualReports.AsNoTracking()
            join historicalEmployee in db.ManualReportEmployees.AsNoTracking()
                on historicalReport.Id equals historicalEmployee.ReportId
            join historicalProduct in db.ManualReportProducts.AsNoTracking()
                on historicalEmployee.Id equals historicalProduct.ReportEmployeeId
            where historicalReport.Id != report.Id
                && historicalReport.OrganizationId == report.OrganizationId
                && historicalReport.EmployerId == report.EmployerId
                && immutableStatuses.Contains(historicalReport.Status)
                && employmentIds.Contains(historicalEmployee.EmploymentId)
                && !historicalReport.IsTechnicalCorrectionDocument
            select new
            {
                historicalEmployee.EmploymentId,
                historicalProduct.Id,
                historicalProduct.ProductType,
                historicalProduct.PolicyNumber,
                historicalProduct.FundExternalKey,
                historicalProduct.FundCode,
                historicalProduct.SalaryMonth
            }).ToListAsync(ct);

        var employeeById = employees.ToDictionary(x => x.Id);
        foreach (var product in products)
        {
            var employee = employeeById[product.ReportEmployeeId];
            var duplicates = historicalRows.Where(x =>
                x.EmploymentId == employee.EmploymentId
                && x.SalaryMonth.Year == product.SalaryMonth.Year
                && x.SalaryMonth.Month == product.SalaryMonth.Month
                && SameProductIdentity(x.ProductType, x.PolicyNumber, x.FundExternalKey, x.FundCode, product)).ToArray();
            if (duplicates.Length > 0)
            {
                issues.Add(new("SUG_SHGIHA_28",
                    $"קוד שגיאה 28: חודש השכר {product.SalaryMonth:MM/yyyy} כבר דווח בעבר עבור {employee.FirstName} {employee.LastName} והמוצר {ProductLabel(product)}. אם זו התאמה לדיווח קודם יש להשתמש במסלול דיווח מתקן.",
                    ValidationScope.Product, employee.Id, product.Id));

                var currentContributions = contributions.Where(x => x.ReportProductId == product.Id)
                    .Select(x => (x.Party, x.Component, x.Amount, x.Percentage, x.ExemptPayments))
                    .OrderBy(x => x.Party).ThenBy(x => x.Component).ToArray();
                var historicalProductIds = duplicates.Select(x => x.Id).ToArray();
                var historicalContributions = await db.ManualContributions.AsNoTracking()
                    .Where(x => historicalProductIds.Contains(x.ReportProductId)).ToListAsync(ct);
                if (duplicates.Any(d =>
                    historicalContributions.Where(x => x.ReportProductId == d.Id)
                        .Select(x => (x.Party, x.Component, x.Amount, x.Percentage, x.ExemptPayments))
                        .OrderBy(x => x.Party).ThenBy(x => x.Component)
                        .SequenceEqual(currentContributions)))
                {
                    issues.Add(new("SUG_SHGIHA_43",
                        $"קוד שגיאה 43: נמצאה תנועה זהה שכבר נשלחה עבור {employee.FirstName} {employee.LastName} והמוצר {ProductLabel(product)}.",
                        ValidationScope.Contribution, employee.Id, product.Id));
                }
            }
        }

        var productIds = products.Select(x => x.Id).ToArray();
        var currentMetadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var explicitTransferIds = currentMetadata
            .Where(x => !string.IsNullOrWhiteSpace(x.InterfaceTransferIdentifier))
            .Select(x => x.InterfaceTransferIdentifier.Trim().ToUpperInvariant()).Distinct().ToArray();
        if (explicitTransferIds.Length > 0)
        {
            var historicalTransferIds = await (
                from metadata in db.EmployerInterfaceReportProductData.AsNoTracking()
                join historicalProduct in db.ManualReportProducts.AsNoTracking()
                    on metadata.ReportProductId equals historicalProduct.Id
                join historicalEmployee in db.ManualReportEmployees.AsNoTracking()
                    on historicalProduct.ReportEmployeeId equals historicalEmployee.Id
                join historicalReport in db.ManualReports.AsNoTracking()
                    on historicalEmployee.ReportId equals historicalReport.Id
                where historicalReport.Id != report.Id
                    && historicalReport.OrganizationId == report.OrganizationId
                    && historicalReport.EmployerId == report.EmployerId
                    && immutableStatuses.Contains(historicalReport.Status)
                    && !string.IsNullOrEmpty(metadata.InterfaceTransferIdentifier)
                select metadata.InterfaceTransferIdentifier
            ).ToListAsync(ct);

            var reused = explicitTransferIds.FirstOrDefault(x =>
                historicalTransferIds.Any(h => string.Equals(h, x, StringComparison.OrdinalIgnoreCase)));
            if (reused is not null)
                issues.Add(new("SUG_SHGIHA_50",
                    $"קוד שגיאה 50: מספר זיהוי העברת הכספים {reused} כבר שימש בדיווח קודם. יש להפיק מזהה העברה חדש.",
                    ValidationScope.Payment));
        }

        var operationByProduct = currentMetadata.ToDictionary(x => x.ReportProductId, x => x.OperationCode);
        if (report.ReportKind == ManualReportKind.Current
            && operationByProduct.Values.Any(x => x is 2 or 3))
        {
            var source = report.SourceReportId.HasValue
                ? await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == report.SourceReportId.Value, ct)
                : null;
            if (!report.ExternalSourceReference
                && (source?.ReportKind != ManualReportKind.Negative
                    || source.Status is not (ManualReportStatus.Sent or ManualReportStatus.Completed)))
                issues.Add(new("SUG_SHGIHA_100",
                    "קוד שגיאה 100: פעולת תיקון 2/3 בשוטף מחייבת דיווח שלילי מקדים בפעולה 6 שנשלח בהצלחה.",
                    ValidationScope.Report));
        }

        if (report.ReportKind == ManualReportKind.Negative
            && operationByProduct.Values.Any(x => x == 6)
            && report.IsTechnicalCorrectionDocument
            && report.CorrectionWorkspaceId.HasValue)
        {
            var workspaceId = report.CorrectionWorkspaceId.Value;
            var workspaceEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
                .Where(x => x.ReportId == workspaceId).Select(x => x.Id).ToArrayAsync(ct);
            var workspaceHasChangedProduct = await db.ManualReportProducts.AsNoTracking()
                .AnyAsync(x => workspaceEmployeeIds.Contains(x.ReportEmployeeId)
                    && x.SourceReportProductId.HasValue && x.IsCorrectionChanged, ct);
            if (workspaceHasChangedProduct)
            {
                var hasCurrentSibling = await db.ManualReports.AsNoTracking().AnyAsync(x =>
                    x.CorrectionWorkspaceId == workspaceId
                    && x.IsTechnicalCorrectionDocument
                    && x.ReportKind == ManualReportKind.Current, ct);
                if (!hasCurrentSibling)
                    issues.Add(new("SUG_SHGIHA_101",
                        "קוד שגיאה 101: פעולת ביטול 6 עבור שינוי מחייבת גם דיווח שוטף מתקן בפעולה 2/3.",
                        ValidationScope.Report));
            }
        }
    }

    private static bool SameProductIdentity(PensionProductType productType, string policyNumber,
        string fundExternalKey, string fundCode, ManualReportProduct current)
    {
        if (productType != current.ProductType) return false;
        if (!string.IsNullOrWhiteSpace(policyNumber) && !string.IsNullOrWhiteSpace(current.PolicyNumber))
            return string.Equals(policyNumber.Trim(), current.PolicyNumber.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(fundExternalKey) && !string.IsNullOrWhiteSpace(current.FundExternalKey))
            return string.Equals(fundExternalKey.Trim(), current.FundExternalKey.Trim(), StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(fundCode) && !string.IsNullOrWhiteSpace(current.FundCode)
            && string.Equals(fundCode.Trim(), current.FundCode.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task AppendEmployerInterfacePreflightAsync(
        ValidationContext result,
        ValidationStage stage,
        EmployerInterface006ExportService exporter,
        CancellationToken ct)
    {
        if (stage != ValidationStage.Final || result.Issues.Count > 0)
            return;

        if (result.Report.ReportKind == ManualReportKind.Differences)
        {
            result.Issues.Add(new("DIFFERENCE_REPORT_NOT_TRANSMITTABLE",
                "דיווח הפרשים הוא טיוטת עבודה ואינו ממשק 006 עצמאי לשידור. יש לממש את ההפרש כדיווח שוטף מתקן או כדיווח שלילי לפני שליחה.",
                ValidationScope.Report));
            return;
        }

        var generated = await exporter.ExportAsync(result.Report, ct);

        // FEDBKA preflight validates a transmission package. If export failed before a
        // payload/name was produced (for example, missing sender identity), report the
        // exporter error only instead of fabricating filename/empty-file FEDBKA findings.
        var hasTransmissionPackage = generated.Bytes is { Length: > 0 }
            && !string.IsNullOrWhiteSpace(generated.PayloadFileName);
        if (hasTransmissionPackage)
        {
            foreach (var finding in EmployerInterface006ClearinghousePreflight.Validate(generated))
                result.Issues.Add(new($"FEDBKA_{finding.FedbkaCode}", finding.Message, ValidationScope.Report));
        }

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
