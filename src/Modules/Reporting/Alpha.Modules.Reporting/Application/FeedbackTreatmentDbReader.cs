using System.Data;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Modules.Reporting.Application;

public sealed record FeedbackDbTreatment(
    int Code, string Choice, string CorrectionTarget, string CorrectionFields, string RecipientType)
{
    public bool CanCorrect => Choice is "correctionOnly" or "correctionOrExternalInquiry";
    public bool CanInquire => Choice is "correctionOrExternalInquiry" or "externalInquiryOnly";
}

public static class FeedbackTreatmentDbReader
{
    public static async Task<IReadOnlyDictionary<int, FeedbackDbTreatment>> LoadAsync(
        AlphaDbContext db, CancellationToken ct)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT code, treatment_choice, correction_target, correction_fields::text,
                   external_recipient_type
            FROM reference_data.feedback_treatment_policies
            """;
        if (command.Connection!.State != ConnectionState.Open)
            await command.Connection.OpenAsync(ct);
        var rows = new Dictionary<int, FeedbackDbTreatment>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new FeedbackDbTreatment(reader.GetInt32(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4));
            if (!rows.TryAdd(row.Code, row))
                throw new InvalidOperationException("Duplicate feedback treatment configuration.");
        }
        return rows;
    }
}
