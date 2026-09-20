using Alpha.Domain.Auditing;
using Alpha.Domain.Billing;
using Alpha.Domain.Employees;
using Alpha.Domain.Employers;
using Alpha.Domain.Identity;
using Alpha.Domain.Organizations;
using Alpha.Domain.Subscriptions;
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


public sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> b)
    {
        b.ToTable("plans", "subscriptions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(50).IsRequired();
        b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
    }
}

public sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> b)
    {
        b.ToTable("subscriptions", "subscriptions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.HasIndex(x => x.OrganizationId).IsUnique();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
    }
}


public sealed class EmployerProfileSettingsConfiguration : IEntityTypeConfiguration<EmployerProfileSettings>
{
    public void Configure(EntityTypeBuilder<EmployerProfileSettings> b)
    {
        b.ToTable("employer_profile_settings", "employers");
        b.HasKey(x => x.Id);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.Street).HasMaxLength(100);
        b.Property(x => x.HouseNumber).HasMaxLength(20);
        b.Property(x => x.Apartment).HasMaxLength(20);
        b.Property(x => x.PostalCode).HasMaxLength(10);
        b.Property(x => x.PostOfficeBox).HasMaxLength(20);
        b.Property(x => x.BillingMode).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.BillingStatus).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.ReportingNotes).HasMaxLength(500);
        b.HasIndex(x => x.EmployerId).IsUnique();
        b.HasOne<Employer>().WithMany().HasForeignKey(x => x.EmployerId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class EmployerPaymentAccountConfiguration : IEntityTypeConfiguration<EmployerPaymentAccount>
{
    public void Configure(EntityTypeBuilder<EmployerPaymentAccount> b)
    {
        b.ToTable("employer_payment_accounts", "employers");
        b.HasKey(x => x.Id);
        b.Property(x => x.AccountNumber).HasMaxLength(30).IsRequired();
        b.Property(x => x.AccountHolderName).HasMaxLength(150).IsRequired();
        b.Property(x => x.AccountHolderId).HasMaxLength(20).IsRequired();
        b.HasIndex(x => new { x.EmployerId, x.BankId, x.BranchId, x.AccountNumber }).IsUnique();
        b.HasIndex(x => x.EmployerId);
        b.HasOne<Employer>().WithMany().HasForeignKey(x => x.EmployerId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class BankDebitMandateConfiguration : IEntityTypeConfiguration<BankDebitMandate>
{
    public void Configure(EntityTypeBuilder<BankDebitMandate> b)
    {
        b.ToTable("bank_debit_mandates", "employers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.ExternalMandateId).HasMaxLength(120);
        b.Property(x => x.DocumentId).HasMaxLength(200);
        b.Ignore(x => x.IsActive);
        b.HasIndex(x => x.EmployerPaymentAccountId).IsUnique();
        b.HasOne<EmployerPaymentAccount>().WithOne().HasForeignKey<BankDebitMandate>(x => x.EmployerPaymentAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class OrganizationProfileSettingsConfiguration : IEntityTypeConfiguration<OrganizationProfileSettings>
{
    public void Configure(EntityTypeBuilder<OrganizationProfileSettings> b)
    {
        b.ToTable("organization_profile_settings", "organizations");
        b.HasKey(x => x.Id);
        b.Property(x => x.RegistrationNumber).HasMaxLength(30);
        b.Property(x => x.City).HasMaxLength(100);
        b.Property(x => x.Street).HasMaxLength(100);
        b.Property(x => x.HouseNumber).HasMaxLength(20);
        b.Property(x => x.Apartment).HasMaxLength(20);
        b.Property(x => x.PostalCode).HasMaxLength(10);
        b.Property(x => x.PostOfficeBox).HasMaxLength(20);
        b.Property(x => x.ContactName).HasMaxLength(150);
        b.Property(x => x.ContactEmail).HasMaxLength(320);
        b.Property(x => x.ContactPhone).HasMaxLength(20);
        b.Property(x => x.BillingStatus).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.InvoiceName).HasMaxLength(200);
        b.Property(x => x.InvoiceRegistrationNumber).HasMaxLength(30);
        b.Property(x => x.InvoiceEmail).HasMaxLength(320);
        b.Property(x => x.BillingContactName).HasMaxLength(150);
        b.Property(x => x.BillingContactPhone).HasMaxLength(20);
        b.HasIndex(x => x.OrganizationId).IsUnique();
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
    }
}


public sealed class BillingAccountConfiguration : IEntityTypeConfiguration<BillingAccount>
{
    public void Configure(EntityTypeBuilder<BillingAccount> b)
    {
        b.ToTable("billing_accounts", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.BillingName).HasMaxLength(200);
        b.Property(x => x.TaxId).HasMaxLength(30);
        b.Property(x => x.InvoiceEmail).HasMaxLength(320);
        b.Property(x => x.BillingAddress).HasMaxLength(500);
        b.Property(x => x.PaymentMethodType).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.PaymentMethodStatus).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.ProviderCustomerId).HasMaxLength(200);
        b.Property(x => x.ProviderPaymentMethodId).HasMaxLength(200);
        b.Property(x => x.CardBrand).HasMaxLength(40);
        b.Property(x => x.CardLast4).HasMaxLength(4);
        b.Property(x => x.BankDebitMandateReference).HasMaxLength(200);
        b.HasIndex(x => x.OrganizationId).IsUnique().HasFilter("\"OrganizationId\" IS NOT NULL");
        b.HasIndex(x => x.EmployerId).IsUnique().HasFilter("\"EmployerId\" IS NOT NULL");
        b.HasOne<Organization>().WithMany().HasForeignKey(x => x.OrganizationId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Employer>().WithMany().HasForeignKey(x => x.EmployerId).OnDelete(DeleteBehavior.Cascade);
    }
}
