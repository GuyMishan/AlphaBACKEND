using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Alpha.Infrastructure.Persistence;
using Alpha.Domain.Billing;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Alpha.Modules.ReferenceData.Application;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
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

namespace Alpha.Api.Tests.Integration;

public sealed class ModuleHttpIntegrationTests
{
    [Fact]
    public async Task Reference_data_and_auditing_preserve_http_security_and_audit_behavior()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new A3WebApplicationFactory(connectionString);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

            using var anonymousReference = await client.GetAsync("/api/platform/reference-data/interfaces", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousReference.StatusCode);

            var tenantUser = new User("tenant-http-test", "tenant-http@test.invalid", "Tenant HTTP Test");
            var platformAdmin = new User("admin-http-test", "admin-http@test.invalid", "Admin HTTP Test");
            platformAdmin.SetPlatformAdmin(true);
            var organization = new Organization("HTTP Tenant", OrganizationType.RegularOrganization);
            var membership = new OrganizationMembership(tenantUser.Id, organization.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, tenantUser.Id);
            var now = DateTime.UtcNow;
            var tenantSession = new UserSession(tenantUser.Id, now, now.AddHours(1));
            var adminSession = new UserSession(platformAdmin.Id, now, now.AddHours(1));
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(tenantUser, platformAdmin, organization, membership, tenantSession, adminSession);
                await db.SaveChangesAsync(ct);
            }

            using var tenantRequest = NewRequest(HttpMethod.Get, "/api/platform/reference-data/interfaces",
                tenantUser.Id, tenantSession.Id);
            using var tenantReference = await client.SendAsync(tenantRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, tenantReference.StatusCode);

            using var adminRequest = NewRequest(HttpMethod.Get, "/api/platform/reference-data/interfaces",
                platformAdmin.Id, adminSession.Id, platformAdmin: true);
            using var adminReference = await client.SendAsync(adminRequest, ct);
            Assert.Equal(HttpStatusCode.OK, adminReference.StatusCode);
            var integrations = await adminReference.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(JsonValueKind.Array, integrations.ValueKind);
            Assert.Equal(ReferenceDataSyncService.Integrations.Count, integrations.GetArrayLength());
            Assert.All(integrations.EnumerateArray(), item =>
            {
                Assert.True(item.TryGetProperty("key", out _));
                Assert.True(item.TryGetProperty("name", out _));
                Assert.Equal(JsonValueKind.Null, item.GetProperty("lastRun").ValueKind);
            });

            using var deniedAuditRequest = NewRequest(HttpMethod.Get, "/api/platform/security/events",
                tenantUser.Id, tenantSession.Id);
            using var deniedAudit = await client.SendAsync(deniedAuditRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, deniedAudit.StatusCode);

            using var invalidRunRequest = NewRequest(HttpMethod.Post,
                "/api/platform/reference-data/interfaces/not-an-integration/run",
                platformAdmin.Id, adminSession.Id, platformAdmin: true);
            using var invalidRun = await client.SendAsync(invalidRunRequest, ct);
            Assert.Equal(HttpStatusCode.NotFound, invalidRun.StatusCode);
            Assert.True(invalidRun.Headers.TryGetValues("X-Correlation-ID", out var correlationValues));
            var correlationId = Assert.Single(correlationValues);
            Assert.False(string.IsNullOrWhiteSpace(correlationId));

            using var auditRequest = NewRequest(HttpMethod.Get, "/api/platform/security/events?take=10",
                platformAdmin.Id, adminSession.Id, platformAdmin: true);
            using var auditResponse = await client.SendAsync(auditRequest, ct);
            Assert.Equal(HttpStatusCode.OK, auditResponse.StatusCode);
            var auditBody = await auditResponse.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.True(auditBody.GetProperty("auditLogging").GetBoolean());
            var auditItem = Assert.Single(auditBody.GetProperty("items").EnumerateArray());
            Assert.Equal(platformAdmin.Id, auditItem.GetProperty("actorUserId").GetGuid());
            Assert.Equal("http.post", auditItem.GetProperty("action").GetString());
            Assert.Equal("api", auditItem.GetProperty("entityType").GetString());
            Assert.Equal(correlationId, auditItem.GetProperty("correlationId").GetString());
            var auditData = JsonDocument.Parse(auditItem.GetProperty("data").GetString()!);
            Assert.Equal("POST", auditData.RootElement.GetProperty("method").GetString());
            Assert.Equal("/api/platform/reference-data/interfaces/not-an-integration/run",
                auditData.RootElement.GetProperty("path").GetString());
            Assert.Equal("failed", auditData.RootElement.GetProperty("result").GetString());
            Assert.Equal(StatusCodes.Status404NotFound, auditData.RootElement.GetProperty("statusCode").GetInt32());
        });
    }

    [Fact]
    public async Task Organization_and_workforce_modules_preserve_http_authorization_tenant_scope_and_responses()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new A3WebApplicationFactory(connectionString);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

            var userA = new User("a4-user-a", "a4-user-a@test.invalid", "A4 User A");
            var userB = new User("a4-user-b", "a4-user-b@test.invalid", "A4 User B");
            var organizationA = new Organization("A4 Organization A", OrganizationType.RegularOrganization);
            var organizationB = new Organization("A4 Organization B", OrganizationType.RegularOrganization);
            organizationA.Activate();
            organizationB.Activate();
            var membershipA = new OrganizationMembership(userA.Id, organizationA.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, userA.Id);
            var membershipB = new OrganizationMembership(userB.Id, organizationB.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, userB.Id);
            var employerA = new Alpha.Domain.Employers.Employer(organizationA.Id, "A4 Employer", "a4-reg", "a4-file");
            var personA = new Alpha.Domain.Employees.Person(organizationA.Id, string.Empty, "A4", "Employee");
            personA.SetProtectedIdentifier(Alpha.Domain.Employees.PersonIdentifierType.IsraeliId,
                "test-encrypted-identifier", "test-identifier-hash");
            var employmentA = new Alpha.Domain.Employees.Employment(organizationA.Id, employerA.Id, personA.Id,
                new DateOnly(2026, 1, 1), "A4-1", 10_000m);
            var now = DateTime.UtcNow;
            var sessionA = new UserSession(userA.Id, now, now.AddHours(1));
            var sessionB = new UserSession(userB.Id, now, now.AddHours(1));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(userA, userB, organizationA, organizationB, membershipA, membershipB,
                    employerA, personA, employmentA, sessionA, sessionB);
                await db.SaveChangesAsync(ct);
            }

            using var anonymousProfile = await client.GetAsync(
                $"/api/organizations/{organizationA.Id}/profile-center/", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousProfile.StatusCode);

            using var ownProfileRequest = NewRequest(HttpMethod.Get,
                $"/api/organizations/{organizationA.Id}/profile-center/", userA.Id, sessionA.Id);
            using var ownProfile = await client.SendAsync(ownProfileRequest, ct);
            Assert.Equal(HttpStatusCode.OK, ownProfile.StatusCode);
            var profileJson = await ownProfile.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(organizationA.Id, profileJson.GetProperty("id").GetGuid());
            Assert.True(profileJson.GetProperty("canManageOrganization").GetBoolean());

            using var crossTenantProfileRequest = NewRequest(HttpMethod.Get,
                $"/api/organizations/{organizationA.Id}/profile-center/", userB.Id, sessionB.Id);
            using var crossTenantProfile = await client.SendAsync(crossTenantProfileRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossTenantProfile.StatusCode);

            using var employerSettingsRequest = NewRequest(HttpMethod.Get,
                $"/api/organizations/{organizationA.Id}/employers/{employerA.Id}/profile-center/settings",
                userA.Id, sessionA.Id);
            using var employerSettings = await client.SendAsync(employerSettingsRequest, ct);
            Assert.Equal(HttpStatusCode.OK, employerSettings.StatusCode);
            var employerSettingsJson = await employerSettings.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.True(employerSettingsJson.TryGetProperty("address", out _));
            Assert.True(employerSettingsJson.TryGetProperty("reporting", out _));

            using var crossTenantEmployerRequest = NewRequest(HttpMethod.Get,
                $"/api/organizations/{organizationA.Id}/employers/{employerA.Id}/profile-center/settings",
                userB.Id, sessionB.Id);
            using var crossTenantEmployer = await client.SendAsync(crossTenantEmployerRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossTenantEmployer.StatusCode);

            using var deniedEmployerAddressRequest = NewRequest(HttpMethod.Put,
                $"/api/organizations/{organizationA.Id}/employers/{employerA.Id}/profile-center/address",
                userB.Id, sessionB.Id);
            deniedEmployerAddressRequest.Content = JsonContent.Create(new
            {
                city = "Cross tenant mutation must not run",
                street = "Denied",
                houseNumber = "1",
                apartment = (string?)null,
                postalCode = "1234567",
                postOfficeBox = (string?)null
            });
            using var deniedEmployerAddress = await client.SendAsync(deniedEmployerAddressRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, deniedEmployerAddress.StatusCode);

            using var updateEmployerAddressRequest = NewRequest(HttpMethod.Put,
                $"/api/organizations/{organizationA.Id}/employers/{employerA.Id}/profile-center/address",
                userA.Id, sessionA.Id);
            updateEmployerAddressRequest.Content = JsonContent.Create(new
            {
                city = "A4 City",
                street = "A4 Street",
                houseNumber = "4",
                apartment = "2",
                postalCode = "1234567",
                postOfficeBox = (string?)null
            });
            using var updateEmployerAddress = await client.SendAsync(updateEmployerAddressRequest, ct);
            Assert.Equal(HttpStatusCode.NoContent, updateEmployerAddress.StatusCode);

            using var pensionMixRequest = NewRequest(HttpMethod.Get,
                $"/api/organizations/{organizationA.Id}/employers/{employerA.Id}/employees/{employmentA.Id}/pension-mix/",
                userA.Id, sessionA.Id);
            using var pensionMix = await client.SendAsync(pensionMixRequest, ct);
            Assert.Equal(HttpStatusCode.OK, pensionMix.StatusCode);
            var pensionMixJson = await pensionMix.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            Assert.Equal(JsonValueKind.Array, pensionMixJson.ValueKind);
            Assert.Empty(pensionMixJson.EnumerateArray());

            using var crossTenantPensionRequest = NewRequest(HttpMethod.Get,
                $"/api/organizations/{organizationA.Id}/employers/{employerA.Id}/employees/{employmentA.Id}/pension-mix/",
                userB.Id, sessionB.Id);
            using var crossTenantPension = await client.SendAsync(crossTenantPensionRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossTenantPension.StatusCode);

            using var invalidPensionMixRequest = NewRequest(HttpMethod.Put,
                $"/api/organizations/{organizationA.Id}/employers/{employerA.Id}/employees/{employmentA.Id}/pension-mix/",
                userA.Id, sessionA.Id);
            invalidPensionMixRequest.Content = JsonContent.Create(new
            {
                monthlySalary = -1m,
                products = Array.Empty<object>()
            });
            using var invalidPensionMix = await client.SendAsync(invalidPensionMixRequest, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalidPensionMix.StatusCode);

            using var updateBillingRequest = NewRequest(HttpMethod.Put,
                $"/api/organizations/{organizationA.Id}/profile-center/billing", userA.Id, sessionA.Id);
            updateBillingRequest.Content = JsonContent.Create(new
            {
                invoiceName = "A4 Invoice",
                invoiceRegistrationNumber = "a4-invoice-reg",
                invoiceEmail = "billing@test.invalid",
                billingContactName = "A4 Billing",
                billingContactPhone = "0500000000"
            });
            using var updateBilling = await client.SendAsync(updateBillingRequest, ct);
            Assert.Equal(HttpStatusCode.NoContent, updateBilling.StatusCode);

            await using var verificationScope = factory.Services.CreateAsyncScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AlphaDbContext>();
            var businessAudit = await verificationDb.AuditEvents.AsNoTracking().SingleAsync(x =>
                x.Action == "organization.billing.updated", ct);
            Assert.Equal(userA.Id, businessAudit.ActorUserId);
            Assert.Equal(organizationA.Id, businessAudit.OrganizationId);
            Assert.Contains("A4 Invoice", businessAudit.Data, StringComparison.Ordinal);
            var employerAudit = await verificationDb.AuditEvents.AsNoTracking().SingleAsync(x =>
                x.Action == "employer.address.updated", ct);
            Assert.Equal(userA.Id, employerAudit.ActorUserId);
            Assert.Equal(organizationA.Id, employerAudit.OrganizationId);
            Assert.Equal(employerA.Id, employerAudit.EmployerId);
            Assert.Contains("A4 City", employerAudit.Data, StringComparison.Ordinal);
            Assert.Equal(10_000m, await verificationDb.Employments.AsNoTracking()
                .Where(x => x.Id == employmentA.Id)
                .Select(x => x.MonthlySalary)
                .SingleAsync(ct));
            Assert.True(await verificationDb.AuditEvents.AsNoTracking().AnyAsync(x =>
                x.Action == "http.put" && x.OrganizationId == organizationA.Id, ct));
        });
    }

    [Fact]
    public async Task Billing_module_preserves_http_security_contracts_and_period_idempotency()
    {
        await InIsolatedDatabase(async (connectionString, ct) =>
        {
            await using var factory = new A3WebApplicationFactory(connectionString);
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });

            var tenantAdmin = new User("a5-tenant-admin", "a5-tenant@test.invalid", "A5 Tenant Admin");
            var otherTenantAdmin = new User("a5-other-admin", "a5-other@test.invalid", "A5 Other Admin");
            var platformAdmin = new User("a5-platform-admin", "a5-platform@test.invalid", "A5 Platform Admin");
            platformAdmin.SetPlatformAdmin(true);
            var organization = new Organization("A5 Billing Organization", OrganizationType.RegularOrganization);
            var otherOrganization = new Organization("A5 Other Organization", OrganizationType.RegularOrganization);
            organization.Activate();
            otherOrganization.Activate();
            var membership = new OrganizationMembership(tenantAdmin.Id, organization.Id, OrganizationRole.Admin,
                EmployerAccessMode.AllEmployers, tenantAdmin.Id);
            var otherMembership = new OrganizationMembership(otherTenantAdmin.Id, otherOrganization.Id,
                OrganizationRole.Admin, EmployerAccessMode.AllEmployers, otherTenantAdmin.Id);
            var now = DateTime.UtcNow;
            var tenantSession = new UserSession(tenantAdmin.Id, now, now.AddHours(1));
            var otherSession = new UserSession(otherTenantAdmin.Id, now, now.AddHours(1));
            var platformSession = new UserSession(platformAdmin.Id, now, now.AddHours(1));

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AlphaDbContext>();
                db.AddRange(tenantAdmin, otherTenantAdmin, platformAdmin, organization, otherOrganization,
                    membership, otherMembership, tenantSession, otherSession, platformSession);
                await db.SaveChangesAsync(ct);
            }

            using var anonymous = await client.GetAsync(
                $"/api/organizations/{organization.Id}/billing-account/", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

            using var crossTenantRequest = NewRequest(HttpMethod.Get,
                $"/api/organizations/{organization.Id}/billing-account/",
                otherTenantAdmin.Id, otherSession.Id);
            using var crossTenant = await client.SendAsync(crossTenantRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, crossTenant.StatusCode);

            using var invalidRequest = NewRequest(HttpMethod.Put,
                $"/api/organizations/{organization.Id}/billing-account/", tenantAdmin.Id, tenantSession.Id);
            invalidRequest.Content = JsonContent.Create(new
            {
                billingName = "A5 Billing",
                taxId = "515151515",
                invoiceEmail = "not-an-email",
                billingAddress = "1 Test Street",
                paymentMethodType = BillingPaymentMethodType.CreditCard
            });
            using var invalid = await client.SendAsync(invalidRequest, ct);
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);

            using var updateRequest = NewRequest(HttpMethod.Put,
                $"/api/organizations/{organization.Id}/billing-account/", tenantAdmin.Id, tenantSession.Id);
            updateRequest.Content = JsonContent.Create(new
            {
                billingName = "A5 Billing",
                taxId = "515151515",
                invoiceEmail = "billing@test.invalid",
                billingAddress = "1 Test Street",
                paymentMethodType = BillingPaymentMethodType.CreditCard
            });
            using var update = await client.SendAsync(updateRequest, ct);
            Assert.Equal(HttpStatusCode.OK, update.StatusCode);
            var billingJson = await update.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var billingAccountId = billingJson.GetProperty("id").GetGuid();
            Assert.Equal(organization.Id, billingJson.GetProperty("organizationId").GetGuid());
            Assert.False(billingJson.TryGetProperty("providerCustomerId", out _));
            Assert.False(billingJson.TryGetProperty("providerPaymentMethodId", out _));

            var periodStart = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            var periodEnd = periodStart.AddMonths(1);
            using var deniedRunRequest = NewRequest(HttpMethod.Post,
                $"/api/platform/billing/accounts/{billingAccountId}/run", tenantAdmin.Id, tenantSession.Id);
            deniedRunRequest.Content = JsonContent.Create(new { periodStart, periodEnd, charge = false });
            using var deniedRun = await client.SendAsync(deniedRunRequest, ct);
            Assert.Equal(HttpStatusCode.Forbidden, deniedRun.StatusCode);

            async Task<Guid> RunPeriodAsync()
            {
                using var request = NewRequest(HttpMethod.Post,
                    $"/api/platform/billing/accounts/{billingAccountId}/run",
                    platformAdmin.Id, platformSession.Id, platformAdmin: true);
                request.Content = JsonContent.Create(new { periodStart, periodEnd, charge = false });
                using var response = await client.SendAsync(request, ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
                Assert.Equal(0m, json.GetProperty("total").GetDecimal());
                return json.GetProperty("billingPeriodId").GetGuid();
            }

            var firstPeriodId = await RunPeriodAsync();
            var repeatedPeriodId = await RunPeriodAsync();
            Assert.Equal(firstPeriodId, repeatedPeriodId);

            await using var verificationScope = factory.Services.CreateAsyncScope();
            var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AlphaDbContext>();
            Assert.Equal(1, await verificationDb.BillingPeriods.AsNoTracking()
                .CountAsync(x => x.BillingAccountId == billingAccountId &&
                                 x.PeriodStart == periodStart && x.PeriodEnd == periodEnd, ct));
            var audit = await verificationDb.AuditEvents.AsNoTracking()
                .SingleAsync(x => x.Action == "billing.organization.updated", ct);
            Assert.Equal(tenantAdmin.Id, audit.ActorUserId);
            Assert.Equal(organization.Id, audit.OrganizationId);
        });
    }

    private static HttpRequestMessage NewRequest(HttpMethod method, string path, Guid userId, Guid sessionId,
        bool platformAdmin = false)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-User-Id", userId.ToString());
        request.Headers.Add("X-Session-Id", sessionId.ToString());
        if (platformAdmin) request.Headers.Add("X-Platform-Admin", "true");
        return request;
    }

    private sealed class A3WebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
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
                var hostedServices = services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)).ToArray();
                foreach (var hostedService in hostedServices) services.Remove(hostedService);
                services.RemoveAll<AlphaDbContext>();
                services.RemoveAll<DbContextOptions<AlphaDbContext>>();
                services.AddDbContext<AlphaDbContext>(options => options.UseNpgsql(connectionString));
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = "A3TestHeaders";
                        options.DefaultChallengeScheme = "A3TestHeaders";
                        options.DefaultForbidScheme = "A3TestHeaders";
                    })
                    .AddScheme<AuthenticationSchemeOptions, A3TestAuthenticationHandler>(
                        "A3TestHeaders", null);
            });
        }
    }

    private sealed class A3TestAuthenticationHandler(
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
        var database = "alpha_a3_http_" + Guid.NewGuid().ToString("N");
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
                // Production already has this catalog. A blank test database needs the
                // prerequisite before the unchanged production initializer order runs.
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
