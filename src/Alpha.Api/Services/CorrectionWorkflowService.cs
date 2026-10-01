using Alpha.Api.Security;
using Alpha.Application.Abstractions;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

public static class CorrectionWorkflowService
{
    public sealed record WorkspaceResult(Guid ReportId, Guid? ReportProductId, bool Created, int PendingChanges);
    public sealed record MaterializedResult(Guid WorkspaceId, Guid SourceReportId, Guid? NegativeReportId,
        Guid? CurrentReportId, int PendingChanges, int RevisionNumber);

    private sealed record DeltaPlan(
        HashSet<Guid> NegativeSourceProductIds,
        HashSet<Guid> CurrentWorkspaceProductIds,
        int PendingChanges);

    private enum CloneMode { Workspace, NegativeCancellation, CurrentCorrection }

    private sealed record ReportGraph(
        ManualReport Report,
        List<ManualReportEmployee> Employees,
        List<ManualReportProduct> Products,
        List<ManualContribution> Contributions,
        List<ManualReportPayment> Payments,
        List<ManualReportAttachment> Attachments,
        Dictionary<Guid, EmployerInterfaceReportProductData> Metadata);

    private sealed record CloneResult(
        ManualReport Report,
        List<ManualReportEmployee> Employees,
        List<ManualReportProduct> Products,
        List<ManualContribution> Contributions,
        List<ManualReportPayment> Payments,
        List<ManualReportAttachment> Attachments,
        List<EmployerInterfaceReportProductData> Metadata,
        Dictionary<Guid, ManualReportProduct> ProductsBySource,
        Dictionary<(Guid ProductId, ContributionParty Party, ContributionComponent Component), ManualContribution> ContributionsBySourceKey);

    public static async Task<WorkspaceResult?> EnsureWorkspaceAsync(
        Guid organizationId, Guid employerId, Guid sourceReportId, Guid? sourceReportProductId,
        IAlphaDbContext db, IDataProtectionService protector, CancellationToken ct)
    {
        var source = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == sourceReportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (source is null || source.IsTechnicalCorrectionDocument
            || source.ReportKind != ManualReportKind.Current
            || source.Status is not (ManualReportStatus.Sent or ManualReportStatus.Completed))
            return null;

        var rootReportId = source.RevisionRootReportId ?? source.Id;
        var sourceRevisionNumber = source.IsRevisionSnapshot ? source.RevisionNumber : 1;
        var newerRevisionExists = await db.ManualReports.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == organizationId && x.EmployerId == employerId
            && x.IsRevisionSnapshot && x.RevisionRootReportId == rootReportId
            && x.RevisionNumber > sourceRevisionNumber && x.Status == ManualReportStatus.Completed, ct);
        if (newerRevisionExists) return null;

