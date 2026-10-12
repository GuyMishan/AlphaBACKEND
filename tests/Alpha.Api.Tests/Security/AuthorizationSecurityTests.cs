using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;
using static Alpha.Api.Tests.TestInfrastructure.TestData;

namespace Alpha.Api.Tests.Security;

/// <summary>
/// Negative authorization and tenant-isolation tests over the real HTTP pipeline. Every denied
/// mutation also verifies that no data or audit side effect was persisted.
/// </summary>
public sealed class AuthorizationSecurityTests
{
    [Fact]
    public async Task Anonymous_endpoints_are_limited_to_the_reviewed_allow_list_and_unknown_routes_are_denied()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
            string Describe(RouteEndpoint endpoint) =>
                $"{string.Join(",", endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])} /{endpoint.RoutePattern.RawText?.TrimStart('/')}";

            var anonymous = endpoints.Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
                .Select(Describe).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(new[]
            {
                "* /health",
                "GET /api/invitations/{token}",
                "GET /health/db",
                "GET /openapi/{documentName}.json",
                "POST /api/auth/otp/request",
                "POST /api/auth/otp/verify",
                "POST /api/auth/register/request",
                "POST /api/auth/register/verify",
                "POST /api/billing/payplus/callback",
                "POST /api/billing/providers/{providerName}/callback"
            }, anonymous);

            // Every other endpoint declares authorization explicitly; the fallback policy is defense in depth.
            var implicitEndpoints = endpoints.Where(e =>
                    e.Metadata.GetMetadata<IAllowAnonymous>() is null && !e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any())
                .Select(Describe).ToArray();
            Assert.Empty(implicitEndpoints);

