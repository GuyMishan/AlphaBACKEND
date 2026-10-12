using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Modules.Reporting.Features.Validation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Alpha.Modules.Reporting.Features.Import;

public static class EmployerInterfaceEndpoints
{
    public static IEndpointRouteBuilder MapEmployerInterfaceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/employer-interface")
            .RequireAuthorization().WithTags("Employer interface");
        group.MapEmployerInterfaceValidationEndpoint();
        group.MapPost("/import", ImportAsync).DisableAntiforgery()
            .WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(21 * 1024 * 1024));
        group.MapEmployerInterfaceValidationExportEndpoints();
        return endpoints;
    }

    private static async Task<IResult> ImportAsync(Guid organizationId, Guid employerId, HttpRequest request,
        IOrganizationAuthorization access, EmployerInterfaceService service, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        var read = await EmployerInterfaceUploadReader.ReadAsync(request, ct);
        if (read.Error is not null) return Results.BadRequest(new { error = read.Error });
        var result = await service.IngestAsync(organizationId, employerId, read.FileName!, read.Bytes!, read.PaymentAccountId, read.SalaryPaymentDate, ct);
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

}

