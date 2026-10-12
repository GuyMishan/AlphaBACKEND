using Alpha.Modules.Billing.Application;
using Alpha.Modules.Billing.Contracts;
using Alpha.Modules.Billing.Features.Accounts;
using Alpha.Modules.Billing.Features.Management;
using Alpha.Modules.Billing.Features.PaymentMethods;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Alpha.Modules.Billing;

public static class BillingModule
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services)
    {
        services.AddScoped<BillingInheritanceService>();
        services.AddScoped<IBillingInheritanceService>(provider =>
            provider.GetRequiredService<BillingInheritanceService>());
        services.AddScoped<EntitlementService>();
        services.AddScoped<IEntitlementService>(provider =>
            provider.GetRequiredService<EntitlementService>());
        services.AddScoped<BillingGateService>();
        services.AddScoped<ITransmissionBillingGate>(provider =>
            provider.GetRequiredService<BillingGateService>());
        services.AddScoped<IBillingCalculator, BillingCalculator>();
        services.AddScoped<IBillingUsageCollector, BillingUsageCollector>();
        services.AddScoped<IBillingCycleService, BillingCycleService>();
        services.AddHostedService<BillingCycleHostedService>();
        return services;
    }

    public static IEndpointRouteBuilder MapBillingModule(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapBillingManagementEndpoints();
        endpoints.MapBillingAccountEndpoints();
        endpoints.MapPaymentProviderEndpoints();
        return endpoints;
    }
}
