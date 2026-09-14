using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Alpha.Infrastructure.Persistence;

public sealed class AlphaDbContextFactory : IDesignTimeDbContextFactory<AlphaDbContext>
{
    public AlphaDbContext CreateDbContext(string[] args)
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable("ConnectionStrings__AlphaDatabase");
        var connectionString = string.IsNullOrWhiteSpace(configuredConnectionString)
            ? "Host=localhost;Database=alpha_design;Username=postgres"
            : DependencyInjection.NormalizePostgresConnectionString(configuredConnectionString);

        var options = new DbContextOptionsBuilder<AlphaDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new AlphaDbContext(options);
    }
}
