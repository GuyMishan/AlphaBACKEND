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
    private static async Task<IResult> UpdateTreatmentAsync(
        Guid organizationId, Guid employerId, Guid reportId, Guid reportProductId, UpdateTreatmentRequest request,
        AlphaDbContext db, IOrganizationAuthorization access, ICurrentUser currentUser, HttpContext http, CancellationToken ct)
    {
        if (!await access.CanCreateReportAsync(organizationId, employerId, ct)) return Results.Forbid();
        if (!TreatmentStatuses.Any(x => x.Code == request.StatusCode)) return Results.BadRequest(new { error = "treatment_status_invalid" });
        if ((request.Note?.Length ?? 0) > 4000) return Results.BadRequest(new { error = "treatment_note_too_long" });
        var belongs = await (from p in db.ManualReportProducts.AsNoTracking()
                             join e in db.ManualReportEmployees.AsNoTracking() on p.ReportEmployeeId equals e.Id
                             where p.Id == reportProductId && e.ReportId == reportId && e.OrganizationId == organizationId && e.EmployerId == employerId
                             select p.Id).AnyAsync(ct);
        if (!belongs) return Results.NotFound();

        var treatment = await db.ReportProductTreatments.SingleOrDefaultAsync(x => x.ReportProductId == reportProductId, ct);
        if (!ReportFeedbackStatusResolver.TreatmentVersionMatches(treatment?.UpdatedAt, request.ExpectedUpdatedAt))
            return Results.Conflict(new { error = "treatment_conflict" });
        var creatingTreatment = treatment is null;
        var previous = treatment?.StatusCode ?? string.Empty;
        if (treatment is null)
        {
            treatment = new ReportProductTreatment(reportProductId, request.StatusCode, request.Note, currentUser.UserId);
            db.ReportProductTreatments.Add(treatment);
        }
        else treatment.Update(request.StatusCode, request.Note, currentUser.UserId);
        db.ReportProductTreatmentHistory.Add(new ReportProductTreatmentHistory(reportProductId, previous, request.StatusCode, request.Note, currentUser.UserId));
        db.AuditEvents.Add(new AuditEvent(
            currentUser.UserId,
            "report-feedback.treatment-updated",
            nameof(ReportProductTreatment),
            treatment.Id,
            organizationId,
            employerId,
            JsonSerializer.Serialize(new { reportId, reportProductId, previousStatus = previous, statusCode = request.StatusCode }),
            http.TraceIdentifier));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Results.Conflict(new { error = "treatment_conflict" });
        }
        catch (DbUpdateException) when (creatingTreatment)
        {
            var existsNow = await db.ReportProductTreatments.AsNoTracking()
                .AnyAsync(x => x.ReportProductId == reportProductId, CancellationToken.None);
            if (existsNow) return Results.Conflict(new { error = "treatment_conflict" });
            throw;
        }
        return Results.Ok(new
        {
            treatment.StatusCode, label = TreatmentStatuses.First(x => x.Code == request.StatusCode).Label,
            treatment.Note, treatment.UpdatedAt
        });
    }
}
