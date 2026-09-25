using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alpha.Infrastructure.Persistence;

public sealed class ReportTransmissionConfiguration : IEntityTypeConfiguration<ReportTransmission>
{
    public void Configure(EntityTypeBuilder<ReportTransmission> b)
    {
        b.ToTable("report_transmissions", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.Provider).HasMaxLength(120).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.ExternalId).HasMaxLength(200);
        b.Property(x => x.PayloadHash).HasMaxLength(128);
        b.Property(x => x.PayloadFileName).HasMaxLength(100);
        b.Property(x => x.Payload).HasColumnType("bytea");
        b.Property(x => x.AttachmentManifestJson).HasColumnType("text");
        b.Property(x => x.ResponsePayload).HasColumnType("text");
        b.Property(x => x.ErrorMessage).HasMaxLength(4000);
        b.HasIndex(x => new { x.ReportId, x.AttemptNumber }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.EmployerId, x.ReportId });
        b.HasOne<ManualReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Cascade);
    }
}
