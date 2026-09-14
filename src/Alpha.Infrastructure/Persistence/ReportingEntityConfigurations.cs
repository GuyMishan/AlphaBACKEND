using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alpha.Infrastructure.Persistence;

public sealed class ManualReportConfiguration : IEntityTypeConfiguration<ManualReport>
{
    public void Configure(EntityTypeBuilder<ManualReport> b)
    {
        b.ToTable("manual_reports", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
        b.HasIndex(x => new { x.OrganizationId, x.EmployerId, x.ReportingMonth });
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employer>().WithMany().HasForeignKey(x => x.EmployerId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ManualReportEmployeeConfiguration : IEntityTypeConfiguration<ManualReportEmployee>
{
    public void Configure(EntityTypeBuilder<ManualReportEmployee> b)
    {
        b.ToTable("manual_report_employees", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.NationalId).HasMaxLength(30).IsRequired();
        b.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(100).IsRequired();
        b.Property(x => x.EmployeeNumber).HasMaxLength(50).IsRequired();
        b.HasIndex(x => new { x.ReportId, x.EmploymentId }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.EmployerId, x.ReportId });
        b.HasOne<ManualReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Employment>().WithMany().HasForeignKey(x => x.EmploymentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ManualReportProductConfiguration : IEntityTypeConfiguration<ManualReportProduct>
{
    public void Configure(EntityTypeBuilder<ManualReportProduct> b)
    {
        b.ToTable("manual_report_products", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductType).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.PolicyNumber).HasMaxLength(100);
        b.Property(x => x.Salary).HasPrecision(18, 2);
        b.Property(x => x.ReportingType).HasMaxLength(80);
        b.Property(x => x.SalaryLayer).HasMaxLength(80);
        b.HasIndex(x => x.ReportEmployeeId);
        b.HasOne<ManualReportEmployee>().WithMany().HasForeignKey(x => x.ReportEmployeeId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ManualContributionConfiguration : IEntityTypeConfiguration<ManualContribution>
{
    public void Configure(EntityTypeBuilder<ManualContribution> b)
    {
        b.ToTable("manual_contributions", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.Party).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Component).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.Property(x => x.Percentage).HasPrecision(9, 4);
        b.Property(x => x.ExemptPayments).HasPrecision(18, 2);
        b.HasIndex(x => new { x.ReportProductId, x.Party, x.Component }).IsUnique();
        b.HasOne<ManualReportProduct>().WithMany().HasForeignKey(x => x.ReportProductId).OnDelete(DeleteBehavior.Cascade);
    }
}
