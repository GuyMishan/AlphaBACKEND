using Alpha.Modules.Workforce.Features.EmployeePensionMix;
using Alpha.Modules.Workforce.Features.EmployerProfile;
using Alpha.Modules.Workforce.Features.EmployerManagement;
using Microsoft.AspNetCore.Routing;

namespace Alpha.Modules.Workforce;

public static class WorkforceModule
{
    public static IEndpointRouteBuilder MapWorkforceModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapEmployeePensionMixEndpoints();
        endpoints.MapEmployerProfileCenterEndpoints();
        endpoints.MapEmployerEndpoints();
        return endpoints;
    }
}
