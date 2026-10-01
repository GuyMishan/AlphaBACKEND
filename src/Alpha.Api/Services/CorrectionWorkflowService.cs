using Alpha.Api.Security;
using Alpha.Application.Abstractions;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

public static class CorrectionWorkflowService
{
    public sealed record WorkspaceResult(Guid ReportId, Guid? ReportProductId, bool Created, int PendingChanges);
    public sealed record MaterializedResult(Guid WorkspaceId, Guid SourceReportId, Guid NegativeReportId,
        Guid CurrentReportId, int PendingChanges);

    private enum CloneMode { Workspace, NegativeCancellation, CurrentCorrection }

    private sealed record ReportGraph(
        ManualReport Report,
        List<ManualReportEmployee> Employees,
        List<ManualReportProduct> Products,
        List<ManualContribution> Contributions,
        List<ManualReportPayment> Payments,
        Dictionary<Guid, EmployerInterfaceReportProductData> Metadata);

    private sealed record CloneResult(
        ManualReport Report,
        List<ManualReportEmployee> Employees,
        List<ManualReportProduct> Products,
        List<ManualContribution> Contributions,
        List<ManualReportPayment> Payments,
        List<EmployerInterfaceReportProductData> Metadata,
        Dictionary<Guid, ManualReportProduct> ProductsBySource,
        Dictionary<(Guid ProductId, ContributionParty Party, ContributionComponent Component), ManualContribution> ContributionsBySourceKey);

    public static async Task<WorkspaceResult?> EnsureWorkspaceAsync(
        Guid organizationId, Guid employerId, Guid sourceReportId, Guid? sourceReportProductId,
        IAlphaDbContext db, IDataProtectionService protector, CancellationToken ct)
    {
        var source = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == sourceReportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (source is null || source.ReportKind != ManualReportKind.Current
            || source.Status is not (ManualReportStatus.Sent or ManualReportStatus.Completed))
            return null;

        var existing = await db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                && x.SourceReportId == sourceReportId && x.IsCorrectionWorkspace
                && (x.Status == ManualReportStatus.Draft
                    || x.Status == ManualReportStatus.ReadyForValidation
                    || x.Status == ManualReportStatus.Error))
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefaultAsync(ct);

        if (existing is not null)
        {
            Guid? mappedProductId = null;
            if (sourceReportProductId.HasValue)
            {
                mappedProductId = await (
                    from product in db.ManualReportProducts.AsNoTracking()
                    join employee in db.ManualReportEmployees.AsNoTracking()
                        on product.ReportEmployeeId equals employee.Id
                    where employee.ReportId == existing.Id
                        && product.SourceReportProductId == sourceReportProductId.Value
                    select (Guid?)product.Id
                ).SingleOrDefaultAsync(ct);
            }
            return new WorkspaceResult(existing.Id, mappedProductId, false,
                await PendingChangeCountAsync(existing.Id, db, ct));
        }

        var graph = await LoadGraphAsync(sourceReportId, db, ct);
        if (graph is null || graph.Products.Count == 0)
            return null;
        if (sourceReportProductId.HasValue && graph.Products.All(x => x.Id != sourceReportProductId.Value))
            return null;

        var clone = CloneGraph(graph, ManualReportKind.Differences, sourceReportId,
            CloneMode.Workspace, null, protector, null, null);
        clone.Report.MarkCorrectionWorkspace();
        AddClone(db, clone);
        await db.SaveChangesAsync(ct);

        Guid? requestedProductId = null;
        if (sourceReportProductId.HasValue
            && clone.ProductsBySource.TryGetValue(sourceReportProductId.Value, out var mapped))
            requestedProductId = mapped.Id;

