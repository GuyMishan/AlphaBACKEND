using Alpha.Application.Abstractions;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Alpha.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("AlphaDatabase")
            ?? throw new InvalidOperationException("Connection string 'AlphaDatabase' is not configured.");
        services.AddDbContext<AlphaDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IAlphaDbContext>(provider => provider.GetRequiredService<AlphaDbContext>());
        return services;
    }
}
