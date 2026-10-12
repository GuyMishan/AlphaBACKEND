using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Alpha.Api.Authentication;

/// <summary>
/// Development-only identity from headers: <c>X-User-Id</c>, <c>X-Session-Id</c> and optional
/// <c>X-Platform-Admin: true</c>. The session must still exist and be valid for that user;
/// <c>SessionActivityMiddleware</c> enforces it exactly as for real tokens.
/// </summary>
public sealed class DevelopmentHeaderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("X-User-Id", out var value) || !Guid.TryParse(value, out var userId))
            return Task.FromResult(AuthenticateResult.NoResult());
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (Request.Headers.TryGetValue("X-Session-Id", out var session) && Guid.TryParse(session, out var sessionId))
            claims.Add(new Claim("alpha:session_id", sessionId.ToString()));
        if (Request.Headers.TryGetValue("X-Platform-Admin", out var admin) && admin == "true")
            claims.Add(new Claim("alpha:platform_admin", "true"));
        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
