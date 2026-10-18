using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alpha.Api.Tests.TestInfrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;

namespace Alpha.Api.Tests.IdentityAccess;

public sealed class RegistrationConsentIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Registration_requires_terms_and_persists_separate_marketing_consent(bool marketing)
    {
        var mail = new RecordingMailGateway();
        var settings = new Dictionary<string, string?>(RecordingMailGateway.Settings)
        {
            ["PrototypeAuth:SigningKey"] = new string('k', 48)
        };
        await RunAsync(async (factory, client, ct) =>
        {
            await factory.WithDbAsync(async db =>
            {
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO reference_data.legal_document_versions
                    (id, version, title, markdown, status, requires_reacceptance, created_at, published_at, effective_at)
                    VALUES (gen_random_uuid(), 1, 'Terms', '# Terms', 'published', false, now(), now(), now())
                    """, ct);
            });

            using (var rejected = await client.PostAsJsonAsync("/api/auth/register/request",
                new { displayName = "Consent User", email = "consent@test.invalid", nationalId = "000000018",
                    phone = "0547654321", acceptedTerms = false, legalVersion = 1, marketingEmailOptIn = marketing }, ct))
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

            using var requested = await client.PostAsJsonAsync("/api/auth/register/request",
                new { displayName = "Consent User", email = "consent@test.invalid", nationalId = "000000018",
                    phone = "0547654321", acceptedTerms = true, legalVersion = 1, marketingEmailOptIn = marketing }, ct);
            Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
            using var requestBody = JsonDocument.Parse(await requested.Content.ReadAsStringAsync(ct));
            var challenge = requestBody.RootElement.GetProperty("challengeId").GetGuid();
            using var deliveredBody = JsonDocument.Parse(Assert.Single(mail.Bodies));
            var code = deliveredBody.RootElement.GetProperty("code").GetString();
            using var completed = await client.PostAsJsonAsync("/api/auth/register/verify", new { challengeId = challenge, code }, ct);
            Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
            using var completedBody = JsonDocument.Parse(await completed.Content.ReadAsStringAsync(ct));
            var userId = completedBody.RootElement.GetProperty("userId").GetGuid();

            await factory.WithDbAsync(async db =>
            {
                var version = await db.Database.SqlQueryRaw<int>(
                    "SELECT \"AcceptedLegalVersion\" AS \"Value\" FROM identity.users WHERE \"Id\" = {0}", userId).SingleAsync(ct);
                var marketingStored = await db.Database.SqlQueryRaw<bool>(
                    "SELECT \"MarketingEmailOptIn\" AS \"Value\" FROM identity.users WHERE \"Id\" = {0}", userId).SingleAsync(ct);
                var count = await db.Database.SqlQueryRaw<int>(
                    "SELECT count(*)::integer AS \"Value\" FROM identity.user_consent_events WHERE user_id = {0}", userId).SingleAsync(ct);
                Assert.Equal(1, version);
                Assert.Equal(marketing, marketingStored);
                Assert.Equal(2, count);
            });
        }, settings, mail.Install);
    }
}
