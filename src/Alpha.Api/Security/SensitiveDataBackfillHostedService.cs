using Alpha.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Security;

public sealed class SensitiveDataBackfillHostedService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<SensitiveDataBackfillHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Security:EnableSensitiveDataBackfill", false)) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IAlphaDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionService>();
        // Phase 2 is intentionally additive: encrypted shadow columns are populated in SQL migration/backfill.
        // Plaintext columns are not removed until application reads are switched and verified.
        logger.LogInformation("Sensitive data backfill enabled. Records: people={People}, paymentAccounts={Accounts}",
            await db.People.CountAsync(stoppingToken), await db.EmployerPaymentAccounts.CountAsync(stoppingToken));
    }
}
