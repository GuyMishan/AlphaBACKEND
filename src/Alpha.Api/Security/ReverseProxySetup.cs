using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Alpha.Api.Security;

/// <summary>
/// Restores the real client address behind a reverse proxy (rate limiting, audit IP, HTTPS scheme).
/// Disabled by default. When enabled, only <c>X-Forwarded-For</c>/<c>X-Forwarded-Proto</c> sent by the
/// configured trusted networks/proxies are honored, and at most <c>ForwardLimit</c> rightmost entries are
/// consumed, so values a client prepends to <c>X-Forwarded-For</c> are never trusted.
/// </summary>
public static class ReverseProxySetup
{
    public const string SectionName = "ReverseProxy";

    /// <summary>Must run before every middleware that reads the client address or scheme.</summary>
    public static IApplicationBuilder UseAlphaReverseProxy(this IApplicationBuilder app, IConfiguration configuration, ILogger logger)
    {
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED makes the framework trust forwarded headers from every address.
        if (configuration.GetValue("FORWARDEDHEADERS_ENABLED", false))
            logger.LogCritical("ASPNETCORE_FORWARDEDHEADERS_ENABLED is set; it trusts X-Forwarded-For from any client. " +
                               "Remove it and configure the ReverseProxy section instead.");

        var section = configuration.GetSection(SectionName);
        if (!section.GetValue("Enabled", false)) return app;

        var options = BuildOptions(section);
        var diagnosticRequests = Math.Clamp(section.GetValue("DiagnosticRequestCount", 0), 0, 100);
        if (diagnosticRequests > 0)
        {
            // Temporary topology check: logs the raw peer and forwarding headers of the next N requests so the
            // operator can confirm hop count and proxy addresses. Off by default; disable after verification.
            var remaining = diagnosticRequests;
            app.Use(async (context, next) =>
            {
                if (Interlocked.Decrement(ref remaining) >= 0)
                    logger.LogWarning("Reverse-proxy diagnostic: peer {Peer}, X-Forwarded-For entries {Entries}, X-Forwarded-Proto {Proto}",
                        context.Connection.RemoteIpAddress?.ToString(),
                        string.Join(" | ", context.Request.Headers["X-Forwarded-For"].ToString().Split(',', StringSplitOptions.TrimEntries)),
                        context.Request.Headers["X-Forwarded-Proto"].ToString());
                await next(context);
            });
        }

        return app.UseForwardedHeaders(options);
    }

    internal static ForwardedHeadersOptions BuildOptions(IConfigurationSection section)
    {
        var networks = section.GetSection("KnownNetworks").Get<string[]>() ?? [];
        var proxies = section.GetSection("KnownProxies").Get<string[]>() ?? [];
        if (networks.Length == 0 && proxies.Length == 0)
            throw new InvalidOperationException(
                "ReverseProxy:Enabled requires ReverseProxy:KnownNetworks or ReverseProxy:KnownProxies. " +
                "Trusting forwarded headers from any address would let clients spoof their IP.");

        var forwardLimit = section.GetValue("ForwardLimit", 1);
        if (forwardLimit is < 1 or > 5)
            throw new InvalidOperationException("ReverseProxy:ForwardLimit must be between 1 and 5.");

        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = forwardLimit,
            // Proto may be absent on some hops; the client address must still be resolved.
            RequireHeaderSymmetry = false
        };
        // Replace the framework defaults (loopback) with exactly the configured trust boundary.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var value in networks)
            options.KnownIPNetworks.Add(System.Net.IPNetwork.TryParse(value.Trim(), out var network)
                ? network
                : throw new InvalidOperationException($"ReverseProxy:KnownNetworks contains an invalid CIDR '{value}'."));
        foreach (var value in proxies)
            options.KnownProxies.Add(IPAddress.TryParse(value.Trim(), out var address)
                ? address
                : throw new InvalidOperationException($"ReverseProxy:KnownProxies contains an invalid address '{value}'."));
        return options;
    }
}
