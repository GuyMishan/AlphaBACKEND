using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Alpha.Application.Abstractions;
using Alpha.Application.Authorization;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Endpoints;

public static class ReportTransmissionEndpoints
{
    public static IEndpointRouteBuilder MapReportTransmissionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/organizations/{organizationId:guid}/employers/{employerId:guid}/manual-reports")
            .RequireAuthorization().WithTags("Report transmission");
        group.MapGet("/{reportId:guid}/transmissions", GetHistoryAsync);
        group.MapPost("/{reportId:guid}/transmissions", SendAsync);
        return endpoints;
    }

    private static async Task<IResult> GetHistoryAsync(Guid organizationId, Guid employerId, Guid reportId,
        IAlphaDbContext db, OrganizationAccessService access, CancellationToken ct)
    {
        if (!await access.CanAccessEmployerAsync(organizationId, employerId, ct)) return Results.Forbid();
        var exists = await db.ManualReports.AsNoTracking().AnyAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (!exists) return Results.NotFound();

        var items = await db.ReportTransmissions.AsNoTracking()
            .Where(x => x.ReportId == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId)
            .OrderByDescending(x => x.AttemptNumber)
            .Select(x => new { x.Id, x.Provider, x.AttemptNumber, x.Status, x.ExternalId, x.ErrorMessage, x.StartedAt, x.SentAt, x.CompletedAt, x.CreatedAt })
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    private static async Task<IResult> SendAsync(Guid organizationId, Guid employerId, Guid reportId,
        SendReportRequest? request, IAlphaDbContext db, OrganizationAccessService access,
        IEnumerable<IReportTransmissionProvider> providers, CancellationToken ct)
    {
        if (!await access.CanEditEmployeeAsync(organizationId, employerId, ct)) return Results.Forbid();
        var report = await db.ManualReports.FirstOrDefaultAsync(x => x.Id == reportId && x.OrganizationId == organizationId && x.EmployerId == employerId, ct);
        if (report is null) return Results.NotFound();
        if (report.Status != ManualReportStatus.Validated)
            return Results.Conflict(new { error = "ניתן לשלוח רק דיווח שעבר ולידציה סופית בהצלחה.", status = report.Status.ToString() });

        var providerName = string.IsNullOrWhiteSpace(request?.Provider) ? "MockClearinghouse" : request.Provider.Trim();
        var provider = providers.FirstOrDefault(x => string.Equals(x.Name, providerName, StringComparison.OrdinalIgnoreCase));
        if (provider is null) return Results.BadRequest(new { error = "ספק השליחה שנבחר אינו קיים.", provider = providerName });

        var payload = await BuildPayloadAsync(report, db, ct);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var attemptNumber = (await db.ReportTransmissions.Where(x => x.ReportId == reportId).MaxAsync(x => (int?)x.AttemptNumber, ct) ?? 0) + 1;
        var transmission = new ReportTransmission(reportId, organizationId, employerId, provider.Name, attemptNumber);
        transmission.Start(hash);
        db.ReportTransmissions.Add(transmission);
        report.MarkTransmissionStarted();
        await db.SaveChangesAsync(ct);

        try
        {
            var result = await provider.SendAsync(new ReportTransmissionEnvelope(reportId, organizationId, employerId, payload, hash), ct);
            var status = result.Success ? ReportTransmissionStatus.Accepted : ReportTransmissionStatus.Rejected;
            transmission.Complete(status, result.ExternalId, result.ResponsePayload, result.ErrorMessage);
            if (result.Success) report.MarkSent();
            else report.MarkTransmissionError(result.ErrorMessage ?? "הדיווח נדחה על ידי ספק השליחה.");
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToResponse(report, transmission));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            transmission.Complete(ReportTransmissionStatus.Error, null, null, ex.Message);
            report.MarkTransmissionError(ex.Message);
            await db.SaveChangesAsync(CancellationToken.None);
            return Results.Json(ToResponse(report, transmission), statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<string> BuildPayloadAsync(ManualReport report, IAlphaDbContext db, CancellationToken ct)
    {
        var employees = await db.ManualReportEmployees.AsNoTracking().Where(x => x.ReportId == report.Id).OrderBy(x => x.EmployeeNumber).ToListAsync(ct);
        var employeeIds = employees.Select(x => x.Id).ToArray();
        var products = await db.ManualReportProducts.AsNoTracking().Where(x => employeeIds.Contains(x.ReportEmployeeId)).OrderBy(x => x.AllocationOrder).ToListAsync(ct);
        var productIds = products.Select(x => x.Id).ToArray();
        var contributions = await db.ManualContributions.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);
        var payments = await db.ManualReportPayments.AsNoTracking().Where(x => productIds.Contains(x.ReportProductId)).ToListAsync(ct);

        var payload = new
        {
            report = new { report.Id, report.OrganizationId, report.EmployerId, report.ReportingMonth, report.SalaryPaymentDate, report.ReportKind, report.SnapshotTakenAt, report.ValidatedAt },
            employees = employees.Select(e => new
            {
                e.Id, e.EmploymentId, e.PersonId, e.NationalId, e.FirstName, e.LastName, e.EmployeeNumber, e.MonthlySalary,
                products = products.Where(p => p.ReportEmployeeId == e.Id).Select(p => new
                {
                    p.Id, p.ProductType, p.PolicyNumber, p.FundExternalKey, p.FundCode, p.FundName, p.FundCompanyName,
                    p.SalaryMonth, p.Salary, p.ReportingType, p.SalaryLayer, p.Section14, p.Section14StartDate,
                    p.SalaryAllocationType, p.SalaryAllocationValue, p.AllocationOrder,
                    contributions = contributions.Where(c => c.ReportProductId == p.Id).Select(c => new { c.Party, c.Component, c.Amount, c.Percentage, c.ExemptPayments }),
                    payment = payments.Where(m => m.ReportProductId == p.Id).Select(m => new { m.ProviderName, m.ProviderAccount, m.PaymentMethod, m.ValueDate, m.ReferenceNumber, m.EmployerBankName, m.EmployerBankCode, m.EmployerBranch, m.EmployerAccount, m.ConfirmationFileName }).FirstOrDefault()
                })
            })
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static object ToResponse(ManualReport report, ReportTransmission transmission) => new
    {
        reportId = report.Id,
        reportStatus = report.Status,
        transmission = new { transmission.Id, transmission.Provider, transmission.AttemptNumber, transmission.Status, transmission.ExternalId, transmission.ErrorMessage, transmission.StartedAt, transmission.SentAt, transmission.CompletedAt }
    };

    public sealed record SendReportRequest(string? Provider);
}
