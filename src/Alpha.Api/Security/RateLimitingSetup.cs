using System.Security.Claims;
using System.Threading.RateLimiting;

namespace Alpha.Api.Security;

public static class RateLimitingSetup
{
    /// <summary>
    /// Fixed one-minute windows: 20 requests per client IP for <c>/api/auth</c> (OTP and registration),
    /// 1200 per authenticated user elsewhere, 1200 per IP for other anonymous traffic.
    /// </summary>
    public static IServiceCollection AddAlphaRateLimiting(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var isAuth = context.Request.Path.StartsWithSegments("/api/auth");
                var ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? context.User.FindFirst("sub")?.Value;
                var partitionKey = isAuth ? $"auth:{ip}" : !string.IsNullOrWhiteSpace(userId) ? $"user:{userId}" : $"anon:{ip}";
                return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = isAuth ? 20 : 1200,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                });
            });
        });
}
