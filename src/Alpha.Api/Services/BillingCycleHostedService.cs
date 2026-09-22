using Alpha.Application.Abstractions;
using Alpha.Application.Billing;
using Alpha.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

public sealed record BillingCycleWindow(DateTimeOffset Start, DateTimeOffset End);

public static class BillingCyclePlanner
{
    public static IReadOnlyList<BillingCycleWindow> PreviousMonthlyWindows(
        DateTimeOffset nowUtc, int catchUpMonths)
    {
        catchUpMonths = Math.Clamp(catchUpMonths, 1, 24);
        var currentMonthStart = new DateTimeOffset(
            nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, TimeSpan.Zero);

        var windows = new List<BillingCycleWindow>(catchUpMonths);
        for (var offset = catchUpMonths; offset >= 1; offset--)
        {
            var start = currentMonthStart.AddMonths(-offset);
            windows.Add(new BillingCycleWindow(start, start.AddMonths(1)));
        }

        return windows;
    }
}

public sealed class BillingCycleHostedService(
    IServiceScopeFactory scopes,
    IConfiguration configuration,
    ILogger<BillingCycleHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (configuration.GetValue("Billing:AutomaticBillingEnabled", false))
                    await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automatic billing cycle failed.");
            }

            var minutes = Math.Clamp(configuration.GetValue("Billing:JobIntervalMinutes", 60), 15, 1440);
            await Task.Delay(TimeSpan.FromMinutes(minutes), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAlphaDbContext>();
        var billing = scope.ServiceProvider.GetRequiredService<IBillingCycleService>();

        var now = DateTimeOffset.UtcNow;
        var catchUpMonths = Math.Clamp(configuration.GetValue("Billing:CatchUpMonths", 3), 1, 24);
        var windows = BillingCyclePlanner.PreviousMonthlyWindows(now, catchUpMonths);

        var accountIds = await db.BillingAccounts.AsNoTracking()
            .Where(x => x.Status == BillingAccountStatus.Active)
            .Select(x => x.Id)
            .ToListAsync(ct);

        foreach (var window in windows)
        {
            foreach (var accountId in accountIds)
            {
                try
                {
                    await billing.RunPeriodAsync(accountId, window.Start, window.End, true, ct);
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogWarning(ex,
                        "Skipped billing account {BillingAccountId} for {PeriodStart} - {PeriodEnd}.",
                        accountId, window.Start, window.End);
                }
                catch (DbUpdateException ex)
                {
                    // Unique period/payment indexes are the final guard if two workers race.
                    logger.LogWarning(ex,
                        "Billing race detected for account {BillingAccountId} and period {PeriodStart} - {PeriodEnd}.",
                        accountId, window.Start, window.End);
                }
            }
        }

        var graceDays = Math.Clamp(configuration.GetValue("Billing:GracePeriodDays", 7), 1, 90);
        var retryHours = Math.Clamp(configuration.GetValue("Billing:RetryDelayHours", 24), 1, 168);
        var maxAttempts = Math.Clamp(configuration.GetValue("Billing:MaxPaymentAttempts", 4), 1, 20);

        await billing.RetryPastDueAsync(
            TimeSpan.FromDays(graceDays),
            TimeSpan.FromHours(retryHours),
            maxAttempts,
            ct);
    }
}
