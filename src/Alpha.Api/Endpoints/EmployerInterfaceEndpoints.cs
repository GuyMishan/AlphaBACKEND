using System.Text;
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
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/employer-interface").RequireAuthorization().WithTags("Employer interface");
        group.MapPost("/validate", ValidateAsync).DisableAntiforgery();
        group.MapPost("/import", ImportAsync).DisableAntiforgery();
        group.MapGet("/reports/{reportId:guid}/xml", ExportAsync);
        return endpoints;
    }

    private static async Task<IResult> ValidateAsync(Guid organizationId, Guid employerId, HttpRequest request, OrganizationAccessService access, EmployerInterfaceService service, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var read = await ReadFileAsync(request, ct); if (read.Error is not null) return Results.BadRequest(new { error = read.Error });
        var validation = service.Validate(read.Text!, read.FileType!);
        return Results.Ok(new { validation.IsValid, validation.InterfaceType, validation.Version, validation.Issues, fileName = read.FileName, fileHash = EmployerInterfaceService.Hash(read.Text!) });
    }

    private static async Task<IResult> ImportAsync(Guid organizationId, Guid employerId, HttpRequest request, OrganizationAccessService access, EmployerInterfaceService service, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var read = await ReadFileAsync(request, ct); if (read.Error is not null) return Results.BadRequest(new { error = read.Error });
        if (!string.Equals(read.FileType, "employer-interface", StringComparison.OrdinalIgnoreCase)) return Results.BadRequest(new { error = "ייבוא ל-Alpha נתמך כרגע עבור קובץ ממשק מעסיקים XML. לקובץ Excel השתמשו בקליטת קובץ השכר." });
        var result = await service.ImportAsync(organizationId, employerId, read.Text!, ct);
        return result.Validation.IsValid ? Results.Ok(new { result.ReportId, result.ImportedEmployees, result.UnmatchedRows, result.Validation, fileName = read.FileName, fileHash = EmployerInterfaceService.Hash(read.Text!) }) : Results.BadRequest(result);
    }

    private static async Task<IResult> ExportAsync(Guid organizationId, Guid employerId, Guid reportId, IAlphaDbContext db, OrganizationAccessService access, EmployerInterfaceService service, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.AsNoTracking().FirstOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.Status is not ManualReportStatus.Validated and not ManualReportStatus.Processing and not ManualReportStatus.Sent and not ManualReportStatus.Completed) return Results.Conflict(new { error = "ניתן להפיק ממשק מעסיקים רק לאחר ולידציה סופית." });
        var xml = await service.ExportAsync(report, ct);
        return Results.File(Encoding.UTF8.GetBytes(xml), "application/xml", $"employer-interface-{report.ReportingMonth:yyyy-MM}-{report.Id:N}.xml");
    }

    private static async Task<(string? Text, string? FileName, string? FileType, string? Error)> ReadFileAsync(HttpRequest request, CancellationToken ct)
    {
        if (!request.HasFormContentType) return (null, null, null, "יש לשלוח multipart/form-data.");
        var form = await request.ReadFormAsync(ct); var file = form.Files.GetFile("file"); var fileType = form["fileType"].ToString();
        if (file is null || file.Length == 0) return (null, null, null, "לא נבחר קובץ.");
        if (file.Length > 20 * 1024 * 1024) return (null, null, null, "הקובץ גדול מדי. הגודל המרבי הוא 20MB.");
        if (fileType == "employer-interface" && !file.FileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) return (null, null, null, "סוג הקובץ שנבחר הוא ממשק מעסיקים ולכן יש להעלות XML.");
        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, true); var text = await reader.ReadToEndAsync(ct);
        return (text, file.FileName, fileType, null);
    }
}
