using Alpha.Api.Security;
using Alpha.Domain.Employees;
using Alpha.Domain.Reporting;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;
using static Alpha.Api.Tests.TestInfrastructure.TestData;

namespace Alpha.Api.Tests.Security;

/// <summary>Bounded, resumable, fault-isolated encryption backfill on isolated databases with synthetic data.</summary>
public sealed class SensitiveDataBackfillTests
{
    [Fact]
    public async Task Legacy_plaintext_is_encrypted_with_the_existing_purposes_and_stays_decryptable()
    {
        await RunAsync(async (factory, _, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "B");
            var legacy = NewPersonWithStoredIdentifier(tenant.Organization.Id, "123456782", "stale-hash", PersonIdentifierType.Passport);
            var report = new ManualReport(tenant.Organization.Id, tenant.Employer.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 5));
            report.SetProtectedEmployerSnapshot("51234567", "912345678", "035555555", "employer@test.invalid", "0501234567");
            var employee = new ManualReportEmployee(report.Id, tenant.Organization.Id, tenant.Employer.Id, tenant.Employment.Id,
                tenant.Employment.PersonId, "123456782", "First", "Last", "E-1", 10_000m);
            var attachment = new ManualReportAttachment(report.Id, null, 3, "a.pdf", "application/pdf", "%PDF legacy"u8.ToArray());
            await factory.WithDbAsync(async db => { db.AddRange(legacy, report, employee, attachment); await db.SaveChangesAsync(ct); });

            var result = await RunBackfillAsync(factory, ct);

            Assert.True(result.Completed);
            await factory.WithDbAsync(async db =>
            {
                var protector = Protector(factory);
                var person = await db.People.AsNoTracking().SingleAsync(x => x.Id == legacy.Id, ct);
                Assert.StartsWith("alpha:v1:", person.NationalIdEncrypted);
                Assert.Equal("123456782", protector.Unprotect(person.NationalIdEncrypted!, "person-national-id"));
                Assert.Equal(protector.LookupHash("123456782", "person-national-id-lookup"), person.NationalIdLookupHash);
                Assert.Equal(PersonIdentifierType.Passport, person.IdentifierType);

                var storedReport = await db.ManualReports.AsNoTracking().SingleAsync(x => x.Id == report.Id, ct);
                Assert.Equal("employer@test.invalid", protector.Unprotect(storedReport.EmployerContactEmailSnapshot, $"report-employer-email:{report.Id}"));

                var storedEmployee = await db.ManualReportEmployees.AsNoTracking().SingleAsync(x => x.Id == employee.Id, ct);
                Assert.Equal("123456782", protector.Unprotect(storedEmployee.NationalId, $"report-employee-national-id:{employee.Id}"));

                var storedAttachment = await db.ManualReportAttachments.AsNoTracking().SingleAsync(x => x.Id == attachment.Id, ct);
                Assert.Equal("%PDF legacy"u8.ToArray(), protector.UnprotectBytes(storedAttachment.Content, $"report-attachment:{report.Id}::3"));
                Assert.True(await db.Database.SqlQueryRaw<bool>(
                    $"SELECT EXISTS (SELECT 1 FROM identity.data_fixes WHERE \"Key\" = '{SensitiveDataBackfill.CompletionKey}') AS \"Value\"").SingleAsync(ct));
            });
        });
    }

    [Fact]
    public async Task Already_encrypted_values_are_never_rewritten()
    {
        await RunAsync(async (factory, _, ct) =>
        {
            var organization = new Alpha.Domain.Organizations.Organization("Encrypted Tenant", Alpha.Domain.Organizations.OrganizationType.RegularOrganization);
            var protector = Protector(factory);
            var person = NewPersonWithStoredIdentifier(organization.Id, protector.Protect("123456782", "person-national-id"),
                protector.LookupHash("123456782", "person-national-id-lookup"), PersonIdentifierType.IsraeliId);
            await factory.WithDbAsync(async db => { db.AddRange(organization, person); await db.SaveChangesAsync(ct); });
            var before = await SnapshotAsync(factory, person.Id, ct);

            var result = await RunBackfillAsync(factory, ct);

            Assert.True(result.Completed);
            Assert.False(result.Changed.ContainsKey("employees.people"));
            Assert.Equal(before, await SnapshotAsync(factory, person.Id, ct));
        });
    }

    [Fact]
    public async Task A_malformed_row_is_reported_left_unchanged_and_retried_without_blocking_other_rows()
    {
        await RunAsync(async (factory, _, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "M");
            var malformed = NewPersonWithStoredIdentifier(tenant.Organization.Id, "alpha:v1:not-base64:x:y", "h", PersonIdentifierType.IsraeliId);
            var good = NewPersonWithStoredIdentifier(tenant.Organization.Id, "123456782", "stale", PersonIdentifierType.IsraeliId);
            await factory.WithDbAsync(async db => { db.AddRange(malformed, good); await db.SaveChangesAsync(ct); });

            var first = await RunBackfillAsync(factory, ct);
            var failure = Assert.Single(first.Failures);
            Assert.Equal(("employees.people", malformed.Id), (failure.Table, failure.Id));
            Assert.False(first.Completed);
            await factory.WithDbAsync(async db =>
            {
                Assert.Equal("alpha:v1:not-base64:x:y", (await db.People.AsNoTracking().SingleAsync(x => x.Id == malformed.Id, ct)).NationalIdEncrypted);
                Assert.StartsWith("alpha:v1:", (await db.People.AsNoTracking().SingleAsync(x => x.Id == good.Id, ct)).NationalIdEncrypted);
                Assert.False(await CompletionRecordedAsync(db, ct));
            });

            // Not completed, so the next start retries and still reports the row.
            Assert.Single((await RunBackfillAsync(factory, ct)).Failures);

            // After the row is repaired the run completes and is not repeated.
            var protector = Protector(factory);
            await factory.WithDbAsync(async db =>
            {
                var row = await db.People.SingleAsync(x => x.Id == malformed.Id, ct);
                row.SetProtectedIdentifier(row.IdentifierType, protector.Protect("000000018", "person-national-id"), "stale");
                await db.SaveChangesAsync(ct);
            });
            Assert.True((await RunBackfillAsync(factory, ct)).Completed);
            var again = await RunBackfillAsync(factory, ct);
            Assert.True(again.Skipped);
            Assert.Equal("already completed", again.SkipReason);
        });
    }

    [Fact]
    public async Task A_constraint_conflict_isolates_only_the_conflicting_row()
    {
        await RunAsync(async (factory, _, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "U");
            // Two legacy rows whose recomputed lookup hashes collide on the unique (organization, type, hash) index.
            var first = NewPersonWithStoredIdentifier(tenant.Organization.Id, "123456782", "stale-1", PersonIdentifierType.IsraeliId);
            var second = NewPersonWithStoredIdentifier(tenant.Organization.Id, "123456782", "stale-2", PersonIdentifierType.IsraeliId);
            var unrelated = NewPersonWithStoredIdentifier(tenant.Organization.Id, "000000018", "stale-3", PersonIdentifierType.IsraeliId);
            await factory.WithDbAsync(async db => { db.AddRange(first, second, unrelated); await db.SaveChangesAsync(ct); });

            var result = await RunBackfillAsync(factory, ct);

            var failure = Assert.Single(result.Failures);
            Assert.Equal("database constraint violation", failure.Reason);
            Assert.Contains(failure.Id, new[] { first.Id, second.Id });
            await factory.WithDbAsync(async db =>
            {
                Assert.StartsWith("alpha:v1:", (await db.People.AsNoTracking().SingleAsync(x => x.Id == unrelated.Id, ct)).NationalIdEncrypted);
                Assert.Equal("123456782", (await db.People.AsNoTracking().SingleAsync(x => x.Id == failure.Id, ct)).NationalIdEncrypted);
                Assert.False(await CompletionRecordedAsync(db, ct));
            });
        });
    }

    [Fact]
    public async Task Completion_is_not_recorded_while_plaintext_remains_that_the_pass_did_not_see()
    {
        await RunAsync(async (factory, _, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "R");
            await factory.WithDbAsync(async db =>
            {
                // Test-database-only trigger: while the pass updates people, a writer inserts a plaintext row
                // whose Id sorts before every other, i.e. behind the keyset cursor.
                await db.Database.ExecuteSqlRawAsync("""
                    CREATE OR REPLACE FUNCTION public.alpha_test_concurrent_plaintext() RETURNS trigger LANGUAGE plpgsql AS $f$
                    DECLARE late employees.people;
                    BEGIN
                        late := NEW;
                        late."Id" := '00000000-0000-0000-0000-000000000001';
                        late."NationalIdEncrypted" := '000000018';
                        late."NationalIdLookupHash" := 'late-plaintext';
                        INSERT INTO employees.people SELECT (late).* ON CONFLICT DO NOTHING;
                        RETURN NEW;
                    END $f$;
                    CREATE TRIGGER alpha_test_concurrent_plaintext AFTER UPDATE ON employees.people
                    FOR EACH ROW WHEN (NEW."Id" <> '00000000-0000-0000-0000-000000000001')
                    EXECUTE FUNCTION public.alpha_test_concurrent_plaintext();
                    """, ct);
            });

            var result = await RunBackfillAsync(factory, ct);

            Assert.Empty(result.Failures);
            Assert.Equal(1, result.RemainingPlaintext);
            Assert.False(result.Completed);
            await factory.WithDbAsync(async db => Assert.False(await CompletionRecordedAsync(db, ct)));

            // The next start picks the row up and only then records completion.
            await factory.WithDbAsync(db => db.Database.ExecuteSqlRawAsync(
                "DROP TRIGGER alpha_test_concurrent_plaintext ON employees.people", ct));
            var next = await RunBackfillAsync(factory, ct);
            Assert.True(next.Completed);
            await factory.WithDbAsync(async db => Assert.True(await CompletionRecordedAsync(db, ct)));
        });
    }

    [Fact]
    public async Task Only_one_instance_runs_at_a_time()
    {
        await RunAsync(async (factory, _, ct) =>
        {
            string connectionString = null!;
            await factory.WithDbAsync(db => { connectionString = db.Database.GetConnectionString()!; return Task.CompletedTask; });
            await using var other = new NpgsqlConnection(connectionString);
            await other.OpenAsync(ct);
            await using (var hold = new NpgsqlCommand($"SELECT pg_advisory_lock({SensitiveDataBackfill.AdvisoryLockKey})", other))
                await hold.ExecuteNonQueryAsync(ct);

            var result = await RunBackfillAsync(factory, ct);
            Assert.True(result.Skipped);
            Assert.Equal("another instance holds the backfill lock", result.SkipReason);
        });
    }

    [Fact]
    public async Task Large_tables_are_processed_in_bounded_batches()
    {
        await RunAsync(async (factory, _, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "L");
            var report = new ManualReport(tenant.Organization.Id, tenant.Employer.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 2, 5));
            await factory.WithDbAsync(async db =>
            {
                db.Add(report);
                for (var i = 0; i < 1_050; i++)
                    db.Add(NewPersonWithStoredIdentifier(tenant.Organization.Id, $"P{i:D6}", $"stale-{i}", PersonIdentifierType.Passport));
                for (var i = 0; i < 45; i++)
                    db.Add(new ManualReportAttachment(report.Id, null, 3, $"a{i}.pdf", "application/pdf", new byte[64 * 1024]));
                await db.SaveChangesAsync(ct);
            });

            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
            var result = await scope.ServiceProvider.GetRequiredService<SensitiveDataBackfill>().RunAsync(100, 7, ct);

            Assert.True(result.Completed);
            Assert.Equal(1_051, result.Changed["employees.people"]); // includes the seeded tenant's person
            Assert.Equal(45, result.Changed["reporting.manual_report_attachments"]);
            Assert.Empty(db.ChangeTracker.Entries());
            Assert.Equal(0, await db.People.CountAsync(x => !x.NationalIdEncrypted!.StartsWith("alpha:v1:"), ct));
        });
    }

    private static async Task<SensitiveDataBackfill.Result> RunBackfillAsync(Factory factory, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SensitiveDataBackfill>().RunAsync(50, 5, ct);
    }

    private static IDataProtectionService Protector(Factory factory) => factory.Services.GetRequiredService<IDataProtectionService>();

    private static Person NewPersonWithStoredIdentifier(Guid organizationId, string stored, string hash, PersonIdentifierType type)
    {
        var person = new Person(organizationId, string.Empty, "Synthetic", "Person");
        person.SetProtectedIdentifier(type, stored, hash);
        return person;
    }

    private static async Task<string> SnapshotAsync(Factory factory, Guid personId, CancellationToken ct)
    {
        var value = string.Empty;
        await factory.WithDbAsync(async db =>
        {
            var person = await db.People.AsNoTracking().SingleAsync(x => x.Id == personId, ct);
            value = $"{person.NationalIdEncrypted}|{person.NationalIdLookupHash}|{person.UpdatedAt:O}";
        });
        return value;
    }

    private static Task<bool> CompletionRecordedAsync(AlphaDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<bool>(
            $"SELECT EXISTS (SELECT 1 FROM identity.data_fixes WHERE \"Key\" = '{SensitiveDataBackfill.CompletionKey}') AS \"Value\"").SingleAsync(ct);
}
