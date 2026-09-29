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

        var reports = await db.ManualReports.ToListAsync(stoppingToken);
        var protectedReportEmployerContacts = 0;
        foreach (var report in reports)
        {
            var values = new[] { report.EmployerRegistrationNumberSnapshot, report.EmployerWithholdingFileNumberSnapshot,
                report.EmployerContactPhoneSnapshot, report.EmployerContactEmailSnapshot, report.EmployerContactMobileSnapshot };
            if (values.All(x => string.IsNullOrWhiteSpace(x) || x.StartsWith("alpha:v1:", StringComparison.Ordinal))) continue;
            report.SetProtectedEmployerSnapshot(
                ProtectLegacy(report.EmployerRegistrationNumberSnapshot, $"report-employer-registration:{report.Id}"),
                ProtectLegacy(report.EmployerWithholdingFileNumberSnapshot, $"report-employer-withholding:{report.Id}"),
                ProtectLegacy(report.EmployerContactPhoneSnapshot, $"report-employer-phone:{report.Id}"),
                ProtectLegacy(report.EmployerContactEmailSnapshot, $"report-employer-email:{report.Id}"),
                ProtectLegacy(report.EmployerContactMobileSnapshot, $"report-employer-mobile:{report.Id}"));
            protectedReportEmployerContacts++;
        }

        string ProtectLegacy(string value, string purpose) =>
            string.IsNullOrWhiteSpace(value) || value.StartsWith("alpha:v1:", StringComparison.Ordinal) ? value : protector.Protect(value, purpose);

        var reportEmployees = await db.ManualReportEmployees.ToListAsync(stoppingToken);
        var protectedReportEmployeeIds = 0;
        foreach (var employee in reportEmployees)
        {
            var nationalId = protector.Unprotect(employee.NationalId, $"report-employee-national-id:{employee.Id}").Trim();
            var interfaceIdentifier = protector.Unprotect(employee.InterfaceIdentifier, $"report-employee-interface-id:{employee.Id}").Trim();
            if (string.IsNullOrWhiteSpace(nationalId) || string.IsNullOrWhiteSpace(interfaceIdentifier))
                throw new InvalidOperationException($"Report employee {employee.Id} has no recoverable identifier.");
            var email = protector.Unprotect(employee.EmailSnapshot, $"report-employee-email:{employee.Id}");
            var mobile = protector.Unprotect(employee.MobileSnapshot, $"report-employee-mobile:{employee.Id}");
            var contactNeedsProtection = (!string.IsNullOrWhiteSpace(email) && !employee.EmailSnapshot.StartsWith("alpha:v1:", StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(mobile) && !employee.MobileSnapshot.StartsWith("alpha:v1:", StringComparison.Ordinal));
            if (contactNeedsProtection)
                employee.SetProtectedContactSnapshot(ProtectLegacy(employee.EmailSnapshot, $"report-employee-email:{employee.Id}"),
                    ProtectLegacy(employee.MobileSnapshot, $"report-employee-mobile:{employee.Id}"));
            var expectedHash = protector.LookupHash(nationalId, "report-employee-national-id-lookup");
            var nationalProtected = employee.NationalId.StartsWith("alpha:v1:", StringComparison.Ordinal);
            var interfaceProtected = employee.InterfaceIdentifier.StartsWith("alpha:v1:", StringComparison.Ordinal);
            if (!nationalProtected || !interfaceProtected || !string.Equals(employee.NationalIdLookupHash, expectedHash, StringComparison.Ordinal))
            {
                employee.SetProtectedIdentifiers(
                    nationalProtected ? employee.NationalId : protector.Protect(nationalId, $"report-employee-national-id:{employee.Id}"),
                    expectedHash,
                    interfaceProtected ? employee.InterfaceIdentifier : protector.Protect(interfaceIdentifier, $"report-employee-interface-id:{employee.Id}"));
                protectedReportEmployeeIds++;
            }
        }

        var reportPayments = await db.ManualReportPayments.ToListAsync(stoppingToken);
        var protectedReportAccounts = 0;
        foreach (var payment in reportPayments)
        {
            if (string.IsNullOrWhiteSpace(payment.EmployerAccount) || payment.EmployerAccount.StartsWith("alpha:v1:", StringComparison.Ordinal)) continue;
            db.Entry(payment).Property(x => x.EmployerAccount).CurrentValue =
                protector.Protect(payment.EmployerAccount, $"report-payment-account:{payment.ReportProductId}");
            protectedReportAccounts++;
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

        var protectedTransmissionResponses = 0;
        foreach (var transmission in transmissions)
        {
            if (string.IsNullOrWhiteSpace(transmission.ResponsePayload) || transmission.ResponsePayload.StartsWith("alpha:v1:", StringComparison.Ordinal)) continue;
            db.Entry(transmission).Property(x => x.ResponsePayload).CurrentValue =
                protector.Protect(transmission.ResponsePayload, $"report-transmission-response:{transmission.Id}");
            protectedTransmissionResponses++;
        }

        var attachments = await db.ManualReportAttachments.ToListAsync(stoppingToken);
        var protectedAttachments = 0;
        foreach (var attachment in attachments)
        {
            var payload = attachment.Content;
            var alreadyProtected = payload.Length >= 4 && payload[0] == (byte)'A' && payload[1] == (byte)'L' && payload[2] == (byte)'P' && payload[3] == 1;
            if (alreadyProtected) continue;
            db.Entry(attachment).Property(x => x.Content).CurrentValue = protector.ProtectBytes(payload,
                $"report-attachment:{attachment.ReportId}:{attachment.ReportProductId}:{attachment.DocumentTypeCode}");
            protectedAttachments++;
        }

        var feedbackItems = await db.EmployerInterfaceFeedback.ToListAsync(stoppingToken);
        var protectedFeedback = 0;
        foreach (var feedback in feedbackItems)
        {
            if (string.IsNullOrWhiteSpace(feedback.RawXml) || feedback.RawXml.StartsWith("alpha:v1:", StringComparison.Ordinal)) continue;
            db.Entry(feedback).Property(x => x.RawXml).CurrentValue =
                protector.Protect(feedback.RawXml, $"employer-interface-feedback:{feedback.PayloadHash}");
            protectedFeedback++;
        }

        await db.SaveChangesAsync(stoppingToken);
        logger.LogInformation("Sensitive-data encryption backfill completed: {ProtectedPeople} people encrypted, {RefreshedHashes} identity hashes refreshed, {ProtectedAccounts} payment accounts encrypted, {RefreshedAccountHashes} account hashes refreshed, {ProtectedReportEmployerContacts} report employer identifiers/contacts protected, {ProtectedReportEmployeeIds} report employee identifiers protected, {ProtectedReportAccounts} report payment account snapshots encrypted, {ProtectedTransmissions} report transmission payloads encrypted, {ProtectedTransmissionResponses} transmission responses encrypted, {ProtectedFeedback} clearinghouse feedback payloads encrypted, {ProtectedAttachments} report attachments encrypted.", protectedPeople, refreshedHashes, protectedAccounts, refreshedAccountHashes, protectedReportEmployerContacts, protectedReportEmployeeIds, protectedReportAccounts, protectedTransmissions, protectedTransmissionResponses, protectedFeedback, protectedAttachments);
    }
}
