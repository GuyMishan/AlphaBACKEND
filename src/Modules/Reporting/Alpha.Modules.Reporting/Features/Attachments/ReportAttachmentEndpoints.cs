using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Reporting;
using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Modules.Reporting.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Alpha.Modules.Reporting.Features.Attachments;

public static class ReportAttachmentEndpoints
{
    private const long MaxAttachmentBytes = 10 * 1024 * 1024;
    private const int MaxAttachmentsPerReport = 20;

    public static IEndpointRouteBuilder MapReportAttachmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/manual-reports/{reportId:guid}/attachments")
            .RequireAuthorization().WithTags("Manual reporting");

        group.MapGet("/", ListAsync);
        group.MapGet("/library-candidates", LibraryCandidatesAsync);
        group.MapPost("/from-library", AttachFromLibraryAsync);
        group.MapPost("/", UploadAsync).DisableAntiforgery().WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(11 * 1024 * 1024));
        group.MapGet("/{attachmentId:guid}/file", DownloadAsync);
        group.MapDelete("/{attachmentId:guid}", DeleteAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid organizationId, Guid employerId, Guid reportId,
        AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
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
            && items.Any(x => x.DocumentTypeCode == 3);

        var requiredCode5ProductIds = await GetRequiredCode5ProductIdsAsync(db, report, ct);
        var eligibleCode5ProductIds = await GetRequiredCode5ProductIdsAsync(db, report, ct, newOnly: false);
        return Results.Ok(new
        {
            items,
            requiredCode5ProductIds,
            eligibleCode5ProductIds,
            annualEmployerAffidavitSatisfied,
            documentTypes = report.ReportKind == ManualReportKind.Negative
                ? new[]
                {
                    new { code = 3, name = "תצהיר מעסיק", scope = "report" },
                    new { code = 4, name = "אישור עובד להשבת כספים", scope = "product" },
                    new { code = 6, name = "הצהרת מעסיק - דיווח שלילי - הסכם קיבוצי", scope = "product" }
                }
                : new[]
                {
                    new { code = 5, name = "בקשת עובד להצטרפות לקרן ברירת מחדל", scope = "product" }
                }
        });
    }

    private static async Task<IResult> UploadAsync(Guid organizationId, Guid employerId, Guid reportId,
        HttpRequest request, AlphaDbContext db, IOrganizationAuthorization access, IMalwareScanner malwareScanner, IDataProtectionService protector, IConfiguration configuration, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports
            .SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.ReportKind == ManualReportKind.Differences)
            return Results.BadRequest(new { error = "Difference reports are not transmitted directly and cannot carry Employer Interface attachments." });
        if (!report.IsEditable)
            return Results.Conflict(new { error = "A sent or completed report cannot be edited." });
        if (!request.HasFormContentType)
            return Results.BadRequest(new { error = "multipart/form-data is required." });

        var form = await request.ReadFormAsync(ct);
        if (!int.TryParse(form["documentTypeCode"].FirstOrDefault(), out var documentTypeCode)
            || documentTypeCode is not (3 or 4 or 5 or 6))
            return Results.BadRequest(new { error = "documentTypeCode must be 3, 4, 5 or 6." });
        if (report.ReportKind == ManualReportKind.Negative && documentTypeCode is not (3 or 4 or 6))
            return Results.BadRequest(new { error = "Negative Version 006 reports support attachment types 3, 4 and 6 only." });
        if (report.ReportKind == ManualReportKind.Current && documentTypeCode != 5)
            return Results.BadRequest(new { error = "Current Version 006 reports support attachment type 5 only." });

        Guid? reportProductId = null;
        var reportProductRaw = form["reportProductId"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(reportProductRaw))
        {
            if (!Guid.TryParse(reportProductRaw, out var parsed))
                return Results.BadRequest(new { error = "reportProductId is invalid." });
            reportProductId = parsed;
        }

        if (documentTypeCode is 4 or 5 or 6)
        {
            if (reportProductId is null)
                return Results.BadRequest(new { error = "Document types 4, 5 and 6 must be linked to the employee/product they apply to." });
            var linkedProduct = await (
                from product in db.ManualReportProducts.AsNoTracking()
                join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
                where product.Id == reportProductId.Value && employee.ReportId == reportId
                select product).SingleOrDefaultAsync(ct);
            if (linkedProduct is null)
                return Results.BadRequest(new { error = "The selected report product does not belong to this report." });

            if (documentTypeCode == 5)
            {
                if (linkedProduct.ProductType != PensionProductType.PensionFund)
                    return Results.BadRequest(new { error = "Version 006 document type 5 is relevant only to a pension fund." });
                var linkedMetadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ReportProductId == linkedProduct.Id, ct);
                if (linkedMetadata?.EmployeeStatus == 14)
                    return Results.BadRequest(new { error = "Version 006 document type 5 is for an existing employee and cannot be attached when employee status is 14 (new employee)." });
            }
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
        var maxBytes = string.Equals(configuration["Security:MalwareScanner:Provider"], "Cloudmersive", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(MaxAttachmentBytes, configuration.GetValue<long?>("Security:MalwareScanner:MaxFileBytes") ?? ConfiguredMalwareScanner.FreeTierSafeMaxBytes)
            : MaxAttachmentBytes;
        if (file.Length > maxBytes) return Results.BadRequest(new { error = $"The maximum attachment size is {maxBytes} bytes." });
        if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Employer Interface attachments must be PDF files." });

        await using var input = file.OpenReadStream();
        using var memory = new MemoryStream();
        await input.CopyToAsync(memory, ct);
        var bytes = memory.ToArray();
        if (bytes.Length < 5 || bytes[0] != (byte)'%' || bytes[1] != (byte)'P' || bytes[2] != (byte)'D' || bytes[3] != (byte)'F' || bytes[4] != (byte)'-')
            return Results.BadRequest(new { error = "The uploaded file is not a valid PDF file." });

        // Validate file format before checking selected-fund eligibility so invalid files
        // consistently receive the established PDF validation errors.
        if (documentTypeCode == 5)
        {
            var eligible = await GetRequiredCode5ProductIdsAsync(db, report, ct, newOnly: false);
            if (!eligible.Contains(reportProductId!.Value))
                return Results.BadRequest(new { error = "Document type 5 is not applicable to this pension fund." });
        }

        await using var scanStream = new MemoryStream(bytes, writable: false);
        var scan = await malwareScanner.ScanAsync(scanStream, file.FileName, ct);
        if (scan.Verdict == MalwareScanVerdict.Unavailable)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (scan.Verdict == MalwareScanVerdict.Infected)
            return Results.BadRequest(new { error = "The uploaded file failed the security scan." });

        var attachment = new ManualReportAttachment(reportId, reportProductId, documentTypeCode,
            Path.GetFileName(file.FileName), "application/pdf", protector.ProtectBytes(bytes, $"report-attachment:{reportId}:{reportProductId}:{documentTypeCode}"),
            bytes.LongLength, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant());
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
        AlphaDbContext db, IOrganizationAuthorization access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var exists = await db.ManualReports.AsNoTracking()
            .AnyAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (!exists) return Results.NotFound();

        var attachment = await db.ManualReportAttachments.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == attachmentId && x.ReportId == reportId, ct);
        if (attachment is null) return Results.NotFound();
        var content = protector.UnprotectBytes(attachment.Content, $"report-attachment:{reportId}:{attachment.ReportProductId}:{attachment.DocumentTypeCode}");
        return Results.File(content, attachment.ContentType, attachment.OriginalFileName);
    }

    private static async Task<IResult> DeleteAsync(Guid organizationId, Guid employerId, Guid reportId, Guid attachmentId,
        AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
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

    private sealed record FromLibraryRequest(Guid EmployerDocumentId, Guid? ReportProductId);
    private sealed record ReportProductScope(ManualReportProduct Product, Guid EmploymentId);

    // Match a saved employee product to the selected report line using strong fund/policy
    // identifiers. Never assume that products from the same manufacturer are equivalent.
    private static bool SamePensionProduct(ManualReportProduct reported, EmployeePensionProduct saved)
    {
        if (reported.ProductType != saved.ProductType ||
            !string.Equals(reported.PolicyNumber?.Trim(), saved.PolicyNumber?.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        var external = !string.IsNullOrWhiteSpace(reported.FundExternalKey) &&
            string.Equals(reported.FundExternalKey.Trim(), saved.FundExternalKey?.Trim(), StringComparison.OrdinalIgnoreCase);
        var code = !string.IsNullOrWhiteSpace(reported.FundCode) &&
            string.Equals(reported.FundCode.Trim(), saved.FundCode?.Trim(), StringComparison.OrdinalIgnoreCase);
        // If both sides have an external identifier it must not conflict with the fallback code.
        if (!string.IsNullOrWhiteSpace(reported.FundExternalKey) &&
            !string.IsNullOrWhiteSpace(saved.FundExternalKey) && !external)
            return false;
        return external || code;
    }

    private static async Task<ReportProductScope?> ResolveProductAsync(
        AlphaDbContext db, Guid reportId, Guid reportProductId, CancellationToken ct)
    {
        var scope = await (from product in db.ManualReportProducts.AsNoTracking()
            join employee in db.ManualReportEmployees.AsNoTracking()
                on product.ReportEmployeeId equals employee.Id
            where product.Id == reportProductId && employee.ReportId == reportId
            select new { Product = product, employee.EmploymentId }).SingleOrDefaultAsync(ct);
        return scope is null ? null : new ReportProductScope(scope.Product, scope.EmploymentId);
    }

    private static async Task<Guid?> MatchSavedProductAsync(
        AlphaDbContext db, ReportProductScope scope, CancellationToken ct)
    {
        var saved = await db.EmployeePensionProducts.AsNoTracking()
            .Where(x => x.EmploymentId == scope.EmploymentId).ToListAsync(ct);
        var matches = saved.Where(x => SamePensionProduct(scope.Product, x)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0].Id : null;
    }

    private static async Task<IResult> LibraryCandidatesAsync(
        Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId,
        AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.ReportKind == ManualReportKind.Differences) return Results.Ok(Array.Empty<object>());
        var scope = await ResolveProductAsync(db, reportId, reportProductId, ct);
        if (scope is null) return Results.NotFound();
        var savedId = await MatchSavedProductAsync(db, scope, ct);
        var negative = report.ReportKind == ManualReportKind.Negative;
        var codes = negative ? new[] { 4, 6 } : new[] { 5 };
        var documents = await db.EmployerDocuments.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId &&
                codes.Contains(x.DocumentTypeCode) &&
                (savedId.HasValue && x.EmploymentId == scope.EmploymentId &&
                 x.EmployeePensionProductId == savedId))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.DocumentTypeCode, x.OriginalFileName, x.SizeBytes, x.CreatedAt })
            .ToListAsync(ct);
        return Results.Ok(documents);
    }

    private static async Task<IResult> AttachFromLibraryAsync(
        Guid organizationId, Guid employerId, Guid reportId, FromLibraryRequest request,
        AlphaDbContext db, IOrganizationAuthorization access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.SingleOrDefaultAsync(x =>
            x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (!report.IsEditable) return Results.Conflict(new { error = "The report is no longer editable." });
        if (report.ReportKind == ManualReportKind.Differences)
            return Results.BadRequest(new { error = "Difference reports cannot carry 006 attachments." });
        var source = await db.EmployerDocuments.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == request.EmployerDocumentId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (source is null) return Results.NotFound();
        var negative = report.ReportKind == ManualReportKind.Negative;
        if ((negative && source.DocumentTypeCode is not (4 or 6)) ||
            (!negative && source.DocumentTypeCode != 5))
            return Results.BadRequest(new { error = "Document type is incompatible with this report." });
        Guid? reportProductId = null;
        if (source.DocumentTypeCode == 3)
        {
            if (request.ReportProductId.HasValue || source.EmploymentId.HasValue || source.EmployeePensionProductId.HasValue)
                return Results.BadRequest(new { error = "Employer affidavit is report-wide." });
        }
        else
        {
            if (!request.ReportProductId.HasValue)
                return Results.BadRequest(new { error = "Select the exact report product for this document." });
            var scope = await ResolveProductAsync(db, reportId, request.ReportProductId.Value, ct);
            if (scope is null) return Results.NotFound();
            var savedProductId = await MatchSavedProductAsync(db, scope, ct);
            if (!savedProductId.HasValue || savedProductId != source.EmployeePensionProductId ||
                scope.EmploymentId != source.EmploymentId)
                return Results.BadRequest(new { error = "Document does not belong to the selected employee and pension product." });
            if (source.DocumentTypeCode == 5)
            {
                if (scope.Product.ProductType != PensionProductType.PensionFund)
                    return Results.BadRequest(new { error = "Type 5 is valid only for a pension fund." });
                var metadata = await db.EmployerInterfaceReportProductData.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ReportProductId == scope.Product.Id, ct);
                if (metadata?.EmployeeStatus == 14)
                    return Results.BadRequest(new { error = "Type 5 is not applicable to new employee status 14." });
            }
            reportProductId = scope.Product.Id;
        }
        if (await db.ManualReportAttachments.CountAsync(x => x.ReportId == reportId, ct) >= MaxAttachmentsPerReport)
            return Results.BadRequest(new { error = "Maximum 20 documents per report." });
        if (await db.ManualReportAttachments.AnyAsync(x => x.ReportId == reportId &&
                x.DocumentTypeCode == source.DocumentTypeCode && x.ReportProductId == reportProductId, ct))
            return Results.Conflict(new { error = "This document type is already attached to the selected report scope." });
        if (source.SizeBytes > MaxAttachmentBytes)
            return Results.BadRequest(new { error = "Document is too large for the report." });

        // The uploaded library PDF was already scanned. Copy immutable plaintext evidence
        // into the report's own purpose-bound encryption context, never link mutable records.
        var plain = protector.UnprotectBytes(source.Content, $"employer-document:{organizationId}:{employerId}:{source.Id}");
        var reportCopy = new ManualReportAttachment(reportId, reportProductId, source.DocumentTypeCode,
            source.OriginalFileName, "application/pdf",
            protector.ProtectBytes(plain, $"report-attachment:{reportId}:{reportProductId}:{source.DocumentTypeCode}"),
            source.SizeBytes, source.Sha256);
        db.ManualReportAttachments.Add(reportCopy);
        report.MarkDirty();
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { reportCopy.Id, reportCopy.ReportProductId, reportCopy.DocumentTypeCode,
            reportCopy.OriginalFileName, reportCopy.TransmissionFileName, reportCopy.ContentType,
            reportCopy.SizeBytes, reportCopy.Sha256, reportCopy.CreatedAt });
    }

    public static async Task<HashSet<Guid>> GetRequiredCode5ProductIdsAsync(AlphaDbContext db, ManualReport report, CancellationToken ct, bool newOnly = true)
    {
        var result = new HashSet<Guid>();
        if (report.ReportKind != ManualReportKind.Current) return result;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.CommandText = "SELECT fund_external_keys FROM employers.employer_default_funds WHERE employer_id = @id";
            command.Parameters.Add(new NpgsqlParameter("id", report.EmployerId));
            if (command.Connection!.State != System.Data.ConnectionState.Open) await command.Connection.OpenAsync(ct);
            var selected = await command.ExecuteScalarAsync(ct);
            if (selected is string[] overrideKeys) keys.UnionWith(overrideKeys);
            else
            {
                await using var defaults = db.Database.GetDbConnection().CreateCommand();
                defaults.CommandText = "SELECT external_key FROM reference_data.pension_products WHERE is_active AND is_system_default_fund";
                await using var reader = await defaults.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) keys.Add(reader.GetString(0));
            }
        }
        if (keys.Count == 0) return result;
        var products = await (
            from product in db.ManualReportProducts.AsNoTracking()
            join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
            join metadata in db.EmployerInterfaceReportProductData.AsNoTracking() on product.Id equals metadata.ReportProductId
            where employee.ReportId == report.Id && product.ProductType == PensionProductType.PensionFund && metadata.EmployeeStatus != 14
            select new { product, employee.EmploymentId }).ToListAsync(ct);
        foreach (var item in products)
        {
            if (!keys.Contains(item.product.FundExternalKey)) continue;
            // Products added after the report was opened are new to ALPHA for this report.
            // Existing products can still include type 5 evidence voluntarily.
            var saved = await db.EmployeePensionProducts.AsNoTracking()
                .Where(x => x.EmploymentId == item.EmploymentId).ToListAsync(ct);
            if (!newOnly || !saved.Any(x => SamePensionProduct(item.product, x) && x.CreatedAt < report.CreatedAt))
                result.Add(item.product.Id);
        }
        return result;
    }
}