            using var unknown = await client.GetAsync("/api/not-a-route", ct);
            Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        });
    }

    [Fact]
    public async Task Platform_user_administration_never_returns_protected_identity_material()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var (admin, session) = await SeedUserAsync(factory, "platform-admin", platformAdmin: true);

            using var createRequest = Request(HttpMethod.Post, "/api/platform/users", admin.Id, session.Id, platformAdminClaim: true,
                json: new { email = "new.user@test.invalid", displayName = "New User", nationalId = "123456782", phone = "0501234567" });
            using var created = await client.SendAsync(createRequest, ct);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            AssertNoProtectedIdentityFields(await created.Content.ReadFromJsonAsync<JsonElement>(ct));

            using var listRequest = Request(HttpMethod.Get, "/api/platform/users", admin.Id, session.Id, platformAdminClaim: true);
            using var list = await client.SendAsync(listRequest, ct);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var users = await list.Content.ReadFromJsonAsync<JsonElement>(ct);
            Assert.Equal(2, users.GetArrayLength());
            foreach (var user in users.EnumerateArray())
            {
                AssertNoProtectedIdentityFields(user);
                Assert.True(user.TryGetProperty("id", out _));
                Assert.True(user.TryGetProperty("email", out _));
                Assert.True(user.TryGetProperty("displayName", out _));
                Assert.True(user.TryGetProperty("isActive", out _));
                Assert.True(user.TryGetProperty("isPlatformAdmin", out _));
            }

            // The protected values are still persisted for lookup; only the API projection changed.
            await factory.WithDbAsync(async db =>
            {
                var stored = await db.Users.AsNoTracking().SingleAsync(x => x.Email == "new.user@test.invalid", ct);
                Assert.StartsWith("alpha:v1:", stored.NationalIdEncrypted);
                Assert.False(string.IsNullOrWhiteSpace(stored.NationalIdLookupHash));
            });
        });
    }

    [Fact]
    public async Task Demoted_platform_admin_token_is_rejected_and_its_session_revoked()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var (admin, adminSession) = await SeedUserAsync(factory, "still-admin", platformAdmin: true);
            var (demoted, demotedSession) = await SeedUserAsync(factory, "demoted-admin", platformAdmin: false);

            using var legitimate = await client.SendAsync(
                Request(HttpMethod.Get, "/api/platform/users", admin.Id, adminSession.Id, platformAdminClaim: true), ct);
            Assert.Equal(HttpStatusCode.OK, legitimate.StatusCode);

            // Token issued while the user was an admin still carries the claim.
            using var stale = await client.SendAsync(
                Request(HttpMethod.Get, "/api/platform/users", demoted.Id, demotedSession.Id, platformAdminClaim: true), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
            Assert.True(stale.Headers.Contains("X-Alpha-Session-Expired"));

            await factory.WithDbAsync(async db =>
                Assert.NotNull((await db.UserSessions.AsNoTracking().SingleAsync(x => x.Id == demotedSession.Id, ct)).RevokedAt));

            // The revoked session cannot be reused even without the stale claim.
            using var reuse = await client.SendAsync(
                Request(HttpMethod.Get, "/api/scope", demoted.Id, demotedSession.Id), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        });
    }

    [Fact]
    public async Task Non_admin_cannot_use_platform_operations_or_read_security_audit()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var (user, session) = await SeedUserAsync(factory, "regular-user", platformAdmin: false);
            foreach (var path in new[] { "/api/platform/users", "/api/platform/security/events", "/api/platform/billing/summary" })
            {
                using var response = await client.SendAsync(Request(HttpMethod.Get, path, user.Id, session.Id), ct);
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            using var create = await client.SendAsync(Request(HttpMethod.Post, "/api/platform/users", user.Id, session.Id,
                json: new { email = "escalation@test.invalid", displayName = "Escalation", nationalId = "123456782", phone = "0501234567" }), ct);
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
            using var promote = await client.SendAsync(Request(HttpMethod.Put, $"/api/platform/users/{user.Id}", user.Id, session.Id,
                json: new { email = user.Email, displayName = user.DisplayName, isActive = true, isPlatformAdmin = true }), ct);
            Assert.Equal(HttpStatusCode.Forbidden, promote.StatusCode);

            await factory.WithDbAsync(async db =>
            {
                Assert.False(await db.Users.AnyAsync(x => x.Email == "escalation@test.invalid", ct));
                Assert.False((await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.Id, ct)).IsPlatformAdmin);
            });
        });
    }

    [Fact]
    public async Task Cross_tenant_reads_writes_and_id_spoofing_are_denied_without_side_effects()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var a = await SeedTenantAsync(factory, "A");
            var b = await SeedTenantAsync(factory, "B");
            var employees = $"/api/organizations/{a.Organization.Id}/employers/{a.Employer.Id}/employees";

            using var own = await client.SendAsync(Request(HttpMethod.Get, employees, a.Admin.Id, a.Session.Id), ct);
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);

            foreach (var path in new[]
                     {
                         employees,
                         $"{employees}/{a.Employment.Id}",
                         $"/api/organizations/{a.Organization.Id}/employers",
                         $"/api/organizations/{a.Organization.Id}/employers/{a.Employer.Id}",
                         $"/api/organizations/{a.Organization.Id}/employers/{a.Employer.Id}/manual-reports?skip=0&take=30",
                         $"/api/organizations/{a.Organization.Id}/employers/{a.Employer.Id}/payment-accounts",
                         $"/api/organizations/{a.Organization.Id}/billing/context",
                         $"/api/organizations/{a.Organization.Id}/access/users"
                     })
            {
                using var response = await client.SendAsync(Request(HttpMethod.Get, path, b.Admin.Id, b.Session.Id), ct);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{path} returned {(int)response.StatusCode}");
            }

            // Tenant-context spoofing: own organization in the route, foreign employer/employment IDs.
            foreach (var path in new[]
                     {
                         $"/api/organizations/{b.Organization.Id}/employers/{a.Employer.Id}/employees",
                         $"/api/organizations/{b.Organization.Id}/employers/{a.Employer.Id}/employees/{a.Employment.Id}",
                         $"/api/organizations/{b.Organization.Id}/employers/{b.Employer.Id}/employees/{a.Employment.Id}"
                     })
            {
                using var response = await client.SendAsync(Request(HttpMethod.Get, path, b.Admin.Id, b.Session.Id), ct);
                Assert.True(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
                    $"{path} returned {(int)response.StatusCode}");
                Assert.DoesNotContain(a.Employment.EmployeeNumber, await response.Content.ReadAsStringAsync(ct));
            }

            var auditBefore = await CountAuditAsync(factory, ct);
            using var update = await client.SendAsync(Request(HttpMethod.Put,
                $"/api/organizations/{a.Organization.Id}/employers/{a.Employer.Id}", b.Admin.Id, b.Session.Id,
                json: new { legalName = "Hijacked Employer", registrationNumber = "99999999", withholdingFileNumber = "999999999" }), ct);
            Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
            using var status = await client.SendAsync(Request(HttpMethod.Put,
                $"/api/organizations/{a.Organization.Id}/employers/{a.Employer.Id}/status", b.Admin.Id, b.Session.Id,
                json: new { status = 3 }), ct);
            Assert.Equal(HttpStatusCode.Forbidden, status.StatusCode);
            using var grant = await client.SendAsync(Request(HttpMethod.Post,
                $"/api/organizations/{a.Organization.Id}/access/users/{b.Admin.Id}/employers/{a.Employer.Id}", b.Admin.Id, b.Session.Id), ct);
            Assert.Equal(HttpStatusCode.Forbidden, grant.StatusCode);

            await factory.WithDbAsync(async db =>
            {
                var employer = await db.Employers.AsNoTracking().SingleAsync(x => x.Id == a.Employer.Id, ct);
                Assert.Equal(a.Employer.LegalName, employer.LegalName);
                Assert.Equal(a.Employer.Status, employer.Status);
                Assert.False(await db.EmployerUserAccesses.AnyAsync(x => x.UserId == b.Admin.Id && x.OrganizationId == a.Organization.Id, ct));
            });
            Assert.Equal(auditBefore, await CountAuditMutationsAsync(factory, ct, excludeHttp: true));
        });
    }

    [Fact]
    public async Task Employer_documents_cannot_be_read_deleted_or_reassigned_across_tenants_or_by_id_spoofing()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var a = await SeedTenantAsync(factory, "DA");
            var b = await SeedTenantAsync(factory, "DB");
            var document = new EmployerDocument(a.Organization.Id, a.Employer.Id, null, null, 3, "affidavit.pdf",
                "%PDF-1.4 synthetic"u8.ToArray(), 18, new string('0', 64));
            await factory.WithDbAsync(async db => { db.Add(document); await db.SaveChangesAsync(); });

            var own = $"/api/organizations/{a.Organization.Id}/employers/{a.Employer.Id}/documents/{document.Id}";
            using var allowed = await client.SendAsync(Request(HttpMethod.Get, $"{own}/file", a.Admin.Id, a.Session.Id), ct);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

            foreach (var (method, path) in new (HttpMethod, string)[]
                     {
                         (HttpMethod.Get, $"{own}/file"), (HttpMethod.Delete, own), (HttpMethod.Patch, $"{own}/product"),
                         (HttpMethod.Get, $"/api/organizations/{a.Organization.Id}/employers/{a.Employer.Id}/documents")
                     })
            {
                using var response = await client.SendAsync(Request(method, path, b.Admin.Id, b.Session.Id,
                    json: method == HttpMethod.Patch ? new { employeePensionProductId = Guid.NewGuid() } : null), ct);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} returned {(int)response.StatusCode}");
            }

            // Own organization and employer in the route, foreign document ID.
            using var spoofed = await client.SendAsync(Request(HttpMethod.Get,
                $"/api/organizations/{b.Organization.Id}/employers/{b.Employer.Id}/documents/{document.Id}/file", b.Admin.Id, b.Session.Id), ct);
            Assert.Equal(HttpStatusCode.NotFound, spoofed.StatusCode);
            using var spoofedDelete = await client.SendAsync(Request(HttpMethod.Delete,
                $"/api/organizations/{b.Organization.Id}/employers/{b.Employer.Id}/documents/{document.Id}", b.Admin.Id, b.Session.Id), ct);
            Assert.Equal(HttpStatusCode.NotFound, spoofedDelete.StatusCode);

            await factory.WithDbAsync(async db => Assert.True(await db.EmployerDocuments.AnyAsync(x => x.Id == document.Id)));
        });
    }

    [Fact]
    public async Task Selected_employer_member_cannot_reach_unassigned_employer_by_direct_id()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "S");
            var other = new Employer(tenant.Organization.Id, "Unassigned Employer", "55555555", "555555555");
            var otherPerson = NewPerson(tenant.Organization.Id, "Hidden");
            var otherEmployment = new Employment(tenant.Organization.Id, other.Id, otherPerson.Id, new DateOnly(2026, 1, 1), "HIDDEN-1", 9_000m);
            var (member, session) = await SeedUserAsync(factory, "selected-member", platformAdmin: false);
            await factory.WithDbAsync(async db =>
            {
                db.AddRange(other, otherPerson, otherEmployment,
                    new OrganizationMembership(member.Id, tenant.Organization.Id, OrganizationRole.OperationsAgent,
                        EmployerAccessMode.SelectedEmployers, tenant.Admin.Id),
                    new EmployerUserAccess(member.Id, tenant.Organization.Id, tenant.Employer.Id, EmployerRole.User));
                await db.SaveChangesAsync(ct);
            });

            var assigned = $"/api/organizations/{tenant.Organization.Id}/employers/{tenant.Employer.Id}/employees";
            using var allowed = await client.SendAsync(Request(HttpMethod.Get, assigned, member.Id, session.Id), ct);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

            using var unassigned = await client.SendAsync(Request(HttpMethod.Get,
                $"/api/organizations/{tenant.Organization.Id}/employers/{other.Id}/employees", member.Id, session.Id), ct);
            Assert.Equal(HttpStatusCode.Forbidden, unassigned.StatusCode);

            // Assigned employer in the route, employment of the unassigned employer as the resource ID.
            using var indirect = await client.SendAsync(Request(HttpMethod.Get, $"{assigned}/{otherEmployment.Id}", member.Id, session.Id), ct);
            Assert.Equal(HttpStatusCode.NotFound, indirect.StatusCode);

            using var list = await client.SendAsync(Request(HttpMethod.Get,
                $"/api/organizations/{tenant.Organization.Id}/employers", member.Id, session.Id), ct);
            Assert.Equal(HttpStatusCode.OK, list.StatusCode);
            var visible = (await list.Content.ReadFromJsonAsync<JsonElement>(ct)).EnumerateArray()
                .Select(x => x.GetProperty("id").GetGuid()).ToArray();
            Assert.Equal(new[] { tenant.Employer.Id }, visible);
        });
    }

    [Fact]
    public async Task Removed_member_loses_access_immediately_and_removal_is_audited_atomically()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "R");
            var (member, session) = await SeedUserAsync(factory, "removed-member", platformAdmin: false);
            await factory.WithDbAsync(async db =>
            {
                db.AddRange(
                    new OrganizationMembership(member.Id, tenant.Organization.Id, OrganizationRole.OperationsAgent,
                        EmployerAccessMode.SelectedEmployers, tenant.Admin.Id),
                    new EmployerUserAccess(member.Id, tenant.Organization.Id, tenant.Employer.Id, EmployerRole.Admin));
                await db.SaveChangesAsync(ct);
            });
            var employees = $"/api/organizations/{tenant.Organization.Id}/employers/{tenant.Employer.Id}/employees";
            using var before = await client.SendAsync(Request(HttpMethod.Get, employees, member.Id, session.Id), ct);
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

            using var remove = await client.SendAsync(Request(HttpMethod.Delete,
                $"/api/organizations/{tenant.Organization.Id}/access/users/{member.Id}", tenant.Admin.Id, tenant.Session.Id), ct);
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);

            // Same, still valid session: access is evaluated from the database on every request.
            using var after = await client.SendAsync(Request(HttpMethod.Get, employees, member.Id, session.Id), ct);
            Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
            using var edit = await client.SendAsync(Request(HttpMethod.Put,
                $"/api/organizations/{tenant.Organization.Id}/employers/{tenant.Employer.Id}", member.Id, session.Id,
                json: new { legalName = "Edited After Removal", registrationNumber = "12345678", withholdingFileNumber = "123456789" }), ct);
            Assert.Equal(HttpStatusCode.Forbidden, edit.StatusCode);

            await factory.WithDbAsync(async db =>
            {
                Assert.False((await db.OrganizationMemberships.AsNoTracking()
                    .SingleAsync(x => x.UserId == member.Id && x.OrganizationId == tenant.Organization.Id, ct)).IsActive);
                Assert.False(await db.EmployerUserAccesses.AnyAsync(x => x.UserId == member.Id, ct));
                Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "membership.removed" && x.OrganizationId == tenant.Organization.Id, ct));
                Assert.NotEqual("Edited After Removal", (await db.Employers.AsNoTracking().SingleAsync(x => x.Id == tenant.Employer.Id, ct)).LegalName);
            });
        });
    }

    [Fact]
    public async Task Viewer_cannot_perform_administrative_or_mutating_actions()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "V");
            var (viewer, session) = await SeedUserAsync(factory, "viewer", platformAdmin: false);
            await factory.WithDbAsync(async db =>
            {
                db.Add(new OrganizationMembership(viewer.Id, tenant.Organization.Id, OrganizationRole.Viewer,
                    EmployerAccessMode.AllEmployers, tenant.Admin.Id));
                await db.SaveChangesAsync(ct);
            });
            var org = $"/api/organizations/{tenant.Organization.Id}";

            using var read = await client.SendAsync(Request(HttpMethod.Get, $"{org}/employers/{tenant.Employer.Id}/employees", viewer.Id, session.Id), ct);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);

            var auditBefore = await CountAuditMutationsAsync(factory, ct, excludeHttp: true);
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                     {
                         (HttpMethod.Put, $"{org}/employers/{tenant.Employer.Id}", new { legalName = "Viewer Edit", registrationNumber = "12345678", withholdingFileNumber = "123456789" }),
                         (HttpMethod.Post, $"{org}/employers", new { legalName = "Viewer Employer", registrationNumber = "12345678", withholdingFileNumber = "123456789" }),
                         (HttpMethod.Delete, $"{org}/access/users/{tenant.Admin.Id}", null),
                         (HttpMethod.Put, $"{org}/access/users/{viewer.Id}", new { role = 1, employerAccessMode = 1, canCreateEmployer = true, canEditEmployer = true, canCreateEmployee = true, canEditEmployee = true, canCreateReport = true, canTransmitReport = true }),
                         (HttpMethod.Put, $"{org}/billing/pricing", new { billingType = "PerEmployee" })
                     })
            {
                using var response = await client.SendAsync(Request(method, path, viewer.Id, session.Id, json: body), ct);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} returned {(int)response.StatusCode}");
            }

            await factory.WithDbAsync(async db =>
            {
                var membership = await db.OrganizationMemberships.AsNoTracking()
                    .SingleAsync(x => x.UserId == viewer.Id && x.OrganizationId == tenant.Organization.Id, ct);
                Assert.Equal(OrganizationRole.Viewer, membership.Role);
                Assert.Equal(1, await db.Employers.CountAsync(x => x.OrganizationId == tenant.Organization.Id, ct));
                Assert.True((await db.OrganizationMemberships.AsNoTracking()
                    .SingleAsync(x => x.UserId == tenant.Admin.Id, ct)).IsActive);
            });
            Assert.Equal(auditBefore, await CountAuditMutationsAsync(factory, ct, excludeHttp: true));
        });
    }

    [Fact]
    public async Task Revoked_expired_and_deactivated_sessions_are_rejected()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var (revokedUser, revoked) = await SeedUserAsync(factory, "revoked", platformAdmin: false);
            var (deactivatedUser, deactivatedSession) = await SeedUserAsync(factory, "deactivated", platformAdmin: false);
            var expiredUser = new User("expired", "expired@test.invalid", "Expired User");
            var expired = new UserSession(expiredUser.Id, DateTime.UtcNow.AddHours(-13), DateTime.UtcNow.AddHours(-1));
            await factory.WithDbAsync(async db =>
            {
                db.AddRange(expiredUser, expired);
                (await db.UserSessions.SingleAsync(x => x.Id == revoked.Id, ct)).Revoke(DateTime.UtcNow);
                (await db.Users.SingleAsync(x => x.Id == deactivatedUser.Id, ct)).Deactivate();
                await db.SaveChangesAsync(ct);
            });

            foreach (var (userId, sessionId) in new[] { (revokedUser.Id, revoked.Id), (expiredUser.Id, expired.Id), (deactivatedUser.Id, deactivatedSession.Id) })
            {
                using var response = await client.SendAsync(Request(HttpMethod.Get, "/api/scope", userId, sessionId), ct);
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            // A session can only be used by the user it was issued to.
            var (other, _) = await SeedUserAsync(factory, "session-thief", platformAdmin: false);
            var (_, victimSession) = await SeedUserAsync(factory, "session-victim", platformAdmin: false);
            using var stolen = await client.SendAsync(Request(HttpMethod.Get, "/api/scope", other.Id, victimSession.Id), ct);
            Assert.Equal(HttpStatusCode.Unauthorized, stolen.StatusCode);
        });
    }

    private static void AssertNoProtectedIdentityFields(JsonElement user)
    {
        foreach (var field in new[] { "nationalIdEncrypted", "nationalIdLookupHash", "phoneEncrypted", "phoneLookupHash", "externalSubject" })
            Assert.False(user.TryGetProperty(field, out _), $"User response exposes '{field}'.");
    }

    private static Task<int> CountAuditAsync(Factory factory, CancellationToken ct) =>
        CountAuditMutationsAsync(factory, ct, excludeHttp: true);

    /// <summary>Business audit rows; the security middleware's generic <c>http.*</c> rows are excluded.</summary>
    private static async Task<int> CountAuditMutationsAsync(Factory factory, CancellationToken ct, bool excludeHttp)
    {
        var count = 0;
        await factory.WithDbAsync(async db => count = await db.AuditEvents
            .CountAsync(x => !excludeHttp || !x.Action.StartsWith("http."), ct));
        return count;
    }
}
