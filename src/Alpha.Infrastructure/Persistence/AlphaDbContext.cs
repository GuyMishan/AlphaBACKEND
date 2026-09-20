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
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<OrganizationMembership> OrganizationMemberships => Set<OrganizationMembership>();
    public DbSet<Employer> Employers => Set<Employer>();
    public DbSet<EmployerUserAccess> EmployerUserAccesses => Set<EmployerUserAccess>();
    public DbSet<Person> People => Set<Person>();
    public DbSet<Employment> Employments => Set<Employment>();
    public DbSet<EmployeePensionProduct> EmployeePensionProducts => Set<EmployeePensionProduct>();
    public DbSet<EmployeePensionContribution> EmployeePensionContributions => Set<EmployeePensionContribution>();
    public DbSet<ManualReport> ManualReports => Set<ManualReport>();
    public DbSet<ManualReportEmployee> ManualReportEmployees => Set<ManualReportEmployee>();
    public DbSet<ManualReportProduct> ManualReportProducts => Set<ManualReportProduct>();
    public DbSet<ManualContribution> ManualContributions => Set<ManualContribution>();
    public DbSet<ManualReportPayment> ManualReportPayments => Set<ManualReportPayment>();
    public DbSet<EmployerInterfaceReportProductData> EmployerInterfaceReportProductData => Set<EmployerInterfaceReportProductData>();
    public DbSet<ReportTransmission> ReportTransmissions => Set<ReportTransmission>();
    public DbSet<EmployerInterfaceFeedback> EmployerInterfaceFeedback => Set<EmployerInterfaceFeedback>();
    public DbSet<ContributionPercentageLimit> ContributionPercentageLimits => Set<ContributionPercentageLimit>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AlphaDbContext).Assembly);
        var otp = modelBuilder.Entity<OtpChallenge>();
        otp.ToTable("otp_challenges", "identity");
        otp.HasKey(x => x.Id);
        otp.Property(x => x.Channel).HasMaxLength(10);
        otp.Property(x => x.CodeHash).HasMaxLength(64);
        otp.HasIndex(x => new { x.UserId, x.CreatedAt });
        var limits = modelBuilder.Entity<ContributionPercentageLimit>();
        limits.ToTable("contribution_percentage_limits", "reporting");
        limits.HasKey(x => x.Id);
        limits.Property(x => x.ProductType).HasConversion<string>().HasMaxLength(40);
        limits.Property(x => x.Party).HasConversion<string>().HasMaxLength(20);
        limits.Property(x => x.Component).HasConversion<string>().HasMaxLength(30);
        limits.Property(x => x.MaxPercentage).HasPrecision(9, 4);
        limits.HasIndex(x => new { x.Year, x.ProductType, x.Party, x.Component }).IsUnique();
        base.OnModelCreating(modelBuilder);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var invalidAuditMutation = ChangeTracker.Entries<AuditEvent>().Any(x => x.State is EntityState.Modified or EntityState.Deleted);
        if (invalidAuditMutation) throw new InvalidOperationException("Audit events are append-only and cannot be changed or deleted.");
        return base.SaveChangesAsync(cancellationToken);
    }
}