        return new WorkspaceResult(clone.Report.Id, requestedProductId, true, 0);
    }

    public static async Task<MaterializedResult?> MaterializeAsync(
        Guid organizationId, Guid employerId, Guid workspaceId, int correctionOperationCode,
        IAlphaDbContext db, IDataProtectionService protector, CancellationToken ct)
    {
        if (correctionOperationCode is not (2 or 3))
            return null;

        var workspace = await db.ManualReports.SingleOrDefaultAsync(x =>
            x.Id == workspaceId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (workspace is null || !workspace.IsCorrectionWorkspace || !workspace.IsEditable
            || !workspace.SourceReportId.HasValue)
            return null;

        var pendingChanges = await PendingChangeCountAsync(workspace.Id, db, ct);
        if (pendingChanges == 0)
            return null;

        var sourceGraph = await LoadGraphAsync(workspace.SourceReportId.Value, db, ct);
        var workspaceGraph = await LoadGraphAsync(workspace.Id, db, ct);
        if (sourceGraph is null || workspaceGraph is null
            || sourceGraph.Report.ReportKind != ManualReportKind.Current
            || sourceGraph.Report.Status is not (ManualReportStatus.Sent or ManualReportStatus.Completed))
            return null;

        if (sourceGraph.Metadata.Count != sourceGraph.Products.Count)
            return null;

        var missingOriginalReference = sourceGraph.Products.Any(product =>
            !sourceGraph.Metadata.TryGetValue(product.Id, out var metadata)
            || string.IsNullOrWhiteSpace(metadata.InterfaceTransferIdentifier)
            || string.IsNullOrWhiteSpace(metadata.ClearingIdentifier));
        if (missingOriginalReference)
            return null;

        var negative = CloneGraph(sourceGraph, ManualReportKind.Negative, sourceGraph.Report.Id,
            CloneMode.NegativeCancellation, null, protector, null, null);

        var current = CloneGraph(workspaceGraph, ManualReportKind.Current, negative.Report.Id,
            CloneMode.CurrentCorrection, correctionOperationCode, protector,
            negative.ProductsBySource, negative.ContributionsBySourceKey);

        AddClone(db, negative);
        AddClone(db, current);
        workspace.MarkCancelled();
        await db.SaveChangesAsync(ct);

        return new MaterializedResult(workspace.Id, sourceGraph.Report.Id,
            negative.Report.Id, current.Report.Id, pendingChanges);
    }

    public static async Task SyncCurrentCorrectionReferencesAsync(
        Guid reportId, IAlphaDbContext db, CancellationToken ct)
    {
        var report = await db.ManualReports.SingleOrDefaultAsync(x => x.Id == reportId, ct);
        if (report is null || report.ReportKind != ManualReportKind.Current || !report.SourceReportId.HasValue)
            return;

        var source = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == report.SourceReportId.Value, ct);
        if (source?.ReportKind != ManualReportKind.Negative)
            return;

        var sourceEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == source.Id).Select(x => x.Id).ToArrayAsync(ct);
        var sourceProducts = await db.ManualReportProducts.AsNoTracking()
            .Where(x => sourceEmployeeIds.Contains(x.ReportEmployeeId)).ToListAsync(ct);
        var sourceProductIds = sourceProducts.Select(x => x.Id).ToArray();
        var sourceMetadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .Where(x => sourceProductIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);

        var sourceByFund = sourceProducts
            .GroupBy(x => x.FundCode, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var product = group.OrderBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt).First();
                    sourceMetadata.TryGetValue(product.Id, out var metadata);
                    return (
                        Identifier: !string.IsNullOrWhiteSpace(metadata?.InterfaceTransferIdentifier)
                            ? metadata.InterfaceTransferIdentifier
                            : product.Id.ToString("D").ToUpperInvariant(),
                        Clearing: metadata?.ClearingIdentifier ?? string.Empty);
                },
                StringComparer.Ordinal);

        var currentEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == report.Id).Select(x => x.Id).ToArrayAsync(ct);
        var currentProducts = await db.ManualReportProducts.AsNoTracking()
            .Where(x => currentEmployeeIds.Contains(x.ReportEmployeeId)).ToListAsync(ct);
        var currentProductIds = currentProducts.Select(x => x.Id).ToArray();
        var currentMetadata = await db.EmployerInterfaceReportProductData
            .Where(x => currentProductIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);

        foreach (var product in currentProducts)
        {
            if (!currentMetadata.TryGetValue(product.Id, out var metadata)
                || metadata.OperationCode is not (2 or 3)
                || !sourceByFund.TryGetValue(product.FundCode, out var previous))
                continue;

            metadata.Update(metadata.OperationCode, metadata.DepositStatus, metadata.EmployeeStatus,
                metadata.StatusStartDate, metadata.EmploymentPercentage, metadata.WorkDaysInMonth,
                metadata.LastDeposit, metadata.RefundReason, metadata.PaymentMethodCode,
                metadata.EmployerAccountType, metadata.ReceiverAccountType,
                previous.Identifier,
                string.IsNullOrWhiteSpace(previous.Clearing) ? null : previous.Clearing,
                metadata.PreviousReferenceExceptionCode, metadata.OldPensionTypeCode);
        }

        await db.SaveChangesAsync(ct);
    }

    public static async Task<int> PendingChangeCountAsync(
        Guid reportId, IAlphaDbContext db, CancellationToken ct)
    {
        var report = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reportId, ct);
        if (report is null)
            return 0;

        var employeeIds = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == reportId).Select(x => x.Id).ToArrayAsync(ct);
        var productChanges = employeeIds.Length == 0
            ? 0
            : await db.ManualReportProducts.AsNoTracking()
                .CountAsync(x => employeeIds.Contains(x.ReportEmployeeId) && x.IsCorrectionChanged, ct);
        return Math.Max(productChanges, report.HasCorrectionChanges ? 1 : 0);
    }

    private static async Task<ReportGraph?> LoadGraphAsync(
        Guid reportId, IAlphaDbContext db, CancellationToken ct)
    {
        var report = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reportId, ct);
        if (report is null) return null;

        var employees = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == reportId).OrderBy(x => x.Id).ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking()
            .Where(x => employeeIds.Contains(x.ReportEmployeeId))
            .OrderBy(x => x.ReportEmployeeId).ThenBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt)
            .ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var payments = await db.ManualReportPayments.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);

        return new ReportGraph(report, employees, products, contributions, payments, metadata);
    }

    private static CloneResult CloneGraph(
        ReportGraph source, ManualReportKind kind, Guid sourceReportId, CloneMode mode,
        int? correctionOperationCode, IDataProtectionService protector,
        IReadOnlyDictionary<Guid, ManualReportProduct>? negativeByOriginalProduct,
        IReadOnlyDictionary<(Guid ProductId, ContributionParty Party, ContributionComponent Component), ManualContribution>? negativeContributionByOriginalKey)
    {
        var report = new ManualReport(source.Report.OrganizationId, source.Report.EmployerId,
            source.Report.ReportingMonth, source.Report.SalaryPaymentDate, kind, sourceReportId);
        CopyReportSnapshot(source.Report, report, protector);

        var employees = new List<ManualReportEmployee>(source.Employees.Count);
        var employeeMap = new Dictionary<Guid, ManualReportEmployee>(source.Employees.Count);
        foreach (var oldEmployee in source.Employees)
        {
            var clone = CloneEmployee(source.Report, report, oldEmployee, protector);
            employees.Add(clone);
            employeeMap[oldEmployee.Id] = clone;
        }

        var sourceTransferByFund = source.Products
            .GroupBy(x => x.FundCode, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group =>
                {
                    var product = group.OrderBy(x => x.AllocationOrder).ThenBy(x => x.CreatedAt).First();
                    source.Metadata.TryGetValue(product.Id, out var metadata);
                    return !string.IsNullOrWhiteSpace(metadata?.InterfaceTransferIdentifier)
                        ? metadata.InterfaceTransferIdentifier
                        : product.Id.ToString("D").ToUpperInvariant();
                },
                StringComparer.Ordinal);

        var products = new List<ManualReportProduct>(source.Products.Count);
        var metadataRows = new List<EmployerInterfaceReportProductData>(source.Products.Count);
        var productMap = new Dictionary<Guid, ManualReportProduct>(source.Products.Count);

        foreach (var oldProduct in source.Products)
        {
            var clone = new ManualReportProduct(employeeMap[oldProduct.ReportEmployeeId].Id,
                oldProduct.ProductType, oldProduct.PolicyNumber, oldProduct.SalaryMonth, oldProduct.Salary,
                oldProduct.ReportingType, oldProduct.SalaryLayer, oldProduct.Section14,
                oldProduct.Section14StartDate, oldProduct.FundExternalKey, oldProduct.FundCode,
                oldProduct.FundName, oldProduct.FundCompanyName, oldProduct.SalaryAllocationType,
                oldProduct.SalaryAllocationValue, oldProduct.AllocationOrder, oldProduct.Section14Code,
                oldProduct.FundClassification);
            clone.SetSourceVersion(oldProduct.Id);
            if (mode == CloneMode.CurrentCorrection && oldProduct.IsCorrectionChanged)
                clone.MarkCorrectionChanged();
            products.Add(clone);
            productMap[oldProduct.Id] = clone;

            if (!source.Metadata.TryGetValue(oldProduct.Id, out var oldMetadata))
                continue;

            var metadata = new EmployerInterfaceReportProductData(clone.Id);
            if (mode == CloneMode.Workspace)
            {
                metadata.Update(oldMetadata.OperationCode, oldMetadata.DepositStatus,
                    oldMetadata.EmployeeStatus, oldMetadata.StatusStartDate,
                    oldMetadata.EmploymentPercentage, oldMetadata.WorkDaysInMonth,
                    oldMetadata.LastDeposit, oldMetadata.RefundReason, oldMetadata.PaymentMethodCode,
                    oldMetadata.EmployerAccountType, oldMetadata.ReceiverAccountType,
                    oldMetadata.PreviousIdentifier, oldMetadata.PreviousClearingIdentifier,
                    oldMetadata.PreviousReferenceExceptionCode, oldMetadata.OldPensionTypeCode);
                metadata.SetInterfaceTransferIdentifier(oldMetadata.InterfaceTransferIdentifier);
                metadata.SetClearingIdentifier(oldMetadata.ClearingIdentifier);
            }
            else if (mode == CloneMode.NegativeCancellation)
            {
                metadata.Update(6, oldMetadata.DepositStatus, oldMetadata.EmployeeStatus,
                    oldMetadata.StatusStartDate, oldMetadata.EmploymentPercentage,
                    oldMetadata.WorkDaysInMonth, oldMetadata.LastDeposit, null, null,
                    oldMetadata.EmployerAccountType, oldMetadata.ReceiverAccountType,
                    sourceTransferByFund[oldProduct.FundCode],
                    string.IsNullOrWhiteSpace(oldMetadata.ClearingIdentifier)
                        ? null : oldMetadata.ClearingIdentifier,
                    null, oldMetadata.OldPensionTypeCode);
            }
            else
            {
                var originalProductId = oldProduct.SourceReportProductId;
                if (originalProductId.HasValue && negativeByOriginalProduct is not null
                    && negativeByOriginalProduct.TryGetValue(originalProductId.Value, out var negativeProduct))
                {
                    var operation = oldProduct.IsCorrectionChanged ? correctionOperationCode!.Value : 2;
                    metadata.Update(operation, oldMetadata.DepositStatus, oldMetadata.EmployeeStatus,
                        oldMetadata.StatusStartDate, oldMetadata.EmploymentPercentage,
                        oldMetadata.WorkDaysInMonth, oldMetadata.LastDeposit, null,
                        operation == 2 ? 1 : oldMetadata.PaymentMethodCode,
                        oldMetadata.EmployerAccountType, oldMetadata.ReceiverAccountType,
                        negativeProduct.Id.ToString("D").ToUpperInvariant(), null, null,
                        oldMetadata.OldPensionTypeCode);
                }
                else
                {
                    metadata.Update(1, oldMetadata.DepositStatus, oldMetadata.EmployeeStatus,
                        oldMetadata.StatusStartDate, oldMetadata.EmploymentPercentage,
                        oldMetadata.WorkDaysInMonth, oldMetadata.LastDeposit, oldMetadata.RefundReason,
                        oldMetadata.PaymentMethodCode, oldMetadata.EmployerAccountType,
                        oldMetadata.ReceiverAccountType, null, null, null, oldMetadata.OldPensionTypeCode);
                }
            }
            metadataRows.Add(metadata);
        }

        var contributions = new List<ManualContribution>(source.Contributions.Count);
        var contributionMap =
            new Dictionary<(Guid ProductId, ContributionParty Party, ContributionComponent Component), ManualContribution>();
        foreach (var oldContribution in source.Contributions)
        {
            string? previousRecordIdentifier = mode switch
            {
                CloneMode.Workspace => oldContribution.PreviousRecordIdentifier,
                CloneMode.NegativeCancellation =>
                    string.IsNullOrWhiteSpace(oldContribution.InterfaceRecordIdentifier)
                        ? oldContribution.Id.ToString("D")
                        : oldContribution.InterfaceRecordIdentifier,
                CloneMode.CurrentCorrection => ResolvePreviousContributionRecord(
                    oldContribution, source.Products, negativeContributionByOriginalKey),
                _ => null
            };

            var clone = new ManualContribution(productMap[oldContribution.ReportProductId].Id,
                oldContribution.Party, oldContribution.Component, oldContribution.Amount,
                oldContribution.Percentage, oldContribution.ExemptPayments, previousRecordIdentifier);
            if (mode == CloneMode.Workspace
                && !string.IsNullOrWhiteSpace(oldContribution.InterfaceRecordIdentifier))
                clone.SetInterfaceRecordIdentifier(oldContribution.InterfaceRecordIdentifier);

            contributions.Add(clone);
            contributionMap[(oldContribution.ReportProductId, oldContribution.Party, oldContribution.Component)] = clone;
        }

        var payments = new List<ManualReportPayment>(source.Payments.Count);
        foreach (var oldPayment in source.Payments)
        {
            var clonedProductId = productMap[oldPayment.ReportProductId].Id;
            var employerAccount = protector.Unprotect(oldPayment.EmployerAccount,
                $"report-payment-account:{oldPayment.ReportProductId}");
            var clone = new ManualReportPayment(clonedProductId);
            clone.Update(oldPayment.ProviderName, oldPayment.ProviderAccount, oldPayment.PaymentMethod,
                oldPayment.ValueDate, oldPayment.TrustAccountValueDate, oldPayment.ReferenceNumber,
                oldPayment.EmployerBankName, oldPayment.EmployerBankCode, oldPayment.EmployerBranch,
                protector.Protect(employerAccount, $"report-payment-account:{clonedProductId}"),
                oldPayment.ConfirmationFileName, oldPayment.ActualDepositAmount, oldPayment.MasavSenderCode);
            payments.Add(clone);
        }

        return new CloneResult(report, employees, products, contributions, payments,
            metadataRows, productMap, contributionMap);
    }

    private static void CopyReportSnapshot(
        ManualReport source, ManualReport target, IDataProtectionService protector)
    {
        var registration = protector.Unprotect(source.EmployerRegistrationNumberSnapshot,
            $"report-employer-registration:{source.Id}");
        var withholding = protector.Unprotect(source.EmployerWithholdingFileNumberSnapshot,
            $"report-employer-withholding:{source.Id}");
        var phone = protector.Unprotect(source.EmployerContactPhoneSnapshot,
            $"report-employer-phone:{source.Id}");
        var email = protector.Unprotect(source.EmployerContactEmailSnapshot,
            $"report-employer-email:{source.Id}");
        var mobile = protector.Unprotect(source.EmployerContactMobileSnapshot,
            $"report-employer-mobile:{source.Id}");

        target.SetEmployerInterfaceSnapshot(source.EmployerLegalNameSnapshot, registration, withholding,
            source.EmployerContactFirstNameSnapshot, source.EmployerContactLastNameSnapshot,
            phone, email, mobile, source.DepositorTypeCodeSnapshot, source.EmployerIdentifierTypeCodeSnapshot);
        target.SetProtectedEmployerSnapshot(
            protector.Protect(registration, $"report-employer-registration:{target.Id}"),
            protector.Protect(withholding, $"report-employer-withholding:{target.Id}"),
            protector.Protect(phone, $"report-employer-phone:{target.Id}"),
            protector.Protect(email, $"report-employer-email:{target.Id}"),
            protector.Protect(mobile, $"report-employer-mobile:{target.Id}"));

        if (source.PaymentAccountId.HasValue && source.PaymentBankId.HasValue && source.PaymentBranchId.HasValue)
        {
            target.SetPaymentAccountSnapshot(source.PaymentAccountId.Value, source.PaymentBankId.Value,
                source.PaymentBranchId.Value, source.PaymentAccountNumberMasked,
                source.PaymentMandateReference);
        }
    }

    private static ManualReportEmployee CloneEmployee(
        ManualReport sourceReport, ManualReport targetReport,
        ManualReportEmployee source, IDataProtectionService protector)
    {
        var nationalId = protector.Unprotect(source.NationalId,
            $"report-employee-national-id:{source.Id}");
        var interfaceIdentifier = protector.Unprotect(source.InterfaceIdentifier,
            $"report-employee-interface-id:{source.Id}");
        var email = protector.Unprotect(source.EmailSnapshot,
            $"report-employee-email:{source.Id}");
        var mobile = protector.Unprotect(source.MobileSnapshot,
            $"report-employee-mobile:{source.Id}");

        var clone = new ManualReportEmployee(targetReport.Id, sourceReport.OrganizationId,
            sourceReport.EmployerId, source.EmploymentId, source.PersonId, nationalId,
            source.FirstName, source.LastName, source.EmployeeNumber, source.MonthlySalary);
        clone.SetInterfaceSnapshot(source.InterfaceIdentifierType, interfaceIdentifier,
            source.BirthDateSnapshot, source.GenderSnapshot, email, mobile,
            source.CitySnapshot, source.StreetSnapshot, source.HouseNumberSnapshot,
            source.ApartmentSnapshot, source.PostalCodeSnapshot, source.PostOfficeBoxSnapshot,
            source.EmploymentStartDateSnapshot);
        clone.SetProtectedIdentifiers(
            protector.Protect(nationalId, $"report-employee-national-id:{clone.Id}"),
            protector.LookupHash(nationalId, "report-employee-national-id-lookup"),
            protector.Protect(interfaceIdentifier, $"report-employee-interface-id:{clone.Id}"));
        clone.SetProtectedContactSnapshot(
            protector.Protect(email, $"report-employee-email:{clone.Id}"),
            protector.Protect(mobile, $"report-employee-mobile:{clone.Id}"));
        return clone;
    }

    private static string? ResolvePreviousContributionRecord(
        ManualContribution workspaceContribution,
        IReadOnlyList<ManualReportProduct> workspaceProducts,
        IReadOnlyDictionary<(Guid ProductId, ContributionParty Party, ContributionComponent Component), ManualContribution>? negativeContributionByOriginalKey)
    {
        if (negativeContributionByOriginalKey is null) return null;
        var workspaceProduct = workspaceProducts.First(x => x.Id == workspaceContribution.ReportProductId);
        if (!workspaceProduct.SourceReportProductId.HasValue) return null;

        if (!negativeContributionByOriginalKey.TryGetValue(
                (workspaceProduct.SourceReportProductId.Value,
                    workspaceContribution.Party, workspaceContribution.Component),
                out var negativeContribution))
            return null;

        return string.IsNullOrWhiteSpace(negativeContribution.InterfaceRecordIdentifier)
            ? negativeContribution.Id.ToString("D")
            : negativeContribution.InterfaceRecordIdentifier;
    }

    private static void AddClone(IAlphaDbContext db, CloneResult clone)
    {
        db.ManualReports.Add(clone.Report);
        db.ManualReportEmployees.AddRange(clone.Employees);
        db.ManualReportProducts.AddRange(clone.Products);
        db.ManualContributions.AddRange(clone.Contributions);
        db.ManualReportPayments.AddRange(clone.Payments);
        db.EmployerInterfaceReportProductData.AddRange(clone.Metadata);
    }
}
