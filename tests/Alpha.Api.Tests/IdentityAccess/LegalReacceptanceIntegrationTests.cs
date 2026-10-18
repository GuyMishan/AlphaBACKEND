using System.Net;
using System.Net.Http.Json;
using Alpha.Api.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;
using static Alpha.Api.Tests.TestInfrastructure.TestData;

namespace Alpha.Api.Tests.IdentityAccess;

public sealed class LegalReacceptanceIntegrationTests
{
    [Fact]
    public async Task Reacceptance_tracks_current_version_and_marketing_is_independent()
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var tenant = await SeedTenantAsync(factory, "LegalReaccept");
            using (var sessionRequest = Request(HttpMethod.Get, "/api/auth/session", tenant.Admin.Id, tenant.Session.Id))
            using (var sessionResponse = await client.SendAsync(sessionRequest, ct))
            {
                Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);
                Assert.Contains("legalConsentRequired", await sessionResponse.Content.ReadAsStringAsync(ct));
            }
            using (var rejected = await client.SendAsync(Request(HttpMethod.Post, "/api/auth/legal/accept",
                tenant.Admin.Id, tenant.Session.Id, json: new { acceptedTerms = false, legalVersion = 1 }), ct))
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using (var stale = await client.SendAsync(Request(HttpMethod.Post, "/api/auth/legal/accept",
                tenant.Admin.Id, tenant.Session.Id, json: new { acceptedTerms = true, legalVersion = 999 }), ct))
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using (var accepted = await client.SendAsync(Request(HttpMethod.Post, "/api/auth/legal/accept",
                tenant.Admin.Id, tenant.Session.Id, json: new { acceptedTerms = true, legalVersion = 1 }), ct))
                Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
            using (var changed = await client.SendAsync(Request(HttpMethod.Put, "/api/me/preferences/marketing",
                tenant.Admin.Id, tenant.Session.Id, json: new { marketingEmailOptIn = true }), ct))
                Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
            await factory.WithDbAsync(async db =>
            {
                var consent = await db.Database.SqlQueryRaw<int>(
                    "SELECT \"AcceptedLegalVersion\" AS \"Value\" FROM identity.users WHERE \"Id\" = {0}", tenant.Admin.Id).SingleAsync(ct);
                var marketing = await db.Database.SqlQueryRaw<bool>(
                    "SELECT \"MarketingEmailOptIn\" AS \"Value\" FROM identity.users WHERE \"Id\" = {0}", tenant.Admin.Id).SingleAsync(ct);
                Assert.Equal(1, consent);
                Assert.True(marketing);
            });
        });
    }
}
