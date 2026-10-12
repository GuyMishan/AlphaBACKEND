using System.Text.RegularExpressions;
using Alpha.Api.Security;
using Alpha.Api.Tests.TestInfrastructure;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;
using static Alpha.Api.Tests.TestInfrastructure.TestData;

namespace Alpha.Api.Tests.Integration;

/// <summary>
/// <c>tools/production-readonly/data-checks.sql</c> is the script an operator runs against production. These tests run
/// it locally, prove it writes nothing and keep its plaintext counts identical in scope to the backfill's own recount.
/// </summary>
public sealed class ProductionReadOnlyChecksTests
{
    private static readonly string Script = RepositorySource.Read("tools/production-readonly/data-checks.sql");
    private static readonly string Grants = RepositorySource.Read("tools/production-readonly/grants.sql");
    private const string CheckRole = "alpha_readonly_checks";

    [Fact]
    public void Script_contains_no_statement_that_can_write()
    {
        var code = Regex.Replace(Script, "--[^\n]*", "");
        Assert.DoesNotMatch(new Regex(@"\b(INSERT|UPDATE|DELETE|MERGE|TRUNCATE|CREATE|ALTER|DROP|GRANT|REVOKE|COPY|CALL|DO|VACUUM|REINDEX|CLUSTER|COMMIT|LOCK)\b",
            RegexOptions.IgnoreCase), code);
        Assert.Matches(@"(?m)^SET default_transaction_read_only = on;\s*$", code);
        Assert.Matches(@"(?m)^BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;\s*$", code);
        Assert.EndsWith("ROLLBACK;", code.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void Plaintext_counts_cover_exactly_the_backfill_recount()
    {
        var backfill = RepositorySource.Read("src/Alpha.Api/Security/SensitiveDataBackfill.cs");
        var recount = backfill[backfill.IndexOf("RemainingPlaintextAsync(CancellationToken", StringComparison.Ordinal)..];
        recount = recount[..recount.IndexOf("\"\"\", ct);", StringComparison.Ordinal)];

        Assert.Equal(Scopes(recount), Scopes(PlaintextCounts()));
    }

    [Fact]
    public async Task Script_runs_read_only_with_only_the_documented_grants_and_reports_what_the_backfill_encrypts()
    {
        await RunAsync(async (factory, _, ct) =>
        {
            // The seeded person carries a legacy (non-prefixed) identifier, i.e. plaintext for the backfill.
            await SeedTenantAsync(factory, "RO");
            var ownerConnection = "";
            await factory.WithDbAsync(db => Task.FromResult(ownerConnection = db.Database.GetConnectionString()!));

            // The check login exists only on the local test server, for the duration of this test.
            var password = Guid.NewGuid().ToString("N");
            await ExecuteAsync(ownerConnection, $"""
                DO $r$ BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{CheckRole}') THEN DROP OWNED BY {CheckRole}; DROP ROLE {CheckRole}; END IF;
                END $r$;
                CREATE ROLE {CheckRole} LOGIN PASSWORD '{password}' CONNECTION LIMIT 1;
                {Grants}
                """, ct);
            var connectionString = new NpgsqlConnectionStringBuilder(ownerConnection) { Username = CheckRole, Password = password, Pooling = false }.ConnectionString;
            try
            {
                var before = await RunScriptAsync(connectionString, ct);
                Assert.Equal("on", before.ReadOnly);
                Assert.Equal(8, before.Counts.Count);
                Assert.True(before.Counts["employees.people"] > 0);

                await using (var scope = factory.Services.CreateAsyncScope())
                    Assert.True((await scope.ServiceProvider.GetRequiredService<SensitiveDataBackfill>().RunAsync(50, 5, ct)).Completed);

                var after = await RunScriptAsync(connectionString, ct);
                Assert.All(after.Counts, c => Assert.Equal(0, c.Value));
                Assert.Contains(SensitiveDataBackfill.CompletionKey, after.Markers);

                // Nothing beyond the counted columns is readable, and nothing is writable.
                var otherColumn = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsync(connectionString, "SELECT \"FirstName\" FROM employees.people", ct));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, otherColumn.SqlState);
                var otherTable = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsync(connectionString, "SELECT count(*) FROM identity.users", ct));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, otherTable.SqlState);
                var write = await Assert.ThrowsAsync<PostgresException>(() =>
                    ExecuteAsync(connectionString, "INSERT INTO identity.data_fixes (\"Key\", \"AppliedAt\") VALUES ('x', now())", ct));
                Assert.Contains(write.SqlState, new[] { PostgresErrorCodes.ReadOnlySqlTransaction, PostgresErrorCodes.InsufficientPrivilege });
            }
            finally
            {
                NpgsqlConnection.ClearAllPools();
                await ExecuteAsync(ownerConnection, $"DROP OWNED BY {CheckRole}; DROP ROLE {CheckRole};", CancellationToken.None);
            }
        });
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task<(string ReadOnly, List<string> Markers, Dictionary<string, long> Counts)> RunScriptAsync(
        string connectionString, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(Script, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);

        await reader.ReadAsync(ct);
        var readOnly = reader.GetString(0);
        await reader.NextResultAsync(ct);
        var markers = new List<string>();
        while (await reader.ReadAsync(ct)) markers.Add(reader.GetString(0));
        await reader.NextResultAsync(ct);
        var counts = new Dictionary<string, long>();
        while (await reader.ReadAsync(ct)) counts[reader.GetString(0)] = reader.GetInt64(1);
        return (readOnly, markers, counts);
    }

    private static string PlaintextCounts()
    {
        var start = Script.IndexOf("-- BEGIN plaintext-counts", StringComparison.Ordinal);
        return Script[start..Script.IndexOf("-- END plaintext-counts", StringComparison.Ordinal)];
    }

    /// <summary>Each scope as "table: sorted columns", from the FROM clause and the quoted columns that follow it.</summary>
    private static string[] Scopes(string sql) =>
        Regex.Matches(sql, @"FROM (?<table>[a-z_]+\.[a-z_]+)(?<body>.*?)(?=FROM [a-z_]+\.[a-z_]+|$)", RegexOptions.Singleline)
            .Select(m => m.Groups["table"].Value + ": " + string.Join(",", Regex.Matches(m.Groups["body"].Value, "\"(?<column>[A-Za-z]+)\"")
                .Select(c => c.Groups["column"].Value).Distinct().Order(StringComparer.Ordinal)))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
