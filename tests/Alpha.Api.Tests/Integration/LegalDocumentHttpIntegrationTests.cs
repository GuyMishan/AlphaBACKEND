using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alpha.Api.Tests.TestInfrastructure;
using Alpha.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Alpha.Api.Tests.Integration;

public sealed class LegalDocumentHttpIntegrationTests
{
    [Fact]
    public async Task Draft_publication_public_history_and_admin_guards()
    {
        await AlphaApiTestHost.RunAsync(async (factory, client, ct) =>
        {
            using var initial = await client.GetAsync("/api/legal-document/active", ct);
            Assert.Equal(HttpStatusCode.OK, initial.StatusCode);
            using (var initialBody = JsonDocument.Parse(await initial.Content.ReadAsStringAsync(ct)))
                Assert.Equal(1, initialBody.RootElement.GetProperty("version").GetInt32());

            var admin = new User("legal-admin", "legal-admin@test.invalid", "Legal Administrator");
            admin.SetPlatformAdmin(true);
            var viewer = new User("legal-viewer", "legal-viewer@test.invalid", "Legal Viewer");
            var now = DateTime.UtcNow;
            var session = new UserSession(admin.Id, now, now.AddHours(1));
            var viewerSession = new UserSession(viewer.Id, now, now.AddHours(1));
            await factory.WithDbAsync(async db =>
            {
                db.AddRange(admin, viewer, session, viewerSession);
                await db.SaveChangesAsync(ct);
            });

            using (var request = AlphaApiTestHost.Request(HttpMethod.Post, "/api/platform/legal-document/draft",
                viewer.Id, viewerSession.Id, json: new { title = "Terms", markdown = "# Hello" }))
            using (var denied = await client.SendAsync(request, ct))
                Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

            using (var request = AlphaApiTestHost.Request(HttpMethod.Post, "/api/platform/legal-document/draft",
                admin.Id, session.Id, platformAdminClaim: true, json: new { title = "Terms", markdown = "<script>alert(1)</script>" }))
            using (var rejected = await client.SendAsync(request, ct))
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

            using (var request = AlphaApiTestHost.Request(HttpMethod.Post, "/api/platform/legal-document/draft",
                admin.Id, session.Id, platformAdminClaim: true, json: new { title = "Terms", markdown = "# First" }))
            using (var created = await client.SendAsync(request, ct))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                using var response = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct));
                Assert.Equal(2, response.RootElement.GetProperty("version").GetInt32());
            }
            using (var unpublished = await client.GetAsync("/api/legal-document/versions/2", ct))
                Assert.Equal(HttpStatusCode.NotFound, unpublished.StatusCode);

            using (var request = AlphaApiTestHost.Request(HttpMethod.Post, "/api/platform/legal-document/draft/publish",
                admin.Id, session.Id, platformAdminClaim: true,
                json: new { effectiveAt = DateTimeOffset.UtcNow.AddSeconds(3), requiresReacceptance = true }))
            using (var published = await client.SendAsync(request, ct))
                Assert.Equal(HttpStatusCode.NoContent, published.StatusCode);

            using (var previous = await client.GetAsync("/api/legal-document/versions/2", ct))
            {
                Assert.Equal(HttpStatusCode.OK, previous.StatusCode);
                using var body = JsonDocument.Parse(await previous.Content.ReadAsStringAsync(ct));
                Assert.Equal("# First", body.RootElement.GetProperty("markdown").GetString());
                Assert.True(body.RootElement.GetProperty("requiresReacceptance").GetBoolean());
            }
            using (var request = AlphaApiTestHost.Request(HttpMethod.Put, "/api/platform/legal-document/draft",
                admin.Id, session.Id, platformAdminClaim: true, json: new { title = "Changed", markdown = "# Changed" }))
            using (var notEditable = await client.SendAsync(request, ct))
                Assert.Equal(HttpStatusCode.NotFound, notEditable.StatusCode);

            using (var request = AlphaApiTestHost.Request(HttpMethod.Post, "/api/platform/legal-document/draft",
                admin.Id, session.Id, platformAdminClaim: true, json: new { title = "Terms 2", markdown = "# Second" }))
            using (var created = await client.SendAsync(request, ct))
            {
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(ct));
                Assert.Equal(3, body.RootElement.GetProperty("version").GetInt32());
            }

            await factory.WithDbAsync(async db =>
            {
                // Re-running the additive initializer must not rewrite the published version.
                await Alpha.Infrastructure.Persistence.LegalDocumentSchemaInitializer.EnsureCreatedAsync(db);
                await Alpha.Infrastructure.Persistence.LegalDocumentSchemaInitializer.PublishVersionOneIfEmptyAsync(db);
                Assert.Equal(3, await db.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::integer AS \"Value\" FROM reference_data.legal_document_versions").SingleAsync(ct));
            });
        });
    }
}
