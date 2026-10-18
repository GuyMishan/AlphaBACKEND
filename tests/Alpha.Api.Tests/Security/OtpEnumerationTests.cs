using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Alpha.Domain.Identity;
using Alpha.Infrastructure.Persistence;
using Alpha.Modules.IdentityAccess.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Alpha.Api.Tests.TestInfrastructure;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;

namespace Alpha.Api.Tests.Security;

/// <summary>
/// Login rejects an unknown national ID/phone pair with a generic error, without identifying the incorrect field.
/// Known accounts still receive the same neutral success response during resend cooldown and hourly limits.
/// </summary>
public sealed class OtpEnumerationTests
{
    private const string NationalId = "123456782";
    private const string Phone = "0521234567";

    private static readonly Dictionary<string, string?> OtpSettings = new(RecordingMailGateway.Settings)
    {
        ["PrototypeAuth:SigningKey"] = new string('k', 48)
    };

    [Fact]
    public async Task Resend_within_the_cooldown_returns_success_and_unknown_account_is_rejected()
    {
        var mail = new RecordingMailGateway();
        await RunAsync(async (factory, client, ct) =>
        {
            await SeedAccountAsync(factory);

            var first = await RequestOtpAsync(client, NationalId, Phone, "10.1.0.1", ct);
            var resend = await RequestOtpAsync(client, NationalId, Phone, "10.1.0.2", ct);
            var unknown = await RequestOtpAsync(client, "987654324", "0539876543", "10.1.0.3", ct);

            Assert.Equal(HttpStatusCode.OK, first.Status);
            Assert.Equal(HttpStatusCode.OK, resend.Status);
            Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
            Assert.Equal(first.ChallengeId, resend.ChallengeId);
            Assert.Equal(1, mail.Deliveries);
        }, OtpSettings, mail.Install);
    }

    [Fact]
    public async Task Hourly_cap_returns_success_but_unknown_account_is_rejected()
    {
        var mail = new RecordingMailGateway();
        await RunAsync(async (factory, client, ct) =>
        {
            var userId = await SeedAccountAsync(factory);
            await factory.WithDbAsync(async db =>
            {
                // Five codes this hour, all past the 60-second cooldown and already expired.
                for (var i = 1; i <= 5; i++)
                    db.OtpChallenges.Add(new OtpChallenge
                    {
                        Id = Guid.NewGuid(), UserId = userId, Channel = "email", CodeHash = "test",
                        CreatedAt = DateTime.UtcNow.AddMinutes(-10 * i), ExpiresAt = DateTime.UtcNow.AddMinutes(-10 * i + 5)
                    });
                await db.SaveChangesAsync(ct);
            });

            var capped = await RequestOtpAsync(client, NationalId, Phone, "10.2.0.1", ct);
            var unknown = await RequestOtpAsync(client, "987654324", "0539876543", "10.2.0.2", ct);

            Assert.Equal(HttpStatusCode.OK, capped.Status);
            Assert.Equal(HttpStatusCode.BadRequest, unknown.Status);
            Assert.Equal(0, mail.Deliveries);
        }, OtpSettings, mail.Install);
    }

    [Fact]
    public async Task Registration_resend_answers_the_same_for_new_and_existing_national_ids()
    {
        var mail = new RecordingMailGateway();
        await RunAsync(async (factory, client, ct) =>
        {
            await SeedAccountAsync(factory);
            // An existing national ID is answered with a neutral 200 before any cooldown is checked.
            var existing = new[]
            {
                await RequestRegistrationAsync(client, NationalId, "10.3.0.1", ct),
                await RequestRegistrationAsync(client, NationalId, "10.3.0.2", ct)
            };
            var fresh = new[]
            {
                await RequestRegistrationAsync(client, "000000018", "10.3.0.3", ct),
                await RequestRegistrationAsync(client, "000000018", "10.3.0.4", ct)
            };

            Assert.All(existing.Concat(fresh), r => Assert.Equal(HttpStatusCode.OK, r.Status));
            Assert.Equal(existing[1].Members, fresh[1].Members);
            Assert.Equal(fresh[0].ChallengeId, fresh[1].ChallengeId);
            Assert.Equal(1, mail.Deliveries);
        }, OtpSettings, mail.Install);
    }

    private static Task<OtpResponse> RequestRegistrationAsync(HttpClient client, string nationalId, string peer, CancellationToken ct) =>
        PostAsync(client, "/api/auth/register/request",
            new { displayName = "New User", email = "applicant@test.invalid", nationalId, phone = "0547654321", acceptedTerms = true, legalVersion = 1, marketingEmailOptIn = false }, peer, ct);

    private static async Task<Guid> SeedAccountAsync(Factory factory)
    {
        var protector = factory.Services.GetRequiredService<IDataProtectionService>();
        var user = new User($"otp-{Guid.NewGuid():N}", "otp-user@test.invalid", "OTP User", NationalId, Phone);
        user.SetProtectedIdentity(
            protector.Protect(NationalId, "auth-national-id"), protector.LookupHash(NationalId, "auth-national-id-lookup"),
            protector.Protect(Phone, "auth-phone"), protector.LookupHash(Phone, "auth-phone-lookup"));
        await factory.WithDbAsync(async db =>
        {
            db.Add(user);
            await db.SaveChangesAsync();
        });
        return user.Id;
    }

    private static Task<OtpResponse> RequestOtpAsync(HttpClient client, string nationalId, string phone, string peer, CancellationToken ct) =>
        PostAsync(client, "/api/auth/otp/request", new { nationalId, phone, channel = "email" }, peer, ct);

    private static async Task<OtpResponse> PostAsync(HttpClient client, string path, object payload, string peer, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(payload) };
        request.Headers.Add("X-Test-Peer", peer);
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode != HttpStatusCode.OK) return new OtpResponse(response.StatusCode, [], null);
        using var json = JsonDocument.Parse(body);
        var members = json.RootElement.EnumerateObject()
            .Select(p => p.Name == "challengeId" ? p.Name : $"{p.Name}={p.Value}")
            .Order(StringComparer.Ordinal).ToArray();
        return new OtpResponse(response.StatusCode, members, json.RootElement.GetProperty("challengeId").GetGuid());
    }

    private sealed record OtpResponse(HttpStatusCode Status, string[] Members, Guid? ChallengeId);
}
