using Alpha.Application.Abstractions;
using Alpha.Domain.Auditing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;

namespace Alpha.Infrastructure.Persistence;

public sealed class AlphaDbContext(DbContextOptions<AlphaDbContext> options) : DbContext(options), IAlphaDbContext
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<Employer> Employers => Set<Employer>();
    public DbSet<EmployerUserAccess> EmployerUserAccesses => Set<EmployerUserAccess>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Employment> Employments => Set<Employment>();
    public DbSet<ManualReport> ManualReports => Set<ManualReport>();
    public DbSet<ManualReportEmployee> ManualReportEmployees => Set<ManualReportEmployee>();
    public DbSet<ManualReportProduct> ManualReportProducts => Set<ManualReportProduct>();
    public DbSet<ManualContribution> ManualContributions => Set<ManualContribution>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AlphaDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var invalidAuditMutation = ChangeTracker.Entries<AuditEvent>()
            .Any(x => x.State is EntityState.Modified or EntityState.Deleted);
        if (invalidAuditMutation)
            throw new InvalidOperationException("Audit events are append-only and cannot be changed or deleted.");
        return base.SaveChangesAsync(cancellationToken);
    }
}
