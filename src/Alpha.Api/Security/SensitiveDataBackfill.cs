using System.Security.Cryptography;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Security;

/// <summary>
/// One-time compatibility backfill: encrypts legacy plaintext sensitive values and refreshes lookup hashes,
/// keeping the existing formats (<c>alpha:v1:</c>, <c>ALP\x01</c>) and purpose strings.
/// <list type="bullet">
/// <item>Bounded: keyset batches by Id; blob tables are filtered in SQL so already-encrypted payloads are never loaded.</item>
/// <item>Resumable and idempotent: each batch commits on its own; values already in the protected format are left untouched.</item>
/// <item>Fault-isolated: a row that cannot be processed is left unchanged, reported by table and Id (never by value), and the run continues.</item>
/// <item>Coordinated: a PostgreSQL session advisory lock ensures one instance runs it at a time.</item>
/// <item>Completes once: a clean pass records <see cref="CompletionKey"/> in <c>identity.data_fixes</c>; later starts skip.</item>
/// </list>
/// </summary>
public sealed class SensitiveDataBackfill(AlphaDbContext db, IDataProtectionService protector, ILogger<SensitiveDataBackfill> logger)
{
    public const string CompletionKey = "sensitive-data-backfill-v1";
    public const long AdvisoryLockKey = 0x416C706861_0001; // "Alpha" + job 1
    private const string Prefix = "alpha:v1:";

    public sealed record Failure(string Table, Guid Id, string Reason);

    public sealed record Result(bool Skipped, string? SkipReason, IReadOnlyDictionary<string, int> Changed, IReadOnlyList<Failure> Failures,
        int RemainingPlaintext = 0)
    {
        /// <summary>True only when the pass had no failures and the recount found no plaintext left.</summary>
        public bool Completed => !Skipped && Failures.Count == 0 && RemainingPlaintext == 0;
    }

