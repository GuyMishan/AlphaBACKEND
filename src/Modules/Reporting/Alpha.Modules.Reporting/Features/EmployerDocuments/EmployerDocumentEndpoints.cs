using Alpha.Domain.Employers;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Modules.Reporting.Infrastructure.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Alpha.Modules.Reporting.Features.EmployerDocuments;

public static class EmployerDocumentEndpoints
{
    private const int MaxDocuments = 100;
    private const long MaxBytes = 10 * 1024 * 1024;

    public static IEndpointRouteBuilder MapEmployerDocumentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/documents")
            .RequireAuthorization().WithTags("Employer documents");
        group.MapGet("/", ListAsync);
        group.MapPost("/", UploadAsync).DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(11 * 1024 * 1024));
        group.MapGet("/{documentId:guid}/file", DownloadAsync);
        group.MapDelete("/{documentId:guid}", DeleteAsync);
        group.MapPatch("/{documentId:guid}/product", AssignProductAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(Guid organizationId, Guid employerId, Guid? employmentId, Guid? employeePensionProductId, AlphaDbContext db,
        IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();
        if (employmentId.HasValue && !await db.Employments.AsNoTracking().AnyAsync(x => x.Id == employmentId && x.EmployerId == employerId && x.OrganizationId == organizationId, ct)) return Results.NotFound();
        if (employeePensionProductId.HasValue && (!employmentId.HasValue ||
            !await db.EmployeePensionProducts.AsNoTracking().AnyAsync(x => x.Id == employeePensionProductId && x.EmploymentId == employmentId, ct))) return Results.NotFound();
        var docs = await db.EmployerDocuments.AsNoTracking()
            .Where(x => x.OrganizationId == organizationId && x.EmployerId == employerId && x.EmploymentId == employmentId && (!employeePensionProductId.HasValue || x.EmployeePensionProductId == employeePensionProductId))
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new { x.Id, x.DocumentTypeCode, x.EmploymentId, x.EmployeePensionProductId, x.OriginalFileName, x.SizeBytes, x.Sha256, x.CreatedAt })
            .ToListAsync(ct);
        return Results.Ok(docs);
    }

    private static async Task<IResult> UploadAsync(Guid organizationId, Guid employerId, HttpRequest request,
        AlphaDbContext db, IOrganizationAuthorization access, IMalwareScanner scanner,
        IDataProtectionService protector, IConfiguration config, CancellationToken ct)
    {
        if (!await access.CanEditEmployerAsync(organizationId, employerId, ct)
            && !await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!await db.Employers.AsNoTracking().AnyAsync(x => x.Id == employerId && x.OrganizationId == organizationId, ct))
            return Results.NotFound();
        if (!request.HasFormContentType) return Results.BadRequest(new { error = "multipart/form-data is required." });
        var form = await request.ReadFormAsync(ct);
        if (!int.TryParse(form["documentTypeCode"].FirstOrDefault(), out int type) || type is not (3 or 4 or 5 or 6 or 7))
            return Results.BadRequest(new { error = "Document type must be 3, 4, 5, 6 or 7." });
        Guid? employmentId = null;
        var employmentRaw = form["employmentId"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(employmentRaw))
        {
            if (!Guid.TryParse(employmentRaw, out var parsed)) return Results.BadRequest(new { error = "Invalid employment identifier." });
            employmentId = parsed;
        }
        Guid? productId = null;
        var productRaw = form["employeePensionProductId"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(productRaw))
        {
            if (!Guid.TryParse(productRaw, out var parsedProduct))
                return Results.BadRequest(new { error = "Invalid pension product." });
            productId = parsedProduct;
        }
        if (type is not (3 or 7) && (!productId.HasValue || !await db.EmployeePensionProducts.AsNoTracking()
            .AnyAsync(x => x.Id == productId.Value && x.EmploymentId == employmentId, ct)))
            return Results.BadRequest(new { error = "Select a saved pension product of this employee." });
        if (type == 5 && !await db.EmployeePensionProducts.AsNoTracking()
            .AnyAsync(x => x.Id == productId && x.ProductType == Alpha.Domain.Reporting.PensionProductType.PensionFund, ct))
            return Results.BadRequest(new { error = "Default pension fund joining request requires a pension fund." });
        if (type is not (3 or 7) && !employmentId.HasValue) return Results.BadRequest(new { error = "This document requires an employee." });
        if (type is (3 or 7) && (employmentId.HasValue || productId.HasValue)) return Results.BadRequest(new { error = "Employer-wide documents cannot belong to an employee or product." });
        if (type is 3 or 7 && !await access.CanEditEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (employmentId.HasValue && !await db.Employments.AsNoTracking()
                .AnyAsync(x => x.Id == employmentId.Value && x.EmployerId == employerId, ct))
            return Results.BadRequest(new { error = "Employee does not belong to the employer." });
        if (await db.EmployerDocuments.CountAsync(x => x.OrganizationId == organizationId && x.EmployerId == employerId, ct) >= MaxDocuments)
            return Results.BadRequest(new { error = "Maximum 100 employer documents." });
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0 || file.Length > MaxBytes || !file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
            return Results.BadRequest(new { error = "Please upload a PDF up to 10 MB." });
        if (Path.GetFileName(file.FileName).Length > 255)
            return Results.BadRequest(new { error = "PDF file name must be at most 255 characters." });
        var scannerLimit = config.GetValue<long?>("Security:MalwareScanner:MaxFileBytes") ?? ConfiguredMalwareScanner.FreeTierSafeMaxBytes;
        if (string.Equals(config["Security:MalwareScanner:Provider"], "Cloudmersive", StringComparison.OrdinalIgnoreCase) && file.Length > scannerLimit)
            return Results.BadRequest(new { error = "PDF exceeds the configured scan size limit." });
        await using var source = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct);
        var content = buffer.ToArray();
        if (content.Length < 5 || content[0] != 37 || content[1] != 80 || content[2] != 68 || content[3] != 70 || content[4] != 45)
            return Results.BadRequest(new { error = "Invalid PDF content." });
        using var scanStream = new MemoryStream(content, writable: false);
        var scan = await scanner.ScanAsync(scanStream, file.FileName, ct);
        if (scan.Verdict == MalwareScanVerdict.Unavailable) return Results.StatusCode(503);
        if (scan.Verdict == MalwareScanVerdict.Infected) return Results.BadRequest(new { error = "Security scan rejected this document." });
        var entry = new EmployerDocument(organizationId, employerId, employmentId, productId, type,
            Path.GetFileName(file.FileName), content, content.LongLength,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant());
        entry.SetEncryptedContent(protector.ProtectBytes(content, $"employer-document:{organizationId}:{employerId}:{entry.Id}"));
        db.EmployerDocuments.Add(entry);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { entry.Id, entry.DocumentTypeCode, entry.EmploymentId, entry.EmployeePensionProductId, entry.OriginalFileName, entry.SizeBytes, entry.Sha256, entry.CreatedAt });
    }

    private sealed record AssignProductRequest(Guid EmployeePensionProductId);

    private static async Task<IResult> AssignProductAsync(Guid organizationId, Guid employerId, Guid documentId,
        AssignProductRequest request, AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var doc = await db.EmployerDocuments.SingleOrDefaultAsync(x =>
            x.Id == documentId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (doc is null) return Results.NotFound();
        if (doc.EmploymentId is null || doc.EmployeePensionProductId.HasValue)
            return Results.Conflict(new { error = "Only unassigned legacy employee documents can be linked to a product." });
        var product = await db.EmployeePensionProducts.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == request.EmployeePensionProductId && x.EmploymentId == doc.EmploymentId, ct);
        if (product is null) return Results.BadRequest(new { error = "Pension product does not belong to the document's employee." });
        if (doc.DocumentTypeCode == 5 && product.ProductType != Alpha.Domain.Reporting.PensionProductType.PensionFund)
            return Results.BadRequest(new { error = "Type 5 documents require a pension fund." });
        doc.AssignProduct(product.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { doc.Id, doc.EmployeePensionProductId });
    }

    private static async Task<IResult> DownloadAsync(Guid organizationId, Guid employerId, Guid documentId,
        AlphaDbContext db, IOrganizationAuthorization access, IDataProtectionService protector, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var doc = await db.EmployerDocuments.AsNoTracking().SingleOrDefaultAsync(x =>
            x.Id == documentId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (doc is null) return Results.NotFound();
        var bytes = protector.UnprotectBytes(doc.Content, $"employer-document:{organizationId}:{employerId}:{doc.Id}");
        return Results.File(bytes, "application/pdf", doc.OriginalFileName);
    }

    private static async Task<IResult> DeleteAsync(Guid organizationId, Guid employerId, Guid documentId,
        AlphaDbContext db, IOrganizationAuthorization access, CancellationToken ct)
    {
        if (!await access.CanEditEmployerAsync(organizationId, employerId, ct)
            && !await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var doc = await db.EmployerDocuments.SingleOrDefaultAsync(x =>
            x.Id == documentId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (doc is null) return Results.NotFound();
        if (doc.EmploymentId is null && !await access.CanEditEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        db.EmployerDocuments.Remove(doc);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
