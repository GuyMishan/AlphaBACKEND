using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class ReportingDatabaseBoundaryIntegrationTests
{
    [Fact]
    public async Task Duplicate_transmission_attempt_number_for_same_report_is_rejected_by_postgres()
    {
        await WithDatabase(async (db, ct) =>
        {
            var organization = new Organization("V006 DB Boundary", OrganizationType.PayrollOffice);
            var employer = new Employer(organization.Id, "Employer", "123456789", "987654321",
                "Test", "Contact", "031234567", "employer@example.test", "0501234567");
            var report = new ManualReport(organization.Id, employer.Id,
                new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 9));

            db.AddRange(organization, employer, report);
            await db.SaveChangesAsync(ct);
            await ReportTransmissionSchemaInitializer.EnsureUpdatedAsync(db);

            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO reporting.report_transmissions
                    ("Id","ReportId","OrganizationId","EmployerId","Provider","AttemptNumber","Status",
                     "ExternalId","PayloadHash","PayloadFileName","Payload","AttachmentManifestJson",
                     "ResponsePayload","ErrorMessage","CreatedAt","UpdatedAt")
                VALUES
                    ({Guid.NewGuid()},{report.Id},{organization.Id},{employer.Id},{"provider-a"},1,{"Pending"},
                     {""},{"hash-a"},{"A.DAT"},{new byte[] { 1 }},{"[]"},{""},{""},{now},{now})
                """, ct);

            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO reporting.report_transmissions
                        ("Id","ReportId","OrganizationId","EmployerId","Provider","AttemptNumber","Status",
                         "ExternalId","PayloadHash","PayloadFileName","Payload","AttachmentManifestJson",
                         "ResponsePayload","ErrorMessage","CreatedAt","UpdatedAt")
                    VALUES
                        ({Guid.NewGuid()},{report.Id},{organization.Id},{employer.Id},{"provider-b"},1,{"Pending"},
                         {""},{"hash-b"},{"B.DAT"},{new byte[] { 2 }},{"[]"},{""},{""},{now},{now})
                    """, ct));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, ex.SqlState);
        });
    }

    [Fact]
    public async Task Duplicate_feedback_payload_hash_for_same_employer_is_rejected_but_same_hash_for_other_employer_is_allowed()
    {
        await WithDatabase(async (db, ct) =>
        {
            var organization = new Organization("V006 Feedback Boundary", OrganizationType.PayrollOffice);
            var employerA = new Employer(organization.Id, "Employer A", "123456789", "987654321",
                "Test", "A", "031234567", "a@example.test", "0501234567");
            var employerB = new Employer(organization.Id, "Employer B", "223456789", "887654321",
                "Test", "B", "031234568", "b@example.test", "0501234568");

            db.AddRange(organization, employerA, employerB);
            await db.SaveChangesAsync(ct);
            await EmployerInterfaceFeedbackSchemaInitializer.EnsureCreatedAsync(db);

            var now = DateTimeOffset.UtcNow;
            const string hash = "same-payload-hash";
            await InsertFeedback(db, organization.Id, employerA.Id, hash, now, ct);

            var duplicate = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertFeedback(db, organization.Id, employerA.Id, hash, now, ct));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);

            await InsertFeedback(db, organization.Id, employerB.Id, hash, now, ct);
        });
    }

    private static Task<int> InsertFeedback(
        AlphaDbContext db, Guid organizationId, Guid employerId, string hash, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO reporting.employer_interface_feedback
                ("Id","OrganizationId","EmployerId","ReportId","TransmissionId","DocumentType","InterfaceVersion",
                 "SourceFileName","InterfaceFileNumber","PayloadHash","RawXml","ReceivedAt","CreatedAt","UpdatedAt")
            VALUES
                ({Guid.NewGuid()},{organizationId},{employerId},NULL,NULL,{"SummaryFeedback"},{"006"},
                 {"feedback.xml"},{""},{hash},{"encrypted"},{now},{now},{now})
            """, ct);

    private static async Task WithDatabase(Func<AlphaDbContext, CancellationToken, Task> test)
    {
        var baseConnection = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;

        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_reporting_boundary_" + Guid.NewGuid().ToString("N");
        var cs = new NpgsqlConnectionStringBuilder(baseConnection) { Database = database, Pooling = false };

        await using (var admin = new NpgsqlConnection(baseConnection))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var options = new DbContextOptionsBuilder<AlphaDbContext>().UseNpgsql(cs.ConnectionString).Options;
            await using var db = new AlphaDbContext(options);
            await db.Database.EnsureCreatedAsync(ct);
            await test(db, ct);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(baseConnection);
            await admin.OpenAsync(CancellationToken.None);
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
