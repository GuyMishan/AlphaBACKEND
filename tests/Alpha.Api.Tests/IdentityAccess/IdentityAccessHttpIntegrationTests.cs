using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Identity;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Modules.ReferenceData.Application;
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

namespace Alpha.Api.Tests.IdentityAccess;

public sealed class IdentityAccessHttpIntegrationTests
{
    private const string SigningKey = "alpha-a6-local-test-signing-key-0123456789abcdef";

    [Fact]
    public async Task Access_endpoints_preserve_authentication_authorization_tenant_and_session_guards()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new IdentityWebApplicationFactory(connectionString, useTestAuthentication: true);
            using var client = CreateClient(factory);

            var admin = new User("a6-admin", "a6-admin@test.invalid", "A6 Admin");
            var viewer = new User("a6-viewer", "a6-viewer@test.invalid", "A6 Viewer");
            var otherAdmin = new User("a6-other", "a6-other@test.invalid", "A6 Other Admin");
            var expiredUser = new User("a6-expired", "a6-expired@test.invalid", "A6 Expired");
            var revokedUser = new User("a6-revoked", "a6-revoked@test.invalid", "A6 Revoked");
            var organization = new Organization("A6 Organization", OrganizationType.RegularOrganization);
            var otherOrganization = new Organization("A6 Other Organization", OrganizationType.RegularOrganization);
            var adminMembership = new OrganizationMembership(admin.Id, organization.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, admin.Id);
            var viewerMembership = new OrganizationMembership(viewer.Id, organization.Id, OrganizationRole.Viewer,
                EmployerAccessMode.AllEmployers, admin.Id);
            var otherMembership = new OrganizationMembership(otherAdmin.Id, otherOrganization.Id,
                OrganizationRole.Admin, EmployerAccessMode.AllEmployers, otherAdmin.Id);
            var now = DateTime.UtcNow;
            var adminSession = new UserSession(admin.Id, now, now.AddHours(1));
            var viewerSession = new UserSession(viewer.Id, now, now.AddHours(1));
            var otherSession = new UserSession(otherAdmin.Id, now, now.AddHours(1));
            var expiredSession = new UserSession(expiredUser.Id, now.AddHours(-2), now.AddMinutes(-1));
            var revokedSession = new UserSession(revokedUser.Id, now, now.AddHours(1));
            revokedSession.Revoke(now);

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(admin, viewer, otherAdmin, expiredUser, revokedUser, organization, otherOrganization,
                    adminMembership, viewerMembership, otherMembership, adminSession, viewerSession, otherSession,
                    expiredSession, revokedSession);
                await db.SaveChangesAsync(ct);
            }

            using var anonymous = await client.GetAsync(
                $"/api/organizations/{organization.Id}/access/users", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var missingSessionRequest = NewRequest(HttpMethod.Get, "/api/auth/session", admin.Id, null);
            using var missingSession = await client.SendAsync(missingSessionRequest, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, missingSession.StatusCode);

            using var expiredRequest = NewRequest(HttpMethod.Get, "/api/auth/session", expiredUser.Id, expiredSession.Id);
            using var expired = await client.SendAsync(expiredRequest, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
            Assert.Equal("true", Assert.Single(expired.Headers.GetValues("X-Alpha-Session-Expired")));

            using var revokedRequest = NewRequest(HttpMethod.Get, "/api/auth/session", revokedUser.Id, revokedSession.Id);
            using var revoked = await client.SendAsync(revokedRequest, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
            Assert.Equal("true", Assert.Single(revoked.Headers.GetValues("X-Alpha-Session-Expired")));

            using var viewerRequest = NewRequest(HttpMethod.Get,
                $"/api/organizations/{organization.Id}/access/users", viewer.Id, viewerSession.Id);
            using var viewerResponse = await client.SendAsync(viewerRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, viewerResponse.StatusCode);

            using var crossTenantRequest = NewRequest(HttpMethod.Get,
                $"/api/organizations/{organization.Id}/access/users", otherAdmin.Id, otherSession.Id);
            using var crossTenant = await client.SendAsync(crossTenantRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);

            using var adminRequest = NewRequest(HttpMethod.Get,
                $"/api/organizations/{organization.Id}/access/users", admin.Id, adminSession.Id);
            using var adminResponse = await client.SendAsync(adminRequest, ct);
            Assert.Equal(HttpStatusCode.OK, adminResponse.StatusCode);
        });
    }

    [Fact]
    public async Task Organization_admin_can_switch_user_to_employer_but_cannot_grant_platform_admin()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new IdentityWebApplicationFactory(connectionString, useTestAuthentication: true);
            using var client = CreateClient(factory);
            var admin = new User("access-type-admin", "access-type-admin@test.invalid", "Access Admin");
            var member = new User("access-type-member", "access-type-member@test.invalid", "Access Member");
            var org = new Organization("Access Type Organization", OrganizationType.RegularOrganization);
            var employer = new Employer(org.Id, "Access Test Employer", "111111111", "999999999");
            var adminMembership = new OrganizationMembership(admin.Id, org.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, admin.Id);
            var memberMembership = new OrganizationMembership(member.Id, org.Id, OrganizationRole.Viewer,
                EmployerAccessMode.AllEmployers, admin.Id);
            var now = DateTime.UtcNow;
            var session = new UserSession(admin.Id, now, now.AddHours(1));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(admin, member, org, employer, adminMembership, memberMembership, session);
                await db.SaveChangesAsync(ct);
            }

            var route = $"/api/organizations/{org.Id}/access/users/{member.Id}/type";
            using (var escalation = NewRequest(HttpMethod.Put, route, admin.Id, session.Id))
            {
                escalation.Content = JsonContent.Create(new
                {
                    type = "platform", role = 4, employerAccessMode = 2,
                    canCreateEmployer = false, canEditEmployer = false,
                    canCreateEmployee = false, canEditEmployee = false,
                    canCreateReport = false, canTransmitReport = false,
                    employers = Array.Empty<object>()
                });
                using var denied = await client.SendAsync(escalation, ct);
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            }

            using (var change = NewRequest(HttpMethod.Put, route, admin.Id, session.Id))
            {
                change.Content = JsonContent.Create(new
                {
                    type = "employer", role = 4, employerAccessMode = 2,
                    canCreateEmployer = false, canEditEmployer = false,
                    canCreateEmployee = false, canEditEmployee = false,
                    canCreateReport = false, canTransmitReport = false,
                    employers = new[] { new { employerId = employer.Id, role = 2 } }
                });
                using var response = await client.SendAsync(change, ct);
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            await using var verify = factory.Services.CreateAsyncScope();
            var data = verify.ServiceProvider.GetRequiredService<AlphaDbContext>();
            Assert.False((await data.OrganizationMemberships.SingleAsync(x =>
                x.UserId == member.Id && x.OrganizationId == org.Id, ct)).IsActive);
            Assert.Equal(EmployerRole.Admin, (await data.EmployerUserAccesses.SingleAsync(x =>
                x.UserId == member.Id && x.OrganizationId == org.Id, ct)).Role);
            Assert.False((await data.Users.SingleAsync(x => x.Id == member.Id, ct)).IsPlatformAdmin);
        });
    }

    [Fact]
    public async Task Otp_rejects_wrong_and_expired_codes_and_issued_jwt_is_validated_by_the_host()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            var user = new User("a6-otp-user", "a6-otp@test.invalid", "A6 OTP User");
            const string validCode = "314159";
            var now = DateTime.UtcNow;
            var valid = Challenge(user.Id, validCode, now.AddMinutes(5));
            var wrong = Challenge(user.Id, "271828", now.AddMinutes(5));
            var expired = Challenge(user.Id, validCode, now.AddMinutes(-1));

            await using (var seedFactory = new IdentityWebApplicationFactory(connectionString, useTestAuthentication: true))
            {
                await using var scope = seedFactory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(user, valid, wrong, expired);
                await db.SaveChangesAsync(ct);
            }

            string accessToken;
            await using (var otpFactory = new IdentityWebApplicationFactory(connectionString, useTestAuthentication: true))
            using (var otpClient = CreateClient(otpFactory))
            {
                using var wrongResponse = await otpClient.PostAsJsonAsync("/api/auth/otp/verify",
                    new { challengeId = wrong.Id, code = "000000" }, ct);
                Assert.Equal(HttpStatusCode.Unauthorized, wrongResponse.StatusCode);

                using var expiredResponse = await otpClient.PostAsJsonAsync("/api/auth/otp/verify",
                    new { challengeId = expired.Id, code = validCode }, ct);
                Assert.Equal(HttpStatusCode.Unauthorized, expiredResponse.StatusCode);

                using var validResponse = await otpClient.PostAsJsonAsync("/api/auth/otp/verify",
                    new { challengeId = valid.Id, code = validCode }, ct);
                Assert.Equal(HttpStatusCode.OK, validResponse.StatusCode);
                var body = await validResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                accessToken = body.GetProperty("accessToken").GetString()!;
                Assert.False(string.IsNullOrWhiteSpace(accessToken));
            }

            await using var jwtFactory = new IdentityWebApplicationFactory(connectionString, useTestAuthentication: false);
            using var jwtClient = CreateClient(jwtFactory);

            using var invalidJwtRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
            invalidJwtRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");
            using var invalidJwt = await jwtClient.SendAsync(invalidJwtRequest, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, invalidJwt.StatusCode);

            using var validJwtRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
            validJwtRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var validJwt = await jwtClient.SendAsync(validJwtRequest, ct);
            Assert.Equal(HttpStatusCode.OK, validJwt.StatusCode);
            var session = await validJwt.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(user.Id, session.GetProperty("userId").GetGuid());

            using var logoutRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
            logoutRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var logout = await jwtClient.SendAsync(logoutRequest, ct);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

            using var revokedTokenRequest = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
            revokedTokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var revokedToken = await jwtClient.SendAsync(revokedTokenRequest, ct);
            Assert.Equal(HttpStatusCode.Unauthorized, revokedToken.StatusCode);
            Assert.Equal("true", Assert.Single(revokedToken.Headers.GetValues("X-Alpha-Session-Expired")));
        });
    }

    [Fact]
    public async Task Authentication_rate_limit_still_rejects_the_twenty_first_request()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new IdentityWebApplicationFactory(connectionString, useTestAuthentication: true);
            using var client = CreateClient(factory);

            Task<HttpResponseMessage> Verify() => client.PostAsJsonAsync("/api/auth/otp/verify", new { challengeId = Guid.NewGuid(), code = "x" }, ct);
            for (var requestNumber = 1; requestNumber <= 20; requestNumber++)
            {
                using var response = await Verify();
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            using var limited = await Verify();
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
            // The session check and logout need a session and are limited per user, not by the credential bucket.
            using var session = await client.GetAsync("/api/auth/session", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
        });
    }

    private static OtpChallenge Challenge(Guid userId, string code, DateTime expiresAt) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Channel = "email",
        CodeHash = HashCode(code),
        CreatedAt = DateTime.UtcNow,
        ExpiresAt = expiresAt
    };

    private static string HashCode(string code)
    {
        var otpKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes(SigningKey),
            Encoding.UTF8.GetBytes("alpha:otp:v1"));
        return Convert.ToHexString(HMACSHA256.HashData(otpKey, Encoding.UTF8.GetBytes(code)));
    }

    private static HttpClient CreateClient(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

    private static HttpRequestMessage NewRequest(HttpMethod method, string path, Guid userId, Guid? sessionId)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-User-Id", userId.ToString());
        if (sessionId.HasValue) request.Headers.Add("X-Session-Id", sessionId.Value.ToString());
        return request;
    }

    private sealed class IdentityWebApplicationFactory(string connectionString, bool useTestAuthentication)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(useTestAuthentication ? Environments.Development : "A6JwtTesting");
            builder.UseSetting("ConnectionStrings:AlphaDatabase", connectionString);
            // The non-Development JWT host never transmits; Mock is refused outside Development.
            builder.UseSetting("EmployerInterface006:ClearinghouseProvider", useTestAuthentication ? "Mock" : "Disabled");
            builder.ConfigureLogging(logging => logging.ClearProviders());
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:AlphaDatabase"] = connectionString,
                    ["Database:ApplyMigrations"] = "false",
                    ["DemoData:Enabled"] = "false",
                    ["Security:DataProtectionKey"] = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
                    ["PrototypeAuth:SigningKey"] = SigningKey
                }));
            builder.ConfigureTestServices(services =>
            {
                var hostedServices = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray();
                foreach (var hostedService in hostedServices) services.Remove(hostedService);
                services.RemoveAll<AlphaDbContext>();
                services.RemoveAll<DbContextOptions<AlphaDbContext>>();
                services.AddDbContext<AlphaDbContext>(options => options.UseNpgsql(connectionString));

                if (useTestAuthentication)
                {
                    services.AddAuthentication(options =>
                        {
                            options.DefaultAuthenticateScheme = "A6TestHeaders";
                            options.DefaultChallengeScheme = "A6TestHeaders";
                            options.DefaultForbidScheme = "A6TestHeaders";
                        })
                        .AddScheme<AuthenticationSchemeOptions, A6TestAuthenticationHandler>(
                            "A6TestHeaders", null);
                }
            });
        }
    }

    private sealed class A6TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue("X-User-Id", out var userValue) ||
                !Guid.TryParse(userValue, out var userId))
                return Task.FromResult(AuthenticateResult.NoResult());

            var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
            if (Request.Headers.TryGetValue("X-Session-Id", out var sessionValue) &&
                Guid.TryParse(sessionValue, out var sessionId))
                claims.Add(new Claim("alpha:session_id", sessionId.ToString()));
            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private static async Task InIsolatedDatabase(Func<string, CancellationToken, Task> test)
    {
        var baseConnection = PostgresTestEnvironment.RequireConnectionString();
        var ct = TestContext.Current.CancellationToken;
        var database = "alpha_a6_identity_" + Guid.NewGuid().ToString("N");
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
