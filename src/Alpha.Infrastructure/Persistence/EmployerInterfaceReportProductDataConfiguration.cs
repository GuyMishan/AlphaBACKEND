using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alpha.Infrastructure.Persistence;

public sealed class EmployerInterfaceReportProductDataConfiguration : IEntityTypeConfiguration<EmployerInterfaceReportProductData>
{
    public void Configure(EntityTypeBuilder<EmployerInterfaceReportProductData> b)
    {
        b.ToTable("employer_interface_report_product_data", "reporting");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.ReportProductId).IsUnique();
        b.Property(x => x.EmploymentPercentage).HasPrecision(5, 2);
        b.Property(x => x.InterfaceTransferIdentifier).HasMaxLength(36);
        b.Property(x => x.ClearingIdentifier).HasMaxLength(36);
        b.Property(x => x.PreviousIdentifier).HasMaxLength(36);
        b.Property(x => x.PreviousClearingIdentifier).HasMaxLength(36);
        b.HasOne<ManualReportProduct>().WithOne()
            .HasForeignKey<EmployerInterfaceReportProductData>(x => x.ReportProductId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
