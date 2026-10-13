using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Alpha.Api.Tests.TestInfrastructure.AlphaApiTestHost;
using static Alpha.Api.Tests.TestInfrastructure.TestData;

namespace Alpha.Api.Tests.Security;

/// <summary>
/// Client address resolution behind a reverse proxy. <c>X-Test-Peer</c> simulates the TCP peer (the proxy);
/// <c>X-Forwarded-For</c> is what the proxy, or an attacker, sends.
/// </summary>
public sealed class ReverseProxySecurityTests
{
    private static readonly Dictionary<string, string?> TrustedProxy = new()
    {
        ["ReverseProxy:Enabled"] = "true",
        ["ReverseProxy:KnownNetworks:0"] = "10.0.0.0/8",
        ["ReverseProxy:KnownNetworks:1"] = "fd00::/8",
        ["ReverseProxy:ForwardLimit"] = "1"
    };

    [Theory]
    [InlineData("10.1.2.3", "203.0.113.7", "203.0.113.7")]                 // trusted proxy appended the client
    [InlineData("10.1.2.3", "6.6.6.6, 203.0.113.7", "203.0.113.7")]        // client-prepended value is ignored
    [InlineData("198.51.100.9", "203.0.113.7", "198.51.100.9")]            // untrusted peer: header ignored
    [InlineData("fd00::5", "2001:db8::42", "2001:db8::42")]                // IPv6 proxy and client
    [InlineData("::ffff:10.1.2.3", "203.0.113.8", "203.0.113.8")]          // IPv4-mapped trusted proxy
    public async Task Audit_records_the_client_address_only_through_a_trusted_proxy(string peer, string forwardedFor, string expected)
    {
        await RunAsync(async (factory, client, ct) =>
        {
            Assert.Equal(expected, await AuditedIpAsync(factory, client, peer, forwardedFor, ct));
        }, TrustedProxy);
    }

    [Fact]
    public async Task Forwarded_headers_are_ignored_unless_explicitly_enabled()
    {
        await RunAsync(async (factory, client, ct) =>
            Assert.Equal("10.1.2.3", await AuditedIpAsync(factory, client, "10.1.2.3", "203.0.113.7", ct)));
    }

    [Fact]
    public async Task Clients_behind_the_same_proxy_get_independent_login_rate_limits_that_cannot_be_reset_by_spoofing()
    {
        await RunAsync(async (_, client, ct) =>
        {
            Task<HttpStatusCode> AuthRequest(string forwardedFor) =>
                CredentialRequestAsync(client, ct, ("X-Test-Peer", "10.1.2.3"), ("X-Forwarded-For", forwardedFor));

            for (var i = 0; i < 20; i++)
                Assert.Equal(HttpStatusCode.BadRequest, await AuthRequest("203.0.113.10"));
            Assert.Equal(HttpStatusCode.TooManyRequests, await AuthRequest("203.0.113.10"));
            // Prepending a fresh fake address does not move the client to a new bucket.
            Assert.Equal(HttpStatusCode.TooManyRequests, await AuthRequest("192.0.2.99, 203.0.113.10"));
            // A different user behind the same proxy is unaffected.
            Assert.Equal(HttpStatusCode.BadRequest, await AuthRequest("203.0.113.11"));
            // Addresses of one IPv6 /64 share a bucket; another /64 does not.
            for (var i = 0; i < 20; i++)
                Assert.Equal(HttpStatusCode.BadRequest, await AuthRequest($"2001:db8:1:2::{i + 1:x}"));
            Assert.Equal(HttpStatusCode.TooManyRequests, await AuthRequest("2001:db8:1:2:ffff::99"));
            Assert.Equal(HttpStatusCode.BadRequest, await AuthRequest("2001:db8:1:3::1"));
        }, TrustedProxy);
    }

    private const string FrontendSecret = "frontend-proxy-test-secret-0123456789abcdef";

