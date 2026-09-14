using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Alpha.Api.Endpoints;

public sealed record PrototypeLoginRequest(string NationalId, string Phone);

public static class AuthEndpoints
{
    private static readonly Guid PrototypeUserId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/auth/prototype-login", (PrototypeLoginRequest request, IConfiguration configuration) =>
        {
            var expectedNationalId = configuration["PrototypeAuth:NationalId"] ?? "123456789";
            var expectedPhone = configuration["PrototypeAuth:Phone"] ?? "0501234567";
            if (request.NationalId != expectedNationalId || request.Phone != expectedPhone)
                return Results.Unauthorized();

            var signingKey = configuration["PrototypeAuth:SigningKey"];
            if (string.IsNullOrWhiteSpace(signingKey) || signingKey.Length < 32)
                return Results.Problem("Prototype authentication is not configured.", statusCode: 503);

            var now = DateTime.UtcNow;
            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, PrototypeUserId.ToString()),
                new Claim("alpha:platform_admin", "true")
            };
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
                userId = PrototypeUserId,
                platformAdmin = true,
                displayName = "מנהל מערכת"
            });
        }).AllowAnonymous().WithTags("Authentication");

        return endpoints;
    }
}
