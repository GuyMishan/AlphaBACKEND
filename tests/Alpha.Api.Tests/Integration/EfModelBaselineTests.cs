using System.Security.Cryptography;
using System.Text;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Alpha.Api.Tests.Integration;

/// <summary>
/// The EF model is a production-compatibility surface: the DDL EF would generate must not change without a
/// reviewed schema decision (docs/database.md). No database connection is opened.
/// </summary>
public sealed class EfModelBaselineTests
{
    // SHA-256 of GenerateCreateScript() with LF line endings. Verified identical to origin/main dc5c609; unchanged up to 90c7ba6.
    private const string ModelBaseline = "1963863a9fd373963e81d0d4cfe8854e801f207796902735fb240eb68c7c28f8";

    [Fact]
    public void Ef_model_matches_the_reviewed_baseline()
    {
        var options = new DbContextOptionsBuilder<AlphaDbContext>().UseNpgsql("Host=localhost;Database=model_only").Options;
        using var db = new AlphaDbContext(options);
        var script = db.Database.GenerateCreateScript().ReplaceLineEndings("\n");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script))).ToLowerInvariant();
        Assert.Equal(ModelBaseline, hash);
    }

    [Theory]
    [InlineData(EntityState.Modified)]
    [InlineData(EntityState.Deleted)]
    public async Task Audit_events_are_append_only_on_every_save_overload(EntityState state)
    {
        // The guard runs before any connection is opened, so no database is needed.
        var options = new DbContextOptionsBuilder<AlphaDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=model_only").Options;
        Func<AlphaDbContext, Task>[] saves =
        [
            db => Task.FromResult(db.SaveChanges()),
            db => Task.FromResult(db.SaveChanges(acceptAllChangesOnSuccess: false)),
            db => db.SaveChangesAsync(),
            db => db.SaveChangesAsync(acceptAllChangesOnSuccess: false)
        ];
        foreach (var save in saves)
        {
            await using var db = new AlphaDbContext(options);
            var audit = new Alpha.Domain.Auditing.AuditEvent(Guid.NewGuid(), "test", "test", Guid.NewGuid(), null, null, "{}", "c");
            db.Attach(audit).State = state;
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => save(db));
            Assert.Contains("append-only", error.Message, StringComparison.Ordinal);
        }
    }
}
