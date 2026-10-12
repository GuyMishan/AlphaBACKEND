using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace Alpha.Api.Tests.Organizations;

public sealed class EmployerTransferHttpIntegrationTests
{
    [Fact]
    public async Task Transfer_preserves_authentication_platform_authorization_http_contract_and_audit_correlation()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new TransferWebApplicationFactory(connectionString);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

            var source = new Organization("Source HTTP", OrganizationType.PayrollOffice);
            var target = new Organization("Target HTTP", OrganizationType.PayrollOffice);
            var employer = new Employer(source.Id, "Moved HTTP", "reg-http", "file-http");
            var tenantAdmin = new User("transfer-tenant-admin", "tenant@test.invalid", "Tenant Admin");
            var platformAdmin = new User("transfer-platform-admin", "platform@test.invalid", "Platform Admin");
            platformAdmin.SetPlatformAdmin(true);
            var now = DateTime.UtcNow;
            var tenantSession = new UserSession(tenantAdmin.Id, now, now.AddHours(1));
            var platformSession = new UserSession(platformAdmin.Id, now, now.AddHours(1));
            var sourceMembership = new OrganizationMembership(tenantAdmin.Id, source.Id,
                OrganizationRole.Admin, EmployerAccessMode.AllEmployers, tenantAdmin.Id);
            var targetMembership = new OrganizationMembership(tenantAdmin.Id, target.Id,
                OrganizationRole.Admin, EmployerAccessMode.AllEmployers, tenantAdmin.Id);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(source, target, employer, new EmployerProfileSettings(employer.Id),
                    tenantAdmin, platformAdmin, tenantSession, platformSession,
                    sourceMembership, targetMembership);
                await db.SaveChangesAsync(ct);
            }

            var path = $"/api/organizations/{source.Id}/employers/{employer.Id}/transfer";
            using var anonymous = await client.PostAsJsonAsync(path,
                new { targetOrganizationId = target.Id }, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var tenantRequest = NewRequest(path, target.Id, tenantAdmin.Id, tenantSession.Id, false,
                Guid.NewGuid().ToString());
            using var tenantResponse = await client.SendAsync(tenantRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, tenantResponse.StatusCode);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                Assert.Equal(source.Id, (await db.Employers.AsNoTracking()
                    .SingleAsync(x => x.Id == employer.Id, ct)).OrganizationId);
            }

            var correlationId = "a8-transfer-http-correlation";
            using var adminRequest = NewRequest(path, target.Id, platformAdmin.Id, platformSession.Id, true, correlationId);
            using var adminResponse = await client.SendAsync(adminRequest, ct);
            Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
            var body = await adminResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(employer.Id, body.GetProperty("employerId").GetGuid());
            Assert.Equal(target.Id, body.GetProperty("organizationId").GetGuid());

            await using var verificationScope = factory.Services.CreateAsyncScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AlphaDbContext>();
            Assert.Equal(target.Id, (await verificationDb.Employers.AsNoTracking()
                .SingleAsync(x => x.Id == employer.Id, ct)).OrganizationId);
            var audit = await verificationDb.AuditEvents.AsNoTracking().SingleAsync(x =>
                x.Action == "employer.transferred" && x.EntityId == employer.Id, ct);
            Assert.Equal(platformAdmin.Id, audit.ActorUserId);
            Assert.Equal(target.Id, audit.OrganizationId);
            Assert.False(string.IsNullOrWhiteSpace(audit.CorrelationId));
        });
    }

    private static HttpRequestMessage NewRequest(string path, Guid targetOrganizationId, Guid userId, Guid sessionId,
        bool platformAdmin, string correlationId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = JsonContent.Create(new { targetOrganizationId })
        };
        request.Headers.Add("X-User-Id", userId.ToString());
        request.Headers.Add("X-Session-Id", sessionId.ToString());
        request.Headers.Add("X-Correlation-ID", correlationId);
        if (platformAdmin) request.Headers.Add("X-Platform-Admin", "true");
        return request;
    }

    private sealed class TransferWebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:AlphaDatabase", connectionString);
            builder.UseSetting("EmployerInterface006:ClearinghouseProvider", "Mock");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AlphaDatabase"] = connectionString,
                    ["Database:ApplyMigrations"] = "false",
                    ["DemoData:Enabled"] = "false",
                    ["Security:DataProtectionKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="
                }));
            builder.ConfigureTestServices(services =>
            {
                foreach (var hostedService in services
                             .Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray())
                    services.Remove(hostedService);
                services.RemoveAll<AlphaDbContext>();
                services.RemoveAll<DbContextOptions<AlphaDbContext>>();
                services.AddDbContext<AlphaDbContext>(options => options.UseNpgsql(connectionString));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "A8TestHeaders";
                        options.DefaultChallengeScheme = "A8TestHeaders";
                        options.DefaultForbidScheme = "A8TestHeaders";
                    })
                    .AddScheme<AuthenticationSchemeOptions, TransferTestAuthenticationHandler>(
                        "A8TestHeaders", null);
            });
        }
    }

    private sealed class TransferTestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User-Id", out var userValue) ||
                !Guid.TryParse(userValue, out var userId) ||
                !Request.Headers.TryGetValue("X-Session-Id", out var sessionValue) ||
                !Guid.TryParse(sessionValue, out var sessionId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId.ToString()),
                new("alpha:session_id", sessionId.ToString())
            };
            if (Request.Headers.TryGetValue("X-Platform-Admin", out var admin) && admin == "true")
                claims.Add(new Claim("alpha:platform_admin", "true"));
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private static async Task InIsolatedDatabase(Func<string, CancellationToken, Task> test)
    {
        var baseConnection = PostgresTestEnvironment.RequireConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_a8_http_" + Guid.NewGuid().ToString("N");
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
            await using (var db = new AlphaDbContext(options))
            {
                await db.Database.EnsureCreatedAsync(ct);
                await ReferenceDataSchemaInitializer.EnsureCreatedAsync(db, ct);
            }
            await test(connection.ConnectionString, ct);
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
