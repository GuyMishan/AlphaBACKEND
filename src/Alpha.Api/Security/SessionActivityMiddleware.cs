using System.Security.Claims;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Api.Security;

/// <summary>
/// Validates the server-side session behind every authenticated request. A token is accepted only while
/// its session exists for the same user, is not revoked, expired or idle, the user is active, and any
/// platform-admin claim is still backed by the database. Otherwise the session is revoked and the
/// request ends with 401 and <c>X-Alpha-Session-Expired</c>, forcing re-authentication.
/// </summary>
public sealed class SessionActivityMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public async Task InvokeAsync(HttpContext context, AlphaDbContext db, IHostEnvironment environment)
    {
        if (context.User.Identity?.IsAuthenticated != true) { await next(context); return; }
        var sidValue = context.User.FindFirstValue("alpha:session_id");
        var uidValue = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? context.User.FindFirstValue("sub");
        if (!Guid.TryParse(sidValue, out var sid) || !Guid.TryParse(uidValue, out var uid))
        { context.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }

        var session = await db.UserSessions.SingleOrDefaultAsync(x => x.Id == sid && x.UserId == uid, context.RequestAborted);
        var now = DateTime.UtcNow;
        // Platform-admin status is a claim fixed at sign-in; a demotion must take effect on the next request.
        var claimsPlatformAdmin = context.User.HasClaim("alpha:platform_admin", "true");
        var userValid = session is not null && await db.Users.AsNoTracking()
            .AnyAsync(x => x.Id == uid && x.IsActive && (!claimsPlatformAdmin || x.IsPlatformAdmin), context.RequestAborted);
        if (session is null || !userValid || session.RevokedAt != null || session.ExpiresAt <= now || session.LastActivityAt <= now.Subtract(IdleTimeout))
        {
            if (session is not null && session.RevokedAt is null) { session.Revoke(now); await db.SaveChangesAsync(context.RequestAborted); }
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["X-Alpha-Session-Expired"] = "true";
            return;
        }

        if (now - session.LastActivityAt >= TouchInterval)
        { session.TouchActivity(now); await db.SaveChangesAsync(context.RequestAborted); }
        // Production-only admission check. Existing sessions and fresh OTP sign-ins share this gate.
        // The consent/session and logout endpoints remain available so the user can accept or leave.
        if (environment.IsProduction() && !context.Request.Path.StartsWithSegments("/api/auth/legal/accept")
            && !context.Request.Path.StartsWithSegments("/api/auth/session")
            && !context.Request.Path.StartsWithSegments("/api/auth/logout")
            && !context.Request.Path.StartsWithSegments("/api/legal-document")
            && !context.Request.Path.StartsWithSegments("/health"))
        {
            var required = await db.Database.SqlQueryRaw<int>(
                """
                SELECT version AS "Value" FROM reference_data.legal_document_versions
                WHERE status='published' AND effective_at <= now() ORDER BY version DESC LIMIT 1
                """).ToListAsync(context.RequestAborted);
            if (required.Count > 0)
            {
                var accepted = await db.Database.SqlQueryRaw<int?>(
                    "SELECT \"AcceptedLegalVersion\" AS \"Value\" FROM identity.users WHERE \"Id\" = {0}", uid)
                    .ToListAsync(context.RequestAborted);
                if (accepted.FirstOrDefault() != required[0])
                {
                    context.Response.StatusCode = StatusCodes.Status409Conflict;
                    await context.Response.WriteAsJsonAsync(new { error = "legal_consent_required" }, context.RequestAborted);
                    return;
                }
            }
        }
        await next(context);
    }
}
