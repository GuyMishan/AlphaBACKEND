using Alpha.Api.Services;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class EmployerInterfaceFileSequenceIntegrationTests
{
    [Fact]
    public async Task Daily_sequence_exhaustion_at_9999_fails_closed_without_wrapping_or_reuse()
    {
        var baseConnection = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;

        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_v006_sequence_exhaust_" + Guid.NewGuid().ToString("N");
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
            await ReportingSchemaInitializer.EnsureCreatedAsync(db, ct);

            var settings = Options.Create(new EmployerInterface006Options
            {
                SenderCode = 3,
                SenderIdentifier = "123456789"
            });
            var service = new EmployerInterfaceFileSequenceService(db, settings);
            var first = await service.ReserveAsync(Guid.NewGuid(), ct);

            await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE reporting.employer_interface_file_sequences
                SET "LastSequence" = 9999
                WHERE "SenderIdentifier" = {first.SenderIdentifier}
                """, ct);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ReserveAsync(Guid.NewGuid(), ct));
            Assert.Contains("maximum 9999", ex.Message, StringComparison.Ordinal);
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

    [Fact]
    public async Task Concurrent_reservations_for_same_sender_and_day_are_unique_and_monotonic()
    {
        var baseConnection = Environment.GetEnvironmentVariable("ALPHA_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnection)) return;

        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_v006_sequence_" + Guid.NewGuid().ToString("N");
        var cs = new NpgsqlConnectionStringBuilder(baseConnection) { Database = database, Pooling = false };

        await using (var admin = new NpgsqlConnection(baseConnection))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var options = new DbContextOptionsBuilder<AlphaDbContext>()
                .UseNpgsql(cs.ConnectionString).Options;

            await using (var setup = new AlphaDbContext(options))
            {
                await setup.Database.EnsureCreatedAsync(ct);
                await ReportingSchemaInitializer.EnsureCreatedAsync(setup, ct);
            }

            var settings = Options.Create(new EmployerInterface006Options
            {
                SenderCode = 3,
                SenderIdentifier = "123456789"
            });

            var tasks = Enumerable.Range(0, 20).Select(async _ =>
            {
                await using var db = new AlphaDbContext(options);
                var service = new EmployerInterfaceFileSequenceService(db, settings);
                return await service.ReserveAsync(Guid.NewGuid(), ct);
            });

            var reservations = await Task.WhenAll(tasks);
            var sequences = reservations.Select(x => x.Sequence).Order().ToArray();

            Assert.Equal(Enumerable.Range(1, 20).ToArray(), sequences);
            Assert.Single(reservations.Select(x => x.SenderIdentifier).Distinct());
            Assert.Single(reservations.Select(x => DateOnly.FromDateTime(x.PreparedAt.Date)).Distinct());
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
