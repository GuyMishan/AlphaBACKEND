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
                throw new InvalidOperationException($"Person {person.Id} is missing encrypted national ID.");

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

        var accounts = await db.EmployerPaymentAccounts.ToListAsync(stoppingToken);
        var protectedAccounts = 0;
        var refreshedAccountHashes = 0;
        foreach (var account in accounts)
        {
            if (string.IsNullOrWhiteSpace(account.AccountNumberEncrypted) || string.IsNullOrWhiteSpace(account.AccountHolderIdEncrypted))
                throw new InvalidOperationException($"Payment account {account.Id} is missing encrypted sensitive values.");
            var accountNumber = protector.Unprotect(account.AccountNumberEncrypted, "bank-account-number").Trim();
            var holderId = protector.Unprotect(account.AccountHolderIdEncrypted, "bank-account-holder-id").Trim();
            if (string.IsNullOrWhiteSpace(accountNumber) || string.IsNullOrWhiteSpace(holderId))
                throw new InvalidOperationException($"Payment account {account.Id} has no recoverable sensitive values.");
            var accountHash = protector.LookupHash(accountNumber, "bank-account-number-lookup");
            if (string.IsNullOrWhiteSpace(account.AccountNumberEncrypted) || string.IsNullOrWhiteSpace(account.AccountHolderIdEncrypted))
            {
                account.SetProtectedValues(protector.Protect(accountNumber, "bank-account-number"), accountHash,
                    protector.Protect(holderId, "bank-account-holder-id"));
                protectedAccounts++;
            }
            else if (!string.Equals(account.AccountNumberLookupHash, accountHash, StringComparison.Ordinal))
            {
                account.SetProtectedValues(account.AccountNumberEncrypted, accountHash, account.AccountHolderIdEncrypted);
                refreshedAccountHashes++;
            }
        }

        var transmissions = await db.ReportTransmissions.ToListAsync(stoppingToken);
        var protectedTransmissions = 0;
        foreach (var transmission in transmissions)
        {
            if (transmission.Payload.Length == 0) continue;
            var payload = transmission.Payload;
            var alreadyProtected = payload.Length >= 4 && payload[0] == (byte)'A' && payload[1] == (byte)'L' && payload[2] == (byte)'P' && payload[3] == 1;
            if (alreadyProtected) continue;
            db.Entry(transmission).Property(x => x.Payload).CurrentValue =
                protector.ProtectBytes(payload, $"report-transmission:{transmission.Id}");
            protectedTransmissions++;
        }

        await db.SaveChangesAsync(stoppingToken);
        logger.LogInformation("Sensitive-data encryption backfill completed: {ProtectedPeople} people encrypted, {RefreshedHashes} identity hashes refreshed, {ProtectedAccounts} payment accounts encrypted, {RefreshedAccountHashes} account hashes refreshed, {ProtectedTransmissions} report transmission payloads encrypted.", protectedPeople, refreshedHashes, protectedAccounts, refreshedAccountHashes, protectedTransmissions);
    }
}
