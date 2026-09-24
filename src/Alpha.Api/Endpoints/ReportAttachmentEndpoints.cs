using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class ReportAttachmentEndpoints
{
    private const long MaxAttachmentBytes = 10 * 1024 * 1024;
    private const int MaxAttachmentsPerReport = 20;

    public static IEndpointRouteBuilder MapReportAttachmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/manual-reports/{reportId:guid}/attachments")
            .RequireAuthorization().WithTags("Manual reporting");

        group.MapGet("/", ListAsync);
        group.MapPost("/", UploadAsync).DisableAntiforgery();
        group.MapGet("/{attachmentId:guid}/file", DownloadAsync);
        group.MapDelete("/{attachmentId:guid}", DeleteAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid organizationId, Guid employerId, Guid reportId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();

        var items = await db.ManualReportAttachments.AsNoTracking()
            .Where(x => x.ReportId == reportId)
            .OrderBy(x => x.DocumentTypeCode).ThenBy(x => x.CreatedAt)
            .Select(x => new
            {
                x.Id,
                x.ReportProductId,
                x.DocumentTypeCode,
                x.OriginalFileName,
                x.TransmissionFileName,
                x.ContentType,
                x.SizeBytes,
                x.Sha256,
                x.CreatedAt
            }).ToListAsync(ct);

        var annualEmployerAffidavitSatisfied = report.ReportKind == ManualReportKind.Negative
            && (items.Any(x => x.DocumentTypeCode == 3)
                || await HasPreviouslySentAnnualAffidavitAsync(db, report, ct));

        return Results.Ok(new
        {
            items,
            annualEmployerAffidavitSatisfied,
            documentTypes = new[]
            {
                new { code = 3, name = "תצהיר מעסיק", scope = "report" },
                new { code = 4, name = "אישור עובד להשבת כספים", scope = "product" },
                new { code = 6, name = "הצהרת מעסיק - דיווח שלילי - הסכם קיבוצי", scope = "product" }
            }
        });
    }

    private static async Task<IResult> UploadAsync(Guid organizationId, Guid employerId, Guid reportId,
        HttpRequest request, IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports
            .SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.ReportKind != ManualReportKind.Negative)
            return Results.BadRequest(new { error = "Attachments of types 3, 4 and 6 are currently supported only for negative Employer Interface reports." });
        if (!report.IsEditable)
            return Results.Conflict(new { error = "A sent or completed report cannot be edited." });
        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "multipart/form-data is required." });

        var form = await request.ReadFormAsync(ct);
        if (!int.TryParse(form["documentTypeCode"].FirstOrDefault(), out var documentTypeCode)
            || documentTypeCode is not (3 or 4 or 6))
            return Results.BadRequest(new { error = "documentTypeCode must be 3, 4 or 6." });

        Guid? reportProductId = null;
        var reportProductRaw = form["reportProductId"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(reportProductRaw))
        {
            if (!Guid.TryParse(reportProductRaw, out var parsed))
                return Results.BadRequest(new { error = "reportProductId is invalid." });
            reportProductId = parsed;
        }

        if (documentTypeCode is 4 or 6)
        {
            if (reportProductId is null)
                return Results.BadRequest(new { error = "Document types 4 and 6 must be linked to the employee/product they apply to." });
            var belongsToReport = await (
                from product in db.ManualReportProducts.AsNoTracking()
                join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                where product.Id == reportProductId.Value && employee.ReportId == reportId
                select product.Id).AnyAsync(ct);
            if (!belongsToReport)
                return Results.BadRequest(new { error = "The selected report product does not belong to this report." });
        }
        else
        {
            reportProductId = null;
        }

        if (await db.ManualReportAttachments.CountAsync(x => x.ReportId == reportId, ct) >= MaxAttachmentsPerReport)
            return Results.BadRequest(new { error = $"A report can contain up to {MaxAttachmentsPerReport} attachments." });

        var duplicate = await db.ManualReportAttachments.AnyAsync(x => x.ReportId == reportId
            && x.ReportProductId == reportProductId && x.DocumentTypeCode == documentTypeCode, ct);
        if (duplicate)
            return Results.Conflict(new { error = "An attachment of this document type already exists for the selected scope. Delete it before uploading a replacement." });

        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return Results.BadRequest(new { error = "No attachment file was selected." });
        if (file.Length > MaxAttachmentBytes) return Results.BadRequest(new { error = "The maximum attachment size is 10MB." });
        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Employer Interface attachments must be PDF files." });

        await using var input = file.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        if (bytes.Length < 5 || bytes[0] != (byte)'%' || bytes[1] != (byte)'P' || bytes[2] != (byte)'D' || bytes[3] != (byte)'F' || bytes[4] != (byte)'-')
            return Results.BadRequest(new { error = "The uploaded file is not a valid PDF file." });

        var attachment = new ManualReportAttachment(reportId, reportProductId, documentTypeCode,
            Path.GetFileName(file.FileName), "application/pdf", bytes);
        db.ManualReportAttachments.Add(attachment);
        report.MarkDirty();
        await db.SaveChangesAsync(ct);

        return Results.Ok(new
        {
            attachment.Id,
            attachment.ReportProductId,
            attachment.DocumentTypeCode,
            attachment.OriginalFileName,
            attachment.TransmissionFileName,
            attachment.ContentType,
            attachment.SizeBytes,
            attachment.Sha256,
            attachment.CreatedAt
        });
    }

    private static async Task<IResult> DownloadAsync(Guid organizationId, Guid employerId, Guid reportId, Guid attachmentId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var exists = await db.ManualReports.AsNoTracking()
            .AnyAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (!exists) return Results.NotFound();

        var attachment = await db.ManualReportAttachments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attachmentId && x.ReportId == reportId, ct);
        if (attachment is null) return Results.NotFound();
        return Results.File(attachment.Content, attachment.ContentType, attachment.OriginalFileName);
    }

    private static async Task<IResult> DeleteAsync(Guid organizationId, Guid employerId, Guid reportId, Guid attachmentId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports
            .SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (!report.IsEditable) return Results.Conflict(new { error = "A sent or completed report cannot be edited." });

        var attachment = await db.ManualReportAttachments
            .SingleOrDefaultAsync(x => x.Id == attachmentId && x.ReportId == reportId, ct);
        if (attachment is null) return Results.NotFound();

        db.ManualReportAttachments.Remove(attachment);
        report.MarkDirty();
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<bool> HasPreviouslySentAnnualAffidavitAsync(IAlphaDbContext db, ManualReport report, CancellationToken ct)
    {
        var yearStart = new DateOnly(report.ReportingMonth.Year, 1, 1);
        var yearEnd = yearStart.AddYears(1);
        return await (
            from attachment in db.ManualReportAttachments.AsNoTracking()
            join previousReport in db.ManualReports.AsNoTracking() on attachment.ReportId equals previousReport.Id
            where attachment.DocumentTypeCode == 3
                  && previousReport.Id != report.Id
                  && previousReport.EmployerId == report.EmployerId
                  && previousReport.ReportingMonth >= yearStart
                  && previousReport.ReportingMonth < yearEnd
                  && (previousReport.Status == ManualReportStatus.Sent || previousReport.Status == ManualReportStatus.Completed)
            select attachment.Id).AnyAsync(ct);
    }
}
