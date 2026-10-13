using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Alpha.Api.Security;

public static class RateLimitingSetup
{
    internal const int CredentialPermitLimit = 20;
    internal const int GeneralPermitLimit = 1200;

    /// <summary>
    /// Fixed one-minute windows: 20 requests per client for the anonymous credential endpoints under <c>/api/auth</c>
    /// (OTP and registration request/verify, and any future one), 1200 per authenticated user elsewhere, including
    /// <c>/api/auth/session</c> and <c>/api/auth/logout</c>, and 1200 per client for other anonymous traffic.
    /// The client is the address resolved by <see cref="ReverseProxySetup"/>; IPv6 clients are grouped by /64 because
    /// one subscriber usually controls a whole /64 and could otherwise open a new bucket per address.
    /// </summary>
    public static IServiceCollection AddAlphaRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return ValueTask.CompletedTask;
            };
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var isCredential = IsCredentialEndpoint(context.Request.Path);
                var client = ClientKey(context.Connection.RemoteIpAddress);
                var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst("sub")?.Value;
                var partitionKey = isCredential ? $"auth:{client}" : !string.IsNullOrWhiteSpace(userId) ? $"user:{userId}" : $"anon:{client}";
                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = isCredential ? CredentialPermitLimit : GeneralPermitLimit,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });

    /// <summary>
    /// Everything under <c>/api/auth</c> except the session check and logout, which require a session and are limited
    /// per user. New auth endpoints are therefore strict unless deliberately listed here.
    /// </summary>
    internal static bool IsCredentialEndpoint(PathString path) =>
        path.StartsWithSegments("/api/auth")
        && !path.Equals("/api/auth/session", StringComparison.OrdinalIgnoreCase)
        && !path.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase);

    internal static string ClientKey(IPAddress? address)
    {
        if (address is null) return "unknown";
        if (address.IsIPv4MappedToIPv6) return address.MapToIPv4().ToString();
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
