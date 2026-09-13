using System.Security.Claims;
using Alpha.Application.Abstractions;

namespace Alpha.Api.Authentication;

public sealed class CurrentUser(IHttpContextAccessor contextAccessor) : ICurrentUser
{
    private ClaimsPrincipal User => contextAccessor.HttpContext?.User ?? new ClaimsPrincipal();
    public bool IsAuthenticated => User.Identity?.IsAuthenticated == true;
    public Guid UserId
    {
        get
        {
            var value = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("sub");
            return Guid.TryParse(value, out var id) ? id
                : throw new UnauthorizedAccessException("The authenticated subject must map to an Alpha user UUID.");
        }
    }
    public bool IsPlatformAdmin => User.HasClaim("alpha:platform_admin", "true");
}
