using System.Security.Cryptography;
using System.Text;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Security;

public sealed class SensitiveDataBackfillHostedService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<SensitiveDataBackfillHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Security:EnableSensitiveDataBackfill", true)) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionService>();

        var people = await db.People.Where(x => x.NationalIdEncrypted == null || x.NationalIdLookupHash == null).ToListAsync(stoppingToken);
        foreach (var person in people)
        {
            var normalized = person.NationalId.Trim();
            person.SetProtectedNationalId(protector.Protect(normalized, "person-national-id"),
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant());
        }

        var accounts = await db.EmployerPaymentAccounts.Where(x => x.AccountNumberEncrypted == null || x.AccountHolderIdEncrypted == null).ToListAsync(stoppingToken);
        foreach (var account in accounts)
            account.SetProtectedValues(protector.Protect(account.AccountNumber, "bank-account-number"),
                protector.Protect(account.AccountHolderId, "bank-account-holder-id"));

        await db.SaveChangesAsync(stoppingToken);
        logger.LogInformation("Sensitive-data encryption backfill completed: {People} people, {Accounts} payment accounts. Plaintext compatibility columns remain until encrypted read/write cutover is verified.", people.Count, accounts.Count);
    }
}
