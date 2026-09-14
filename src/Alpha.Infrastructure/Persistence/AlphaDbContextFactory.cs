using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Alpha.Infrastructure.Persistence;

public sealed class AlphaDbContextFactory : IDesignTimeDbContextFactory<AlphaDbContext>
{
    public AlphaDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AlphaDbContext>()
            .UseNpgsql("Host=localhost;Database=alpha_design;Username=postgres")
            .Options;

        return new AlphaDbContext(options);
    }
}
