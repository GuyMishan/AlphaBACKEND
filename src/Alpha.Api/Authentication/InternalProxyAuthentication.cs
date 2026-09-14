using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Alpha.Api.Authentication;

public sealed class InternalProxyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configuredSecret = configuration["ALPHA_INTERNAL_PROXY_SECRET"];
        if (string.IsNullOrWhiteSpace(configuredSecret) || configuredSecret.Length < 32)
            return Task.FromResult(AuthenticateResult.Fail("Internal proxy authentication is not configured."));

        if (!Request.Headers.TryGetValue("X-Alpha-Internal-Secret", out var suppliedSecret) || suppliedSecret.ToString() != configuredSecret)
            return Task.FromResult(AuthenticateResult.Fail("Invalid internal proxy secret."));

        if (!Request.Headers.TryGetValue("X-User-Id", out var userIdValue) || !Guid.TryParse(userIdValue, out var userId))
            return Task.FromResult(AuthenticateResult.Fail("A valid user id is required."));

        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (Request.Headers.TryGetValue("X-Platform-Admin", out var admin) && admin.ToString() == "true")
            claims.Add(new Claim("alpha:platform_admin", "true"));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
    }
}
