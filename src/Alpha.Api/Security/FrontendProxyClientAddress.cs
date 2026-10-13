using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Alpha.Api.Security;

/// <summary>
/// Restores the browser's address for requests relayed by the Alpha frontend server (Next.js route handler).
/// That server calls the API from shared, changing egress addresses, so neither the TCP peer nor
/// <c>X-Forwarded-For</c> identifies the user. The frontend proves it is the caller with a shared secret
/// (<see cref="SecretHeader"/>) and states the client address it received from its own edge
/// (<see cref="ClientAddressHeader"/>). Without the exact secret the stated address is ignored, so callers cannot
/// choose their rate-limit bucket or audit IP. Off unless <c>FrontendProxy:SharedSecret</c> is configured.
/// </summary>
public static class FrontendProxyClientAddress
{
    public const string SectionName = "FrontendProxy";
    public const string SecretHeader = "X-Alpha-Proxy-Secret";
    public const string ClientAddressHeader = "X-Alpha-Client-IP";
    internal const int MinimumSecretBytes = 32;

    public static IApplicationBuilder UseAlphaFrontendProxyClientAddress(this IApplicationBuilder app, IConfiguration configuration)
    {
        var secret = configuration[$"{SectionName}:SharedSecret"];
        if (string.IsNullOrEmpty(secret)) return app;

        // Hashing gives both sides a fixed length, so the comparison time reveals nothing about the secret.
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        return app.Use(async (context, next) =>
        {
            var headers = context.Request.Headers;
            if (headers.TryGetValue(SecretHeader, out var supplied) && supplied.Count == 1
                && CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(supplied[0] ?? "")))
                && headers.TryGetValue(ClientAddressHeader, out var stated) && stated.Count == 1
                && IPAddress.TryParse(stated[0], out var address))
                context.Connection.RemoteIpAddress = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

            // Nothing downstream may read the secret or an unverified address.
            headers.Remove(SecretHeader);
            headers.Remove(ClientAddressHeader);
            await next(context);
        });
    }
}
