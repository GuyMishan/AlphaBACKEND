using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alpha.Infrastructure.Persistence;

public sealed class EmployeePensionProductConfiguration : IEntityTypeConfiguration<EmployeePensionProduct>
{
    public void Configure(EntityTypeBuilder<EmployeePensionProduct> b)
    {
        b.ToTable("employee_pension_products", "employees");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProductType).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.PolicyNumber).HasMaxLength(100);
        b.Property(x => x.Salary).HasPrecision(18, 2);
        b.Property(x => x.ReportingType).HasMaxLength(80);
        b.Property(x => x.SalaryLayer).HasMaxLength(80);
        b.Property(x => x.InstitutionalBody).HasMaxLength(160);
        b.Property(x => x.Manufacturer).HasMaxLength(160);
        b.HasIndex(x => x.EmploymentId);
        b.HasIndex(x => new { x.EmploymentId, x.IsActive, x.EffectiveFrom, x.EffectiveTo });
        b.HasOne<Employment>().WithMany().HasForeignKey(x => x.EmploymentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EmployeePensionContributionConfiguration : IEntityTypeConfiguration<EmployeePensionContribution>
{
    public void Configure(EntityTypeBuilder<EmployeePensionContribution> b)
    {
        b.ToTable("employee_pension_contributions", "employees");
        b.HasKey(x => x.Id);
        b.Property(x => x.Party).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Component).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Percentage).HasPrecision(9, 4);
        b.HasIndex(x => new { x.EmployeePensionProductId, x.Party, x.Component }).IsUnique();
        b.HasOne<EmployeePensionProduct>().WithMany().HasForeignKey(x => x.EmployeePensionProductId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ManualReportConfiguration : IEntityTypeConfiguration<ManualReport>
{
    public void Configure(EntityTypeBuilder<ManualReport> b)
    {
        b.ToTable("manual_reports", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.ReportKind).HasConversion<string>().HasMaxLength(30);
        b.HasIndex(x => new { x.OrganizationId, x.EmployerId, x.ReportingMonth });
        b.HasIndex(x => x.SourceReportId);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employer>().WithMany().HasForeignKey(x => x.EmployerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ManualReport>().WithMany().HasForeignKey(x => x.SourceReportId).OnDelete(DeleteBehavior.Restrict);
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

public sealed class ManualReportPaymentConfiguration : IEntityTypeConfiguration<ManualReportPayment>
{
    public void Configure(EntityTypeBuilder<ManualReportPayment> b)
    {
        b.ToTable("manual_report_payments", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProviderName).HasMaxLength(160);
        b.Property(x => x.ProviderAccount).HasMaxLength(120);
        b.Property(x => x.PaymentMethod).HasMaxLength(80);
        b.Property(x => x.ReferenceNumber).HasMaxLength(120);
        b.Property(x => x.EmployerBankName).HasMaxLength(120);
        b.Property(x => x.EmployerBankCode).HasMaxLength(30);
        b.Property(x => x.EmployerBranch).HasMaxLength(30);
        b.Property(x => x.EmployerAccount).HasMaxLength(80);
        b.Property(x => x.ConfirmationFileName).HasMaxLength(260);
        b.HasIndex(x => x.ReportProductId).IsUnique();
        b.HasOne<ManualReportProduct>().WithOne().HasForeignKey<ManualReportPayment>(x => x.ReportProductId).OnDelete(DeleteBehavior.Cascade);
    }
}
