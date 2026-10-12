using System.Globalization;
using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.Reporting.Application;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Reporting;
using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Modules.Reporting.Features.Corrections;
using Alpha.Modules.Reporting.Features.EmployerInterface006;
using Alpha.Modules.Reporting.Features.FeedbackIngestion;
using Alpha.Modules.Reporting.Features.Validation;
using Alpha.Modules.Reporting.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Alpha.Modules.Reporting.Features.Feedback;

public static partial class ReportFeedbackEndpoints
{
    private static async Task<IResult> OpenExternalCaseAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        OpenExternalCaseRequest request,
        AlphaDbContext db,
        IOrganizationAuthorization access,
        IDataProtectionService protector,
        ICurrentUser currentUser,
        HttpContext http,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (request.ProblemIds is null || request.ProblemIds.Count == 0 || string.IsNullOrWhiteSpace(request.GroupKey))
            return Results.BadRequest(new { error = "external_case_targets_required" });
        var caseAction = string.Equals(request.Action, "reconcile", StringComparison.OrdinalIgnoreCase)
            ? "reconcile"
            : "external";

        var rows = (await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct)).ToArray();
        var context = await BuildResolutionContextAsync(
            "report", organizationId, employerId, reportId, null,
            canCreateReport: true, canEditEmployee: false, rows, db, protector, ct);
        var group = context.Groups.SingleOrDefault(item =>
            string.Equals(item.GroupKey, request.GroupKey, StringComparison.Ordinal));
        if (group is null) return Results.Conflict(new { error = "resolution_group_stale" });

        var selected = group.Problems
            .Where(problem => request.ProblemIds.Contains(problem.ProblemId, StringComparer.Ordinal))
            .ToArray();
        if (selected.Length != request.ProblemIds.Distinct(StringComparer.Ordinal).Count())
            return Results.BadRequest(new { error = "resolution_problem_mismatch" });
        var requiredAction = caseAction == "reconcile" ? "reconcile" : "openExternalCase";
        if (selected.Any(problem => !problem.AvailableActions.Contains(requiredAction, StringComparer.Ordinal)))
            return Results.BadRequest(new { error = "external_case_not_allowed" });

        var caseKey = request.GroupKey.Trim();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.EmployerId == employerId && item.CaseKey == caseKey, ct);

        var created = false;
        if (externalCase is null)
        {
            var first = selected[0];
            var destination = caseAction == "reconcile"
                ? "התאמת כספים / גבייה"
                : ExternalCaseDestination(first.Family);
            var subject = caseAction == "reconcile"
                ? $"ALPHA - התאמת כספים למשוב קוד {first.Code}"
                : $"ALPHA - בירור משוב מסלקה קוד {first.Code}";
            var template = BuildExternalCaseTemplate(first);
            externalCase = new FeedbackExternalCase(
                organizationId, employerId, caseKey, destination, subject, template, currentUser.UserId);
            db.FeedbackExternalCases.Add(externalCase);
            db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
                externalCase.Id, "created", request.Note, currentUser.UserId));
            created = true;
        }
        else if (!string.IsNullOrWhiteSpace(request.Note))
        {
            db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
                externalCase.Id, "note", request.Note, currentUser.UserId));
        }

        var existingProblemIds = await db.FeedbackExternalCaseProblems.AsNoTracking()
            .Where(item => item.CaseId == externalCase.Id)
            .Select(item => item.ProblemId)
            .ToListAsync(ct);
        var existingSet = existingProblemIds.ToHashSet(StringComparer.Ordinal);

        var activeById = rows
            .Where(row => row.ErrorCode.HasValue)
            .ToDictionary(
                row => FeedbackResolutionWireProjection.BuildProblemId(
                    row.FeedbackId, row.ContributionId, row.Sequence, row.ErrorCode!.Value),
                row => row,
                StringComparer.Ordinal);

        foreach (var problem in selected)
        {
            if (!existingSet.Contains(problem.ProblemId)
                && activeById.TryGetValue(problem.ProblemId, out var row))
            {
                db.FeedbackExternalCaseProblems.Add(new FeedbackExternalCaseProblem(
                    externalCase.Id, problem.ProblemId, row.FeedbackId, row.ReportId,
                    row.ReportProductId, row.ContributionId, row.ErrorCode!.Value));
                db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
                    externalCase.Id, "problem-linked", $"קוד {problem.Code}", currentUser.UserId));
            }

            if (string.Equals(problem.ResolutionType, "decision", StringComparison.Ordinal)
                && !string.Equals(problem.LatestDecision, caseAction, StringComparison.Ordinal))
            {
                var decisionRow = activeById[problem.ProblemId];
                db.FeedbackProblemDecisions.Add(new FeedbackProblemDecision(
                    problem.ProblemId, decisionRow.FeedbackId, decisionRow.ReportId, decisionRow.ReportProductId,
                    decisionRow.ContributionId, decisionRow.ErrorCode!.Value, caseAction, request.Note, currentUser.UserId));
            }
        }

        if (externalCase.Status == "resolved")
            externalCase.SetStatus("open");

        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "feedback-resolution.external-case-opened",
            nameof(FeedbackExternalCase),
            externalCase.Id,
            organizationId,
            employerId,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                externalCase.Id,
                caseKey,
                created,
                problemIds = selected.Select(item => item.ProblemId).ToArray(),
                action = caseAction
            }),
            http.TraceIdentifier));

        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(externalCase.Id, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> ExternalCaseDetailsAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        AlphaDbContext db,
        IOrganizationAuthorization access,
        CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var exists = await db.FeedbackExternalCases.AsNoTracking().AnyAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (!exists) return Results.NotFound();
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> AddExternalCaseEventAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        ExternalCaseEventRequest request,
        AlphaDbContext db,
        IOrganizationAuthorization access,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (externalCase is null) return Results.NotFound();
        if (string.IsNullOrWhiteSpace(request.Note))
            return Results.BadRequest(new { error = "case_note_required" });
        db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
            caseId, "note", request.Note, currentUser.UserId));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> AssignExternalCaseAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        ExternalCaseAssignmentRequest request,
        AlphaDbContext db,
        IOrganizationAuthorization access,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (externalCase is null) return Results.NotFound();

        var assignee = request.AssignToMe ? currentUser.UserId : (Guid?)null;
        externalCase.Assign(assignee);
        db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
            caseId, assignee.HasValue ? "assigned" : "unassigned",
            assignee.HasValue ? "התיק נלקח לטיפול." : "השיוך הוסר.", currentUser.UserId));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> UpdateExternalCaseStatusAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        ExternalCaseStatusRequest request,
        AlphaDbContext db,
        IOrganizationAuthorization access,
        ICurrentUser currentUser,
        HttpContext http,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (externalCase is null) return Results.NotFound();

        var normalized = request.Status?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized is not ("open" or "waiting" or "resolved"))
            return Results.BadRequest(new { error = "external_case_status_invalid" });

        externalCase.SetStatus(normalized);
        db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
            caseId, "status", request.Note, currentUser.UserId));

        if (normalized == "resolved")
        {
            var links = await db.FeedbackExternalCaseProblems.AsNoTracking()
                .Where(item => item.CaseId == caseId)
                .ToListAsync(ct);
            var reportIds = links.Select(item => item.ReportId).Distinct().ToArray();
            var activeRows = await ActiveActionableFeedbackAsync(reportIds, db, ct);
            var activeByProblemId = activeRows
                .Where(row => row.ErrorCode.HasValue)
                .ToDictionary(
                    row => FeedbackResolutionWireProjection.BuildProblemId(
                        row.FeedbackId, row.ContributionId, row.Sequence, row.ErrorCode!.Value),
                    row => row,
                    StringComparer.Ordinal);
            var alreadyResolved = await db.FeedbackProblemResolutions.AsNoTracking()
                .Where(item => links.Select(link => link.ProblemId).Contains(item.ProblemId))
                .Select(item => item.ProblemId)
                .ToListAsync(ct);
            var resolvedSet = alreadyResolved.ToHashSet(StringComparer.Ordinal);

            foreach (var link in links)
            {
                if (resolvedSet.Contains(link.ProblemId)
                    || !activeByProblemId.TryGetValue(link.ProblemId, out var row)
                    || !FeedbackResolutionPlaybookCatalog.TryGet(row.ErrorCode!.Value, out var playbook)
                    || (!playbook.Actions.HasFlag(FeedbackResolutionAction.OpenExternalCase)
                        && !playbook.Actions.HasFlag(FeedbackResolutionAction.Reconcile)))
                    continue;

                db.FeedbackProblemResolutions.Add(new FeedbackProblemResolution(
                    link.ProblemId, row.FeedbackId, row.ReportId, row.ReportProductId,
                    row.ContributionId, row.ErrorCode.Value, "external-case-resolved", currentUser.UserId));
            }
        }

        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "feedback-resolution.external-case-status",
            nameof(FeedbackExternalCase),
            externalCase.Id,
            organizationId,
            employerId,
            System.Text.Json.JsonSerializer.Serialize(new { status = normalized }),
            http.TraceIdentifier));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> UpdateExternalCaseTemplateAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        ExternalCaseTemplateRequest request,
        AlphaDbContext db,
        IOrganizationAuthorization access,
        ICurrentUser currentUser,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (externalCase is null) return Results.NotFound();

        try
        {
            externalCase.UpdateTemplate(request.Destination, request.Subject, request.MessageTemplate);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "external_case_template_invalid" });
        }

        db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
            caseId, "template-updated", "תבנית הפנייה עודכנה.", currentUser.UserId));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> UploadExternalCaseAttachmentAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        HttpRequest request,
        AlphaDbContext db,
        IOrganizationAuthorization access,
        ICurrentUser currentUser,
        IMalwareScanner scanner,
        IDataProtectionService protector,
        IConfiguration configuration,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var externalCase = await db.FeedbackExternalCases.SingleOrDefaultAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (externalCase is null) return Results.NotFound();
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data required" });

        var file = (await request.ReadFormAsync(ct)).Files.GetFile("file");
        var maxBytes = Math.Min(10L * 1024 * 1024,
            configuration.GetValue<long?>("Security:MalwareScanner:MaxFileBytes") ?? 10L * 1024 * 1024);
        if (file is null || file.Length == 0 || file.Length > maxBytes)
            return Results.BadRequest(new { error = "case_attachment_size_invalid" });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
            return Results.BadRequest(new { error = "case_attachment_type_invalid" });

        await using var input = file.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        var validSignature = ext switch
        {
            ".pdf" => bytes.Length >= 5 && bytes.AsSpan().StartsWith("%PDF-"u8),
            ".png" => bytes.Length >= 8
                && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
                && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A,
            ".jpg" or ".jpeg" => bytes.Length >= 3
                && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF,
            _ => false
        };
        if (!validSignature)
            return Results.BadRequest(new { error = "case_attachment_signature_invalid" });

        await using var scanStream = new MemoryStream(bytes, writable: false);
        var scan = await scanner.ScanAsync(scanStream, file.FileName, ct);
        if (scan.Verdict == MalwareScanVerdict.Unavailable)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (scan.Verdict == MalwareScanVerdict.Infected)
            return Results.BadRequest(new { error = "case_attachment_security_failed" });

        var encrypted = protector.ProtectBytes(bytes, $"feedback-external-case-attachment:{caseId:N}");
        var attachment = new FeedbackExternalCaseAttachment(
            caseId, file.FileName, file.ContentType, encrypted, bytes.LongLength,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            currentUser.UserId);
        db.FeedbackExternalCaseAttachments.Add(attachment);
        db.FeedbackExternalCaseEvents.Add(new FeedbackExternalCaseEvent(
            caseId, "attachment", attachment.OriginalFileName, currentUser.UserId));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ExternalCaseResponseAsync(caseId, organizationId, employerId, db, ct));
    }

    private static async Task<IResult> DownloadExternalCaseAttachmentAsync(
        Guid organizationId,
        Guid employerId,
        Guid caseId,
        Guid attachmentId,
        AlphaDbContext db,
        IOrganizationAuthorization access,
        IDataProtectionService protector,
        CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var belongs = await db.FeedbackExternalCases.AsNoTracking().AnyAsync(item =>
            item.Id == caseId && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        if (!belongs) return Results.NotFound();
        var attachment = await db.FeedbackExternalCaseAttachments.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == attachmentId && item.CaseId == caseId, ct);
        if (attachment is null) return Results.NotFound();

        var bytes = protector.UnprotectBytes(
            attachment.Content, $"feedback-external-case-attachment:{caseId:N}");
        if (!string.Equals(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
                attachment.Sha256,
                StringComparison.Ordinal))
            return Results.Problem("Attachment integrity check failed.", statusCode: 500);
        return Results.File(bytes, attachment.ContentType, attachment.OriginalFileName);
    }

    private static async Task<object> ExternalCaseResponseAsync(
        Guid caseId,
        Guid organizationId,
        Guid employerId,
        AlphaDbContext db,
        CancellationToken ct)
    {
        var externalCase = await db.FeedbackExternalCases.AsNoTracking()
            .SingleAsync(item => item.Id == caseId
                && item.OrganizationId == organizationId && item.EmployerId == employerId, ct);
        var assigneeName = externalCase.AssignedToUserId.HasValue
            ? await db.Users.AsNoTracking()
                .Where(user => user.Id == externalCase.AssignedToUserId.Value)
                .Select(user => user.DisplayName)
                .SingleOrDefaultAsync(ct) ?? string.Empty
            : string.Empty;
        var problemRows = await db.FeedbackExternalCaseProblems.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderBy(item => item.CreatedAt)
            .ToListAsync(ct);
        var problems = problemRows.Select(item => new
        {
            item.ProblemId,
            item.ReportId,
            item.ReportProductId,
            item.ErrorCode,
            description = EmployerInterfaceLineFeedbackParser.Description(item.ErrorCode),
            item.CreatedAt
        }).ToArray();
        var events = await db.FeedbackExternalCaseEvents.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderByDescending(item => item.CreatedAt)
            .ToListAsync(ct);
        var actorIds = events.Select(item => item.ActorUserId).Distinct().ToArray();
        var actors = await db.Users.AsNoTracking()
            .Where(user => actorIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, user => user.DisplayName, ct);
        var attachments = await db.FeedbackExternalCaseAttachments.AsNoTracking()
            .Where(item => item.CaseId == caseId)
            .OrderByDescending(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.OriginalFileName,
                item.ContentType,
                item.SizeBytes,
                item.Sha256,
                item.CreatedAt
            })
            .ToListAsync(ct);

        return new
        {
            externalCase.Id,
            externalCase.CaseKey,
            externalCase.Status,
            externalCase.Destination,
            externalCase.Subject,
            externalCase.MessageTemplate,
            externalCase.AssignedToUserId,
            assigneeName,
            externalCase.CreatedAt,
            externalCase.UpdatedAt,
            externalCase.ClosedAt,
            problems,
            events = events.Select(item => new
            {
                item.Id,
                item.EventType,
                item.Note,
                item.ActorUserId,
                actorName = actors.GetValueOrDefault(item.ActorUserId) ?? string.Empty,
                item.CreatedAt
            }),
            attachments
        };
    }

    private static string ExternalCaseDestination(string family) => family switch
    {
        "payment" or "refund" => "מסלקה / גוף מוסדי",
        "externalInstitution" => "גוף מוסדי",
        "documents" => "גוף מוסדי / מסלקה",
        _ => "גוף חיצוני"
    };

    private static string BuildExternalCaseTemplate(FeedbackResolutionProblemDto problem)
    {
        var lines = new List<string>
        {
            "שלום,",
            "",
            $"נבקש את בדיקתכם בנוגע למשוב מסלקה קוד {problem.Code}.",
            problem.Description,
        };
        if (!string.IsNullOrWhiteSpace(problem.EmployeeName))
            lines.Add($"עובד: {problem.EmployeeName}");
        if (!string.IsNullOrWhiteSpace(problem.FundCompanyName) || !string.IsNullOrWhiteSpace(problem.ProductName))
            lines.Add($"מוצר/יצרן: {problem.FundCompanyName} {problem.ProductName}".Trim());
        if (!string.IsNullOrWhiteSpace(problem.PolicyNumber))
            lines.Add($"מספר פוליסה: {problem.PolicyNumber}");
        lines.Add($"דיווח: {problem.ReportId:D}");
        lines.Add("");
        lines.Add("נודה לבדיקה ולעדכון.");
        return string.Join(Environment.NewLine, lines);
    }
}
