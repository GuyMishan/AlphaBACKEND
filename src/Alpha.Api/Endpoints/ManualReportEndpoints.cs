using Alpha.Api.Security;
using Alpha.Api.Services;
using Alpha.Domain.Auditing;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Reporting;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Alpha.Infrastructure.Persistence;
using System.Data;

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
        group.MapDelete("/{reportId:guid}", DeleteDraftAsync);
        group.MapPut("/{reportId:guid}/details", UpdateDetailsAsync);
        group.MapPut("/{reportId:guid}/payment-account", UpdatePaymentAccountAsync);
        group.MapPut("/{reportId:guid}/selection", SyncSelectionAsync);
        group.MapGet("/{reportId:guid}/employees", GetEmployeesAsync);
        group.MapGet("/{reportId:guid}/employees/{reportEmployeeId:guid}", GetEmployeeAsync);
        group.MapPut("/{reportId:guid}/employees/{reportEmployeeId:guid}", SaveEmployeeAsync);
        group.MapGet("/{reportId:guid}/deposits", GetDepositsAsync);
        group.MapPut("/{reportId:guid}/deposits/{reportProductId:guid}", SaveDepositPaymentAsync);
        return endpoints;
    }

    private static async Task<IResult> GetOpenReportsAsync(Guid organizationId, Guid employerId,
        string? search, int skip, int take, IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        skip = Math.Max(0, skip);
        take = Math.Clamp(take == 0 ? 30 : take, 1, 100);

        var query = db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                && !x.IsCorrectionWorkspace
                && (x.Status == ManualReportStatus.Draft || x.Status == ManualReportStatus.ReadyForValidation
                    || x.Status == ManualReportStatus.Error));

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
                x.ReportKind,
                x.SourceReportId,
                x.IsCorrectionWorkspace,
                x.HasCorrectionChanges,
                x.PaymentAccountId,
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
        ReportPaymentAccountService paymentAccounts, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.EmploymentIds.Count > MaxEmployeesPerDraft)
            return Results.BadRequest(new { error = $"Manual reports are limited to {MaxEmployeesPerDraft} employees per draft." });
        var employer = await db.Employers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (employer is null) return Results.NotFound();
        var profileSettings = await db.EmployerProfileSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.EmployerId == employerId, ct);

        var report = new ManualReport(organizationId, employerId, request.ReportingMonth, request.SalaryPaymentDate);
        report.SetEmployerInterfaceSnapshot(employer.LegalName, employer.RegistrationNumber, employer.WithholdingFileNumber,
            employer.ContactFirstName, employer.ContactLastName, employer.ContactPhone, employer.ContactEmail,
            employer.ContactMobile, profileSettings?.DefaultDepositorTypeCode ?? 1,
            profileSettings?.DefaultEmployerIdentifierTypeCode ?? 1);
        report.SetProtectedEmployerSnapshot(
            protector.Protect(report.EmployerRegistrationNumberSnapshot, $"report-employer-registration:{report.Id}"),
            protector.Protect(report.EmployerWithholdingFileNumberSnapshot, $"report-employer-withholding:{report.Id}"),
            protector.Protect(report.EmployerContactPhoneSnapshot, $"report-employer-phone:{report.Id}"),
            protector.Protect(report.EmployerContactEmailSnapshot, $"report-employer-email:{report.Id}"),
            protector.Protect(report.EmployerContactMobileSnapshot, $"report-employer-mobile:{report.Id}"));
        var paymentAccount = await paymentAccounts.ResolveForReportAsync(employerId, request.PaymentAccountId, ct);
        if (paymentAccount is null) return Results.Conflict(new { error = "payment_account_required" });
        await paymentAccounts.ApplySnapshotAsync(report, paymentAccount, protector.Unprotect(paymentAccount.AccountNumberEncrypted ?? throw new InvalidOperationException("Encrypted account number missing."), "bank-account-number"), ct);
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
                    item.Employment.Id, item.Person.Id, protector.Unprotect(item.Person.NationalIdEncrypted ?? throw new InvalidOperationException("Encrypted national ID missing."), "person-national-id"), item.Person.FirstName,
                    item.Person.LastName, item.Employment.EmployeeNumber, item.Employment.MonthlySalary);
                reportEmployee.SetInterfaceSnapshot((int)item.Person.IdentifierType, protector.Unprotect(item.Person.NationalIdEncrypted ?? throw new InvalidOperationException("Encrypted national ID missing."), "person-national-id"), item.Person.BirthDate,
                    item.Person.Gender.HasValue ? (int)item.Person.Gender.Value : null, item.Person.Email, item.Person.Mobile,
                    item.Person.City, item.Person.Street, item.Person.HouseNumber, item.Person.Apartment,
                    item.Person.PostalCode, item.Person.PostOfficeBox, item.Employment.StartDate);
                reportEmployee.SetProtectedIdentifiers(
                    protector.Protect(reportEmployee.NationalId, $"report-employee-national-id:{reportEmployee.Id}"),
                    protector.LookupHash(reportEmployee.NationalId, "report-employee-national-id-lookup"),
                    protector.Protect(reportEmployee.InterfaceIdentifier, $"report-employee-interface-id:{reportEmployee.Id}"));
                reportEmployee.SetProtectedContactSnapshot(
                    protector.Protect(reportEmployee.EmailSnapshot, $"report-employee-email:{reportEmployee.Id}"),
                    protector.Protect(reportEmployee.MobileSnapshot, $"report-employee-mobile:{reportEmployee.Id}"));
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

    private static async Task<IResult> DeleteDraftAsync(Guid organizationId, Guid employerId, Guid reportId,
        IAlphaDbContext db, OrganizationAccessService access, ICurrentUser currentUser,
        PaymentEvidenceStorage evidenceStorage, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();

        var report = await db.ManualReports.SingleOrDefaultAsync(x =>
            x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (!report.IsEditable)
            return Results.Conflict(new { error = "report_delete_requires_unsent_draft" });

        var hasExternalHistory =
            await db.ReportTransmissions.AsNoTracking().AnyAsync(x => x.ReportId == reportId, ct)
            || await db.EmployerInterfaceFeedback.AsNoTracking().AnyAsync(x => x.ReportId == reportId, ct);
        if (hasExternalHistory)
            return Results.Conflict(new { error = "report_has_external_history" });

        if (await db.ManualReports.AsNoTracking().AnyAsync(x => x.SourceReportId == reportId, ct))
            return Results.Conflict(new { error = "report_has_derived_versions" });

        var confirmations = await db.PaymentConfirmations
            .Where(x => x.ReportId == reportId).ToListAsync(ct);
        foreach (var confirmation in confirmations)
            await evidenceStorage.DeleteOrphanAsync(confirmation.StoragePath, ct);
        if (confirmations.Count > 0)
            db.PaymentConfirmations.RemoveRange(confirmations);

        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "manual-report.deleted",
            nameof(ManualReport),
            report.Id,
            organizationId,
            employerId,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                report.ReportingMonth,
                report.ReportKind,
                report.SourceReportId,
                report.IsCorrectionWorkspace
            }),
            http.TraceIdentifier));

        db.ManualReports.Remove(report);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
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
            report.ReportKind, report.SourceReportId, report.IsCorrectionWorkspace, report.HasCorrectionChanges,
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
        if (report.IsCorrectionWorkspace) report.MarkCorrectionChanged();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdatePaymentAccountAsync(Guid organizationId, Guid employerId, Guid reportId,
        UpdateReportPaymentAccountRequest request, IAlphaDbContext db, OrganizationAccessService access,
        ReportPaymentAccountService paymentAccounts, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.SingleOrDefaultAsync(x => x.Id == reportId &&
            x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (!report.IsEditable) return Results.Conflict(new { error = "report_not_editable" });

        var account = await paymentAccounts.ResolveForReportAsync(employerId, request.PaymentAccountId, ct);
        if (account is null) return Results.Conflict(new { error = "payment_account_required" });
        await paymentAccounts.ApplySnapshotAsync(report, account, protector.Unprotect(account.AccountNumberEncrypted ?? throw new InvalidOperationException("Encrypted account number missing."), "bank-account-number"), ct);
        if (report.IsCorrectionWorkspace) report.MarkCorrectionChanged();
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            report.PaymentAccountId,
            report.PaymentBankId,
            report.PaymentBranchId,
            report.PaymentAccountNumberMasked,
            report.PaymentMandateReference
        });
    }

    private static async Task<IResult> SyncSelectionAsync(Guid organizationId, Guid employerId, Guid reportId,
        UpdateManualReportSelectionRequest request, IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.EmploymentIds.Count > MaxEmployeesPerDraft)
            return Results.BadRequest(new { error = $"Manual reports are limited to {MaxEmployeesPerDraft} employees per draft." });
        var report = await db.ManualReports.SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (!report.IsEditable) return Results.Conflict(new { error = "report_not_editable" });

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
                    item.Employment.Id, item.Person.Id, protector.Unprotect(item.Person.NationalIdEncrypted ?? throw new InvalidOperationException("Encrypted national ID missing."), "person-national-id"), item.Person.FirstName,
                    item.Person.LastName, item.Employment.EmployeeNumber, item.Employment.MonthlySalary);
                reportEmployee.SetInterfaceSnapshot((int)item.Person.IdentifierType, protector.Unprotect(item.Person.NationalIdEncrypted ?? throw new InvalidOperationException("Encrypted national ID missing."), "person-national-id"), item.Person.BirthDate,
                    item.Person.Gender.HasValue ? (int)item.Person.Gender.Value : null, item.Person.Email, item.Person.Mobile,
                    item.Person.City, item.Person.Street, item.Person.HouseNumber, item.Person.Apartment,
                    item.Person.PostalCode, item.Person.PostOfficeBox, item.Employment.StartDate);
                reportEmployee.SetProtectedIdentifiers(
                    protector.Protect(reportEmployee.NationalId, $"report-employee-national-id:{reportEmployee.Id}"),
                    protector.LookupHash(reportEmployee.NationalId, "report-employee-national-id-lookup"),
                    protector.Protect(reportEmployee.InterfaceIdentifier, $"report-employee-interface-id:{reportEmployee.Id}"));
                reportEmployee.SetProtectedContactSnapshot(
                    protector.Protect(reportEmployee.EmailSnapshot, $"report-employee-email:{reportEmployee.Id}"),
                    protector.Protect(reportEmployee.MobileSnapshot, $"report-employee-mobile:{reportEmployee.Id}"));
                db.ManualReportEmployees.Add(reportEmployee);
                await SeedProductsFromMixAsync(db, reportEmployee, report.ReportingMonth, ct);
            }
        }
        if (report.IsCorrectionWorkspace) report.MarkCorrectionChanged();
        else report.MarkDirty();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetEmployeesAsync(Guid organizationId, Guid employerId, Guid reportId,
        string? search, int skip, int take, IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
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
            var idHash = protector.LookupHash(search.Trim(), "report-employee-national-id-lookup");
            query = query.Where(x => x.FirstName.ToLower().Contains(term) || x.LastName.ToLower().Contains(term) || x.NationalIdLookupHash == idHash || x.EmployeeNumber.Contains(term));
        }
        var page = await query.OrderBy(x => x.LastName).ThenBy(x => x.FirstName).Skip(skip).Take(take + 1).ToListAsync(ct);
        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var ids = page.Select(x => x.Id).ToArray();
        var productCounts = await db.ManualReportProducts.AsNoTracking().Where(x => ids.Contains(x.ReportEmployeeId))
            .GroupBy(x => x.ReportEmployeeId).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var items = page.Select(x => new
        {
            x.Id, x.EmploymentId, x.PersonId, NationalId = protector.Unprotect(x.NationalId, $"report-employee-national-id:{x.Id}"), x.FirstName, x.LastName, x.EmployeeNumber, x.MonthlySalary,
            productCount = productCounts.GetValueOrDefault(x.Id),
            validationStatus = productCounts.GetValueOrDefault(x.Id) > 0 && x.MonthlySalary > 0 ? "ready" : "missing-products"
        });
        return Results.Ok(new { items, hasMore });
    }

    private static async Task<IResult> GetDepositsAsync(Guid organizationId, Guid employerId, Guid reportId,
        string? search, Guid? reportProductId, int skip, int take, AlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        skip = Math.Max(0, skip);
        take = Math.Clamp(take == 0 ? 50 : take, 1, 100);
        var query = from product in db.ManualReportProducts.AsNoTracking()
                    join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                    where employee.ReportId == reportId
                    select new { Product = product, Employee = employee };

        if (reportProductId.HasValue)
            query = query.Where(x => x.Product.Id == reportProductId.Value);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(x => x.Employee.FirstName.ToLower().Contains(term)
                || x.Employee.LastName.ToLower().Contains(term)
                || x.Employee.NationalIdLookupHash == protector.LookupHash(search.Trim(), "report-employee-national-id-lookup")
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
        // A payment row may not exist before the editor is first opened. Use the canonical
        // fund reference data for the table and modal alike, without forcing a save per row.
        var referenceAccounts = new Dictionary<string, string>(StringComparer.Ordinal);
        var fundKeys = page.Select(x => x.Product.FundExternalKey)
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if (fundKeys.Length > 0)
        {
            var connection = db.Database.GetDbConnection();
            var openedHere = connection.State != ConnectionState.Open;
            if (openedHere) await connection.OpenAsync(ct);
            try
            {
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT external_key, bank_code::text, branch_code::text, account_number
                    FROM reference_data.pension_products WHERE external_key = ANY(@keys)
                    """;
                var parameter = command.CreateParameter();
                parameter.ParameterName = "keys";
                parameter.Value = fundKeys;
                command.Parameters.Add(parameter);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var key = reader.GetString(0);
                    if (referenceAccounts.ContainsKey(key) || reader.IsDBNull(3)) continue;
                    var parts = new[] {
                        reader.IsDBNull(1) ? "" : reader.GetString(1),
                        reader.IsDBNull(2) ? "" : reader.GetString(2),
                        reader.GetString(3)
                    }.Where(x => !string.IsNullOrWhiteSpace(x));
                    referenceAccounts[key] = string.Join(" - ", parts);
                }
            }
            finally { if (openedHere) await connection.CloseAsync(); }
        }
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);
        var employments = await db.Employments.AsNoTracking()
            .Where(x => page.Select(p => p.Employee.EmploymentId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, ct);

        var items = page.Select(x =>
        {
            payments.TryGetValue(x.Product.Id, out var payment);
            metadata.TryGetValue(x.Product.Id, out var productMetadata);
            employments.TryGetValue(x.Employee.EmploymentId, out var employment);
            var monthEnd = report.ReportingMonth.AddMonths(1).AddDays(-1);
            var endedByMonth = employment?.Status == Alpha.Domain.Employees.EmploymentStatus.Ended && employment.EndDate.HasValue && employment.EndDate.Value <= monthEnd;
            var startedInMonth = x.Employee.EmploymentStartDateSnapshot.HasValue && x.Employee.EmploymentStartDateSnapshot.Value.Year == report.ReportingMonth.Year && x.Employee.EmploymentStartDateSnapshot.Value.Month == report.ReportingMonth.Month;
            var automaticDebit = report.ReportKind != ManualReportKind.Negative && report.ReportKind != ManualReportKind.Differences && !string.IsNullOrWhiteSpace(report.PaymentMandateReference);
            return new
            {
                id = x.Product.Id,
                reportEmployeeId = x.Employee.Id,
                x.Employee.EmploymentId,
                employeeName = x.Employee.FirstName + " " + x.Employee.LastName,
                NationalId = protector.Unprotect(x.Employee.NationalId, $"report-employee-national-id:{x.Employee.Id}"),
                x.Employee.MonthlySalary,
                x.Product.ProductType,
                x.Product.PolicyNumber,
                x.Product.FundExternalKey,
                x.Product.FundCode,
                x.Product.FundName,
                x.Product.FundCompanyName,
                x.Product.FundClassification,
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
                providerAccount = !string.IsNullOrWhiteSpace(payment?.ProviderAccount) ? payment.ProviderAccount : referenceAccounts.GetValueOrDefault(x.Product.FundExternalKey) ?? string.Empty,
                paymentMethod = payment?.PaymentMethod ?? "העברה בנקאית",
                valueDate = payment?.ValueDate,
                trustAccountValueDate = payment?.TrustAccountValueDate,
                actualDepositAmount = payment?.ActualDepositAmount,
                masavSenderCode = payment?.MasavSenderCode ?? string.Empty,
                referenceNumber = payment?.ReferenceNumber ?? string.Empty,
                employerBankName = payment?.EmployerBankName ?? string.Empty,
                employerBankCode = payment?.EmployerBankCode ?? string.Empty,
                employerBranch = payment?.EmployerBranch ?? string.Empty,
                employerAccount = payment is null ? string.Empty : protector.Unprotect(payment.EmployerAccount, $"report-payment-account:{payment.ReportProductId}"),
                confirmationFileName = payment?.ConfirmationFileName ?? string.Empty,
                operationCode = productMetadata?.OperationCode ?? (report.ReportKind == ManualReportKind.Current ? 1 : null),
                depositStatus = productMetadata?.DepositStatus ?? (report.ReportKind == ManualReportKind.Current ? 1 : null),
                employeeStatus = productMetadata?.EmployeeStatus ?? (report.ReportKind == ManualReportKind.Current ? (startedInMonth ? 14 : endedByMonth ? 2 : 1) : null),
                statusStartDate = productMetadata?.StatusStartDate ?? (report.ReportKind == ManualReportKind.Current ? (endedByMonth ? employment!.EndDate : x.Employee.EmploymentStartDateSnapshot ?? report.ReportingMonth) : null),
                lastDeposit = productMetadata?.LastDeposit ?? (report.ReportKind == ManualReportKind.Current ? (endedByMonth ? 1 : 2) : null),
                paymentMethodCode = productMetadata?.PaymentMethodCode ?? (automaticDebit ? 6 : null),
                employerAccountType = productMetadata?.EmployerAccountType ?? (automaticDebit ? 1 : null),
                receiverAccountType = productMetadata?.ReceiverAccountType ?? (automaticDebit ? 1 : null),
                requiresCompletion = report.ReportKind == ManualReportKind.Negative || (x.Product.ProductType == PensionProductType.PensionFund && x.Product.FundClassification.Contains("ותיק") && productMetadata?.OldPensionTypeCode == null)
            };
        });
        return Results.Ok(new { items, hasMore });
    }

    private static async Task<IResult> SaveDepositPaymentAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportProductId, SaveManualReportPaymentRequest request, IAlphaDbContext db,
        OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.SingleOrDefaultAsync(x => x.Id == reportId
            && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (!report.IsEditable) return Results.Conflict(new { error = "report_not_editable" });

        var product = await (from reportProduct in db.ManualReportProducts
                             join employee in db.ManualReportEmployees.AsNoTracking() on reportProduct.ReportEmployeeId equals employee.Id
                             where reportProduct.Id == reportProductId && employee.ReportId == reportId
                                 && employee.OrganizationId == organizationId && employee.EmployerId == employerId
                             select reportProduct).SingleOrDefaultAsync(ct);
        if (product is null) return Results.NotFound();

        var payment = await db.ManualReportPayments.SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        if (payment is null)
        {
            payment = new ManualReportPayment(reportProductId);
            db.ManualReportPayments.Add(payment);
        }
        payment.Update(request.ProviderName, request.ProviderAccount, request.PaymentMethod, request.ValueDate,
            request.TrustAccountValueDate, request.ReferenceNumber, request.EmployerBankName, request.EmployerBankCode,
            request.EmployerBranch, protector.Protect(request.EmployerAccount ?? string.Empty, $"report-payment-account:{reportProductId}"), request.ConfirmationFileName,
            request.ActualDepositAmount, request.MasavSenderCode);
        if (report.IsCorrectionWorkspace)
        {
            product.MarkCorrectionChanged(request.ActualDepositAmount.GetValueOrDefault() > 0 ? 3 : 2);
            report.MarkCorrectionChanged();
        }
        else report.MarkDirty();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetEmployeeAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportEmployeeId, IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
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
            employee.Id, employee.EmploymentId, employee.PersonId, NationalId = protector.Unprotect(employee.NationalId, $"report-employee-national-id:{employee.Id}"), employee.FirstName, employee.LastName, employee.EmployeeNumber, employee.MonthlySalary,
            employee.PostalCodeSnapshot, employee.PostOfficeBoxSnapshot,
            products = products.Select(p => new
            {
                p.Id, p.SourceReportProductId, p.IsCorrectionChanged, p.CorrectionOperationCode, p.ProductType, p.PolicyNumber, p.FundExternalKey, p.FundCode, p.FundName, p.FundCompanyName, p.FundClassification,
                p.SalaryMonth, p.Salary, p.SalaryAllocationType, p.SalaryAllocationValue, p.AllocationOrder,
                p.ReportingType, p.SalaryLayer, p.Section14, p.Section14Code, p.Section14StartDate,
                employerContributions = contributions.Where(c => c.ReportProductId == p.Id && c.Party == ContributionParty.Employer).OrderBy(c => c.Component),
                employeeContributions = contributions.Where(c => c.ReportProductId == p.Id && c.Party == ContributionParty.Employee).OrderBy(c => c.Component)
            })
        });
    }

    private static async Task<IResult> SaveEmployeeAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportEmployeeId, SaveManualReportEmployeeRequest request, IAlphaDbContext db,
        OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.Products.Count > MaxProductsPerEmployee)
            return Results.BadRequest(new { error = $"An employee can have up to {MaxProductsPerEmployee} products in a report." });

        var report = await db.ManualReports.SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (!report.IsEditable) return Results.Conflict(new { error = "report_not_editable" });

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
        if (request.Snapshot is not null)
        {
            employee.SetInterfaceSnapshot(request.Snapshot.IdentifierType, request.Snapshot.Identifier,
                request.Snapshot.BirthDate, request.Snapshot.Gender, request.Snapshot.Email, request.Snapshot.Mobile,
                request.Snapshot.City, request.Snapshot.Street, request.Snapshot.HouseNumber, request.Snapshot.Apartment,
                request.Snapshot.PostalCode, request.Snapshot.PostOfficeBox, request.Snapshot.EmploymentStartDate);
            var snapshotIdentifier = request.Snapshot.Identifier.Trim();
            employee.SetProtectedIdentifiers(
                protector.Protect(snapshotIdentifier, $"report-employee-national-id:{employee.Id}"),
                protector.LookupHash(snapshotIdentifier, "report-employee-national-id-lookup"),
                protector.Protect(snapshotIdentifier, $"report-employee-interface-id:{employee.Id}"));
            employee.SetProtectedContactSnapshot(
                protector.Protect(request.Snapshot.Email ?? string.Empty, $"report-employee-email:{employee.Id}"),
                protector.Protect(request.Snapshot.Mobile ?? string.Empty, $"report-employee-mobile:{employee.Id}"));
        }
        var existingProducts = await db.ManualReportProducts
            .Where(x => x.ReportEmployeeId == reportEmployeeId)
            .OrderBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);

        if (!report.IsCorrectionWorkspace)
        {
            var existingProductIds = existingProducts.Select(x => x.Id).ToArray();
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
                    item.AllocationType, item.AllocationValue, item.AllocationOrder, input.Section14Code, input.FundClassification);
                db.ManualReportProducts.Add(product);
                AddContributions(db, product.Id, ContributionParty.Employer, item.InsuredSalary, input.EmployerContributions);
                AddContributions(db, product.Id, ContributionParty.Employee, item.InsuredSalary, input.EmployeeContributions);
            }
        }
        else
        {
            var existingBySource = existingProducts
                .Where(x => x.SourceReportProductId.HasValue)
                .ToDictionary(x => x.SourceReportProductId!.Value);
            var requestedSourceIds = resolvedProducts.Items
                .Select(x => x.Input.SourceReportProductId)
                .Where(x => x.HasValue)
                .Select(x => x!.Value)
                .ToHashSet();

            var sourceProducts = requestedSourceIds.Count == 0
                ? new Dictionary<Guid, ManualReportProduct>()
                : await db.ManualReportProducts.AsNoTracking()
                    .Where(x => requestedSourceIds.Contains(x.Id))
                    .ToDictionaryAsync(x => x.Id, ct);
            var sourceIds = sourceProducts.Keys.ToArray();
            var sourceContributions = sourceIds.Length == 0
                ? new Dictionary<Guid, List<ManualContribution>>()
                : (await db.ManualContributions.AsNoTracking()
                        .Where(x => sourceIds.Contains(x.ReportProductId))
                        .ToListAsync(ct))
                    .GroupBy(x => x.ReportProductId)
                    .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var item in resolvedProducts.Items)
            {
                var input = item.Input;
                if (input.SourceReportProductId.HasValue)
                {
                    if (!existingBySource.TryGetValue(input.SourceReportProductId.Value, out var product))
                        return Results.Conflict(new { error = "correction_product_lineage_missing" });

                    product.Update(input.ProductType, input.PolicyNumber, input.SalaryMonth, item.InsuredSalary,
                        input.ReportingType, input.SalaryLayer, input.Section14, input.Section14StartDate,
                        input.FundExternalKey, input.FundCode, input.FundName, input.FundCompanyName,
                        item.AllocationType, item.AllocationValue, item.AllocationOrder, input.Section14Code, input.FundClassification);

                    await db.ManualContributions.Where(x => x.ReportProductId == product.Id).ExecuteDeleteAsync(ct);
                    AddContributions(db, product.Id, ContributionParty.Employer, item.InsuredSalary, input.EmployerContributions);
                    AddContributions(db, product.Id, ContributionParty.Employee, item.InsuredSalary, input.EmployeeContributions);

                    var changed = !sourceProducts.TryGetValue(input.SourceReportProductId.Value, out var sourceProduct)
                        || ProductChanged(sourceProduct, item, input)
                        || ContributionsChanged(sourceContributions.GetValueOrDefault(input.SourceReportProductId.Value) ?? [], input);
                    product.SetCorrectionState(changed, changed ? product.CorrectionOperationCode ?? 2 : null);
                }
                else
                {
                    var product = new ManualReportProduct(reportEmployeeId, input.ProductType, input.PolicyNumber,
                        input.SalaryMonth, item.InsuredSalary, input.ReportingType, input.SalaryLayer, input.Section14,
                        input.Section14StartDate, input.FundExternalKey, input.FundCode, input.FundName, input.FundCompanyName,
                        item.AllocationType, item.AllocationValue, item.AllocationOrder, input.Section14Code, input.FundClassification);
                    product.SetCorrectionState(true, 2);
                    db.ManualReportProducts.Add(product);
                    AddContributions(db, product.Id, ContributionParty.Employer, item.InsuredSalary, input.EmployerContributions);
                    AddContributions(db, product.Id, ContributionParty.Employee, item.InsuredSalary, input.EmployeeContributions);
                }
            }

            // Products from the immutable source that are no longer present remain in the workspace as
            // explicit removals. Materialization omits them from the follow-up current report while the
            // full negative report still reverses the source report.
            foreach (var removed in existingProducts.Where(x => x.SourceReportProductId.HasValue
                && !requestedSourceIds.Contains(x.SourceReportProductId.Value)))
            {
                removed.SetCorrectionState(true, 2);
                removed.SetValidationResult(false, "המוצר הוסר מטיוטת התיקון.");
            }
        }
        if (report.IsCorrectionWorkspace) report.MarkCorrectionChanged();
        else report.MarkDirty();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static bool ProductChanged(ManualReportProduct source, ResolvedReportProduct resolved, ManualProductInput input) =>
        source.ProductType != input.ProductType
        || !string.Equals(source.PolicyNumber, input.PolicyNumber?.Trim() ?? string.Empty, StringComparison.Ordinal)
        || source.SalaryMonth != new DateOnly(input.SalaryMonth.Year, input.SalaryMonth.Month, 1)
        || source.Salary != resolved.InsuredSalary
        || !string.Equals(source.ReportingType, input.ReportingType?.Trim() ?? string.Empty, StringComparison.Ordinal)
        || !string.Equals(source.SalaryLayer, input.SalaryLayer?.Trim() ?? string.Empty, StringComparison.Ordinal)
        || source.Section14Code != (input.Section14Code ?? source.Section14Code)
        || source.Section14StartDate != input.Section14StartDate
        || !string.Equals(source.FundExternalKey, input.FundExternalKey?.Trim() ?? string.Empty, StringComparison.Ordinal)
        || !string.Equals(source.FundCode, input.FundCode?.Trim() ?? string.Empty, StringComparison.Ordinal)
        || !string.Equals(source.FundName, input.FundName?.Trim() ?? string.Empty, StringComparison.Ordinal)
        || !string.Equals(source.FundCompanyName, input.FundCompanyName?.Trim() ?? string.Empty, StringComparison.Ordinal)
        || source.SalaryAllocationType != resolved.AllocationType
        || source.SalaryAllocationValue != resolved.AllocationValue
        || source.AllocationOrder != resolved.AllocationOrder
        || !string.Equals(source.FundClassification, input.FundClassification?.Trim() ?? string.Empty, StringComparison.Ordinal);

    private static bool ContributionsChanged(IReadOnlyCollection<ManualContribution> source, ManualProductInput input)
    {
        static string Key(ContributionParty party, ContributionComponent component) => $"{(int)party}:{(int)component}";
        var sourceMap = source
            .Where(x => x.Amount != 0 || x.Percentage != 0 || x.ExemptPayments != 0)
            .ToDictionary(x => Key(x.Party, x.Component), x => (x.Amount, x.Percentage, x.ExemptPayments));
        var requested = input.EmployerContributions
            .Select(x => (Party: ContributionParty.Employer, Input: x))
            .Concat(input.EmployeeContributions.Select(x => (Party: ContributionParty.Employee, Input: x)))
            .Where(x => x.Input.Amount != 0 || x.Input.Percentage != 0 || x.Input.ExemptPayments != 0)
            .ToDictionary(x => Key(x.Party, x.Input.Component),
                x => (x.Input.Amount, x.Input.Percentage, x.Input.ExemptPayments));
        if (sourceMap.Count != requested.Count) return true;
        return sourceMap.Any(x => !requested.TryGetValue(x.Key, out var value) || value != x.Value);
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
        var reportingMonthStart = new DateOnly(reportingMonth.Year, reportingMonth.Month, 1);
        var reportingMonthEnd = reportingMonthStart.AddMonths(1).AddDays(-1);
        var mixProducts = await db.EmployeePensionProducts.AsNoTracking()
            .Where(x => x.EmploymentId == reportEmployee.EmploymentId && x.IsActive
                && x.EffectiveFrom <= reportingMonthEnd
                && (x.EffectiveTo == null || x.EffectiveTo >= reportingMonthStart))
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
                mix.SalaryAllocationType, mix.SalaryAllocationValue, mix.AllocationOrder, mix.Section14Code, mix.FundClassification);
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
            // Do not persist the editor's unused 0/0/0 component placeholders.
            if (item.Amount == 0m && item.Percentage == 0m && item.ExemptPayments == 0m)
                continue;
            var amount = item.Amount > 0
                ? item.Amount
                : Math.Round(insuredSalary * item.Percentage / 100m, 2, MidpointRounding.AwayFromZero);
            db.ManualContributions.Add(new ManualContribution(productId, party, item.Component, amount, item.Percentage, item.ExemptPayments));
        }
    }

    private sealed record ResolvedReportProduct(ManualProductInput Input, SalaryAllocationType AllocationType,
        decimal? AllocationValue, int AllocationOrder, decimal InsuredSalary);
}

public sealed record CreateManualReportRequest(DateOnly ReportingMonth, DateOnly? SalaryPaymentDate,
    IReadOnlyCollection<Guid> EmploymentIds, Guid? PaymentAccountId = null);
public sealed record UpdateManualReportDetailsRequest(DateOnly ReportingMonth, DateOnly? SalaryPaymentDate);
public sealed record UpdateReportPaymentAccountRequest(Guid PaymentAccountId);
public sealed record UpdateManualReportSelectionRequest(IReadOnlyCollection<Guid> EmploymentIds);
public sealed record SaveManualReportEmployeeRequest(decimal MonthlySalary, IReadOnlyCollection<ManualProductInput> Products,
    ManualReportEmployeeSnapshotInput? Snapshot = null);
public sealed record ManualReportEmployeeSnapshotInput(int IdentifierType, string Identifier, DateOnly? BirthDate, int? Gender,
    string? Email, string? Mobile, string? City, string? Street, string? HouseNumber, string? Apartment,
    string? PostalCode, string? PostOfficeBox, DateOnly? EmploymentStartDate);
public sealed record ManualProductInput(PensionProductType ProductType, string PolicyNumber, DateOnly SalaryMonth,
    decimal Salary, string ReportingType, string SalaryLayer, bool Section14, DateOnly? Section14StartDate, int? Section14Code,
    string? FundExternalKey, string? FundCode, string? FundName, string? FundCompanyName, string? FundClassification,
    SalaryAllocationType? SalaryAllocationType, decimal? SalaryAllocationValue, int? AllocationOrder,
    IReadOnlyCollection<ManualContributionInput> EmployerContributions, IReadOnlyCollection<ManualContributionInput> EmployeeContributions,
    Guid? SourceReportProductId = null);
public sealed record ManualContributionInput(ContributionComponent Component, decimal Amount, decimal Percentage, decimal ExemptPayments);
public sealed record SaveManualReportPaymentRequest(string ProviderName, string ProviderAccount, string PaymentMethod,
    DateOnly? ValueDate, string ReferenceNumber, string EmployerBankName, string EmployerBankCode,
    string EmployerBranch, string EmployerAccount, string ConfirmationFileName, DateOnly? TrustAccountValueDate = null,
    decimal? ActualDepositAmount = null, string? MasavSenderCode = null);

