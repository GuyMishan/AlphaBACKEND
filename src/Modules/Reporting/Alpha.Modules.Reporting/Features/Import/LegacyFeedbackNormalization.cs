using System.Security.Cryptography;
using System.Xml;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Alpha.Modules.Reporting.Features.Import;

/// <summary>
/// One-time startup backfill (introduced 2026-10-01) that normalizes feedback stored before ingestion did it.
/// Holds one payload in memory at a time, keeps the original processing order, never fails startup because of a
/// single row, and records <see cref="CompletionKey"/> after a pass with no failures so later starts skip it.
/// </summary>
public sealed class LegacyFeedbackNormalization(
    AlphaDbContext db, EmployerInterfaceService feedback, ILogger<LegacyFeedbackNormalization> logger)
{
    public const string CompletionKey = "legacy-feedback-normalization-v1";

    public sealed record Result(bool Skipped, int Processed, IReadOnlyList<Guid> FailedFeedbackIds);

    public async Task<Result> RunAsync(CancellationToken ct)
    {
        if (await DataFixMarkers.IsAppliedAsync(db, CompletionKey, ct)) return new Result(true, 0, []);

        // Ids only: the payloads are loaded one at a time below.
        var feedbackIds = await db.EmployerInterfaceFeedback.AsNoTracking()
            .Where(x => x.ReportId.HasValue)
            .OrderBy(x => x.ReceivedAt).ThenBy(x => x.Id)
            .Select(x => x.Id)
            .ToListAsync(ct);

        var failed = new List<Guid>();
        foreach (var id in feedbackIds)
        {
            try
            {
                await feedback.NormalizeStoredFeedbackAsync(id, ct);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or XmlException or DbUpdateException)
            {
                failed.Add(id);
                logger.LogError("Legacy feedback normalization failed for feedback {FeedbackId} ({ExceptionType}); it was left unchanged.",
                    id, ex.GetType().Name);
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        if (failed.Count == 0)
        {
            await DataFixMarkers.MarkAppliedAsync(db, CompletionKey, ct);
            logger.LogInformation("Legacy feedback normalization completed for {Count} feedback files and recorded as {Key}.",
                feedbackIds.Count, CompletionKey);
        }
        else
        {
            logger.LogError("Legacy feedback normalization left {FailedCount} of {Count} feedback files unresolved; it runs again on the next start.",
                failed.Count, feedbackIds.Count);
        }

        return new Result(false, feedbackIds.Count, failed);
    }
}
