using Alpha.Domain.Auditing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alpha.Infrastructure.Persistence;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users", "identity");
        b.HasKey(x => x.Id);
        b.Property(x => x.ExternalSubject).HasMaxLength(200).IsRequired();
        b.Property(x => x.Email).HasMaxLength(320).IsRequired();
        b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Appearance).HasMaxLength(16).IsRequired().HasDefaultValue("system");
        b.HasIndex(x => x.ExternalSubject).IsUnique();
        b.HasIndex(x => x.Email).IsUnique();
    }
}

public sealed class OrganizationConfiguration : IEntityTypeConfiguration<Organization>
{
    public void Configure(EntityTypeBuilder<Organization> b)
    {
        b.ToTable("organizations", "organizations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
    }
}

public sealed class OrganizationMembershipConfiguration : IEntityTypeConfiguration<OrganizationMembership>
{
    public void Configure(EntityTypeBuilder<OrganizationMembership> b)
    {
        b.ToTable("organization_memberships", "organizations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.EmployerAccessMode).HasConversion<string>().HasMaxLength(40);
        b.HasIndex(x => new { x.UserId, x.OrganizationId }).IsUnique();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EmployerConfiguration : IEntityTypeConfiguration<Employer>
{
    public void Configure(EntityTypeBuilder<Employer> b)
    {
        b.ToTable("employers", "employers");
        b.HasKey(x => x.Id);
        b.Property(x => x.LegalName).HasMaxLength(250).IsRequired();
        b.Property(x => x.RegistrationNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.WithholdingFileNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.ContactFirstName).HasMaxLength(20);
        b.Property(x => x.ContactLastName).HasMaxLength(20);
        b.Property(x => x.ContactPhone).HasMaxLength(20);
        b.Property(x => x.ContactEmail).HasMaxLength(50);
        b.Property(x => x.ContactMobile).HasMaxLength(15);
        b.HasIndex(x => new { x.OrganizationId, x.RegistrationNumber }).IsUnique();
        b.HasIndex(x => new { x.OrganizationId, x.WithholdingFileNumber }).IsUnique();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EmployerUserAccessConfiguration : IEntityTypeConfiguration<EmployerUserAccess>
{
    public void Configure(EntityTypeBuilder<EmployerUserAccess> b)
    {
        b.ToTable("employer_user_access", "employers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.HasIndex(x => new { x.UserId, x.EmployerId }).IsUnique();
        b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employer>().WithMany().HasForeignKey(x => x.EmployerId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    public void Configure(EntityTypeBuilder<Person> b)
    {
        b.ToTable("people", "employees");
        b.HasKey(x => x.Id);
        b.Property(x => x.NationalId).HasMaxLength(30).IsRequired();
        b.Property(x => x.FirstName).HasMaxLength(100).IsRequired();
        b.Property(x => x.LastName).HasMaxLength(100).IsRequired();
        b.Property(x => x.Gender).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Email).HasMaxLength(50);
        b.Property(x => x.Mobile).HasMaxLength(15);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.Street).HasMaxLength(100);
        b.Property(x => x.HouseNumber).HasMaxLength(20);
        b.Property(x => x.Apartment).HasMaxLength(20);
        b.Property(x => x.PostalCode).HasMaxLength(10);
        b.Property(x => x.PostOfficeBox).HasMaxLength(20);
        b.HasIndex(x => new { x.OrganizationId, x.NationalId }).IsUnique();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EmploymentConfiguration : IEntityTypeConfiguration<Employment>
{
    public void Configure(EntityTypeBuilder<Employment> b)
    {
        b.ToTable("employments", "employees");
        b.HasKey(x => x.Id);
        b.Property(x => x.EmployeeNumber).HasMaxLength(50).IsRequired();
        b.Property(x => x.MonthlySalary).HasPrecision(18, 2).HasDefaultValue(0);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40);
        b.HasIndex(x => new { x.EmployerId, x.EmployeeNumber }).IsUnique();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Employer>().WithMany().HasForeignKey(x => x.EmployerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Person>().WithMany().HasForeignKey(x => x.PersonId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("audit_events", "audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(100).IsRequired();
        b.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
        b.Property(x => x.CorrelationId).HasMaxLength(100).IsRequired();
        b.Property(x => x.Data).HasColumnType("jsonb");
        b.HasIndex(x => new { x.OrganizationId, x.CreatedAt });
    }
}
