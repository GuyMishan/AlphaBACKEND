using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace Alpha.Api.Authentication;

public static class AuthenticationSetup
{
    /// <summary>
    /// Development uses header-based identities. Every other environment validates JWTs: either the
    /// Alpha-issued prototype token (symmetric key) or tokens from the configured external authority.
    /// Session validity is enforced afterwards by <c>SessionActivityMiddleware</c>.
    /// </summary>
    public static IServiceCollection AddAlphaAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        if (environment.IsDevelopment())
        {
            services.AddAuthentication("DevelopmentHeaders")
                .AddScheme<AuthenticationSchemeOptions, DevelopmentHeaderAuthenticationHandler>("DevelopmentHeaders", null);
        }
        else
        {
            services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
            {
                var key = configuration["PrototypeAuth:SigningKey"];
                if (!string.IsNullOrWhiteSpace(key))
                {
                    options.TokenValidationParameters = new TokenValidationParameters
                    {
                        ValidateIssuer = true,
                        ValidIssuer = "alpha-prototype",
                        ValidateAudience = true,
                        ValidAudience = "alpha-frontend",
                        ValidateIssuerSigningKey = true,
                        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)),
                        ValidateLifetime = true,
                        ClockSkew = TimeSpan.FromMinutes(1)
                    };
                }
                else
                {
                    options.Authority = configuration["Authentication:Authority"];
                    options.Audience = configuration["Authentication:Audience"];
                }
            });
        }

        // Default-deny: an endpoint that declares neither RequireAuthorization nor AllowAnonymous
        // still requires an authenticated user. Resource-level checks remain in each use case.
        services.AddAuthorization(options => options.FallbackPolicy =
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        return services;
    }
}
