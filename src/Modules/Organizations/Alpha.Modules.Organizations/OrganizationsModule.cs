using Alpha.Modules.Organizations.Features.Dashboard;
using Alpha.Modules.Organizations.Features.EmployerTransfers;
using Alpha.Modules.Organizations.Features.Management;
using Alpha.Modules.Organizations.Features.Onboarding;
using Alpha.Modules.Organizations.Features.ProfileCenter;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Alpha.Modules.Organizations;

public static class OrganizationsModule
{
    public static IServiceCollection AddOrganizationsModule(this IServiceCollection services)
    {
        services.AddScoped<EmployerTransferService>();
        return services;
    }

    public static IEndpointRouteBuilder MapOrganizationsModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapDashboardEndpoints();
        endpoints.MapOrganizationEndpoints();
        endpoints.MapOrganizationProfileEndpoints();
        endpoints.MapOnboardingEndpoints();
        return endpoints;
    }
}
