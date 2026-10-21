using System.Data;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.IdentityAccess.Contracts;
using Alpha.Modules.Reporting.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.Reporting.Features.Feedback;

/// <summary>Platform-only management of DB-owned V006 treatment routing.</summary>
public static class FeedbackTreatmentAdministrationEndpoints
{
    public sealed record UpdateTreatment(string TreatmentChoice, string CorrectionTarget,
        string ExternalRecipientType);

    public static IEndpointRouteBuilder MapFeedbackTreatmentAdministrationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/admin/feedback-treatment-policies")
            .RequireAuthorization().WithTags("Feedback treatment administration");
        group.MapGet("/", ListAsync);
        group.MapPut("/{code:int}", UpdateAsync);
        return endpoints;
    }

    private static async Task<IResult> ListAsync(AlphaDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (!user.IsPlatformAdmin) return Results.Forbid();
        var items = (await FeedbackTreatmentDbReader.LoadAsync(db, ct)).Values
            .OrderBy(x => x.Code)
            .Select(x => new
            {
                x.Code, treatmentChoice = x.Choice, x.CorrectionTarget,
                x.CorrectionFields, externalRecipientType = x.RecipientType
            });
        return Results.Ok(new { items });
    }

    private static async Task<IResult> UpdateAsync(int code, UpdateTreatment request,
        AlphaDbContext db, ICurrentUser user, CancellationToken ct)
    {
        if (!user.IsPlatformAdmin) return Results.Forbid();
        if (!FeedbackResolutionPlaybookCatalog.TryGet(code, out var playbook))
            return Results.NotFound();
        var validChoice = request.TreatmentChoice is
            "correctionOnly" or "correctionOrExternalInquiry" or "externalInquiryOnly" or "informationOnly";
        var correct = request.TreatmentChoice is "correctionOnly" or "correctionOrExternalInquiry";
        var external = request.TreatmentChoice is "correctionOrExternalInquiry" or "externalInquiryOnly";
        var supportedCorrection = (playbook.Actions &
            (FeedbackResolutionAction.EditEmployee | FeedbackResolutionAction.EditEmployment
             | FeedbackResolutionAction.EditProduct | FeedbackResolutionAction.EditContribution
             | FeedbackResolutionAction.EditPayment | FeedbackResolutionAction.UploadDocument
             | FeedbackResolutionAction.PrepareCorrection | FeedbackResolutionAction.PrepareNegative
             | FeedbackResolutionAction.LinkOriginalRecord | FeedbackResolutionAction.Reconcile)) != 0;
        var validTarget = request.CorrectionTarget == FeedbackResolutionWireProjection.WireName(playbook.Resolver);
        var validRecipient = request.ExternalRecipientType is "manufacturer" or "clearinghouse" or "institution";
        if (!validChoice || (correct && (!supportedCorrection || !validTarget))
            || (external && (!playbook.Actions.HasFlag(FeedbackResolutionAction.OpenExternalCase) || !validRecipient))
            || (!external && request.ExternalRecipientType != "none")
            || (!correct && request.CorrectionTarget != "none")
            || (playbook.ResolutionType == FeedbackResolutionType.Informational
                && request.TreatmentChoice != "informationOnly"))
            return Results.BadRequest(new { error = "invalid_feedback_treatment_policy" });

        var affected = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE reference_data.feedback_treatment_policies
            SET treatment_choice = {request.TreatmentChoice},
                correction_target = {request.CorrectionTarget},
                external_recipient_type = {request.ExternalRecipientType},
                updated_at = now()
            WHERE code = {code}
            """, ct);
        if (affected == 0) return Results.NotFound();
        return Results.NoContent();
    }
}
