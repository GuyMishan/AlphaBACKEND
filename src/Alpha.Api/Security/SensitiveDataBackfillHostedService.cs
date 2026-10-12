namespace Alpha.Api.Security;

/// <summary>
/// Runs <see cref="SensitiveDataBackfill"/> once in the background after startup. It never blocks request
/// handling and never stops the host: failures are logged by table and row Id and retried on the next start.
/// Settings: <c>Security:EnableSensitiveDataBackfill</c> (default true), <c>Security:SensitiveDataBackfill:BatchSize</c>
/// (default 500) and <c>:BlobBatchSize</c> (default 20).
/// </summary>
public sealed class SensitiveDataBackfillHostedService(IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<SensitiveDataBackfillHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Security:EnableSensitiveDataBackfill", true)) return;
        await Task.Yield();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var backfill = scope.ServiceProvider.GetRequiredService<SensitiveDataBackfill>();
            var result = await backfill.RunAsync(
                configuration.GetValue("Security:SensitiveDataBackfill:BatchSize", 500),
                configuration.GetValue("Security:SensitiveDataBackfill:BlobBatchSize", 20),
                stoppingToken);

            if (result.Skipped)
                logger.LogInformation("Sensitive-data backfill skipped: {Reason}.", result.SkipReason);
            else if (result.Completed)
                logger.LogInformation("Sensitive-data backfill completed and recorded as {Key}. Rows updated: {Changed}.",
                    SensitiveDataBackfill.CompletionKey, Describe(result.Changed));
            else
                logger.LogError("Sensitive-data backfill finished with {FailureCount} unprocessable rows (left unchanged, see previous errors). " +
                                "Rows updated: {Changed}. It will run again on the next start until every row is resolved.",
                    result.Failures.Count, Describe(result.Changed));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            logger.LogError("Sensitive-data backfill aborted ({ExceptionType}); committed batches are kept and the job resumes on the next start.",
                ex.GetType().Name);
        }
    }

    private static string Describe(IReadOnlyDictionary<string, int> changed) =>
        changed.Count == 0 ? "none" : string.Join(", ", changed.Select(x => $"{x.Key}={x.Value}"));
}