    [Theory]
    [InlineData(FrontendSecret, "203.0.113.20", "203.0.113.20")]                    // attested by the frontend server
    [InlineData(FrontendSecret, "::ffff:203.0.113.21", "203.0.113.21")]             // IPv4-mapped normalized
    [InlineData("wrong-secret-wrong-secret-wrong-secret-00", "203.0.113.22", "198.51.100.9")] // wrong secret: peer
    [InlineData(null, "203.0.113.23", "198.51.100.9")]                              // no secret: peer
    [InlineData(FrontendSecret, "not-an-ip", "198.51.100.9")]                        // unparsable: peer
    public async Task Frontend_attested_client_address_is_used_only_with_the_shared_secret(string? secret, string clientAddress, string expected)
    {
        await RunAsync(async (factory, client, ct) =>
        {
            var headers = new List<(string, string)> { ("X-Test-Peer", "198.51.100.9"), ("X-Alpha-Client-IP", clientAddress) };
            if (secret is not null) headers.Add(("X-Alpha-Proxy-Secret", secret));
            Assert.Equal(expected, await AuditedIpAsync(factory, client, [.. headers], ct));
        }, new Dictionary<string, string?> { ["FrontendProxy:SharedSecret"] = FrontendSecret });
    }

    [Fact]
    public async Task Users_behind_the_frontend_server_get_independent_login_rate_limits()
    {
        await RunAsync(async (_, client, ct) =>
        {
            Task<HttpStatusCode> ViaFrontend(string clientAddress) => CredentialRequestAsync(client, ct, ("X-Test-Peer", "198.51.100.9"),
                ("X-Alpha-Proxy-Secret", FrontendSecret), ("X-Alpha-Client-IP", clientAddress));

            for (var i = 0; i < 20; i++)
                Assert.Equal(HttpStatusCode.BadRequest, await ViaFrontend("203.0.113.30"));
            Assert.Equal(HttpStatusCode.TooManyRequests, await ViaFrontend("203.0.113.30"));
            Assert.Equal(HttpStatusCode.BadRequest, await ViaFrontend("203.0.113.31"));
        }, new Dictionary<string, string?> { ["FrontendProxy:SharedSecret"] = FrontendSecret });
    }

    [Fact]
    public async Task A_short_frontend_proxy_secret_fails_startup()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync((_, _, _) => Task.CompletedTask,
            new Dictionary<string, string?> { ["FrontendProxy:SharedSecret"] = "too-short" }));
        Assert.Contains("FrontendProxy:SharedSecret", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>An anonymous credential request rejected as invalid input (400) unless rate limited (429).</summary>
    private static async Task<HttpStatusCode> CredentialRequestAsync(HttpClient client, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/otp/verify")
        {
            Content = JsonContent.Create(new { challengeId = Guid.NewGuid(), code = "x" })
        };
        foreach (var (name, value) in headers) request.Headers.Add(name, value);
        using var response = await client.SendAsync(request, ct);
        return response.StatusCode;
    }

    [Fact]
    public async Task Enabling_forwarded_headers_without_a_trust_boundary_fails_startup()
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => RunAsync((_, _, _) => Task.CompletedTask,
            new Dictionary<string, string?> { ["ReverseProxy:Enabled"] = "true" }));
        Assert.Contains("KnownNetworks", error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Sends an audited request (authenticated logout) and returns the IP stored in the audit row.</summary>
    private static Task<string?> AuditedIpAsync(Factory factory, HttpClient client, string peer, string forwardedFor, CancellationToken ct) =>
        AuditedIpAsync(factory, client, [("X-Test-Peer", peer), ("X-Forwarded-For", forwardedFor)], ct);

    private static async Task<string?> AuditedIpAsync(Factory factory, HttpClient client, (string Name, string Value)[] headers, CancellationToken ct)
    {
        var (user, session) = await SeedUserAsync(factory, $"proxy-{Guid.NewGuid():N}", platformAdmin: false);
        using var request = Request(HttpMethod.Post, "/api/auth/logout", user.Id, session.Id);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        string? ip = null;
        await factory.WithDbAsync(async db =>
        {
            var audit = await db.AuditEvents.AsNoTracking()
                .Where(x => x.ActorUserId == user.Id && x.Action == "http.post").SingleAsync(ct);
            ip = JsonDocument.Parse(audit.Data).RootElement.GetProperty("ip").GetString();
        });
        return ip;
    }
}