    public async Task<Result> RunAsync(int batchSize, int blobBatchSize, CancellationToken ct)
    {
        batchSize = Math.Clamp(batchSize, 1, 5_000);
        blobBatchSize = Math.Clamp(blobBatchSize, 1, 500);
        var changed = new Dictionary<string, int>();
        var failures = new List<Failure>();
        var remainingPlaintext = 0;

        await db.Database.OpenConnectionAsync(ct);
        try
        {
            if (!await ScalarAsync<bool>($"pg_try_advisory_lock({AdvisoryLockKey})", ct))
                return new Result(true, "another instance holds the backfill lock", changed, failures);
            try
            {
                if (await DataFixMarkers.IsAppliedAsync(db, CompletionKey, ct))
                    return new Result(true, "already completed", changed, failures);

                await PeopleAsync(batchSize, changed, failures, ct);
                await PaymentAccountsAsync(batchSize, changed, failures, ct);
                await ReportEmployerSnapshotsAsync(batchSize, changed, failures, ct);
                await ReportEmployeesAsync(batchSize, changed, failures, ct);
                await ReportPaymentAccountsAsync(batchSize, changed, failures, ct);
                await TransmissionPayloadsAsync(blobBatchSize, changed, failures, ct);
                await TransmissionResponsesAsync(blobBatchSize, changed, failures, ct);
                await ReportAttachmentsAsync(blobBatchSize, changed, failures, ct);
                await FeedbackXmlAsync(blobBatchSize, changed, failures, ct);

                // Completion is claimed only when an independent recount finds no plaintext left in scope,
                // so rows written during the pass or missed by a bug keep the job running on later starts.
                var remaining = failures.Count == 0 ? await RemainingPlaintextAsync(ct) : -1;
                if (remaining == 0)
                    await DataFixMarkers.MarkAppliedAsync(db, CompletionKey, ct);
                else if (remaining > 0)
                    logger.LogError("Sensitive-data backfill found {Remaining} plaintext values after its pass; completion was not recorded.", remaining);
                remainingPlaintext = remaining;
            }
            finally
            {
                await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_unlock({AdvisoryLockKey})", CancellationToken.None);
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }

        return new Result(false, null, changed, failures, remainingPlaintext);
    }

    private Task PeopleAsync(int batch, Dictionary<string, int> changed, List<Failure> failures, CancellationToken ct) =>
        ProcessAsync("employees.people", "TRUE", batch, ids => db.People.Where(x => ids.Contains(x.Id)), x => x.Id, person =>
        {
            if (string.IsNullOrWhiteSpace(person.NationalIdEncrypted)) throw new BackfillException("missing national ID");
            var nationalId = protector.Unprotect(person.NationalIdEncrypted, "person-national-id").Trim();
            if (nationalId.Length == 0) throw new BackfillException("empty national ID");
            var hash = protector.LookupHash(nationalId, "person-national-id-lookup");
            var encrypted = person.NationalIdEncrypted.StartsWith(Prefix, StringComparison.Ordinal)
                ? person.NationalIdEncrypted
                : protector.Protect(nationalId, "person-national-id");
            if (encrypted == person.NationalIdEncrypted && hash == person.NationalIdLookupHash) return false;
            // Keep the identifier type: SetProtectedNationalId would silently turn a passport into an Israeli ID.
            person.SetProtectedIdentifier(person.IdentifierType, encrypted, hash);
            return true;
        }, changed, failures, ct);

    private Task PaymentAccountsAsync(int batch, Dictionary<string, int> changed, List<Failure> failures, CancellationToken ct) =>
        ProcessAsync("employers.employer_payment_accounts", "TRUE", batch, ids => db.EmployerPaymentAccounts.Where(x => ids.Contains(x.Id)), x => x.Id, account =>
        {
            if (string.IsNullOrWhiteSpace(account.AccountNumberEncrypted) || string.IsNullOrWhiteSpace(account.AccountHolderIdEncrypted))
                throw new BackfillException("missing account number or holder ID");
            var number = protector.Unprotect(account.AccountNumberEncrypted, "bank-account-number").Trim();
            var holder = protector.Unprotect(account.AccountHolderIdEncrypted, "bank-account-holder-id").Trim();
            if (number.Length == 0 || holder.Length == 0) throw new BackfillException("empty account number or holder ID");
            var hash = protector.LookupHash(number, "bank-account-number-lookup");
            var numberEncrypted = account.AccountNumberEncrypted.StartsWith(Prefix, StringComparison.Ordinal)
                ? account.AccountNumberEncrypted : protector.Protect(number, "bank-account-number");
            var holderEncrypted = account.AccountHolderIdEncrypted.StartsWith(Prefix, StringComparison.Ordinal)
                ? account.AccountHolderIdEncrypted : protector.Protect(holder, "bank-account-holder-id");
            if (numberEncrypted == account.AccountNumberEncrypted && holderEncrypted == account.AccountHolderIdEncrypted &&
                hash == account.AccountNumberLookupHash) return false;
            account.SetProtectedValues(numberEncrypted, hash, holderEncrypted);
            return true;
        }, changed, failures, ct);

    private Task ReportEmployerSnapshotsAsync(int batch, Dictionary<string, int> changed, List<Failure> failures, CancellationToken ct) =>
        ProcessAsync("reporting.manual_reports",
            string.Join(" OR ", new[] { "EmployerRegistrationNumberSnapshot", "EmployerWithholdingFileNumberSnapshot",
                    "EmployerContactPhoneSnapshot", "EmployerContactEmailSnapshot", "EmployerContactMobileSnapshot" }
                .Select(c => $"(COALESCE(\"{c}\", '') <> '' AND \"{c}\" NOT LIKE '{Prefix}%')")),
            batch, ids => db.ManualReports.Where(x => ids.Contains(x.Id)), x => x.Id, report =>
            {
                report.SetProtectedEmployerSnapshot(
                    ProtectLegacy(report.EmployerRegistrationNumberSnapshot, $"report-employer-registration:{report.Id}"),
                    ProtectLegacy(report.EmployerWithholdingFileNumberSnapshot, $"report-employer-withholding:{report.Id}"),
                    ProtectLegacy(report.EmployerContactPhoneSnapshot, $"report-employer-phone:{report.Id}"),
                    ProtectLegacy(report.EmployerContactEmailSnapshot, $"report-employer-email:{report.Id}"),
                    ProtectLegacy(report.EmployerContactMobileSnapshot, $"report-employer-mobile:{report.Id}"));
                return true;
            }, changed, failures, ct);

    private Task ReportEmployeesAsync(int batch, Dictionary<string, int> changed, List<Failure> failures, CancellationToken ct) =>
        ProcessAsync("reporting.manual_report_employees", "TRUE", batch, ids => db.ManualReportEmployees.Where(x => ids.Contains(x.Id)), x => x.Id, employee =>
        {
            var nationalId = protector.Unprotect(employee.NationalId, $"report-employee-national-id:{employee.Id}").Trim();
            var interfaceIdentifier = protector.Unprotect(employee.InterfaceIdentifier, $"report-employee-interface-id:{employee.Id}").Trim();
            if (nationalId.Length == 0 || interfaceIdentifier.Length == 0) throw new BackfillException("empty identifier");
            var email = protector.Unprotect(employee.EmailSnapshot, $"report-employee-email:{employee.Id}");
            var mobile = protector.Unprotect(employee.MobileSnapshot, $"report-employee-mobile:{employee.Id}");
            var result = false;
            if ((!string.IsNullOrWhiteSpace(email) && !employee.EmailSnapshot.StartsWith(Prefix, StringComparison.Ordinal))
                || (!string.IsNullOrWhiteSpace(mobile) && !employee.MobileSnapshot.StartsWith(Prefix, StringComparison.Ordinal)))
            {
                employee.SetProtectedContactSnapshot(ProtectLegacy(employee.EmailSnapshot, $"report-employee-email:{employee.Id}"),
                    ProtectLegacy(employee.MobileSnapshot, $"report-employee-mobile:{employee.Id}"));
                result = true;
            }

            var hash = protector.LookupHash(nationalId, "report-employee-national-id-lookup");
            var nationalProtected = employee.NationalId.StartsWith(Prefix, StringComparison.Ordinal);
            var interfaceProtected = employee.InterfaceIdentifier.StartsWith(Prefix, StringComparison.Ordinal);
            if (!nationalProtected || !interfaceProtected || !string.Equals(employee.NationalIdLookupHash, hash, StringComparison.Ordinal))
            {
                employee.SetProtectedIdentifiers(
                    nationalProtected ? employee.NationalId : protector.Protect(nationalId, $"report-employee-national-id:{employee.Id}"),
                    hash,
                    interfaceProtected ? employee.InterfaceIdentifier : protector.Protect(interfaceIdentifier, $"report-employee-interface-id:{employee.Id}"));
                result = true;
            }
            return result;
        }, changed, failures, ct);

    private Task ReportPaymentAccountsAsync(int batch, Dictionary<string, int> changed, List<Failure> failures, CancellationToken ct) =>
        ProcessAsync("reporting.manual_report_payments", $"COALESCE(\"EmployerAccount\", '') <> '' AND \"EmployerAccount\" NOT LIKE '{Prefix}%'",
            batch, ids => db.ManualReportPayments.Where(x => ids.Contains(x.Id)), x => x.Id, payment =>
            {
                db.Entry(payment).Property(x => x.EmployerAccount).CurrentValue =
                    protector.Protect(payment.EmployerAccount, $"report-payment-account:{payment.ReportProductId}");
                return true;
            }, changed, failures, ct);

    private Task TransmissionPayloadsAsync(int batch, Dictionary<string, int> changed, List<Failure> failures, CancellationToken ct) =>
        ProcessAsync("reporting.report_transmissions", "length(\"Payload\") > 0 AND substring(\"Payload\" from 1 for 4) <> '\\x414c5001'::bytea",
            batch, ids => db.ReportTransmissions.Where(x => ids.Contains(x.Id)), x => x.Id, transmission =>
            {
                db.Entry(transmission).Property(x => x.Payload).CurrentValue =
                    protector.ProtectBytes(transmission.Payload, $"report-transmission:{transmission.Id}");
                return true;
            }, changed, failures, ct, "reporting.report_transmissions.payload");

    private Task TransmissionResponsesAsync(int batch, Dictionary<string, int> changed, List<Failure> failures, CancellationToken ct) =>
        ProcessAsync("reporting.report_transmissions", $"COALESCE(\"ResponsePayload\", '') <> '' AND \"ResponsePayload\" NOT LIKE '{Prefix}%'",
            batch, ids => db.ReportTransmissions.Where(x => ids.Contains(x.Id)), x => x.Id, transmission =>
            {
                db.Entry(transmission).Property(x => x.ResponsePayload).CurrentValue =
                    protector.Protect(transmission.ResponsePayload!, $"report-transmission-response:{transmission.Id}");
                return true;
            }, changed, failures, ct, "reporting.report_transmissions.response");

    private Task ReportAttachmentsAsync(int batch, Dictionary<string, int> changed, List<Failure> failures, CancellationToken ct) =>
        ProcessAsync("reporting.manual_report_attachments", "length(\"Content\") > 0 AND substring(\"Content\" from 1 for 4) <> '\\x414c5001'::bytea",
            batch, ids => db.ManualReportAttachments.Where(x => ids.Contains(x.Id)), x => x.Id, attachment =>
            {
                db.Entry(attachment).Property(x => x.Content).CurrentValue = protector.ProtectBytes(attachment.Content,
                    $"report-attachment:{attachment.ReportId}:{attachment.ReportProductId}:{attachment.DocumentTypeCode}");
                return true;
            }, changed, failures, ct);

    private Task FeedbackXmlAsync(int batch, Dictionary<string, int> changed, List<Failure> failures, CancellationToken ct) =>
        ProcessAsync("reporting.employer_interface_feedback", $"COALESCE(\"RawXml\", '') <> '' AND \"RawXml\" NOT LIKE '{Prefix}%'",
            batch, ids => db.EmployerInterfaceFeedback.Where(x => ids.Contains(x.Id)), x => x.Id, feedback =>
            {
                db.Entry(feedback).Property(x => x.RawXml).CurrentValue =
                    protector.Protect(feedback.RawXml, $"employer-interface-feedback:{feedback.PayloadHash}");
                return true;
            }, changed, failures, ct);

    /// <summary>
    /// Pages candidate Ids in SQL (keyset on Id, so memory is bounded by the batch), loads and processes that batch,
    /// and commits it. A row whose processing throws is detached (left unchanged) and reported.
    /// </summary>
    private async Task ProcessAsync<TEntity>(string table, string candidateFilter, int batchSize,
        Func<Guid[], IQueryable<TEntity>> load, Func<TEntity, Guid> id, Func<TEntity, bool> process,
        Dictionary<string, int> changed, List<Failure> failures, CancellationToken ct, string? label = null) where TEntity : class
    {
        label ??= table;
        var lastId = Guid.Empty;
        while (true)
        {
            // Table, filter and batch size are constants of this class; only lastId is a parameter.
#pragma warning disable EF1002
            var ids = await db.Database.SqlQueryRaw<Guid>(
                $"SELECT \"Id\" AS \"Value\" FROM {table} WHERE \"Id\" > {{0}} AND ({candidateFilter}) ORDER BY \"Id\" LIMIT {batchSize}",
                lastId).ToArrayAsync(ct);
#pragma warning restore EF1002
            if (ids.Length == 0) return;
            lastId = ids[^1];

            var batchChanged = 0;
            foreach (var entity in await load(ids).ToListAsync(ct))
                if (TryProcess(entity)) batchChanged++;

            try
            {
                await db.SaveChangesAsync(ct);
                if (batchChanged > 0) changed[label] = changed.GetValueOrDefault(label) + batchChanged;
            }
            catch (DbUpdateException)
            {
                // A constraint (for example a unique lookup hash) rejected the batch. Retry row by row so one
                // conflicting row cannot block the rest; the conflicting row is reported and left unchanged.
                db.ChangeTracker.Clear();
                var alreadyReported = failures.Where(f => f.Table == label).Select(f => f.Id).ToHashSet();
                foreach (var rowId in ids)
                {
                    if (alreadyReported.Contains(rowId)) continue;
                    var entity = await load([rowId]).SingleOrDefaultAsync(ct);
                    if (entity is null || !TryProcess(entity)) { db.ChangeTracker.Clear(); continue; }
                    try
                    {
                        await db.SaveChangesAsync(ct);
                        changed[label] = changed.GetValueOrDefault(label) + 1;
                    }
                    catch (DbUpdateException)
                    {
                        Report(rowId, "database constraint violation");
                    }
                    db.ChangeTracker.Clear();
                }
            }

            db.ChangeTracker.Clear();
        }

        bool TryProcess(TEntity entity)
        {
            try
            {
                return process(entity);
            }
            catch (Exception ex) when (ex is BackfillException or CryptographicException or FormatException)
            {
                db.Entry(entity).State = EntityState.Detached;
                Report(id(entity), ex is BackfillException ? ex.Message : ex.GetType().Name);
                return false;
            }
        }

        void Report(Guid rowId, string reason)
        {
            failures.Add(new Failure(label, rowId, reason));
            logger.LogError("Sensitive-data backfill could not process {Table} row {Id}: {Reason}. The row was left unchanged.",
                label, rowId, reason);
        }
    }

    /// <summary>Counts values in every backfill scope that are still not in the protected format.</summary>
    private async Task<int> RemainingPlaintextAsync(CancellationToken ct) => (int)await ScalarAsync<long>($"""
        (SELECT count(*) FROM employees.people WHERE COALESCE("NationalIdEncrypted", '') NOT LIKE '{Prefix}%')
        + (SELECT count(*) FROM employers.employer_payment_accounts WHERE COALESCE("AccountNumberEncrypted", '') NOT LIKE '{Prefix}%'
              OR COALESCE("AccountHolderIdEncrypted", '') NOT LIKE '{Prefix}%')
        + (SELECT count(*) FROM reporting.manual_reports WHERE
              (COALESCE("EmployerRegistrationNumberSnapshot", '') <> '' AND "EmployerRegistrationNumberSnapshot" NOT LIKE '{Prefix}%')
           OR (COALESCE("EmployerWithholdingFileNumberSnapshot", '') <> '' AND "EmployerWithholdingFileNumberSnapshot" NOT LIKE '{Prefix}%')
           OR (COALESCE("EmployerContactPhoneSnapshot", '') <> '' AND "EmployerContactPhoneSnapshot" NOT LIKE '{Prefix}%')
           OR (COALESCE("EmployerContactEmailSnapshot", '') <> '' AND "EmployerContactEmailSnapshot" NOT LIKE '{Prefix}%')
           OR (COALESCE("EmployerContactMobileSnapshot", '') <> '' AND "EmployerContactMobileSnapshot" NOT LIKE '{Prefix}%'))
        + (SELECT count(*) FROM reporting.manual_report_employees WHERE "NationalId" NOT LIKE '{Prefix}%' OR "InterfaceIdentifier" NOT LIKE '{Prefix}%'
              OR (COALESCE("EmailSnapshot", '') <> '' AND "EmailSnapshot" NOT LIKE '{Prefix}%')
              OR (COALESCE("MobileSnapshot", '') <> '' AND "MobileSnapshot" NOT LIKE '{Prefix}%'))
        + (SELECT count(*) FROM reporting.manual_report_payments WHERE COALESCE("EmployerAccount", '') <> '' AND "EmployerAccount" NOT LIKE '{Prefix}%')
        + (SELECT count(*) FROM reporting.report_transmissions WHERE
              (length("Payload") > 0 AND substring("Payload" from 1 for 4) <> '\x414c5001'::bytea)
           OR (COALESCE("ResponsePayload", '') <> '' AND "ResponsePayload" NOT LIKE '{Prefix}%'))
        + (SELECT count(*) FROM reporting.manual_report_attachments WHERE length("Content") > 0 AND substring("Content" from 1 for 4) <> '\x414c5001'::bytea)
        + (SELECT count(*) FROM reporting.employer_interface_feedback WHERE COALESCE("RawXml", '') <> '' AND "RawXml" NOT LIKE '{Prefix}%')
        """, ct);

    private string ProtectLegacy(string value, string purpose) =>
        string.IsNullOrWhiteSpace(value) || value.StartsWith(Prefix, StringComparison.Ordinal) ? value : protector.Protect(value, purpose);

    private async Task<T> ScalarAsync<T>(string expression, CancellationToken ct)
    {
#pragma warning disable EF1002 // constant SQL
        return await db.Database.SqlQueryRaw<T>($"SELECT ({expression}) AS \"Value\"").SingleAsync(ct);
#pragma warning restore EF1002
    }

    private sealed class BackfillException(string message) : Exception(message);
}
