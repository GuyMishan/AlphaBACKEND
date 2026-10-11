using Alpha.Domain.Employers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alpha.Infrastructure.Persistence;

public sealed class EmployerDocumentConfiguration : IEntityTypeConfiguration<EmployerDocument>
{
    public void Configure(EntityTypeBuilder<EmployerDocument> b)
    {
        b.ToTable("employer_documents", "employers");
        b.HasKey(x => x.Id);
        b.Property(x => x.OriginalFileName).HasMaxLength(255);
        b.Property(x => x.Sha256).HasMaxLength(64);
        b.Property(x => x.Content).HasColumnType("bytea");
        b.Ignore(x => x.UpdatedAt);
        b.HasIndex(x => new { x.OrganizationId, x.EmployerId, x.CreatedAt });
        b.HasIndex(x => new { x.EmployerId, x.EmploymentId, x.DocumentTypeCode });
        b.HasIndex(x => x.EmployeePensionProductId);
    }
}
