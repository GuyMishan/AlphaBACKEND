using Alpha.Application.Abstractions;
using Alpha.Application.Billing;
using Alpha.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Services;

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
        var currentMonthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var previousMonthStart = currentMonthStart.AddMonths(-1);

        var accountIds = await db.BillingAccounts.AsNoTracking()
            .Where(x => x.Status == BillingAccountStatus.Active)
            .Select(x => x.Id)
            .ToListAsync(ct);

        foreach (var accountId in accountIds)
        {
            try
            {
                await billing.RunPeriodAsync(accountId, previousMonthStart, currentMonthStart, true, ct);
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "Skipped billing account {BillingAccountId}.", accountId);
            }
        }

        var graceDays = Math.Clamp(configuration.GetValue("Billing:GracePeriodDays", 7), 1, 90);
        var retryHours = Math.Clamp(configuration.GetValue("Billing:RetryDelayHours", 24), 1, 168);
        await billing.RetryPastDueAsync(TimeSpan.FromDays(graceDays), TimeSpan.FromHours(retryHours), ct);
    }
}
