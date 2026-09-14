using Alpha.Application.Abstractions;
using Alpha.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Alpha.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var configuredConnectionString = configuration.GetConnectionString("AlphaDatabase")
            ?? throw new InvalidOperationException("Connection string 'AlphaDatabase' is not configured.");

        var connectionString = NormalizePostgresConnectionString(configuredConnectionString);
        services.AddDbContext<AlphaDbContext>(options => options.UseNpgsql(connectionString));
        services.AddScoped<IAlphaDbContext>(provider => provider.GetRequiredService<AlphaDbContext>());
        return services;
    }

    private static string NormalizePostgresConnectionString(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        var uri = new Uri(trimmed);
        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo.Length == 0 || string.IsNullOrWhiteSpace(userInfo[0]))
            throw new InvalidOperationException("PostgreSQL URL does not contain a username.");

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty,
            Database = uri.AbsolutePath.Trim('/'),
            SslMode = SslMode.Require,
            Timeout = 15,
            KeepAlive = 30
        };

        return builder.ConnectionString;
    }
}
