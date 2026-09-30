using System.Security.Cryptography;
using Alpha.Api.Security;
using Alpha.Api.Services;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

/// <summary>Payment evidence is private operational material and never transmitted as V006 document type.</summary>
public static class PaymentConfirmationEndpoints
{
    private const long MaxBytes = 10 * 1024 * 1024;
    public static IEndpointRouteBuilder MapPaymentConfirmationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/manual-reports/{reportId:guid}/payment-confirmations")
            .RequireAuthorization().WithTags("Manual reporting");
        group.MapGet("/", ListForReportAsync);
        group.MapGet("/{reportProductId:guid}", ListAsync);
        group.MapPost("/{reportProductId:guid}", UploadAsync).DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(11 * 1024 * 1024));
        group.MapGet("/{reportProductId:guid}/{confirmationId:guid}/file", DownloadAsync);
        return endpoints;
    }
    private static async Task<bool> ProductInReport(IAlphaDbContext db, Guid reportId, Guid productId, CancellationToken ct) =>
        await (from product in db.ManualReportProducts.AsNoTracking()
               join employee in db.ManualReportEmployees.AsNoTracking() on product.ReportEmployeeId equals employee.Id
               where product.Id == productId && employee.ReportId == reportId
               select product.Id).AnyAsync(ct);

    // One authorized, scoped query for the deposit table instead of a request per visible row.
    private static async Task<IResult> ListForReportAsync(Guid organizationId, Guid employerId, Guid reportId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.ManualReports.AsNoTracking().AnyAsync(x => x.Id == reportId &&
            x.OrganizationId == organizationId && x.EmployerId == employerId, ct)) return Results.NotFound();
        var items = await db.PaymentConfirmations.AsNoTracking()
            .Where(x => x.ReportId == reportId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.ReportProductId, x.Id, x.OriginalFileName, x.ContentType,
                x.SizeBytes, x.Sha256, x.CreatedAt }).ToListAsync(ct);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> ListAsync(Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.ManualReports.AnyAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct)
            || !await ProductInReport(db, reportId, reportProductId, ct)) return Results.NotFound();
        var items = await db.PaymentConfirmations.AsNoTracking()
            .Where(x => x.ReportId == reportId && x.ReportProductId == reportProductId)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.OriginalFileName, x.ContentType, x.SizeBytes, x.Sha256, x.CreatedAt })
            .ToListAsync(ct);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> UploadAsync(Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId,
        HttpRequest request, IAlphaDbContext db, OrganizationAccessService access, ICurrentUser user,
        PaymentEvidenceStorage storage, IMalwareScanner scanner, IConfiguration configuration, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.SingleOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null || !await ProductInReport(db, reportId, reportProductId, ct)) return Results.NotFound();
        if (!report.IsEditable) return Results.Conflict(new { error = "report_not_editable" });
        if (!storage.IsConfigured) return Results.Json(new { error = "storage_not_configured" }, statusCode: 503);
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data required" });
        var file = (await request.ReadFormAsync(ct)).Files.GetFile("file");
        var maxBytes = string.Equals(configuration["Security:MalwareScanner:Provider"], "Cloudmersive", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(MaxBytes, configuration.GetValue<long?>("Security:MalwareScanner:MaxFileBytes") ?? ConfiguredMalwareScanner.FreeTierSafeMaxBytes)
            : MaxBytes;
        if (file is null || file.Length == 0 || file.Length > maxBytes)
            return Results.BadRequest(new { error = $"File must be 1 byte to {maxBytes} bytes." });
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
            return Results.BadRequest(new { error = "PDF, JPEG or PNG only." });
        await using var input = file.OpenReadStream();
        using var stream = new MemoryStream();
        await input.CopyToAsync(stream, ct);
        var bytes = stream.ToArray();
        var isPdf = ext == ".pdf" && bytes.AsSpan().StartsWith("%PDF-"u8);
        var isJpeg = ext is ".jpg" or ".jpeg" && bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xd8, 0xff });
        var isPng = ext == ".png" && bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        if (!isPdf && !isJpeg && !isPng) return Results.BadRequest(new { error = "File signature does not match extension." });
        await using var scanStream = new MemoryStream(bytes, writable: false);
        var scan = await scanner.ScanAsync(scanStream, file.FileName, ct);
        if (scan.Verdict == MalwareScanVerdict.Unavailable)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        if (scan.Verdict == MalwareScanVerdict.Infected)
            return Results.BadRequest(new { error = "The uploaded file failed the security scan." });
        var contentType = isPdf ? "application/pdf" : isPng ? "image/png" : "image/jpeg";
        var id = Guid.NewGuid();
        var path = $"{organizationId:N}/{employerId:N}/{reportId:N}/{reportProductId:N}/{id:N}{ext}";
        if (!await storage.UploadAsync(path, bytes, contentType, ct)) return Results.StatusCode(503);
        var record = new PaymentConfirmation(reportId, reportProductId, path, file.FileName, contentType,
            bytes.LongLength, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), user.UserId);
        try
        {
            db.PaymentConfirmations.Add(record);
            report.MarkDirty();
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            await storage.DeleteOrphanAsync(path, ct);
            throw;
        }
        return Results.Ok(new { record.Id, record.OriginalFileName, record.ContentType, record.SizeBytes, record.Sha256, record.CreatedAt });
    }

    private static async Task<IResult> DownloadAsync(Guid organizationId, Guid employerId, Guid reportId,
        Guid reportProductId, Guid confirmationId, IAlphaDbContext db, OrganizationAccessService access,
        PaymentEvidenceStorage storage, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.ManualReports.AnyAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct))
            return Results.NotFound();
        var record = await db.PaymentConfirmations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == confirmationId
            && x.ReportId == reportId && x.ReportProductId == reportProductId, ct);
        if (record is null) return Results.NotFound();
        if (!storage.IsConfigured) return Results.StatusCode(503);
        var content = await storage.DownloadAsync(record.StoragePath, ct);
        if (content is null) return Results.StatusCode(502);
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(content)), record.Sha256, StringComparison.OrdinalIgnoreCase))
            return Results.StatusCode(502);
        return Results.File(content, record.ContentType, record.OriginalFileName,
            enableRangeProcessing: false);
    }
}
