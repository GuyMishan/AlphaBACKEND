namespace Alpha.Worker;

public sealed class Worker(ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Alpha background worker started.");
        await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
    }
}
