using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Alpha.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Alpha.Api.Endpoints;

public sealed record PrototypeLoginRequest(string NationalId, string Phone);

public static class AuthEndpoints
{
    private static readonly Guid PrototypeUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/auth/prototype-login", async (
            PrototypeLoginRequest request,
            IConfiguration configuration,
            IAlphaDbContext db,
            CancellationToken ct) =>
        {
            var nationalId = request.NationalId.Trim();
            var phone = request.Phone.Trim();

            if (IsIsraeliId(nationalId) && IsIsraeliMobile(phone))
            {
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.IsActive && x.NationalId == nationalId && x.Phone == phone, ct);

                if (user is not null)
                    return CreateTokenResult(configuration, user.Id, user.DisplayName, platformAdmin: false);
            }

            // Keep the prototype platform-admin login available for operational access,
            // but do not expose its credentials in the UI.
            var expectedNationalId = configuration["PrototypeAuth:NationalId"] ?? "123456789";
            var expectedPhone = configuration["PrototypeAuth:Phone"] ?? "0501234567";
            if (nationalId == expectedNationalId && phone == expectedPhone)
                return CreateTokenResult(configuration, PrototypeUserId, "מנהל מערכת", platformAdmin: true);

            return Results.Unauthorized();
        }).AllowAnonymous().WithTags("Authentication");

        return endpoints;
    }

    private static IResult CreateTokenResult(IConfiguration configuration, Guid userId, string displayName, bool platformAdmin)
    {
        var signingKey = configuration["PrototypeAuth:SigningKey"];
        if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32)
            return Results.Problem("Prototype authentication is not configured.", statusCode: 503);

        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString())
        };
        if (platformAdmin)
            claims.Add(new Claim("alpha:platform_admin", "true"));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: "alpha-prototype",
            audience: "alpha-frontend",
            claims: claims,
            notBefore: now,
            expires: now.AddHours(12),
            signingCredentials: credentials);

        return Results.Ok(new
        {
            accessToken = new JwtSecurityTokenHandler().WriteToken(token),
            userId,
            platformAdmin,
            displayName
        });
    }

    private static bool IsIsraeliMobile(string value) =>
        value.Length == 10 && value.StartsWith("05", StringComparison.Ordinal) && value.All(char.IsDigit);

    private static bool IsIsraeliId(string value)
    {
        if (value.Length is < 1 or > 9 || !value.All(char.IsDigit)) return false;
        var id = value.PadLeft(9, '0');
        var sum = 0;
        for (var i = 0; i < id.Length; i++)
        {
            var number = (id[i] - '0') * (i % 2 == 0 ? 1 : 2);
            sum += number > 9 ? number - 9 : number;
        }
        return sum % 10 == 0;
    }
}
