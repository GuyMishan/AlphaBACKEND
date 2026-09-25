using Alpha.Domain.Employers;
using Alpha.Domain.Organizations;
using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alpha.Infrastructure.Persistence;

public sealed class EmployerInterfaceFeedbackConfiguration : IEntityTypeConfiguration<EmployerInterfaceFeedback>
{
    public void Configure(EntityTypeBuilder<EmployerInterfaceFeedback> b)
    {
        b.ToTable("employer_interface_feedback", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.DocumentType).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.InterfaceVersion).HasMaxLength(10).IsRequired();
        b.Property(x => x.SourceFileName).HasMaxLength(260).IsRequired();
        b.Property(x => x.InterfaceFileNumber).HasMaxLength(100);
        b.Property(x => x.PayloadHash).HasMaxLength(128).IsRequired();
        b.Property(x => x.RawXml).HasColumnType("text").IsRequired();
        b.HasIndex(x => new { x.EmployerId, x.PayloadHash }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.EmployerId, x.ReceivedAt });
        b.HasIndex(x => x.ReportId);
        b.HasIndex(x => x.TransmissionId);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employer>().WithMany().HasForeignKey(x => x.EmployerId).OnDelete(DeleteBehavior.Restrict);
    }
}
