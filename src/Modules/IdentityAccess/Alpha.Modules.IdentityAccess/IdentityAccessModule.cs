using Alpha.Modules.IdentityAccess.Application.Authorization;
using Alpha.Modules.IdentityAccess.Application.Invitations;
using Alpha.Modules.IdentityAccess.Infrastructure.Delivery;
using Alpha.Modules.IdentityAccess.Infrastructure.Retention;
using Alpha.Modules.IdentityAccess.Features.AccessManagement;
using Alpha.Modules.IdentityAccess.Features.Authentication;
using Alpha.Modules.IdentityAccess.Features.Invitations;
using Alpha.Modules.IdentityAccess.Features.PlatformAdministration;
using Alpha.Modules.IdentityAccess.Features.Referents;
using Alpha.Modules.IdentityAccess.Features.Scope;
using Alpha.Modules.IdentityAccess.Features.UserPreferences;
using Microsoft.Extensions.DependencyInjection;

namespace Alpha.Modules.IdentityAccess;

public static class IdentityAccessModule
{
    public static IServiceCollection AddIdentityAccessModule(this IServiceCollection services)
    {
        services.AddScoped<OrganizationAccessService>();
        services.AddScoped<IOrganizationAuthorization>(provider =>
            provider.GetRequiredService<OrganizationAccessService>());
        services.AddScoped<InvitationService>();
        services.AddScoped<OtpDelivery>();
        services.AddHttpClient("otp-sms", client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddHttpClient("otp-email", client => client.Timeout = TimeSpan.FromSeconds(25));
        services.AddHostedService<SecurityRetentionHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityAccessModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAuthEndpoints();
        endpoints.MapScopeEndpoints();
        endpoints.MapInvitationEndpoints();
        endpoints.MapReferentEndpoints();
        endpoints.MapAccessEndpoints();
        endpoints.MapUserPreferencesEndpoints();
        return endpoints;
    }

    /// <summary>Platform user administration, attached to the host-owned <c>/api/platform</c> group.</summary>
    public static RouteGroupBuilder MapIdentityAccessPlatformEndpoints(this RouteGroupBuilder platform) =>
        platform.MapPlatformUserEndpoints();
}
