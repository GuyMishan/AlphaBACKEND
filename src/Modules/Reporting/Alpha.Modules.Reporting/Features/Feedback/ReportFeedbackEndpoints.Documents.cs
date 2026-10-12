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
    private static async Task<IResult> UploadResolutionDocumentAsync(
        Guid organizationId,
        Guid employerId,
        Guid reportId,
        string problemId,
        HttpRequest request,
        AlphaDbContext db,
        IOrganizationAuthorization access,
        ICurrentUser currentUser,
        IMalwareScanner scanner,
        IDataProtectionService protector,
        IConfiguration configuration,
        HttpContext http,
        CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var reportExists = await db.ManualReports.AsNoTracking().AnyAsync(x =>
            x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId
            && !x.IsCorrectionWorkspace && !x.IsTechnicalCorrectionDocument, ct);
        if (!reportExists) return Results.NotFound();

        var activeRows = await ActiveActionableFeedbackAsync(new[] { reportId }, db, ct);
        var row = activeRows.FirstOrDefault(x => x.ErrorCode.HasValue
            && string.Equals(
                FeedbackResolutionWireProjection.BuildProblemId(
                    x.FeedbackId, x.ContributionId, x.Sequence, x.ErrorCode.Value),
                problemId,
                StringComparison.Ordinal));
        if (row is null) return Results.Conflict(new { error = "resolution_problem_stale" });
        if (!FeedbackResolutionPlaybookCatalog.TryGet(row.ErrorCode!.Value, out var playbook)
            || playbook.Resolver != FeedbackResolverType.Documents
            || !playbook.Actions.HasFlag(FeedbackResolutionAction.UploadDocument))
            return Results.BadRequest(new { error = "resolution_action_not_allowed" });

        if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data required" });
        var file = (await request.ReadFormAsync(ct)).Files.GetFile("file");
        var maxBytes = Math.Min(10L * 1024 * 1024,
            configuration.GetValue<long?>("Security:MalwareScanner:MaxFileBytes") ?? 10L * 1024 * 1024);
        if (file is null || file.Length == 0 || file.Length > maxBytes)
            return Results.BadRequest(new { error = $"File must be 1 byte to {maxBytes} bytes." });
        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "PDF only." });

        await using var input = file.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        if (bytes.Length < 5 || !bytes.AsSpan().StartsWith("%PDF-"u8))
            return Results.BadRequest(new { error = "The uploaded file is not a valid PDF file." });

        await using var scanStream = new MemoryStream(bytes, writable: false);
        var scan = await scanner.ScanAsync(scanStream, file.FileName, ct);
        if (scan.Verdict == MalwareScanVerdict.Unavailable)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (scan.Verdict == MalwareScanVerdict.Infected)
            return Results.BadRequest(new { error = "The uploaded file failed the security scan." });

        var encrypted = protector.ProtectBytes(bytes, $"feedback-resolution-document:{problemId}");
        var document = new FeedbackResolutionDocument(
            problemId, reportId, row.ReportProductId, file.FileName, "application/pdf", encrypted,
            bytes.LongLength, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant(),
            currentUser.UserId);
        db.FeedbackResolutionDocuments.Add(document);
        db.FeedbackProblemResolutions.Add(new FeedbackProblemResolution(
            problemId, row.FeedbackId, row.ReportId, row.ReportProductId, row.ContributionId,
            row.ErrorCode.Value, "document-upload", currentUser.UserId));
        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "feedback-resolution.problem-resolved",
            nameof(EmployerInterfaceContributionFeedback),
            row.Id,
            organizationId,
            employerId,
            System.Text.Json.JsonSerializer.Serialize(new
            {
                problemId,
                row.ErrorCode,
                source = "document-upload",
                documentId = document.Id
            }),
            http.TraceIdentifier));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { error = "resolution_problem_already_resolved" });
        }

        return Results.Ok(new
        {
            document.Id,
            document.OriginalFileName,
            document.SizeBytes,
            document.Sha256,
            resolvedProblemId = problemId
        });
    }
}