        var processingExists = await db.ManualReports.AsNoTracking().AnyAsync(x =>
            x.OrganizationId == organizationId && x.EmployerId == employerId
            && x.SourceReportId == sourceReportId && x.IsCorrectionWorkspace
            && x.Status == ManualReportStatus.Processing, ct);
        if (processingExists) return null;

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
                await PendingChangeCountAsync(existing.Id, db, protector, ct));
        }

        var graph = await LoadGraphAsync(sourceReportId, db, ct);
        if (graph is null || graph.Products.Count == 0)
            return null;
        if (sourceReportProductId.HasValue && graph.Products.All(x => x.Id != sourceReportProductId.Value))
            return null;

        var clone = CloneGraph(graph, ManualReportKind.Differences, sourceReportId,
            CloneMode.Workspace, null, protector, null, null);
        clone.Report.MarkCorrectionWorkspace(rootReportId, sourceRevisionNumber + 1);
        AddClone(db, clone);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException)
        {
            if (db is DbContext ef) ef.ChangeTracker.Clear();
            var concurrent = await db.ManualReports.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                    && x.SourceReportId == sourceReportId && x.IsCorrectionWorkspace
                    && (x.Status == ManualReportStatus.Draft || x.Status == ManualReportStatus.ReadyForValidation
                        || x.Status == ManualReportStatus.Error || x.Status == ManualReportStatus.Processing))
                .OrderByDescending(x => x.UpdatedAt).FirstOrDefaultAsync(CancellationToken.None);
            if (concurrent is null) throw;
            if (concurrent.Status == ManualReportStatus.Processing) return null;

            Guid? concurrentProductId = null;
            if (sourceReportProductId.HasValue)
                concurrentProductId = await (
                    from product in db.ManualReportProducts.AsNoTracking()
                    join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                    where employee.ReportId == concurrent.Id && product.SourceReportProductId == sourceReportProductId.Value
                    select (Guid?)product.Id).SingleOrDefaultAsync(CancellationToken.None);

            return new WorkspaceResult(concurrent.Id, concurrentProductId, false,
                await PendingChangeCountAsync(concurrent.Id, db, protector, CancellationToken.None));
        }

        Guid? requestedProductId = null;
        if (sourceReportProductId.HasValue
            && clone.ProductsBySource.TryGetValue(sourceReportProductId.Value, out var mapped))
            requestedProductId = mapped.Id;

        return new WorkspaceResult(clone.Report.Id, requestedProductId, true, 0);
    }

    public static async Task<MaterializedResult?> MaterializeAsync(
        Guid organizationId, Guid employerId, Guid workspaceId,
        IAlphaDbContext db, IDataProtectionService protector, CancellationToken ct)
    {
        var workspace = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == workspaceId && x.OrganizationId == organizationId && x.EmployerId == employerId
            && x.IsCorrectionWorkspace && x.SourceReportId.HasValue
            && (x.Status == ManualReportStatus.Draft || x.Status == ManualReportStatus.ReadyForValidation || x.Status == ManualReportStatus.Error), ct);
        if (workspace is null) return null;

        var sourceGraph = await LoadGraphAsync(workspace.SourceReportId!.Value, db, ct);
        var workspaceGraph = await LoadGraphAsync(workspace.Id, db, ct);
        if (sourceGraph is null || workspaceGraph is null
            || sourceGraph.Report.IsTechnicalCorrectionDocument
            || sourceGraph.Report.ReportKind != ManualReportKind.Current
            || sourceGraph.Report.Status is not (ManualReportStatus.Sent or ManualReportStatus.Completed))
            return null;

        var plan = BuildDeltaPlan(sourceGraph, workspaceGraph, protector);
        if (plan.PendingChanges == 0) return null;

        if (plan.NegativeSourceProductIds.Any(id => !sourceGraph.Metadata.ContainsKey(id)))
            return null;

        if (db is not DbContext ef)
            throw new InvalidOperationException("Correction materialization requires the EF Core database context.");

        await using var transaction = await ef.Database.BeginTransactionAsync(ct);
        try
        {
            var claimed = await db.ManualReports
                .Where(x => x.Id == workspaceId && x.OrganizationId == organizationId && x.EmployerId == employerId
                    && x.IsCorrectionWorkspace
                    && (x.Status == ManualReportStatus.Draft || x.Status == ManualReportStatus.ReadyForValidation || x.Status == ManualReportStatus.Error))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ManualReportStatus.Processing)
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), ct);
            if (claimed != 1)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            sourceGraph = await LoadGraphAsync(workspace.SourceReportId!.Value, db, ct);
            workspaceGraph = await LoadGraphAsync(workspace.Id, db, ct);
            if (sourceGraph is null || workspaceGraph is null)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            plan = BuildDeltaPlan(sourceGraph, workspaceGraph, protector);
            if (plan.PendingChanges == 0)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            CloneResult? negative = null;
            CloneResult? current = null;
            if (plan.NegativeSourceProductIds.Count > 0)
            {
                var negativeSource = FilterGraph(sourceGraph, plan.NegativeSourceProductIds);
                negative = CloneGraph(negativeSource, ManualReportKind.Negative, sourceGraph.Report.Id,
                    CloneMode.NegativeCancellation, null, protector, null, null);
                negative.Report.MarkTechnicalCorrectionDocument(workspace.Id,
                    workspace.RevisionRootReportId ?? sourceGraph.Report.RevisionRootReportId ?? sourceGraph.Report.Id,
                    workspace.RevisionNumber);
                AddClone(db, negative);
            }

            if (plan.CurrentWorkspaceProductIds.Count > 0)
            {
                var currentSource = FilterGraph(workspaceGraph, plan.CurrentWorkspaceProductIds);
                current = CloneGraph(currentSource, ManualReportKind.Current,
                    negative?.Report.Id ?? sourceGraph.Report.Id,
                    CloneMode.CurrentCorrection, null, protector,
                    negative?.ProductsBySource, negative?.ContributionsBySourceKey);
                current.Report.MarkTechnicalCorrectionDocument(workspace.Id,
                    workspace.RevisionRootReportId ?? sourceGraph.Report.RevisionRootReportId ?? sourceGraph.Report.Id,
                    workspace.RevisionNumber);
                AddClone(db, current);
            }

            if (negative is null && current is null)
            {
                await transaction.RollbackAsync(ct);
                return null;
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);

            return new MaterializedResult(workspace.Id, sourceGraph.Report.Id,
                negative?.Report.Id, current?.Report.Id, plan.PendingChanges, workspace.RevisionNumber);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await db.ManualReports.Where(x => x.Id == workspaceId
                    && x.OrganizationId == organizationId && x.EmployerId == employerId
                    && x.IsCorrectionWorkspace
                    && (x.Status == ManualReportStatus.Draft || x.Status == ManualReportStatus.ReadyForValidation
                        || x.Status == ManualReportStatus.Error || x.Status == ManualReportStatus.Processing))
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, ManualReportStatus.Error)
                    .SetProperty(x => x.ValidationError, "Correction materialization failed.")
                    .SetProperty(x => x.UpdatedAt, DateTimeOffset.UtcNow), CancellationToken.None);
            throw;
        }
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
            .GroupBy(CorrectionFundKey, StringComparer.Ordinal)
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
                || !sourceByFund.TryGetValue(CorrectionFundKey(product), out var previous))
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

    public static async Task<bool> FinalizeRevisionIfCompleteAsync(
        Guid technicalReportId, IAlphaDbContext db, CancellationToken ct)
    {
        var technical = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == technicalReportId && x.IsTechnicalCorrectionDocument
                && x.CorrectionWorkspaceId.HasValue, ct);
        if (technical is null) return false;

        var workspaceId = technical.CorrectionWorkspaceId!.Value;
        var documents = await db.ManualReports.AsNoTracking()
            .Where(x => x.CorrectionWorkspaceId == workspaceId && x.IsTechnicalCorrectionDocument)
            .ToListAsync(ct);
        if (documents.Count == 0
            || documents.Any(x => x.Status is not (ManualReportStatus.Sent or ManualReportStatus.Completed)))
            return false;

        var workspace = await db.ManualReports.SingleOrDefaultAsync(x =>
            x.Id == workspaceId && x.IsCorrectionWorkspace && x.Status == ManualReportStatus.Processing, ct);
        if (workspace is null) return false;

        var currentDocument = documents
            .Where(x => x.ReportKind == ManualReportKind.Current)
            .OrderByDescending(x => x.UpdatedAt)
            .FirstOrDefault();

        if (currentDocument is not null)
        {
            var currentEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
                .Where(x => x.ReportId == currentDocument.Id).Select(x => x.Id).ToArrayAsync(ct);
            var currentProducts = await db.ManualReportProducts.AsNoTracking()
                .Where(x => currentEmployeeIds.Contains(x.ReportEmployeeId)).ToListAsync(ct);
            var workspaceProductIds = currentProducts
                .Where(x => x.SourceReportProductId.HasValue)
                .Select(x => x.SourceReportProductId!.Value).Distinct().ToArray();

            var currentProductIds = currentProducts.Select(x => x.Id).ToArray();
            var currentMetadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
                .Where(x => currentProductIds.Contains(x.ReportProductId))
                .ToDictionaryAsync(x => x.ReportProductId, ct);
            var workspaceMetadata = await db.EmployerInterfaceReportProductData
                .Where(x => workspaceProductIds.Contains(x.ReportProductId))
                .ToDictionaryAsync(x => x.ReportProductId, ct);

            foreach (var currentProduct in currentProducts)
            {
                if (!currentProduct.SourceReportProductId.HasValue
                    || !workspaceMetadata.TryGetValue(currentProduct.SourceReportProductId.Value, out var target)
                    || !currentMetadata.TryGetValue(currentProduct.Id, out var sourceMetadata))
                    continue;

                target.SetInterfaceTransferIdentifier(
                    string.IsNullOrWhiteSpace(sourceMetadata.InterfaceTransferIdentifier)
                        ? currentProduct.Id.ToString("D").ToUpperInvariant()
                        : sourceMetadata.InterfaceTransferIdentifier);
                if (!string.IsNullOrWhiteSpace(sourceMetadata.ClearingIdentifier))
                    target.SetClearingIdentifier(sourceMetadata.ClearingIdentifier);
            }

            var currentContributions = await db.ManualContributions.AsNoTracking()
                .Where(x => currentProductIds.Contains(x.ReportProductId)).ToListAsync(ct);
            var workspaceContributions = await db.ManualContributions
                .Where(x => workspaceProductIds.Contains(x.ReportProductId)).ToListAsync(ct);
            var currentProductMap = currentProducts
                .Where(x => x.SourceReportProductId.HasValue)
                .ToDictionary(x => x.Id, x => x.SourceReportProductId!.Value);
            var workspaceContributionMap = workspaceContributions.ToDictionary(
                x => (x.ReportProductId, x.Party, x.Component));

            foreach (var currentContribution in currentContributions)
            {
                if (!currentProductMap.TryGetValue(currentContribution.ReportProductId, out var workspaceProductId)
                    || !workspaceContributionMap.TryGetValue(
                        (workspaceProductId, currentContribution.Party, currentContribution.Component), out var target))
                    continue;

                target.SetInterfaceRecordIdentifier(
                    string.IsNullOrWhiteSpace(currentContribution.InterfaceRecordIdentifier)
                        ? currentContribution.Id.ToString("D").ToUpperInvariant()
                        : currentContribution.InterfaceRecordIdentifier);
            }
        }

        workspace.PromoteCorrectionWorkspaceToRevision();
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static async Task<int> PendingChangeCountAsync(
        Guid reportId, IAlphaDbContext db, IDataProtectionService protector, CancellationToken ct)
    {
        var workspace = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reportId && x.IsCorrectionWorkspace && x.SourceReportId.HasValue, ct);
        if (workspace is null) return 0;
        var sourceGraph = await LoadGraphAsync(workspace.SourceReportId!.Value, db, ct);
        var workspaceGraph = await LoadGraphAsync(workspace.Id, db, ct);
        return sourceGraph is null || workspaceGraph is null
            ? 0
            : BuildDeltaPlan(sourceGraph, workspaceGraph, protector).PendingChanges;
    }

    private static DeltaPlan BuildDeltaPlan(
        ReportGraph source, ReportGraph workspace, IDataProtectionService protector)
    {
        var sourceProducts = source.Products.ToDictionary(x => x.Id);
        var workspaceBySource = workspace.Products
            .Where(x => x.SourceReportProductId.HasValue)
            .GroupBy(x => x.SourceReportProductId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.UpdatedAt).First());

        var removed = sourceProducts.Keys.Where(id => !workspaceBySource.ContainsKey(id)).ToHashSet();
        var added = workspace.Products.Where(x => !x.SourceReportProductId.HasValue).Select(x => x.Id).ToHashSet();
        var changed = new HashSet<Guid>();

        var sourceEmployees = source.Employees.ToDictionary(x => x.EmploymentId);
        var workspaceEmployees = workspace.Employees.ToDictionary(x => x.EmploymentId);
        var globalChange = !ReportHeaderEquivalent(source.Report, workspace.Report)
            || !ReportLevelAttachmentsEquivalent(source, workspace);

        foreach (var pair in sourceEmployees)
        {
            if (!workspaceEmployees.TryGetValue(pair.Key, out var currentEmployee)) continue;
            if (globalChange || !EmployeeEquivalent(source.Report, pair.Value, workspace.Report, currentEmployee, protector))
            {
                foreach (var sourceProduct in source.Products.Where(x => x.ReportEmployeeId == pair.Value.Id))
                    if (workspaceBySource.ContainsKey(sourceProduct.Id)) changed.Add(sourceProduct.Id);
            }
        }

        foreach (var pair in workspaceBySource)
        {
            if (!sourceProducts.TryGetValue(pair.Key, out var original)) continue;
            var current = pair.Value;
            if (!ProductEquivalent(original, current)
                || !ContributionSetEquivalent(source, original.Id, workspace, current.Id)
                || !PaymentEquivalent(source, original.Id, workspace, current.Id, protector)
                || !MetadataEquivalent(source.Metadata.GetValueOrDefault(original.Id), workspace.Metadata.GetValueOrDefault(current.Id))
                || !ProductAttachmentsEquivalent(source, original.Id, workspace, current.Id))
                changed.Add(original.Id);
        }

        if (globalChange)
            foreach (var sourceProductId in workspaceBySource.Keys)
                changed.Add(sourceProductId);

        var negative = removed.Concat(changed).ToHashSet();
        var current = added.ToHashSet();
        foreach (var sourceProductId in changed)
            if (workspaceBySource.TryGetValue(sourceProductId, out var workspaceProduct))
                current.Add(workspaceProduct.Id);

        return new DeltaPlan(negative, current, removed.Count + changed.Count + added.Count);
    }

    private static bool ReportHeaderEquivalent(ManualReport source, ManualReport workspace) =>
        source.ReportingMonth == workspace.ReportingMonth
        && source.SalaryPaymentDate == workspace.SalaryPaymentDate
        && source.PaymentAccountId == workspace.PaymentAccountId
        && source.PaymentBankId == workspace.PaymentBankId
        && source.PaymentBranchId == workspace.PaymentBranchId
        && string.Equals(source.PaymentAccountNumberMasked, workspace.PaymentAccountNumberMasked, StringComparison.Ordinal)
        && string.Equals(source.PaymentMandateReference, workspace.PaymentMandateReference, StringComparison.Ordinal)
        && string.Equals(source.EmployerLegalNameSnapshot, workspace.EmployerLegalNameSnapshot, StringComparison.Ordinal)
        && source.DepositorTypeCodeSnapshot == workspace.DepositorTypeCodeSnapshot
        && source.EmployerIdentifierTypeCodeSnapshot == workspace.EmployerIdentifierTypeCodeSnapshot;

    private static bool EmployeeEquivalent(
        ManualReport sourceReport, ManualReportEmployee source,
        ManualReport workspaceReport, ManualReportEmployee workspace,
        IDataProtectionService protector)
    {
        if (source.PersonId != workspace.PersonId
            || source.MonthlySalary != workspace.MonthlySalary
            || source.InterfaceIdentifierType != workspace.InterfaceIdentifierType
            || source.BirthDateSnapshot != workspace.BirthDateSnapshot
            || source.GenderSnapshot != workspace.GenderSnapshot
            || !string.Equals(source.FirstName, workspace.FirstName, StringComparison.Ordinal)
            || !string.Equals(source.LastName, workspace.LastName, StringComparison.Ordinal)
            || !string.Equals(source.EmployeeNumber, workspace.EmployeeNumber, StringComparison.Ordinal)
            || !string.Equals(source.CitySnapshot, workspace.CitySnapshot, StringComparison.Ordinal)
            || !string.Equals(source.StreetSnapshot, workspace.StreetSnapshot, StringComparison.Ordinal)
            || !string.Equals(source.HouseNumberSnapshot, workspace.HouseNumberSnapshot, StringComparison.Ordinal)
            || !string.Equals(source.ApartmentSnapshot, workspace.ApartmentSnapshot, StringComparison.Ordinal)
            || !string.Equals(source.PostalCodeSnapshot, workspace.PostalCodeSnapshot, StringComparison.Ordinal)
            || !string.Equals(source.PostOfficeBoxSnapshot, workspace.PostOfficeBoxSnapshot, StringComparison.Ordinal)
            || source.EmploymentStartDateSnapshot != workspace.EmploymentStartDateSnapshot)
            return false;

        var sourceIdentifier = protector.Unprotect(source.InterfaceIdentifier,
            $"report-employee-interface-id:{source.Id}");
        var workspaceIdentifier = protector.Unprotect(workspace.InterfaceIdentifier,
            $"report-employee-interface-id:{workspace.Id}");
        var sourceEmail = protector.Unprotect(source.EmailSnapshot, $"report-employee-email:{source.Id}");
        var workspaceEmail = protector.Unprotect(workspace.EmailSnapshot, $"report-employee-email:{workspace.Id}");
        var sourceMobile = protector.Unprotect(source.MobileSnapshot, $"report-employee-mobile:{source.Id}");
        var workspaceMobile = protector.Unprotect(workspace.MobileSnapshot, $"report-employee-mobile:{workspace.Id}");
        return string.Equals(sourceIdentifier, workspaceIdentifier, StringComparison.Ordinal)
            && string.Equals(sourceEmail, workspaceEmail, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sourceMobile, workspaceMobile, StringComparison.Ordinal);
    }

    private static bool ProductEquivalent(ManualReportProduct source, ManualReportProduct workspace) =>
        source.ProductType == workspace.ProductType
        && string.Equals(source.PolicyNumber, workspace.PolicyNumber, StringComparison.Ordinal)
        && source.SalaryMonth == workspace.SalaryMonth
        && source.Salary == workspace.Salary
        && string.Equals(source.ReportingType, workspace.ReportingType, StringComparison.Ordinal)
        && string.Equals(source.SalaryLayer, workspace.SalaryLayer, StringComparison.Ordinal)
        && source.Section14Code == workspace.Section14Code
        && source.Section14StartDate == workspace.Section14StartDate
        && string.Equals(source.FundExternalKey, workspace.FundExternalKey, StringComparison.Ordinal)
        && string.Equals(source.FundCode, workspace.FundCode, StringComparison.Ordinal)
        && string.Equals(source.FundName, workspace.FundName, StringComparison.Ordinal)
        && string.Equals(source.FundCompanyName, workspace.FundCompanyName, StringComparison.Ordinal)
        && string.Equals(source.FundClassification, workspace.FundClassification, StringComparison.Ordinal)
        && source.SalaryAllocationType == workspace.SalaryAllocationType
        && source.SalaryAllocationValue == workspace.SalaryAllocationValue
        && source.AllocationOrder == workspace.AllocationOrder;

    private static bool ContributionSetEquivalent(
        ReportGraph source, Guid sourceProductId, ReportGraph workspace, Guid workspaceProductId)
    {
        static string Key(ManualContribution x) => $"{(int)x.Party}:{(int)x.Component}";
        var sourceMap = source.Contributions.Where(x => x.ReportProductId == sourceProductId)
            .ToDictionary(Key, x => (x.Amount, x.Percentage, x.ExemptPayments));
        var workspaceMap = workspace.Contributions.Where(x => x.ReportProductId == workspaceProductId)
            .ToDictionary(Key, x => (x.Amount, x.Percentage, x.ExemptPayments));
        return sourceMap.Count == workspaceMap.Count
            && sourceMap.All(x => workspaceMap.TryGetValue(x.Key, out var value) && value == x.Value);
    }

    private static bool PaymentEquivalent(
        ReportGraph source, Guid sourceProductId, ReportGraph workspace, Guid workspaceProductId,
        IDataProtectionService protector)
    {
        var left = source.Payments.FirstOrDefault(x => x.ReportProductId == sourceProductId);
        var right = workspace.Payments.FirstOrDefault(x => x.ReportProductId == workspaceProductId);
        if (left is null || right is null) return left is null && right is null;
        var leftAccount = protector.Unprotect(left.EmployerAccount, $"report-payment-account:{sourceProductId}");
        var rightAccount = protector.Unprotect(right.EmployerAccount, $"report-payment-account:{workspaceProductId}");
        return string.Equals(left.ProviderName, right.ProviderName, StringComparison.Ordinal)
            && string.Equals(left.ProviderAccount, right.ProviderAccount, StringComparison.Ordinal)
            && string.Equals(left.PaymentMethod, right.PaymentMethod, StringComparison.Ordinal)
            && left.ValueDate == right.ValueDate
            && left.TrustAccountValueDate == right.TrustAccountValueDate
            && left.ActualDepositAmount == right.ActualDepositAmount
            && string.Equals(left.MasavSenderCode, right.MasavSenderCode, StringComparison.Ordinal)
            && string.Equals(left.ReferenceNumber, right.ReferenceNumber, StringComparison.Ordinal)
            && string.Equals(left.EmployerBankName, right.EmployerBankName, StringComparison.Ordinal)
            && string.Equals(left.EmployerBankCode, right.EmployerBankCode, StringComparison.Ordinal)
            && string.Equals(left.EmployerBranch, right.EmployerBranch, StringComparison.Ordinal)
            && string.Equals(leftAccount, rightAccount, StringComparison.Ordinal);
    }

    private static bool MetadataEquivalent(
        EmployerInterfaceReportProductData? source, EmployerInterfaceReportProductData? workspace)
    {
        if (source is null || workspace is null) return source is null && workspace is null;
        return source.DepositStatus == workspace.DepositStatus
            && source.EmployeeStatus == workspace.EmployeeStatus
            && source.StatusStartDate == workspace.StatusStartDate
            && source.EmploymentPercentage == workspace.EmploymentPercentage
            && source.WorkDaysInMonth == workspace.WorkDaysInMonth
            && source.LastDeposit == workspace.LastDeposit
            && source.RefundReason == workspace.RefundReason
            && source.PaymentMethodCode == workspace.PaymentMethodCode
            && source.EmployerAccountType == workspace.EmployerAccountType
            && source.ReceiverAccountType == workspace.ReceiverAccountType
            && source.OldPensionTypeCode == workspace.OldPensionTypeCode;
    }

    private static bool ReportLevelAttachmentsEquivalent(ReportGraph source, ReportGraph workspace)
    {
        static string Key(ManualReportAttachment x) => $"{x.DocumentTypeCode}:{x.Sha256}";
        return source.Attachments.Where(x => !x.ReportProductId.HasValue).Select(Key).Order().SequenceEqual(
            workspace.Attachments.Where(x => !x.ReportProductId.HasValue).Select(Key).Order());
    }

    private static bool ProductAttachmentsEquivalent(
        ReportGraph source, Guid sourceProductId, ReportGraph workspace, Guid workspaceProductId)
    {
        static string Key(ManualReportAttachment x) => $"{x.DocumentTypeCode}:{x.Sha256}";
        return source.Attachments.Where(x => x.ReportProductId == sourceProductId).Select(Key).Order().SequenceEqual(
            workspace.Attachments.Where(x => x.ReportProductId == workspaceProductId).Select(Key).Order());
    }

    private static ReportGraph FilterGraph(ReportGraph source, HashSet<Guid> productIds)
    {
        var products = source.Products.Where(x => productIds.Contains(x.Id)).ToList();
        var employeeIds = products.Select(x => x.ReportEmployeeId).ToHashSet();
        var employees = source.Employees.Where(x => employeeIds.Contains(x.Id)).ToList();
        var contributions = source.Contributions.Where(x => productIds.Contains(x.ReportProductId)).ToList();
        var payments = source.Payments.Where(x => productIds.Contains(x.ReportProductId)).ToList();
        var attachments = source.Attachments.Where(x => !x.ReportProductId.HasValue
            || productIds.Contains(x.ReportProductId.Value)).ToList();
        var metadata = source.Metadata.Where(x => productIds.Contains(x.Key))
            .ToDictionary(x => x.Key, x => x.Value);
        return new ReportGraph(source.Report, employees, products, contributions, payments, attachments, metadata);
    }

    private static async Task<bool> HasReportLevelChangesAsync(
        ManualReport workspace, IAlphaDbContext db, IDataProtectionService protector, CancellationToken ct)
    {
        if (!workspace.SourceReportId.HasValue) return false;
        var source = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == workspace.SourceReportId.Value, ct);
        if (source is null) return true;

        if (workspace.ReportingMonth != source.ReportingMonth
            || workspace.SalaryPaymentDate != source.SalaryPaymentDate
            || workspace.PaymentAccountId != source.PaymentAccountId
            || workspace.PaymentBankId != source.PaymentBankId
            || workspace.PaymentBranchId != source.PaymentBranchId
            || !string.Equals(workspace.PaymentAccountNumberMasked, source.PaymentAccountNumberMasked, StringComparison.Ordinal)
            || !string.Equals(workspace.PaymentMandateReference, source.PaymentMandateReference, StringComparison.Ordinal))
            return true;

        var sourceEmployees = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == source.Id).ToListAsync(ct);
        var workspaceEmployees = await db.ManualReportEmployees.AsNoTracking()
            .Where(x => x.ReportId == workspace.Id).ToListAsync(ct);
        if (sourceEmployees.Count != workspaceEmployees.Count) return true;

        var sourceByEmployment = sourceEmployees.ToDictionary(x => x.EmploymentId);
        foreach (var employee in workspaceEmployees)
        {
            if (!sourceByEmployment.TryGetValue(employee.EmploymentId, out var original)) return true;
            if (employee.PersonId != original.PersonId
                || employee.MonthlySalary != original.MonthlySalary
                || employee.InterfaceIdentifierType != original.InterfaceIdentifierType
                || !string.Equals(employee.NationalIdLookupHash, original.NationalIdLookupHash, StringComparison.Ordinal)
                || employee.BirthDateSnapshot != original.BirthDateSnapshot
                || employee.GenderSnapshot != original.GenderSnapshot
                || !string.Equals(employee.FirstName, original.FirstName, StringComparison.Ordinal)
                || !string.Equals(employee.LastName, original.LastName, StringComparison.Ordinal)
                || !string.Equals(employee.EmployeeNumber, original.EmployeeNumber, StringComparison.Ordinal)
                || !string.Equals(employee.CitySnapshot, original.CitySnapshot, StringComparison.Ordinal)
                || !string.Equals(employee.StreetSnapshot, original.StreetSnapshot, StringComparison.Ordinal)
                || !string.Equals(employee.HouseNumberSnapshot, original.HouseNumberSnapshot, StringComparison.Ordinal)
                || !string.Equals(employee.ApartmentSnapshot, original.ApartmentSnapshot, StringComparison.Ordinal)
                || !string.Equals(employee.PostalCodeSnapshot, original.PostalCodeSnapshot, StringComparison.Ordinal)
                || !string.Equals(employee.PostOfficeBoxSnapshot, original.PostOfficeBoxSnapshot, StringComparison.Ordinal)
                || employee.EmploymentStartDateSnapshot != original.EmploymentStartDateSnapshot)
                return true;

            var workspaceInterfaceIdentifier = protector.Unprotect(employee.InterfaceIdentifier,
                $"report-employee-interface-id:{employee.Id}");
            var sourceInterfaceIdentifier = protector.Unprotect(original.InterfaceIdentifier,
                $"report-employee-interface-id:{original.Id}");
            var workspaceEmail = protector.Unprotect(employee.EmailSnapshot,
                $"report-employee-email:{employee.Id}");
            var sourceEmail = protector.Unprotect(original.EmailSnapshot,
                $"report-employee-email:{original.Id}");
            var workspaceMobile = protector.Unprotect(employee.MobileSnapshot,
                $"report-employee-mobile:{employee.Id}");
            var sourceMobile = protector.Unprotect(original.MobileSnapshot,
                $"report-employee-mobile:{original.Id}");
            if (!string.Equals(workspaceInterfaceIdentifier, sourceInterfaceIdentifier, StringComparison.Ordinal)
                || !string.Equals(workspaceEmail, sourceEmail, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(workspaceMobile, sourceMobile, StringComparison.Ordinal))
                return true;
        }
        return false;
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
        var attachments = await db.ManualReportAttachments.AsNoTracking()
            .Where(x => x.ReportId == reportId).ToListAsync(ct);
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);

        return new ReportGraph(report, employees, products, contributions, payments, attachments, metadata);
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
            .GroupBy(CorrectionFundKey, StringComparer.Ordinal)
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
            if (mode == CloneMode.CurrentCorrection
                && oldProduct.ValidationStatus == ManualReportItemStatus.Error
                && string.Equals(oldProduct.ValidationError, "המוצר הוסר מטיוטת התיקון.", StringComparison.Ordinal))
                continue;

            var clone = new ManualReportProduct(employeeMap[oldProduct.ReportEmployeeId].Id,
                oldProduct.ProductType, oldProduct.PolicyNumber, oldProduct.SalaryMonth, oldProduct.Salary,
                oldProduct.ReportingType, oldProduct.SalaryLayer, oldProduct.Section14,
                oldProduct.Section14StartDate, oldProduct.FundExternalKey, oldProduct.FundCode,
                oldProduct.FundName, oldProduct.FundCompanyName, oldProduct.SalaryAllocationType,
                oldProduct.SalaryAllocationValue, oldProduct.AllocationOrder, oldProduct.Section14Code,
                oldProduct.FundClassification);
            clone.SetSourceVersion(oldProduct.Id);
            if (mode == CloneMode.CurrentCorrection && oldProduct.IsCorrectionChanged)
                clone.MarkCorrectionChanged(oldProduct.CorrectionOperationCode);
            products.Add(clone);
            productMap[oldProduct.Id] = clone;

            source.Metadata.TryGetValue(oldProduct.Id, out var oldMetadata);
            if (oldMetadata is null && mode == CloneMode.CurrentCorrection && !oldProduct.SourceReportProductId.HasValue)
            {
                var templateProduct = source.Products.FirstOrDefault(x => x.Id != oldProduct.Id
                    && string.Equals(CorrectionFundKey(x), CorrectionFundKey(oldProduct), StringComparison.Ordinal)
                    && source.Metadata.ContainsKey(x.Id));
                if (templateProduct is not null) oldMetadata = source.Metadata[templateProduct.Id];
            }
            if (oldMetadata is null)
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
                    sourceTransferByFund[CorrectionFundKey(oldProduct)],
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
                    var operation = oldProduct.IsCorrectionChanged ? oldProduct.CorrectionOperationCode ?? 2 : 2;
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
            if (!productMap.ContainsKey(oldContribution.ReportProductId)) continue;
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
            if (!productMap.TryGetValue(oldPayment.ReportProductId, out var mappedPaymentProduct)) continue;
            var clonedProductId = mappedPaymentProduct.Id;
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

        if (mode == CloneMode.CurrentCorrection)
        {
            foreach (var oldProduct in source.Products.Where(x => productMap.ContainsKey(x.Id)))
            {
                var clonedProductId = productMap[oldProduct.Id].Id;
                if (payments.Any(x => x.ReportProductId == clonedProductId)) continue;
                var templateProduct = source.Products.FirstOrDefault(x => x.Id != oldProduct.Id
                    && string.Equals(CorrectionFundKey(x), CorrectionFundKey(oldProduct), StringComparison.Ordinal)
                    && source.Payments.Any(p => p.ReportProductId == x.Id));
                if (templateProduct is null) continue;
                var templatePayment = source.Payments.First(x => x.ReportProductId == templateProduct.Id);
                var account = protector.Unprotect(templatePayment.EmployerAccount,
                    $"report-payment-account:{templatePayment.ReportProductId}");
                var inherited = new ManualReportPayment(clonedProductId);
                inherited.Update(templatePayment.ProviderName, templatePayment.ProviderAccount, templatePayment.PaymentMethod,
                    templatePayment.ValueDate, templatePayment.TrustAccountValueDate, templatePayment.ReferenceNumber,
                    templatePayment.EmployerBankName, templatePayment.EmployerBankCode, templatePayment.EmployerBranch,
                    protector.Protect(account, $"report-payment-account:{clonedProductId}"),
                    templatePayment.ConfirmationFileName, templatePayment.ActualDepositAmount, templatePayment.MasavSenderCode);
                payments.Add(inherited);
            }
        }

        var attachments = new List<ManualReportAttachment>();
        if (mode is CloneMode.Workspace or CloneMode.CurrentCorrection)
        {
            foreach (var oldAttachment in source.Attachments)
            {
                Guid? mappedProductId = null;
                if (oldAttachment.ReportProductId.HasValue)
                {
                    if (!productMap.TryGetValue(oldAttachment.ReportProductId.Value, out var mappedProduct)) continue;
                    mappedProductId = mappedProduct.Id;
                }

                var plain = protector.UnprotectBytes(oldAttachment.Content,
                    $"report-attachment:{source.Report.Id}:{oldAttachment.ReportProductId}:{oldAttachment.DocumentTypeCode}");
                var protectedContent = protector.ProtectBytes(plain,
                    $"report-attachment:{report.Id}:{mappedProductId}:{oldAttachment.DocumentTypeCode}");
                attachments.Add(new ManualReportAttachment(report.Id, mappedProductId, oldAttachment.DocumentTypeCode,
                    oldAttachment.OriginalFileName, oldAttachment.ContentType, protectedContent,
                    oldAttachment.SizeBytes, oldAttachment.Sha256));
            }
        }

        return new CloneResult(report, employees, products, contributions, payments,
            attachments, metadataRows, productMap, contributionMap);
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

    private static string CorrectionFundKey(ManualReportProduct product) =>
        !string.IsNullOrWhiteSpace(product.FundExternalKey)
            ? product.FundExternalKey.Trim()
            : $"{product.FundCode.Trim()}|{product.FundCompanyName.Trim()}";

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
        db.ManualReportAttachments.AddRange(clone.Attachments);
        db.EmployerInterfaceReportProductData.AddRange(clone.Metadata);
    }
}
