using Alpha.Modules.ReferenceData.Features.Administration;
using Alpha.Modules.ReferenceData.Application;
using Alpha.Modules.ReferenceData.Features.PublicCatalogs;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Alpha.Modules.ReferenceData;

public static class ReferenceDataModule
{
    public static IServiceCollection AddReferenceDataModule(this IServiceCollection services)
    {
        services.AddHttpClient<ReferenceDataSyncService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("AlphaReferenceDataSync/1.0");
        });
        return services;
    }

    public static IEndpointRouteBuilder MapReferenceDataModule(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapReferenceDataEndpoints();
    }

    public static RouteGroupBuilder MapGeneralReferenceData(this RouteGroupBuilder group) =>
        group.MapGeneralReferenceDataEndpoints();
}
