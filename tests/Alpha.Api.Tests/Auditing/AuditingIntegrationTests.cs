using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Auditing;
using Alpha.Modules.Auditing.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests.Auditing;

public sealed class AuditingIntegrationTests
{
    [Fact]
    public async Task Persisted_audit_event_cannot_be_deleted()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var auditEvent = new AuditEvent(Guid.NewGuid(), "test.created", "TestEntity", Guid.NewGuid(),
                null, null, "{}", "test-correlation-id");
            db.AuditEvents.Add(auditEvent);
            await db.SaveChangesAsync(ct);

            db.AuditEvents.Remove(auditEvent);
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(ct));
            Assert.Equal("Audit events are append-only and cannot be changed or deleted.", exception.Message);

            db.ChangeTracker.Clear();
            Assert.True(await db.AuditEvents.AnyAsync(x => x.Id == auditEvent.Id, ct));
        });
    }

    [Fact]
    public async Task Middleware_preserves_correlation_header_and_audit_payload()
    {
        await InIsolatedDatabase(async (db, ct) =>
        {
            var userId = Guid.NewGuid();
            var organizationId = Guid.NewGuid();
            var employerId = Guid.NewGuid();
            var context = new DefaultHttpContext
            {
                TraceIdentifier = "audit-correlation-test"
            };
            context.Request.Method = HttpMethods.Post;
            context.Request.Path = $"/api/organizations/{organizationId}/employers/{employerId}";
            context.Request.RouteValues["organizationId"] = organizationId;
            context.Request.RouteValues["employerId"] = employerId;
            context.Request.Headers.UserAgent = "Alpha-A3-Test";

            var middleware = new SecurityAuditMiddleware(
                nextContext =>
                {
                    nextContext.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                },
                NullLogger<SecurityAuditMiddleware>.Instance);

            await middleware.InvokeAsync(context, new FakeCurrentUser(userId), db);

            Assert.Equal("audit-correlation-test", context.Response.Headers["X-Correlation-ID"]);
            db.ChangeTracker.Clear();
            var auditEvent = await db.AuditEvents.SingleAsync(ct);
            Assert.Equal(userId, auditEvent.ActorUserId);
            Assert.Equal("http.post", auditEvent.Action);
            Assert.Equal("api", auditEvent.EntityType);
            Assert.Equal(employerId, auditEvent.EntityId);
            Assert.Equal(organizationId, auditEvent.OrganizationId);
            Assert.Equal(employerId, auditEvent.EmployerId);
            Assert.Equal("audit-correlation-test", auditEvent.CorrelationId);

            using var payload = JsonDocument.Parse(auditEvent.Data);
            Assert.Equal(HttpMethods.Post, payload.RootElement.GetProperty("method").GetString());
            Assert.Equal(context.Request.Path.Value, payload.RootElement.GetProperty("path").GetString());
            Assert.Equal("success", payload.RootElement.GetProperty("result").GetString());
            Assert.Equal(StatusCodes.Status204NoContent, payload.RootElement.GetProperty("statusCode").GetInt32());
            Assert.Equal("Alpha-A3-Test", payload.RootElement.GetProperty("userAgent").GetString());
        });
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId => userId;
        public bool IsAuthenticated => true;
        public bool IsPlatformAdmin => false;
    }

    private static async Task InIsolatedDatabase(Func<AlphaDbContext, CancellationToken, Task> test)
    {
        var baseConnection = PostgresTestEnvironment.RequireConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_audit_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(baseConnection)
        {
            Database = database,
            Pooling = false
        };

        await using (var admin = new NpgsqlConnection(baseConnection))
        {
            await admin.OpenAsync(ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(ct);
        }

        try
        {
            var options = new DbContextOptionsBuilder<AlphaDbContext>()
                .UseNpgsql(connection.ConnectionString).Options;
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
