using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Api.Security;
using Alpha.Api.Services;
using Alpha.Domain.Auditing;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class ReportFeedbackEndpoints
{
    private sealed record TreatmentStatusOption(string Code, string Label);
    private static readonly TreatmentStatusOption[] TreatmentStatuses =
    [
        new("manual-refund-completed", "בוצע החזר ידני על ידי הקופה"),
        new("mobility-completed", "בוצע ניוד/מעבר לקופה אחרת"),
        new("file-retransmitted-completed", "בוצע שידור חוזר לקובץ"),
        new("negative-correction-completed", "בוצע שלילי-תיקון תנועות"),
        new("refund-request-excess", "בקשת החזר (שלילי)-כסף עודף"),
        new("refund-request-wrong-fund", "בקשת החזר (שלילי)-קופה שגויה"),
        new("employer-negative-cancelled", "הועבר קובץ שלילי ממעסיק-בוטל ביצרן"),
        new("agent-ownership-pending", "יש לפנות לסוכן ממתין לביצוע קבלת בעלות"),
        new("agent-policy-pending", "יש לפנות לסוכן ממתין להפקת פוליסה"),
        new("agent-changes-pending", "יש לפנות לסוכן ממתין לביצוע שינויים"),
        new("refund-not-approved", "לא אושר החזר (כסף לא הוחזר)"),
        new("reopened-after-received", "לטיפול חוזר לאחר שהוגדר נקלט ושונה"),
        new("refund-pending", "ממתין לביצוע החזר"),
        new("company-inquiry-pending", "ממתין לברור בחברה"),
        new("split-report-pending", "ממתין לדוח פיצול"),
        new("payment-report-pending", "ממתין לדוח תשלומים"),
        new("employer-declaration-pending", "ממתין להצהרת מעסיק (צרופה)"),
        new("treatment-pending", "ממתין לטיפול"),
        new("company-treatment-pending", "ממתין לטיפול בחברה"),
        new("fault-treatment-pending", "ממתין לטיפול בתקלה"),
        new("internal-treatment-pending", "ממתין לטיפול פנימי"),
        new("employer-power-of-attorney-pending", "ממתין ליפוי כח מהמעסיק"),
        new("employer-documents-pending", "ממתין למסמכים ממעסיק"),
        new("fund-power-of-attorney-update-pending", "ממתין לעידכון יפוי כח בקופה"),
        new("manual-settlement-pending", "ממתין לפרעון ידני"),
        new("reference-pending", "ממתין לקבלת אסמכתא"),
        new("negative-file-pending", "ממתין לקובץ שלילי"),
        new("negative-and-payroll-change-pending", "ממתין לקובץ שלילי ושינוי בתוכנת שכר"),
        new("employer-file-transmission-pending", "ממתין לשידור קובץ מעסיק"),
        new("payroll-and-corrective-file-pending", "ממתין לתיקון בת.השכר וק.מתקן"),
        new("employer-response-pending", "ממתין לתשובת מעסיק"),
        new("payroll-change-required", "נדרש שינוי בתוכנת השכר"),
        new("received", "נקלט"),
        new("received-with-excess", "נקלט ונשאר עודף בפיקדון-לברר מול סוכן"),
        new("partially-received-fault-pending", "נקלט חלקי- ממתין לטיפול בתקלה"),
        new("manufacturer-received-changed", "נקלט שונה ביצרן"),
        new("duplicate-file-cancelled", "קובץ כפול ממעסיק-בוטל ביצרן"),
        new("invalid-file-cancelled", "קובץ שגוי ממעסיק-בוטל ביצרן"),
        new("file-retransmission", "שידור חוזר לקובץ"),
    ];

    public static IEndpointRouteBuilder MapReportFeedbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/report-feedback")
            .RequireAuthorization().WithTags("Report feedback");
        group.MapGet("/", ListAsync);
        group.MapGet("/employer-context", EmployerContextAsync);
        group.MapGet("/treatment-statuses", TreatmentStatusOptionsAsync);
        group.MapGet("/{reportId:guid}", DetailsAsync);
        group.MapGet("/{reportId:guid}/context", ReportContextAsync);
        group.MapGet("/{reportId:guid}/deposits", DepositListAsync);
        group.MapGet("/{reportId:guid}/deposits/{reportProductId:guid}", DepositDetailsAsync);
        group.MapPut("/{reportId:guid}/deposits/{reportProductId:guid}/treatment", UpdateTreatmentAsync);
        group.MapGet("/{reportId:guid}/exports/{exportType}", ExportAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(
        Guid organizationId, Guid employerId, string? feedbackStatus, string? month, string? reportKind,
        string? search, string? product, string? treatmentStatus, bool? requiresAttention, int skip, int take,
        IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var employerName = await db.Employers.AsNoTracking()
            .Where(x => x.Id == employerId && x.OrganizationId == organizationId)
            .Select(x => x.LegalName)
            .SingleOrDefaultAsync(ct) ?? string.Empty;
        var canCreateReport = await access.CanCreateReportAsync(organizationId, employerId, ct);
        skip = Math.Max(skip, 0); take = Math.Clamp(take == 0 ? 50 : take, 1, 100);
        var query = db.ManualReports.AsNoTracking().Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
            && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument);

        if (!string.IsNullOrWhiteSpace(month))
        {
            var raw = month.Trim().Length == 7 ? month.Trim() + "-01" : month.Trim();
            if (DateOnly.TryParse(raw, out var parsed))
            {
                var target = new DateOnly(parsed.Year, parsed.Month, 1);
                query = query.Where(x => x.ReportingMonth == target);
            }
        }
        if (int.TryParse(reportKind, out var kind) && Enum.IsDefined(typeof(ManualReportKind), kind))
            query = query.Where(x => (int)x.ReportKind == kind);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            var idHash = protector.LookupHash(search.Trim(), "report-employee-national-id-lookup");
            var byEmployee = db.ManualReportEmployees.AsNoTracking()
                .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                    && (x.FirstName.ToLower().Contains(term) || x.LastName.ToLower().Contains(term) || x.NationalIdLookupHash == idHash))
                .Select(x => x.ReportId);
            var byProduct = from p in db.ManualReportProducts.AsNoTracking()
                            join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                            where e.OrganizationId == organizationId && e.EmployerId == employerId
                                && (p.FundName.ToLower().Contains(term) || p.FundCompanyName.ToLower().Contains(term) || p.PolicyNumber.ToLower().Contains(term))
                            select e.ReportId;
            query = query.Where(x => byEmployee.Contains(x.Id) || byProduct.Contains(x.Id));
        }

        if (!string.IsNullOrWhiteSpace(product))
        {
            var term = product.Trim().ToLower();
            var matches = from p in db.ManualReportProducts.AsNoTracking()
                          join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                          where e.OrganizationId == organizationId && e.EmployerId == employerId
                              && (p.FundName.ToLower().Contains(term) || p.FundCompanyName.ToLower().Contains(term) || p.FundCode.ToLower().Contains(term))
                          select e.ReportId;
            query = query.Where(x => matches.Contains(x.Id));
        }

        if (!string.IsNullOrWhiteSpace(treatmentStatus))
        {
            var matches = from t in db.ReportProductTreatments.AsNoTracking()
                          join p in db.ManualReportProducts.AsNoTracking() on t.ReportProductId equals p.Id
                          join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                          where e.OrganizationId == organizationId && e.EmployerId == employerId && t.StatusCode == treatmentStatus
                          select e.ReportId;
            query = query.Where(x => matches.Contains(x.Id));
        }

        var candidateIds = await query.Select(x => x.Id).ToListAsync(ct);
        if (candidateIds.Count == 0) return Results.Ok(new { items = Array.Empty<object>(), hasMore = false, summary = new { total = 0, completed = 0, attention = 0, pending = 0 } });

        var transmissions = await db.ReportTransmissions.AsNoTracking().Where(x => candidateIds.Contains(x.ReportId))
            .OrderByDescending(x => x.AttemptNumber).ToListAsync(ct);
        var latestTransmission = transmissions.GroupBy(x => x.ReportId).ToDictionary(g => g.Key, g => g.First());
        var feedbackFiles = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(x => x.ReportId.HasValue && candidateIds.Contains(x.ReportId.Value))
            .Select(x => new { x.Id, ReportId = x.ReportId!.Value, x.TransmissionId })
            .ToListAsync(ct);
        var activeFeedbackFiles = feedbackFiles.Where(x =>
        {
            latestTransmission.TryGetValue(x.ReportId, out var tx);
            return tx is null ? x.TransmissionId is null : x.TransmissionId == tx.Id;
        }).ToArray();
        var activeFeedbackIds = activeFeedbackFiles.Select(x => x.Id).ToHashSet();
        var officialCounts = activeFeedbackFiles
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Count());
        var expectedContributionCounts = await (
                from contribution in db.ManualContributions.AsNoTracking()
                join reportProduct in db.ManualReportProducts.AsNoTracking() on contribution.ReportProductId equals reportProduct.Id
                join employee in db.ManualReportEmployees.AsNoTracking() on reportProduct.ReportEmployeeId equals employee.Id
                where candidateIds.Contains(employee.ReportId)
                    && (contribution.Amount != 0m || contribution.Percentage != 0m || contribution.ExemptPayments != 0m)
                group contribution by employee.ReportId into g
                select new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var contributionFeedbackRows = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
            .Where(x => candidateIds.Contains(x.ReportId) && activeFeedbackIds.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt)
            .ThenByDescending(x => x.CreatedAt)
            .Select(x => new { x.ReportId, x.ReportProductId, x.ContributionId, x.ErrorCode, x.ReceivedAt, x.CreatedAt })
            .ToListAsync(ct);
        var latestContributionFeedback = contributionFeedbackRows
            .GroupBy(x => x.ContributionId)
            .Select(g => g.First())
            .ToArray();
        var feedbackContributionCounts = latestContributionFeedback
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ContributionId).Distinct().Count());
        var errorCounts = latestContributionFeedback
            .Where(x => ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode))
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ContributionId).Distinct().Count());
        var attentionProductCounts = latestContributionFeedback
            .Where(x => ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode))
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ReportProductId).Distinct().Count());
        var reportIssueCounts = latestContributionFeedback
            .Where(x => ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode))
            .Where(x =>
            {
                var scope = EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode);
                return scope is EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Report
                    or EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Money;
            })
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.ErrorCode).Distinct().Count());

        var completedRevisionIds = await db.ManualReports.AsNoTracking()
            .Where(x => candidateIds.Contains(x.Id) && x.IsRevisionSnapshot
                && x.Status == ManualReportStatus.Completed)
            .Select(x => x.Id).ToHashSetAsync(ct);

        string State(Guid id)
        {
            if (completedRevisionIds.Contains(id)) return "completed";
            latestTransmission.TryGetValue(id, out var tx);
            return ReportFeedbackStatusResolver.ResolveReportState(
                tx?.Status,
                officialCounts.GetValueOrDefault(id),
                expectedContributionCounts.GetValueOrDefault(id),
                feedbackContributionCounts.GetValueOrDefault(id),
                errorCounts.GetValueOrDefault(id));
        }

        var normalizedStatus = feedbackStatus?.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(normalizedStatus) && normalizedStatus != "all")
            candidateIds = candidateIds.Where(id => State(id) == normalizedStatus).ToList();
        if (requiresAttention == true)
            candidateIds = candidateIds.Where(id => State(id) == "attention").ToList();
        if (candidateIds.Count == 0) return Results.Ok(new { items = Array.Empty<object>(), hasMore = false, summary = new { total = 0, completed = 0, attention = 0, pending = 0 } });

        var summary = new
        {
            total = candidateIds.Count,
            completed = candidateIds.Count(id => State(id) == "completed"),
            attention = candidateIds.Sum(id => attentionProductCounts.GetValueOrDefault(id)),
            pending = candidateIds.Count(id => State(id) is "pending" or "partial")
        };

        var page = await db.ManualReports.AsNoTracking().Where(x => candidateIds.Contains(x.Id))
            .OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.CreatedAt)
            .Skip(skip).Take(take + 1).ToListAsync(ct);
        var hasMore = page.Count > take; if (hasMore) page.RemoveAt(page.Count - 1);
        var pageIds = page.Select(x => x.Id).ToArray();

        var correctionWorkspaces = await db.ManualReports.AsNoTracking()
            .Where(x => x.IsCorrectionWorkspace && x.SourceReportId.HasValue
                && pageIds.Contains(x.SourceReportId.Value)
                && (x.Status == ManualReportStatus.Draft
                    || x.Status == ManualReportStatus.ReadyForValidation
                    || x.Status == ManualReportStatus.Error))
            .Select(x => new { x.Id, SourceReportId = x.SourceReportId!.Value, x.HasCorrectionChanges })
            .ToListAsync(ct);
        var workspaceBySource = correctionWorkspaces.ToDictionary(x => x.SourceReportId);
        var workspaceChangedCounts = new Dictionary<Guid, int>();
        foreach (var correctionWorkspace in correctionWorkspaces)
            workspaceChangedCounts[correctionWorkspace.Id] =
                await CorrectionWorkflowService.PendingChangeCountAsync(correctionWorkspace.Id, db, protector, ct);

        var employeeCounts = await db.ManualReportEmployees.AsNoTracking().Where(x => pageIds.Contains(x.ReportId))
            .GroupBy(x => x.ReportId).Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var productCounts = await (
                from reportProduct in db.ManualReportProducts.AsNoTracking()
                join employee in db.ManualReportEmployees.AsNoTracking() on reportProduct.ReportEmployeeId equals employee.Id
                where pageIds.Contains(employee.ReportId)
                group reportProduct by employee.ReportId into g
                select new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var pageOperations = await (
                from metadata in db.EmployerInterfaceReportProductData.AsNoTracking()
                join reportProduct in db.ManualReportProducts.AsNoTracking() on metadata.ReportProductId equals reportProduct.Id
                join employee in db.ManualReportEmployees.AsNoTracking() on reportProduct.ReportEmployeeId equals employee.Id
                where pageIds.Contains(employee.ReportId)
                select new { employee.ReportId, metadata.OperationCode })
            .ToListAsync(ct);
        var metadataCounts = pageOperations
            .GroupBy(x => x.ReportId)
            .ToDictionary(g => g.Key, g => g.Count());
        var operation6Reports = pageOperations
            .GroupBy(x => x.ReportId)
            .Where(g => g.Count() == productCounts.GetValueOrDefault(g.Key) && g.All(x => x.OperationCode == 6))
            .Select(g => g.Key)
            .ToHashSet();
        var totals = await (from c in db.ManualContributions.AsNoTracking()
                            join p in db.ManualReportProducts.AsNoTracking() on c.ReportProductId equals p.Id
                            join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                            where pageIds.Contains(e.ReportId)
                            group c by e.ReportId into g select new { Id = g.Key, Total = g.Sum(x => x.Amount) })
            .ToDictionaryAsync(x => x.Id, x => x.Total, ct);
        var transferRows = await db.EmployerInterfaceTransferFeedback.AsNoTracking()
            .Where(x => pageIds.Contains(x.ReportId) && activeFeedbackIds.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).ToListAsync(ct);
        var money = transferRows.GroupBy(x => new { x.ReportId, x.TransferIdentifier }).Select(g => g.First())
            .GroupBy(x => x.ReportId).ToDictionary(g => g.Key, g => new
            {
                Reported = g.Sum(x => x.ReportedDepositAmount),
                Allocated = g.Sum(x => x.AllocatedAmount),
                Received = g.Sum(x => x.ActualReceivedAmount),
                InTransit = g.Sum(x => x.InTransitAmount)
            });

        var revisionRoots = page.Select(x => x.RevisionRootReportId ?? x.Id).Distinct().ToArray();
        var latestRevisionByRoot = await db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                && x.IsRevisionSnapshot && x.RevisionRootReportId.HasValue
                && revisionRoots.Contains(x.RevisionRootReportId.Value)
                && x.Status == ManualReportStatus.Completed)
            .GroupBy(x => x.RevisionRootReportId!.Value)
            .Select(g => new { RootId = g.Key, RevisionNumber = g.Max(x => x.RevisionNumber) })
            .ToDictionaryAsync(x => x.RootId, x => x.RevisionNumber, ct);

        var items = page.Select(report =>
        {
            latestTransmission.TryGetValue(report.Id, out var tx); money.TryGetValue(report.Id, out var cash);
            var rootId = report.RevisionRootReportId ?? report.Id;
            var hasNewerRevision = latestRevisionByRoot.TryGetValue(rootId, out var latestRevisionNumber)
                && (!report.IsRevisionSnapshot || report.RevisionNumber < latestRevisionNumber);
            var total = totals.GetValueOrDefault(report.Id);
            var payoffRate = cash is null
                ? null
                : ReportFeedbackStatusResolver.ResolvePayoffRate(cash.Reported, cash.Allocated);
            var issues = errorCounts.GetValueOrDefault(report.Id)
                + (string.IsNullOrWhiteSpace(report.ValidationError) ? 0 : 1)
                + (tx is null || string.IsNullOrWhiteSpace(tx.ErrorMessage) ? 0 : 1);
            return new
            {
                report.Id, employerName, report.ReportingMonth, report.SalaryPaymentDate, report.ReportKind, report.Status,
                revisionRootReportId = rootId, report.RevisionNumber, report.IsRevisionSnapshot,
                feedbackStatus = State(report.Id), hasFeedback = officialCounts.GetValueOrDefault(report.Id) > 0,
                issueCount = issues, requiresAttentionCount = attentionProductCounts.GetValueOrDefault(report.Id), reportIssueCount = reportIssueCounts.GetValueOrDefault(report.Id),
                employeeCount = employeeCounts.GetValueOrDefault(report.Id), totalAmount = total, payoffRate,
                allocatedAmount = cash?.Allocated, actualReceivedAmount = cash?.Received, inTransitAmount = cash?.InTransit,
                canEdit = canCreateReport && report.IsEditable,
                canDelete = canCreateReport && report.IsEditable
                    && tx is null && officialCounts.GetValueOrDefault(report.Id) == 0,
                canStartCorrectionWorkspace = canCreateReport
                    && report.ReportKind == ManualReportKind.Current
                    && report.Status is ManualReportStatus.Sent or ManualReportStatus.Completed
                    && !hasNewerRevision,
                correctionWorkspaceId = workspaceBySource.TryGetValue(report.Id, out var workspace)
                    ? workspace.Id : (Guid?)null,
                pendingCorrectionCount = workspaceBySource.TryGetValue(report.Id, out var pendingWorkspace)
                    ? workspaceChangedCounts.GetValueOrDefault(pendingWorkspace.Id)
                    : 0,
                canCreateCorrection = ReportFeedbackStatusResolver.CanCreateCorrection(
                    canCreateReport, report.IsEditable, report.Status, report.ReportKind,
                    productCounts.GetValueOrDefault(report.Id) > 0
                        && metadataCounts.GetValueOrDefault(report.Id) == productCounts.GetValueOrDefault(report.Id),
                    operation6Reports.Contains(report.Id)),
                report.CreatedAt, report.UpdatedAt,
                lastTransmission = tx is null ? null : new { tx.Id, tx.AttemptNumber, tx.Status, tx.Provider, tx.ExternalId, tx.ErrorMessage, tx.StartedAt, tx.SentAt, tx.CompletedAt }
            };
        }).ToList();
        return Results.Ok(new { items, hasMore, summary });
    }

    private static async Task<IResult> TreatmentStatusOptionsAsync(Guid organizationId, Guid employerId, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        return Results.Ok(TreatmentStatuses.Select(x => new { x.Code, x.Label }));
    }

    private static async Task<IResult> DepositListAsync(
        Guid organizationId, Guid employerId, Guid reportId, string? search, string? manufacturer, int skip, int take,
        IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        skip = Math.Max(0, skip); take = Math.Clamp(take == 0 ? 50 : take, 1, 100);
        var query = from p in db.ManualReportProducts.AsNoTracking()
                    join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                    where e.ReportId == reportId select new { Product = p, Employee = e };

        var manufacturers = await query
            .Select(x => string.IsNullOrWhiteSpace(x.Product.FundCompanyName) ? x.Product.FundName : x.Product.FundCompanyName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .OrderBy(x => x)
            .ToListAsync(ct);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            var idHash = protector.LookupHash(search.Trim(), "report-employee-national-id-lookup");
            query = query.Where(x => x.Employee.FirstName.ToLower().Contains(term) || x.Employee.LastName.ToLower().Contains(term)
                || x.Employee.NationalIdLookupHash == idHash || x.Product.FundName.ToLower().Contains(term)
                || x.Product.FundCompanyName.ToLower().Contains(term) || x.Product.PolicyNumber.ToLower().Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(manufacturer))
        {
            var manufacturerTerm = manufacturer.Trim().ToLower();
            query = query.Where(x =>
                (!string.IsNullOrWhiteSpace(x.Product.FundCompanyName)
                    ? x.Product.FundCompanyName.ToLower()
                    : x.Product.FundName.ToLower()) == manufacturerTerm);
        }
        var page = await query.OrderBy(x => x.Employee.LastName).ThenBy(x => x.Employee.FirstName)
            .ThenBy(x => x.Product.AllocationOrder).ThenBy(x => x.Product.CreatedAt).Skip(skip).Take(take + 1).ToListAsync(ct);
        var hasMore = page.Count > take; if (hasMore) page.RemoveAt(page.Count - 1);
        var productIds = page.Select(x => x.Product.Id).ToArray();
        var correctionWorkspace = await db.ManualReports.AsNoTracking()
            .Where(x => x.IsCorrectionWorkspace && x.SourceReportId == reportId
                && (x.Status == ManualReportStatus.Draft
                    || x.Status == ManualReportStatus.ReadyForValidation
                    || x.Status == ManualReportStatus.Error))
            .Select(x => new { x.Id, x.HasCorrectionChanges })
            .SingleOrDefaultAsync(ct);

        var pendingBySourceProduct = new Dictionary<Guid, (Guid ProductId, bool Changed)>();
        if (correctionWorkspace is not null)
        {
            var workspaceEmployeeIds = await db.ManualReportEmployees.AsNoTracking()
                .Where(x => x.ReportId == correctionWorkspace.Id)
                .Select(x => x.Id)
                .ToArrayAsync(ct);
            var workspaceProducts = await db.ManualReportProducts.AsNoTracking()
                .Where(x => workspaceEmployeeIds.Contains(x.ReportEmployeeId)
                    && x.SourceReportProductId.HasValue
                    && productIds.Contains(x.SourceReportProductId.Value))
                .Select(x => new
                {
                    SourceId = x.SourceReportProductId!.Value,
                    ProductId = x.Id,
                    x.IsCorrectionChanged
                })
                .ToListAsync(ct);
            pendingBySourceProduct = workspaceProducts.ToDictionary(
                x => x.SourceId,
                x => (x.ProductId, x.IsCorrectionChanged));
        }

        var activeFeedbackIdsForReport = await ActiveFeedbackIdsAsync(reportId, db, ct);

        var totals = await db.ManualContributions.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId)
                && (x.Amount != 0m || x.Percentage != 0m || x.ExemptPayments != 0m))
            .GroupBy(x => x.ReportProductId).Select(g => new { Id = g.Key, Total = g.Sum(x => x.Amount), Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, ct);
        var feedback = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId) && activeFeedbackIdsForReport.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).ToListAsync(ct);
        var latestFeedback = feedback
            .GroupBy(x => x.ContributionId)
            .SelectMany(group =>
            {
                var latest = group.First();
                return group.Where(x => x.FeedbackId == latest.FeedbackId);
            })
            .GroupBy(x => x.ReportProductId)
            .ToDictionary(g => g.Key, g => g.ToArray());
        var treatments = await db.ReportProductTreatments.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId))
            .ToDictionaryAsync(x => x.ReportProductId, ct);
        var transfers = await db.EmployerInterfaceTransferFeedback.AsNoTracking()
            .Where(x => x.ReportId == reportId && activeFeedbackIdsForReport.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).ToListAsync(ct);
        var latestTransfers = transfers.GroupBy(x => x.TransferIdentifier, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var labels = TreatmentStatuses.ToDictionary(x => x.Code, x => x.Label, StringComparer.Ordinal);

        var items = page.Select(x =>
        {
            latestFeedback.TryGetValue(x.Product.Id, out var rows); treatments.TryGetValue(x.Product.Id, out var treatment);
            metadata.TryGetValue(x.Product.Id, out var productMetadata); totals.TryGetValue(x.Product.Id, out var total);
            var expected = total?.Count ?? 0; var received = rows?.Length ?? 0;
            var actionableFeedback = (rows ?? [])
                .Where(item => ReportFeedbackStatusResolver.IsActionableFeedbackError(item.ErrorCode))
                .ToArray();
            var actionableErrors = actionableFeedback
                .Select(item => string.IsNullOrWhiteSpace(item.ErrorDescription)
                    ? $"קוד שגיאה {item.ErrorCode}"
                    : item.ErrorDescription.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var errorSummary = actionableFeedback
                .Where(item => item.ErrorCode.HasValue)
                .Select(item => new
                {
                    Code = item.ErrorCode!.Value,
                    Scope = EmployerInterfaceLineFeedbackParser.ErrorScope(item.ErrorCode)
                })
                .Distinct()
                .GroupBy(item => item.Scope)
                .ToDictionary(
                    group => group.Key.ToString().ToLowerInvariant(),
                    group => group.Count(),
                    StringComparer.Ordinal);
            var hasError = actionableErrors.Length > 0;
            var feedbackState = received == 0 ? "pending" : hasError ? "attention" : received < expected ? "partial" : "completed";
            var transferKey = !string.IsNullOrWhiteSpace(productMetadata?.InterfaceTransferIdentifier)
                ? productMetadata.InterfaceTransferIdentifier : x.Product.Id.ToString("D").ToUpperInvariant();
            latestTransfers.TryGetValue(transferKey, out var transfer);
            var totalAmount = total?.Total ?? 0m;
            var moneyState = transfer is null ? "pending" : ReportFeedbackStatusResolver.ResolveMoneyState(
                transfer.ReportedDepositAmount, transfer.ActualReceivedAmount, transfer.AllocatedAmount, transfer.InTransitAmount);
            var timestamps = new List<DateTimeOffset>();
            if (rows is { Length: > 0 }) timestamps.Add(rows.Max(item => item.ReceivedAt));
            if (treatment is not null) timestamps.Add(treatment.UpdatedAt);
            if (transfer is not null) timestamps.Add(transfer.ReceivedAt);
            return new
            {
                id = x.Product.Id, reportEmployeeId = x.Employee.Id, x.Employee.EmploymentId,
                employeeName = x.Employee.FirstName + " " + x.Employee.LastName,
                x.Product.ProductType, x.Product.FundName, x.Product.FundCompanyName, x.Product.PolicyNumber, x.Product.SalaryMonth,
                totalAmount, hasFeedback = received > 0 || transfer is not null, feedbackStatus = feedbackState,
                feedbackLabel = feedbackState switch { "completed" => "נקלט", "attention" => "דורש טיפול", "partial" => "משוב חלקי", _ => "ממתין למשוב" },
                feedbackErrors = actionableErrors,
                feedbackErrorSummary = new
                {
                    deposit = errorSummary.GetValueOrDefault("deposit"),
                    employee = errorSummary.GetValueOrDefault("employee"),
                    money = errorSummary.GetValueOrDefault("money"),
                    report = errorSummary.GetValueOrDefault("report"),
                    contribution = errorSummary.GetValueOrDefault("contribution")
                },
                moneyStatus = moneyState,
                moneyStatusLabel = moneyState switch { "allocated" => "שויך במלואו", "in-transit" => "כספים במעבר", "received-partial" => "נקלט חלקית", "unresolved" => "טרם שויך", _ => "אין משוב כספי" },
                treatmentStatus = treatment?.StatusCode ?? "",
                treatmentStatusLabel = treatment is null ? "" : labels.GetValueOrDefault(treatment.StatusCode) ?? treatment.StatusCode,
                updatedAt = timestamps.Count == 0 ? (DateTimeOffset?)null : timestamps.Max(),
                requiresAttention = hasError,
                pendingCorrectionReportId = correctionWorkspace?.Id,
                pendingCorrectionProductId = pendingBySourceProduct.TryGetValue(x.Product.Id, out var pending)
                    ? pending.ProductId : (Guid?)null,
                hasPendingCorrection = correctionWorkspace is not null
                    && (!pendingBySourceProduct.TryGetValue(x.Product.Id, out var correctionProduct)
                        || correctionProduct.Changed)
            };
        });
        return Results.Ok(new { items, hasMore, manufacturers });
    }

    private static async Task<IResult> DepositDetailsAsync(
        Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId,
        IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var canCreateReport = await access.CanCreateReportAsync(organizationId, employerId, ct);
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        var row = await (from p in db.ManualReportProducts.AsNoTracking()
                         join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                         where p.Id == reportProductId && e.ReportId == reportId select new { Product = p, Employee = e })
            .SingleOrDefaultAsync(ct);
        if (row is null) return Results.NotFound();

        var activeFeedbackIdsForReport = await ActiveFeedbackIdsAsync(reportId, db, ct);
        var contributions = await db.ManualContributions.AsNoTracking()
            .Where(x => x.ReportProductId == reportProductId
                && (x.Amount != 0m || x.Percentage != 0m || x.ExemptPayments != 0m))
            .OrderBy(x => x.Party).ThenBy(x => x.Component).ToListAsync(ct);
        var feedbackRows = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
            .Where(x => x.ReportProductId == reportProductId && activeFeedbackIdsForReport.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).ThenBy(x => x.Sequence).ToListAsync(ct);
        var manufacturer = feedbackRows.GroupBy(x => x.ContributionId).SelectMany(group =>
        {
            var latest = group.First(); return group.Where(x => x.FeedbackId == latest.FeedbackId).OrderBy(x => x.Sequence);
        }).ToArray();
        var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking().SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        var transferKey = !string.IsNullOrWhiteSpace(metadata?.InterfaceTransferIdentifier)
            ? metadata.InterfaceTransferIdentifier : reportProductId.ToString("D").ToUpperInvariant();
        var transfer = await db.EmployerInterfaceTransferFeedback.AsNoTracking()
            .Where(x => x.ReportId == reportId && x.TransferIdentifier == transferKey
                && activeFeedbackIdsForReport.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).FirstOrDefaultAsync(ct);
        var payment = await db.ManualReportPayments.AsNoTracking().SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        var treatment = await db.ReportProductTreatments.AsNoTracking().SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        var history = await db.ReportProductTreatmentHistory.AsNoTracking().Where(x => x.ReportProductId == reportProductId)
            .OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct);
        var userIds = history.Select(x => x.UpdatedByUserId).Append(treatment?.UpdatedByUserId ?? Guid.Empty).Where(x => x != Guid.Empty).Distinct().ToArray();
        var users = await db.Users.AsNoTracking().Where(x => userIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.DisplayName, ct);
        var labels = TreatmentStatuses.ToDictionary(x => x.Code, x => x.Label, StringComparer.Ordinal);

        static int TypeCode(ManualContribution c) => (c.Party, c.Component) switch
        {
            (ContributionParty.Employer, ContributionComponent.Severance) => 1,
            (ContributionParty.Employee, ContributionComponent.Benefits) => 2,
            (ContributionParty.Employer, ContributionComponent.Benefits) => 3,
            (ContributionParty.Employee, ContributionComponent.Severance) => 4,
            (ContributionParty.Employee, ContributionComponent.Disability) => 5,
            (ContributionParty.Employer, ContributionComponent.Disability) => 6,
            (ContributionParty.Employee, ContributionComponent.Other) => 7,
            (ContributionParty.Employer, ContributionComponent.Other) => 8,
            _ => 0
        };
        static string Label(ManualContribution c) => (c.Party, c.Component) switch
        {
            (ContributionParty.Employee, ContributionComponent.Benefits) => "תגמולי עובד",
            (ContributionParty.Employer, ContributionComponent.Benefits) => "תגמולי מעביד",
            (ContributionParty.Employer, ContributionComponent.Severance) => "פיצויים",
            (ContributionParty.Employee, ContributionComponent.Severance) => "תגמולים 47",
            (ContributionParty.Employee, ContributionComponent.Disability) => "אכ״ע עובד",
            (ContributionParty.Employer, ContributionComponent.Disability) => "אכ״ע מעסיק",
            (ContributionParty.Employee, ContributionComponent.Other) => "רכיב עובד נוסף",
            (ContributionParty.Employer, ContributionComponent.Other) => "רכיב מעסיק נוסף",
            _ => "רכיב הפרשה"
        };

        return Results.Ok(new
        {
            report = new { report.Id, report.ReportingMonth, report.ReportKind, report.Status, canEdit = report.IsEditable && canCreateReport },
            employee = new
            {
                row.Employee.Id, row.Employee.EmploymentId, name = row.Employee.FirstName + " " + row.Employee.LastName,
                nationalId = protector.Unprotect(row.Employee.NationalId, $"report-employee-national-id:{row.Employee.Id}"),
                row.Employee.MonthlySalary
            },
            product = new
            {
                row.Product.Id, row.Product.FundName, row.Product.FundCompanyName, row.Product.FundCode,
                row.Product.PolicyNumber, row.Product.SalaryMonth, row.Product.Salary, row.Product.ReportingType,
                totalAmount = contributions.Sum(x => x.Amount)
            },
            employerContributions = contributions.Select(x => new
            {
                id = x.Id, contributionTypeCode = TypeCode(x), label = Label(x), x.Amount, x.Percentage, x.ExemptPayments
            }),
            manufacturerContributions = manufacturer.Select(x => new
            {
                x.ContributionId, x.RecordIdentifier, x.Sequence, x.IntakeStatus, x.ErrorCode, x.ErrorDescription,
                errorScope = EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode).ToString().ToLowerInvariant(),
                x.ErrorAmount, x.ErrorDate, x.ContributionTypeCode, x.CalculatedSalary, x.SalaryMonth, x.PolicyNumber,
                x.ContributionRate, x.ContributionAmount, x.SourceFileName, x.ReceivedAt
            }),
            money = transfer is null ? null : new
            {
                transfer.ReportedDepositAmount, transfer.ActualReceivedAmount, transfer.AllocatedAmount, transfer.InTransitAmount,
                transfer.ProactiveRefundAmount, transfer.EmployerAccountRefundAmount, transfer.MoneyTreatmentStatus,
                transfer.StatusDetail, transfer.PaymentReference, transfer.ValueDate, transfer.TrustAccountValueDate,
                transfer.CorrectnessTimestamp, transfer.ClearingIdentifier, transfer.ReceivedAt
            },
            payment = payment is null ? null : new
            {
                payment.ProviderName, payment.ProviderAccount, payment.PaymentMethod, payment.ValueDate, payment.TrustAccountValueDate,
                payment.ActualDepositAmount, payment.MasavSenderCode, payment.ReferenceNumber, payment.EmployerBankName,
                payment.EmployerBankCode, payment.EmployerBranch,
                employerAccount = protector.Unprotect(payment.EmployerAccount, $"report-payment-account:{payment.ReportProductId}")
            },
            treatment = treatment is null ? null : new
            {
                treatment.StatusCode, label = labels.GetValueOrDefault(treatment.StatusCode) ?? treatment.StatusCode,
                treatment.Note, treatment.UpdatedAt, updatedBy = users.GetValueOrDefault(treatment.UpdatedByUserId) ?? "משתמש"
            },
            treatmentHistory = history.Select(x => new
            {
                x.PreviousStatusCode, previousStatusLabel = labels.GetValueOrDefault(x.PreviousStatusCode) ?? x.PreviousStatusCode,
                x.StatusCode, statusLabel = labels.GetValueOrDefault(x.StatusCode) ?? x.StatusCode, x.Note, x.CreatedAt,
                updatedBy = users.GetValueOrDefault(x.UpdatedByUserId) ?? "משתמש"
            }),
            canUpdateTreatment = canCreateReport,
            canCreateCorrection = ReportFeedbackStatusResolver.CanCreateCorrection(
                canCreateReport, report.IsEditable, report.Status, report.ReportKind,
                metadata is not null, metadata?.OperationCode == 6)
        });
    }


    private static object FeedbackIssue(EmployerInterfaceContributionFeedback row) => new
    {
        code = row.ErrorCode ?? 0,
        description = string.IsNullOrWhiteSpace(row.ErrorDescription)
            ? EmployerInterfaceLineFeedbackParser.Description(row.ErrorCode)
            : row.ErrorDescription,
        scope = EmployerInterfaceLineFeedbackParser.ErrorScope(row.ErrorCode).ToString().ToLowerInvariant(),
        row.ReportId,
        row.ReportProductId,
        row.ContributionId,
        row.ReceivedAt
    };

    private static async Task<IReadOnlyList<EmployerInterfaceContributionFeedback>> ActiveActionableFeedbackAsync(
        IReadOnlyCollection<Guid> reportIds, IAlphaDbContext db, CancellationToken ct)
    {
        if (reportIds.Count == 0) return Array.Empty<EmployerInterfaceContributionFeedback>();
        var activeFeedbackIds = new HashSet<Guid>();
        foreach (var reportId in reportIds)
            activeFeedbackIds.UnionWith(await ActiveFeedbackIdsAsync(reportId, db, ct));

        if (activeFeedbackIds.Count == 0) return Array.Empty<EmployerInterfaceContributionFeedback>();

        var rows = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
            .Where(x => reportIds.Contains(x.ReportId) && activeFeedbackIds.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt)
            .ThenByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        return rows
            .GroupBy(x => x.ContributionId)
            .SelectMany(group =>
            {
                var latest = group.First();
                return group.Where(x => x.FeedbackId == latest.FeedbackId).OrderBy(x => x.Sequence);
            })
            .Where(x => ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode))
            .ToArray();
    }

    private static async Task<IResult> EmployerContextAsync(
        Guid organizationId, Guid employerId, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();

        var employer = await db.Employers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (employer is null) return Results.NotFound();

        var reportIds = await db.ManualReports.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId
                && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument)
            .Select(x => x.Id)
            .ToListAsync(ct);

        var rows = await ActiveActionableFeedbackAsync(reportIds, db, ct);
        var issues = rows
            .Where(x => EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode)
                == EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Money)
            .GroupBy(x => new { x.ErrorCode, x.ErrorDescription })
            .Select(g => FeedbackIssue(g.OrderByDescending(x => x.ReceivedAt).First()))
            .ToArray();

        return Results.Ok(new
        {
            employer = new
            {
                employer.Id,
                employer.LegalName,
                employer.RegistrationNumber,
                employer.WithholdingFileNumber,
                status = employer.Status.ToString(),
                contactName = string.Join(" ", new[] { employer.ContactFirstName, employer.ContactLastName }
                    .Where(x => !string.IsNullOrWhiteSpace(x))),
                employer.ContactPhone,
                employer.ContactEmail,
                employer.ContactMobile
            },
            issues
        });
    }

    private static async Task<IResult> ReportContextAsync(
        Guid organizationId, Guid employerId, Guid reportId, IAlphaDbContext db,
        OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();

        var report = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId
                && x.EmployerId == employerId && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument, ct);
        if (report is null) return Results.NotFound();

        var employer = await db.Employers.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct);
        if (employer is null) return Results.NotFound();

        var rows = await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct);
        var scopedIssues = rows
            .Where(x =>
            {
                var scope = EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode);
                return scope is EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Report
                    or EmployerInterfaceLineFeedbackParser.FeedbackErrorScope.Money;
            })
            .GroupBy(x => new { x.ErrorCode, x.ErrorDescription, Scope = EmployerInterfaceLineFeedbackParser.ErrorScope(x.ErrorCode) })
            .Select(g => FeedbackIssue(g.First()))
            .ToList<object>();

        var latestTransmission = await db.ReportTransmissions.AsNoTracking()
            .Where(x => x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId)
            .OrderByDescending(x => x.AttemptNumber)
            .FirstOrDefaultAsync(ct);

        if (!string.IsNullOrWhiteSpace(report.ValidationError))
            scopedIssues.Insert(0, new
            {
                code = 0,
                description = report.ValidationError,
                scope = "report",
                ReportId = report.Id,
                ReportProductId = (Guid?)null,
                ContributionId = (Guid?)null,
                ReceivedAt = report.UpdatedAt
            });
        if (latestTransmission is not null && !string.IsNullOrWhiteSpace(latestTransmission.ErrorMessage))
            scopedIssues.Insert(0, new
            {
                code = 0,
                description = latestTransmission.ErrorMessage,
                scope = "report",
                ReportId = report.Id,
                ReportProductId = (Guid?)null,
                ContributionId = (Guid?)null,
                ReceivedAt = latestTransmission.UpdatedAt
            });

        var employeeCount = await db.ManualReportEmployees.AsNoTracking().CountAsync(x => x.ReportId == reportId, ct);
        var productIds = await (from product in db.ManualReportProducts.AsNoTracking()
                                join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                                where employee.ReportId == reportId
                                select product.Id).ToArrayAsync(ct);
        var totalAmount = productIds.Length == 0
            ? 0m
            : await db.ManualContributions.AsNoTracking()
                .Where(x => productIds.Contains(x.ReportProductId))
                .SumAsync(x => x.Amount, ct);

        return Results.Ok(new
        {
            employer = new { employer.Id, employer.LegalName },
            report = new
            {
                report.Id,
                report.ReportingMonth,
                report.SalaryPaymentDate,
                report.ReportKind,
                report.Status,
                employeeCount,
                totalAmount,
                report.CreatedAt,
                report.UpdatedAt
            },
            issues = scopedIssues
        });
    }

    private static async Task<IResult> UpdateTreatmentAsync(
        Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId, UpdateTreatmentRequest request,
        IAlphaDbContext db, OrganizationAccessService access, ICurrentUser currentUser, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!TreatmentStatuses.Any(x => x.Code == request.StatusCode)) return Results.BadRequest(new { error = "treatment_status_invalid" });
        if ((request.Note?.Length ?? 0) > 4000) return Results.BadRequest(new { error = "treatment_note_too_long" });
        var belongs = await (from p in db.ManualReportProducts.AsNoTracking()
                             join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                             where p.Id == reportProductId && e.ReportId == reportId && e.OrganizationId == organizationId && e.EmployerId == employerId
                             select p.Id).AnyAsync(ct);
        if (!belongs) return Results.NotFound();

        var treatment = await db.ReportProductTreatments.SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        if (!ReportFeedbackStatusResolver.TreatmentVersionMatches(treatment?.UpdatedAt, request.ExpectedUpdatedAt))
            return Results.Conflict(new { error = "treatment_conflict" });
        var creatingTreatment = treatment is null;
        var previous = treatment?.StatusCode ?? string.Empty;
        if (treatment is null)
        {
            treatment = new ReportProductTreatment(reportProductId, request.StatusCode, request.Note, currentUser.UserId);
            db.ReportProductTreatments.Add(treatment);
        }
        else treatment.Update(request.StatusCode, request.Note, currentUser.UserId);
        db.ReportProductTreatmentHistory.Add(new ReportProductTreatmentHistory(reportProductId, previous, request.StatusCode, request.Note, currentUser.UserId));
        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "report-feedback.treatment-updated",
            nameof(ReportProductTreatment),
            treatment.Id,
            organizationId,
            employerId,
            JsonSerializer.Serialize(new { reportId, reportProductId, previousStatus = previous, statusCode = request.StatusCode }),
            http.TraceIdentifier));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { error = "treatment_conflict" });
        }
        catch (DbUpdateException) when (creatingTreatment)
        {
            var existsNow = await db.ReportProductTreatments.AsNoTracking()
                .AnyAsync(x => x.ReportProductId == reportProductId, CancellationToken.None);
            if (existsNow) return Results.Conflict(new { error = "treatment_conflict" });
            throw;
        }
        return Results.Ok(new
        {
            treatment.StatusCode, label = TreatmentStatuses.First(x => x.Code == request.StatusCode).Label,
            treatment.Note, treatment.UpdatedAt
        });
    }

    private static async Task<IResult> ExportAsync(
        Guid organizationId, Guid employerId, Guid reportId, string exportType,
        IAlphaDbContext db, OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        var employees = await db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == reportId).ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking().Where(x => employeeIds.Contains(x.ReportEmployeeId)).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking()
            .Where(x => productIds.Contains(x.ReportProductId)
                && (x.Amount != 0m || x.Percentage != 0m || x.ExemptPayments != 0m))
            .ToListAsync(ct);
        var employeeById = employees.ToDictionary(x => x.Id);
        var productById = products.ToDictionary(x => x.Id);

        static string ContributionName(ManualContribution c) => (c.Party, c.Component) switch
        {
            (ContributionParty.Employee, ContributionComponent.Benefits) => "תגמולי עובד",
            (ContributionParty.Employer, ContributionComponent.Benefits) => "תגמולי מעביד",
            (ContributionParty.Employer, ContributionComponent.Severance) => "פיצויים",
            (ContributionParty.Employee, ContributionComponent.Severance) => "תגמולים 47",
            (ContributionParty.Employee, ContributionComponent.Disability) => "אכ״ע עובד",
            (ContributionParty.Employer, ContributionComponent.Disability) => "אכ״ע מעסיק",
            (ContributionParty.Employee, ContributionComponent.Other) => "רכיב עובד נוסף",
            (ContributionParty.Employer, ContributionComponent.Other) => "רכיב מעסיק נוסף",
            _ => "רכיב הפרשה"
        };

        var rows = new List<IReadOnlyList<object?>>();
        var normalized = exportType.Trim().ToLowerInvariant();
        var sheetName = "דוח";

        if (normalized == "contributions")
        {
            sheetName = "פירוט הפרשות";
            rows.Add(new object?[] { "חודש דיווח", "עובד", "מזהה עובד", "יצרן", "מוצר", "פוליסה/חשבון", "רכיב", "שכר מבוטח", "שיעור", "סכום" });
            foreach (var contribution in contributions.OrderBy(x => x.ReportProductId).ThenBy(x => x.Party).ThenBy(x => x.Component))
            {
                var product = productById[contribution.ReportProductId];
                var employee = employeeById[product.ReportEmployeeId];
                rows.Add(new object?[]
                {
                    report.ReportingMonth.ToString("yyyy-MM"),
                    employee.FirstName + " " + employee.LastName,
                    protector.Unprotect(employee.NationalId, $"report-employee-national-id:{employee.Id}"),
                    product.FundCompanyName,
                    product.FundName,
                    product.PolicyNumber,
                    ContributionName(contribution),
                    product.Salary,
                    contribution.Percentage,
                    contribution.Amount
                });
            }
        }
        else if (normalized == "deposits")
        {
            sheetName = "סיכום הפקדות";
            var payments = await db.ManualReportPayments.AsNoTracking()
                .Where(x => productIds.Contains(x.ReportProductId)).ToDictionaryAsync(x => x.ReportProductId, ct);
            var totals = contributions.GroupBy(x => x.ReportProductId).ToDictionary(g => g.Key, g => g.Sum(x => x.Amount));
            rows.Add(new object?[] { "חודש דיווח", "עובד", "יצרן", "מוצר", "פוליסה/חשבון", "סכום כולל", "אמצעי תשלום", "חשבון יצרן", "אסמכתא", "תאריך ערך" });
            foreach (var product in products.OrderBy(x => employeeById[x.ReportEmployeeId].LastName).ThenBy(x => x.FundCompanyName))
            {
                var employee = employeeById[product.ReportEmployeeId];
                payments.TryGetValue(product.Id, out var payment);
                rows.Add(new object?[]
                {
                    report.ReportingMonth.ToString("yyyy-MM"),
                    employee.FirstName + " " + employee.LastName,
                    product.FundCompanyName,
                    product.FundName,
                    product.PolicyNumber,
                    totals.GetValueOrDefault(product.Id),
                    payment?.PaymentMethod ?? string.Empty,
                    payment?.ProviderAccount ?? string.Empty,
                    payment?.ReferenceNumber ?? string.Empty,
                    payment?.ValueDate?.ToString("dd/MM/yyyy") ?? string.Empty
                });
            }
        }
        else if (normalized == "feedback")
        {
            sheetName = "משוב יצרן";
            var activeFeedbackIdsForReport = await ActiveFeedbackIdsAsync(reportId, db, ct);
            var feedbackHistory = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
                .Where(x => x.ReportId == reportId && activeFeedbackIdsForReport.Contains(x.FeedbackId))
                .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.CreatedAt)
                .ThenBy(x => x.RecordIdentifier).ThenBy(x => x.Sequence)
                .ToListAsync(ct);
            var feedback = feedbackHistory.GroupBy(x => x.ContributionId).SelectMany(group =>
            {
                var latest = group.First();
                return group.Where(x => x.FeedbackId == latest.FeedbackId).OrderBy(x => x.Sequence);
            }).ToArray();
            var contributionById = contributions.ToDictionary(x => x.Id);
            rows.Add(new object?[] { "עובד", "יצרן", "מוצר", "רכיב", "סכום מעסיק", "שיעור מעסיק", "סכום יצרן", "שיעור יצרן", "שכר מחושב יצרן", "סטטוס קליטה", "קוד שגיאה", "פירוט", "תאריך משוב", "קובץ מקור" });
            foreach (var item in feedback)
            {
                if (!contributionById.TryGetValue(item.ContributionId, out var contribution)) continue;
                var product = productById[item.ReportProductId];
                var employee = employeeById[product.ReportEmployeeId];
                rows.Add(new object?[]
                {
                    employee.FirstName + " " + employee.LastName,
                    product.FundCompanyName,
                    product.FundName,
                    ContributionName(contribution),
                    contribution.Amount,
                    contribution.Percentage,
                    item.ContributionAmount,
                    item.ContributionRate,
                    item.CalculatedSalary,
                    item.IntakeStatus,
                    item.ErrorCode,
                    item.ErrorDescription,
                    item.ReceivedAt.ToString("dd/MM/yyyy HH:mm:ss"),
                    item.SourceFileName
                });
            }
        }
        else
        {
            return Results.BadRequest(new { error = "export_type_invalid" });
        }

        var bytes = ExcelWorkbookBuilder.Build(sheetName, rows);
        var fileLabel = normalized switch
        {
            "contributions" => "פירוט עובדים והפרשות",
            "deposits" => "סיכום הפקדות",
            _ => "משוב קופות"
        };
        var fileName = $"{fileLabel} - {report.ReportingMonth:MM-yyyy}.xlsx";
        return Results.File(
            bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }

    private static async Task<HashSet<Guid>> ActiveFeedbackIdsAsync(
        Guid reportId, IAlphaDbContext db, CancellationToken ct)
    {
        var latestTransmissionId = await db.ReportTransmissions.AsNoTracking()
            .Where(x => x.ReportId == reportId)
            .OrderByDescending(x => x.AttemptNumber)
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);

        var query = db.EmployerInterfaceFeedback.AsNoTracking().Where(x => x.ReportId == reportId);
        query = latestTransmissionId.HasValue
            ? query.Where(x => x.TransmissionId == latestTransmissionId.Value)
            : query.Where(x => x.TransmissionId == null);
        return (await query.Select(x => x.Id).ToListAsync(ct)).ToHashSet();
    }

    private static async Task<IResult> DetailsAsync(
        Guid organizationId, Guid employerId, Guid reportId, IAlphaDbContext db,
        OrganizationAccessService access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(
            x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        var transmissions = await db.ReportTransmissions.AsNoTracking()
            .Where(x => x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId)
            .OrderByDescending(x => x.AttemptNumber).ToListAsync(ct);
        var latest = transmissions.FirstOrDefault();
        var officialFeedback = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.ReportId == reportId)
            .OrderByDescending(x => x.ReceivedAt)
            .Select(x => new { x.Id, x.DocumentType, x.SourceFileName, x.InterfaceFileNumber, x.PayloadHash, x.TransmissionId, x.ReceivedAt })
            .ToListAsync(ct);
        var activeFeedbackIdsForReport = await ActiveFeedbackIdsAsync(reportId, db, ct);
        var employees = await db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == reportId).ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking().Where(x => employeeIds.Contains(x.ReportEmployeeId)).ToListAsync(ct);
        var employeesById = employees.ToDictionary(x => x.Id);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var productsByRecordId = contributions
            .GroupBy(x => (string.IsNullOrWhiteSpace(x.InterfaceRecordIdentifier) ? x.Id.ToString("D") : x.InterfaceRecordIdentifier).ToUpperInvariant(), StringComparer.Ordinal)
            .Where(group => group.Select(x => x.ReportProductId).Distinct().Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().ReportProductId, StringComparer.Ordinal);
        var recordFeedback = new Dictionary<Guid, List<EmployerInterfaceLineFeedbackParser.RecordStatus>>();
        foreach (var file in await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.ReportId == reportId
                && activeFeedbackIdsForReport.Contains(x.Id))
            .OrderByDescending(x => x.ReceivedAt).ToListAsync(ct))
        {
            var xml = protector.Unprotect(file.RawXml, $"employer-interface-feedback:{file.PayloadHash}");
            foreach (var item in EmployerInterfaceLineFeedbackParser.Parse(xml))
            {
                if (!productsByRecordId.TryGetValue(item.RecordIdentifier, out var productId)) continue;
                if (!recordFeedback.TryGetValue(productId, out var matches)) recordFeedback[productId] = matches = [];
                if (matches.Any(x => x.RecordIdentifier == item.RecordIdentifier)) continue;
                matches.Add(item with { SourceFileName = file.SourceFileName, ReceivedAt = file.ReceivedAt });
            }
        }
        var depositFeedback = products.Select(productRow =>
        {
            recordFeedback.TryGetValue(productRow.Id, out var statuses);
            return new { reportProductId = productRow.Id, hasRecordFeedback = statuses is { Count: > 0 }, records = statuses ?? [] };
        }).ToArray();

        var activeContributionRows = await db.EmployerInterfaceContributionFeedback.AsNoTracking()
            .Where(x => x.ReportId == reportId && activeFeedbackIdsForReport.Contains(x.FeedbackId))
            .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.CreatedAt)
            .Select(x => new { x.ContributionId, x.ErrorCode })
            .ToListAsync(ct);
        var latestActiveContributionRows = activeContributionRows.GroupBy(x => x.ContributionId).Select(g => g.First()).ToArray();
        var expectedContributionCount = contributions.Count(ReportFeedbackStatusResolver.IsEffectiveContribution);
        var activeErrorCount = latestActiveContributionRows.Count(x => ReportFeedbackStatusResolver.IsActionableFeedbackError(x.ErrorCode));
        var normalizedFeedbackStatus = ReportFeedbackStatusResolver.ResolveReportState(
            latest?.Status,
            activeFeedbackIdsForReport.Count,
            expectedContributionCount,
            latestActiveContributionRows.Length,
            activeErrorCount);

        var issues = new List<object>();
        if (!string.IsNullOrWhiteSpace(report.ValidationError))
            issues.Add(new { source = "report", code = "REPORT_VALIDATION", description = report.ValidationError, employeeId = (Guid?)null, employeeName = (string?)null, productId = (Guid?)null, productName = (string?)null, actionType = "EditReport" });
        if (latest is not null && !string.IsNullOrWhiteSpace(latest.ErrorMessage))
            issues.Add(new { source = "transmission", code = latest.Status.ToString().ToUpperInvariant(), description = latest.ErrorMessage, employeeId = (Guid?)null, employeeName = (string?)null, productId = (Guid?)null, productName = (string?)null, actionType = "RetryTransmission" });
        foreach (var employee in employees.Where(x => x.ValidationStatus == ManualReportItemStatus.Error && !string.IsNullOrWhiteSpace(x.ValidationError)))
            issues.Add(new { source = "employee", code = "EMPLOYEE_VALIDATION", description = employee.ValidationError, employeeId = (Guid?)employee.EmploymentId, employeeName = $"{employee.FirstName} {employee.LastName}", productId = (Guid?)null, productName = (string?)null, actionType = "EditReport" });
        foreach (var productRow in products.Where(x => x.ValidationStatus == ManualReportItemStatus.Error && !string.IsNullOrWhiteSpace(x.ValidationError)))
        {
            employeesById.TryGetValue(productRow.ReportEmployeeId, out var employee);
            issues.Add(new { source = "product", code = "PRODUCT_VALIDATION", description = productRow.ValidationError, employeeId = employee?.EmploymentId, employeeName = employee is null ? null : $"{employee.FirstName} {employee.LastName}", productId = (Guid?)productRow.Id, productName = string.IsNullOrWhiteSpace(productRow.FundName) ? productRow.PolicyNumber : productRow.FundName, actionType = "EditReport" });
        }

        return Results.Ok(new
        {
            report = new { report.Id, report.ReportingMonth, report.SalaryPaymentDate, report.ReportKind, report.Status, report.ValidationError, report.CreatedAt, report.UpdatedAt },
            feedbackStatus = normalizedFeedbackStatus, issueCount = issues.Count + activeErrorCount, issues, officialFeedback, depositFeedback,
            transmissions = transmissions.Select(x => new { x.Id, x.AttemptNumber, x.Status, x.Provider, x.ExternalId, x.ErrorMessage, x.StartedAt, x.SentAt, x.CompletedAt, x.CreatedAt })
        });
    }

    public sealed record UpdateTreatmentRequest(string StatusCode, string? Note, DateTimeOffset? ExpectedUpdatedAt);
}
