using Alpha.Api.Security;
using System.Globalization;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Application.Reporting;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class DerivedReportEndpoints
{
    private const int MaxSourceEmployees = 5000;

    public static IEndpointRouteBuilder MapDerivedReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/manual-reports")
            .RequireAuthorization().WithTags("Manual reporting");

        group.MapGet("/source-reports", GetSourceReportsAsync);
        group.MapPost("/derived", CreateDerivedReportAsync);
        group.MapGet("/{reportId:guid}/metadata", GetMetadataAsync);
        return endpoints;
    }

    private static async Task<IResult> GetSourceReportsAsync(Guid organizationId, Guid employerId,
        string? search, int skip, int take, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        skip = Math.Max(0, skip);
        take = Math.Clamp(take == 0 ? 30 : take, 1, 100);

        var query = db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                && x.Status != ManualReportStatus.Cancelled);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            if (DateOnly.TryParseExact(term, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var monthOnly)
                || DateOnly.TryParse(term, out monthOnly))
            {
                var month = new DateOnly(monthOnly.Year, monthOnly.Month, 1);
                query = query.Where(x => x.ReportingMonth == month);
            }
        }

        var page = await query
            .OrderByDescending(x => x.ReportingMonth)
            .ThenByDescending(x => x.UpdatedAt)
            .Skip(skip)
            .Take(take + 1)
            .ToListAsync(ct);

        var hasMore = page.Count > take;
        if (hasMore) page.RemoveAt(page.Count - 1);
        var ids = page.Select(x => x.Id).ToArray();

        var employeeCounts = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => ids.Contains(x.ReportId))
            .GroupBy(x => x.ReportId)
            .Select(g => new { ReportId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ReportId, x => x.Count, ct);

        var productCounts = await (
                from product in db.ManualReportProducts.AsNoTracking()
                join employee in db.ManualReportEmployees.AsNoTracking()
                    on product.ReportEmployeeId equals employee.Id
                where ids.Contains(employee.ReportId)
                group product by employee.ReportId into g
                select new { ReportId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ReportId, x => x.Count, ct);

        var sourceOperations = await (
                from metadata in db.EmployerInterfaceReportProductData.AsNoTracking()
                join product in db.ManualReportProducts.AsNoTracking() on metadata.ReportProductId equals product.Id
                join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                where ids.Contains(employee.ReportId)
                select new { employee.ReportId, metadata.OperationCode })
            .ToListAsync(ct);
        var operation6Reports = sourceOperations
            .GroupBy(x => x.ReportId)
            .Where(g => g.Any() && g.All(x => x.OperationCode == 6))
            .Select(g => g.Key)
            .ToHashSet();

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
                canBeCurrentCorrectionSource = x.ReportKind == ManualReportKind.Negative
                    && operation6Reports.Contains(x.Id)
                    && (x.Status == ManualReportStatus.Sent || x.Status == ManualReportStatus.Completed || x.ExternalSourceReference),
                x.CreatedAt,
                x.UpdatedAt,
                employeeCount = employeeCounts.GetValueOrDefault(x.Id),
                productCount = productCounts.GetValueOrDefault(x.Id)
            }),
            hasMore
        });
    }

    private static async Task<IResult> GetMetadataAsync(Guid organizationId, Guid employerId, Guid reportId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == reportId &&
            x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        return report is null ? Results.NotFound() : Results.Ok(new
        {
            report.Id,
            report.ReportingMonth,
            report.SalaryPaymentDate,
            report.Status,
            report.ReportKind,
            report.SourceReportId
        });
    }

    private static async Task<IResult> CreateDerivedReportAsync(Guid organizationId, Guid employerId,
        CreateDerivedManualReportRequest request, IAlphaDbContext db, OrganizationAccessService access,
        ReportPaymentAccountService paymentAccounts, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var isCurrentCorrection = request.ReportKind == ManualReportKind.Current && request.CorrectionOperationCode is 2 or 3;
        if (request.ReportKind is not (ManualReportKind.Differences or ManualReportKind.Negative) && !isCurrentCorrection)
            return Results.BadRequest(new { error = "Derived reports must be Differences, Negative, or a Current correction using operation 2 or 3." });

        var source = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.SourceReportId &&
            x.OrganizationId == organizationId && x.EmployerId == employerId && x.Status != ManualReportStatus.Cancelled, ct);
        if (source is null) return Results.BadRequest(new { error = "Source report was not found for this employer." });

        var sourceEmployees = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == source.Id)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        var sourceEmployeeIds = sourceEmployees.Select(x => x.Id).ToArray();
        var sourceProducts = await db.ManualReportProducts.AsNoTracking()
            .Where(x => sourceEmployeeIds.Contains(x.ReportEmployeeId))
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

        var requestedProductIds = request.ReportProductIds?.Where(x => x != Guid.Empty).Distinct().ToHashSet();
        if (requestedProductIds is { Count: > 0 })
        {
            var availableIds = sourceProducts.Select(x => x.Id).ToHashSet();
            if (requestedProductIds.Any(x => !availableIds.Contains(x)))
                return Results.BadRequest(new { error = "One or more selected products do not belong to the source report." });

            sourceProducts = sourceProducts.Where(x => requestedProductIds.Contains(x.Id)).ToList();
            var selectedEmployeeIds = sourceProducts.Select(x => x.ReportEmployeeId).Distinct().ToHashSet();
            sourceEmployees = sourceEmployees.Where(x => selectedEmployeeIds.Contains(x.Id)).ToList();
        }

        if (sourceEmployees.Count == 0 || sourceProducts.Count == 0)
            return Results.BadRequest(new { error = "The derived report must contain at least one employee and product." });
        if (sourceEmployees.Count > MaxSourceEmployees)
            return Results.BadRequest(new { error = $"Source reports are limited to {MaxSourceEmployees} employees for derived drafts." });

        var sourceProductIds = sourceProducts.Select(x => x.Id).ToArray();
        var sourceContributions = await db.ManualContributions.AsNoTracking()
            .Where(x => sourceProductIds.Contains(x.ReportProductId))
            .ToListAsync(ct);
        var sourcePayments = await db.ManualReportPayments.AsNoTracking()
            .Where(x => sourceProductIds.Contains(x.ReportProductId))
            .ToListAsync(ct);
        var sourceMetadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .Where(x => sourceProductIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);

        if (isCurrentCorrection)
        {
            if (source.ReportKind != ManualReportKind.Negative)
                return Results.BadRequest(new { error = "A current operation 2/3 correction must be based on a negative report." });
            if (source.Status is not (ManualReportStatus.Sent or ManualReportStatus.Completed) && !source.ExternalSourceReference)
                return Results.Conflict(new { error = "The negative source must already have been transmitted, or be an imported external negative report." });
            if (sourceProducts.Count == 0 || sourceProducts.Any(p => !sourceMetadata.TryGetValue(p.Id, out var m) || m.OperationCode != 6))
                return Results.BadRequest(new { error = "A current operation 2/3 correction must reference a negative operation 6 report." });
        }

        var report = new ManualReport(organizationId, employerId, request.ReportingMonth, request.SalaryPaymentDate,
            request.ReportKind, source.Id);
        // Employer snapshot secrets are purpose-bound to the source report ID. Rebind them
        // to the derived report instead of copying ciphertext that cannot be decrypted under
        // the new report's protection purposes.
        var employerRegistrationNumber = protector.Unprotect(source.EmployerRegistrationNumberSnapshot,
            $"report-employer-registration:{source.Id}");
        var employerWithholdingFileNumber = protector.Unprotect(source.EmployerWithholdingFileNumberSnapshot,
            $"report-employer-withholding:{source.Id}");
        var employerContactPhone = protector.Unprotect(source.EmployerContactPhoneSnapshot,
            $"report-employer-phone:{source.Id}");
        var employerContactEmail = protector.Unprotect(source.EmployerContactEmailSnapshot,
            $"report-employer-email:{source.Id}");
        var employerContactMobile = protector.Unprotect(source.EmployerContactMobileSnapshot,
            $"report-employer-mobile:{source.Id}");
        report.SetEmployerInterfaceSnapshot(source.EmployerLegalNameSnapshot, employerRegistrationNumber,
            employerWithholdingFileNumber, source.EmployerContactFirstNameSnapshot, source.EmployerContactLastNameSnapshot,
            employerContactPhone, employerContactEmail, employerContactMobile,
            source.DepositorTypeCodeSnapshot, source.EmployerIdentifierTypeCodeSnapshot);
        report.SetProtectedEmployerSnapshot(
            protector.Protect(employerRegistrationNumber, $"report-employer-registration:{report.Id}"),
            protector.Protect(employerWithholdingFileNumber, $"report-employer-withholding:{report.Id}"),
            protector.Protect(employerContactPhone, $"report-employer-phone:{report.Id}"),
            protector.Protect(employerContactEmail, $"report-employer-email:{report.Id}"),
            protector.Protect(employerContactMobile, $"report-employer-mobile:{report.Id}"));
        var paymentAccount = await paymentAccounts.ResolveForReportAsync(employerId, request.PaymentAccountId, ct);
        if (paymentAccount is null) return Results.Conflict(new { error = "payment_account_required" });
        await paymentAccounts.ApplySnapshotAsync(report, paymentAccount, protector.Unprotect(paymentAccount.AccountNumberEncrypted ?? throw new InvalidOperationException("Encrypted account number missing."), "bank-account-number"), ct);
        db.ManualReports.Add(report);

        var employeeMap = new Dictionary<Guid, ManualReportEmployee>(sourceEmployees.Count);
        foreach (var oldEmployee in sourceEmployees)
        {
            var nationalId = protector.Unprotect(oldEmployee.NationalId, $"report-employee-national-id:{oldEmployee.Id}");
            var interfaceIdentifier = protector.Unprotect(oldEmployee.InterfaceIdentifier, $"report-employee-interface-id:{oldEmployee.Id}");
            var email = protector.Unprotect(oldEmployee.EmailSnapshot, $"report-employee-email:{oldEmployee.Id}");
            var mobile = protector.Unprotect(oldEmployee.MobileSnapshot, $"report-employee-mobile:{oldEmployee.Id}");
            var clone = new ManualReportEmployee(report.Id, organizationId, employerId, oldEmployee.EmploymentId,
                oldEmployee.PersonId, nationalId, oldEmployee.FirstName, oldEmployee.LastName,
                oldEmployee.EmployeeNumber, oldEmployee.MonthlySalary);
            clone.SetProtectedIdentifiers(
                protector.Protect(nationalId, $"report-employee-national-id:{clone.Id}"),
                protector.LookupHash(nationalId, "report-employee-national-id-lookup"),
                protector.Protect(interfaceIdentifier, $"report-employee-interface-id:{clone.Id}"));
            clone.SetInterfaceSnapshot(oldEmployee.InterfaceIdentifierType,
                protector.Protect(interfaceIdentifier, $"report-employee-interface-id:{clone.Id}"),
                oldEmployee.BirthDateSnapshot, oldEmployee.GenderSnapshot,
                protector.Protect(email, $"report-employee-email:{clone.Id}"),
                protector.Protect(mobile, $"report-employee-mobile:{clone.Id}"), oldEmployee.CitySnapshot, oldEmployee.StreetSnapshot,
                oldEmployee.HouseNumberSnapshot, oldEmployee.ApartmentSnapshot, oldEmployee.PostalCodeSnapshot,
                oldEmployee.PostOfficeBoxSnapshot, oldEmployee.EmploymentStartDateSnapshot);
            db.ManualReportEmployees.Add(clone);
            employeeMap[oldEmployee.Id] = clone;
        }

        var sourceTransferIdentifierByFund = sourceProducts
            .GroupBy(x => x.FundCode, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var first = g.OrderBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt).First();
                    return sourceMetadata.TryGetValue(first.Id, out var metadata)
                        && !string.IsNullOrWhiteSpace(metadata.InterfaceTransferIdentifier)
                            ? metadata.InterfaceTransferIdentifier
                            : first.Id.ToString("D");
                },
                StringComparer.Ordinal);

        var productMap = new Dictionary<Guid, ManualReportProduct>(sourceProducts.Count);
        foreach (var oldProduct in sourceProducts)
        {
            var clone = new ManualReportProduct(employeeMap[oldProduct.ReportEmployeeId].Id, oldProduct.ProductType,
                oldProduct.PolicyNumber, oldProduct.SalaryMonth, oldProduct.Salary, oldProduct.ReportingType, oldProduct.SalaryLayer,
                oldProduct.Section14, oldProduct.Section14StartDate,
                oldProduct.FundExternalKey, oldProduct.FundCode, oldProduct.FundName, oldProduct.FundCompanyName,
                oldProduct.SalaryAllocationType, oldProduct.SalaryAllocationValue, oldProduct.AllocationOrder,
                oldProduct.Section14Code, oldProduct.FundClassification);
            db.ManualReportProducts.Add(clone);
            productMap[oldProduct.Id] = clone;

            if (sourceMetadata.TryGetValue(oldProduct.Id, out var oldMetadata))
            {
                var metadataClone = new EmployerInterfaceReportProductData(clone.Id);
                metadataClone.Update(
                    request.ReportKind == ManualReportKind.Negative ? null
                        : isCurrentCorrection ? request.CorrectionOperationCode
                        : oldMetadata.OperationCode,
                    oldMetadata.DepositStatus,
                    oldMetadata.EmployeeStatus,
                    oldMetadata.StatusStartDate,
                    oldMetadata.EmploymentPercentage,
                    oldMetadata.WorkDaysInMonth,
                    oldMetadata.LastDeposit,
                    request.ReportKind == ManualReportKind.Negative || isCurrentCorrection ? null : oldMetadata.RefundReason,
                    request.ReportKind == ManualReportKind.Negative ? null
                        : isCurrentCorrection && request.CorrectionOperationCode == 2 ? 1
                        : isCurrentCorrection ? null
                        : oldMetadata.PaymentMethodCode,
                    oldMetadata.EmployerAccountType,
                    oldMetadata.ReceiverAccountType,
                    sourceTransferIdentifierByFund[oldProduct.FundCode],
                    string.IsNullOrWhiteSpace(oldMetadata.ClearingIdentifier) ? null : oldMetadata.ClearingIdentifier,
                    null,
                    oldMetadata.OldPensionTypeCode);
                db.EmployerInterfaceReportProductData.Add(metadataClone);
            }
        }

        foreach (var oldContribution in sourceContributions)
        {
            db.ManualContributions.Add(new ManualContribution(productMap[oldContribution.ReportProductId].Id,
                oldContribution.Party, oldContribution.Component, oldContribution.Amount, oldContribution.Percentage,
                oldContribution.ExemptPayments,
                string.IsNullOrWhiteSpace(oldContribution.InterfaceRecordIdentifier)
                    ? oldContribution.Id.ToString("D")
                    : oldContribution.InterfaceRecordIdentifier));
        }

        foreach (var oldPayment in sourcePayments)
        {
            var clonedProductId = productMap[oldPayment.ReportProductId].Id;
            var employerAccount = protector.Unprotect(oldPayment.EmployerAccount, $"report-payment-account:{oldPayment.ReportProductId}");
            var clone = new ManualReportPayment(clonedProductId);
            clone.Update(oldPayment.ProviderName, oldPayment.ProviderAccount, oldPayment.PaymentMethod, oldPayment.ValueDate,
                oldPayment.TrustAccountValueDate, oldPayment.ReferenceNumber, oldPayment.EmployerBankName, oldPayment.EmployerBankCode,
                oldPayment.EmployerBranch, protector.Protect(employerAccount, $"report-payment-account:{clonedProductId}"), oldPayment.ConfirmationFileName,
                oldPayment.ActualDepositAmount, oldPayment.MasavSenderCode);
            db.ManualReportPayments.Add(clone);
        }

        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/organizations/{organizationId}/employers/{employerId}/manual-reports/{report.Id}", new
        {
            report.Id,
            report.ReportingMonth,
            report.SalaryPaymentDate,
            report.Status,
            report.ReportKind,
            report.SourceReportId,
            report.PaymentAccountId,
            report.PaymentBankId,
            report.PaymentBranchId,
            report.PaymentAccountNumberMasked,
            report.PaymentMandateReference,
            employeeCount = sourceEmployees.Count
        });
    }
}

public sealed record CreateDerivedManualReportRequest(Guid SourceReportId, ManualReportKind ReportKind,
    DateOnly ReportingMonth, DateOnly? SalaryPaymentDate, Guid? PaymentAccountId = null,
    int? CorrectionOperationCode = null, IReadOnlyCollection<Guid>? ReportProductIds = null);
