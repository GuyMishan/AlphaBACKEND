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

        var people = await db.People.ToListAsync(stoppingToken);
        var protectedPeople = 0;
        var refreshedHashes = 0;
        foreach (var person in people)
        {
            string normalized;
            if (!string.IsNullOrWhiteSpace(person.NationalIdEncrypted))
                normalized = protector.Unprotect(person.NationalIdEncrypted, "person-national-id").Trim();
            else
                normalized = person.NationalId.Trim();

            if (string.IsNullOrWhiteSpace(normalized))
                throw new InvalidOperationException($"Person {person.Id} has no recoverable national ID.");

            var expectedHash = protector.LookupHash(normalized, "person-national-id-lookup");
            if (string.IsNullOrWhiteSpace(person.NationalIdEncrypted))
            {
                person.SetProtectedNationalId(protector.Protect(normalized, "person-national-id"), expectedHash);
                protectedPeople++;
            }
            else if (!string.Equals(person.NationalIdLookupHash, expectedHash, StringComparison.Ordinal))
            {
                person.SetProtectedNationalId(person.NationalIdEncrypted, expectedHash);
                refreshedHashes++;
            }
        }

        var accounts = await db.EmployerPaymentAccounts.Where(x => x.AccountNumberEncrypted == null || x.AccountHolderIdEncrypted == null).ToListAsync(stoppingToken);
        foreach (var account in accounts)
            account.SetProtectedValues(protector.Protect(account.AccountNumber, "bank-account-number"),
                protector.Protect(account.AccountHolderId, "bank-account-holder-id"));

        await db.SaveChangesAsync(stoppingToken);
        logger.LogInformation("Sensitive-data encryption backfill completed: {ProtectedPeople} people encrypted, {RefreshedHashes} identity hashes refreshed, {Accounts} payment accounts encrypted. Plaintext compatibility columns remain until encrypted read/write cutover is verified.", protectedPeople, refreshedHashes, accounts.Count);
    }
}
