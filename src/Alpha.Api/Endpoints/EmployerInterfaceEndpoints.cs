using Alpha.Api.Services;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class EmployerInterfaceEndpoints
{
    public static IEndpointRouteBuilder MapEmployerInterfaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/employer-interface")
            .RequireAuthorization().WithTags("Employer interface");
        group.MapPost("/validate", ValidateAsync).DisableAntiforgery();
        group.MapPost("/import", ImportAsync).DisableAntiforgery();
        group.MapGet("/schemas/status", SchemaStatusAsync);
        group.MapGet("/reports/{reportId:guid}/xml", ExportAsync);
        return endpoints;
    }

    private static async Task<IResult> ValidateAsync(Guid organizationId, Guid employerId, HttpRequest request,
        OrganizationAccessService access, EmployerInterfaceService service, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var read = await ReadXmlAsync(request, ct);
        if (read.Error is not null) return Results.BadRequest(new { error = read.Error });
        var validation = service.Validate(read.Bytes!);
        return Results.Ok(new
        {
            validation.IsValid,
            documentType = validation.DocumentType?.ToString(),
            validation.Version,
            validation.SchemaFileName,
            validation.Issues,
            fileName = read.FileName,
            fileHash = EmployerInterfaceService.Hash(read.Bytes!)
        });
    }

    private static async Task<IResult> ImportAsync(Guid organizationId, Guid employerId, HttpRequest request,
        OrganizationAccessService access, EmployerInterfaceService service, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var read = await ReadXmlAsync(request, ct);
        if (read.Error is not null) return Results.BadRequest(new { error = read.Error });
        var result = await service.IngestAsync(organizationId, employerId, read.FileName!, read.Bytes!, ct);
        var response = new
        {
            result.ReportId,
            result.FeedbackId,
            documentType = result.Validation.DocumentType?.ToString(),
            result.ImportedEmployees,
            result.UnmatchedRows,
            result.Validation,
            fileName = read.FileName,
            fileHash = EmployerInterfaceService.Hash(read.Bytes!)
        };
        return result.Validation.IsValid ? Results.Ok(response) : Results.BadRequest(response);
    }

    private static async Task<IResult> SchemaStatusAsync(Guid organizationId, Guid employerId,
        OrganizationAccessService access, EmployerInterfaceSchemaRegistry schemas, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var missing = schemas.MissingSchemas();
        return Results.Ok(new { version = EmployerInterfaceSchemaRegistry.Version, ready = missing.Count == 0, missing, runtimeDirectory = schemas.SchemaDirectory });
    }

    private static async Task<IResult> ExportAsync(Guid organizationId, Guid employerId, Guid reportId,
        IAlphaDbContext db, OrganizationAccessService access, EmployerInterface006ExportService exporter, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.Status is not ManualReportStatus.Validated and not ManualReportStatus.Processing and not ManualReportStatus.Sent and not ManualReportStatus.Completed)
            return Results.Conflict(new { error = "Employer Interface XML can only be exported after final Alpha report validation." });
        var generated = await exporter.ExportAsync(report, ct);
        if (!generated.Validation.IsValid)
            return Results.BadRequest(new { error = "Generated Employer Interface XML does not validate against its official report-type-specific 006 XSD.", generated.Validation });
        return Results.File(generated.Bytes, "application/xml", $"employer-interface-{report.ReportingMonth:yyyy-MM}-{report.Id:N}.xml");
    }

    private static async Task<(byte[]? Bytes, string? FileName, string? Error)> ReadXmlAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType) return (null, null, "multipart/form-data is required.");
        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file");
        if (file is null || file.Length == 0) return (null, null, "No XML file was selected.");
        if (file.Length > 20 * 1024 * 1024) return (null, null, "The maximum Employer Interface XML size is 20MB.");
        if (!file.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !file.FileName.EndsWith(".dat", StringComparison.OrdinalIgnoreCase) && !file.FileName.EndsWith(".tst", StringComparison.OrdinalIgnoreCase))
            return (null, null, "Employer Interface uploads must be XML/DAT/TST files containing XML.");
        await using var stream = file.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, ct);
        return (memory.ToArray(), Path.GetFileName(file.FileName), null);
    }
}
