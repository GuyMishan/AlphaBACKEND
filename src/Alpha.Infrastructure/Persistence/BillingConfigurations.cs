using Alpha.Domain.Billing;
using Alpha.Domain.Employers;
using Alpha.Domain.Subscriptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alpha.Infrastructure.Persistence;

public sealed class PlanPricingComponentConfiguration : IEntityTypeConfiguration<PlanPricingComponent>
{
    public void Configure(EntityTypeBuilder<PlanPricingComponent> b)
    {
        b.ToTable("plan_pricing_components", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.MetricType).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.CorrectionMode).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.PricingType).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.UnitPrice).HasPrecision(18, 4);
        b.Property(x => x.IncludedQuantity).HasPrecision(18, 4);
        b.Property(x => x.MinimumCharge).HasPrecision(18, 2);
        b.Property(x => x.MaximumCharge).HasPrecision(18, 2);
        b.HasIndex(x => new { x.PlanId, x.Version, x.MetricType }).IsUnique();
        b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class PlanPricingTierConfiguration : IEntityTypeConfiguration<PlanPricingTier>
{
    public void Configure(EntityTypeBuilder<PlanPricingTier> b)
    {
        b.ToTable("plan_pricing_tiers", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.FromQuantity).HasPrecision(18, 4);
        b.Property(x => x.ToQuantity).HasPrecision(18, 4);
        b.Property(x => x.UnitPrice).HasPrecision(18, 4);
        b.HasIndex(x => new { x.ComponentId, x.FromQuantity }).IsUnique();
        b.HasOne<PlanPricingComponent>().WithMany().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BillingAccountPricingComponentConfiguration : IEntityTypeConfiguration<BillingAccountPricingComponent>
{
    public void Configure(EntityTypeBuilder<BillingAccountPricingComponent> b)
    {
        b.ToTable("billing_account_pricing_components", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.MetricType).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.CorrectionMode).HasConversion<string>().HasMaxLength(40);
        b.Property(x => x.PricingType).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.UnitPrice).HasPrecision(18, 4);
        b.Property(x => x.IncludedQuantity).HasPrecision(18, 4);
        b.Property(x => x.MinimumCharge).HasPrecision(18, 2);
        b.Property(x => x.MaximumCharge).HasPrecision(18, 2);
        b.HasIndex(x => new { x.BillingAccountId, x.Version, x.MetricType }).IsUnique();
        b.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BillingAccountPricingTierConfiguration : IEntityTypeConfiguration<BillingAccountPricingTier>
{
    public void Configure(EntityTypeBuilder<BillingAccountPricingTier> b)
    {
        b.ToTable("billing_account_pricing_tiers", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.FromQuantity).HasPrecision(18, 4);
        b.Property(x => x.ToQuantity).HasPrecision(18, 4);
        b.Property(x => x.UnitPrice).HasPrecision(18, 4);
        b.HasIndex(x => new { x.ComponentId, x.FromQuantity }).IsUnique();
        b.HasOne<BillingAccountPricingComponent>().WithMany().HasForeignKey(x => x.ComponentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BillingPeriodConfiguration : IEntityTypeConfiguration<BillingPeriod>
{
    public void Configure(EntityTypeBuilder<BillingPeriod> b)
    {
        b.ToTable("billing_periods", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Subtotal).HasPrecision(18, 2);
        b.Property(x => x.Total).HasPrecision(18, 2);
        b.Property(x => x.CalculationSnapshotJson).HasColumnType("jsonb");
        b.HasIndex(x => new { x.BillingAccountId, x.PeriodStart, x.PeriodEnd }).IsUnique();
        b.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class BillingUsageConfiguration : IEntityTypeConfiguration<BillingUsage>
{
    public void Configure(EntityTypeBuilder<BillingUsage> b)
    {
        b.ToTable("billing_usage", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.MetricType).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.Quantity).HasPrecision(18, 4);
        b.Property(x => x.IncludedQuantity).HasPrecision(18, 4);
        b.Property(x => x.BillableQuantity).HasPrecision(18, 4);
        b.Property(x => x.UnitPrice).HasPrecision(18, 4);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.Property(x => x.SourceType).HasMaxLength(80);
        b.Property(x => x.SourceId).HasMaxLength(160);
        b.HasIndex(x => new { x.BillingPeriodId, x.MetricType, x.EmployerId });
        b.HasIndex(x => new { x.BillingAccountId, x.SourceType, x.SourceId, x.MetricType })
            .IsUnique().HasFilter("\"SourceId\" <> ''");
        b.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BillingPeriod>().WithMany().HasForeignKey(x => x.BillingPeriodId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<Employer>().WithMany().HasForeignKey(x => x.EmployerId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class BillingPaymentMethodConfiguration : IEntityTypeConfiguration<PaymentMethod>
{
    public void Configure(EntityTypeBuilder<PaymentMethod> b)
    {
        b.ToTable("payment_methods", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.Provider).HasMaxLength(40).IsRequired();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.ProviderCustomerId).HasMaxLength(200);
        b.Property(x => x.ProviderPaymentMethodId).HasMaxLength(200);
        b.Property(x => x.CardBrand).HasMaxLength(40);
        b.Property(x => x.CardLast4).HasMaxLength(4);
        b.Property(x => x.MandateReference).HasMaxLength(200);
        b.HasIndex(x => new { x.Provider, x.ProviderPaymentMethodId }).IsUnique().HasFilter("\"ProviderPaymentMethodId\" <> ''");
        b.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class BillingPaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> b)
    {
        b.ToTable("payments", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.Provider).HasMaxLength(40);
        b.Property(x => x.ProviderTransactionId).HasMaxLength(200);
        b.Property(x => x.InvoiceReference).HasMaxLength(200);
        b.Property(x => x.FailureCode).HasMaxLength(120);
        b.Property(x => x.FailureMessage).HasMaxLength(1000);
        b.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => x.BillingPeriodId);
        b.HasOne<BillingAccount>().WithMany().HasForeignKey(x => x.BillingAccountId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BillingPeriod>().WithMany().HasForeignKey(x => x.BillingPeriodId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> b)
    {
        b.ToTable("payment_attempts", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.Provider).HasMaxLength(40).IsRequired();
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
        b.Property(x => x.ProviderTransactionId).HasMaxLength(200);
        b.Property(x => x.ErrorCode).HasMaxLength(120);
        b.Property(x => x.ErrorMessage).HasMaxLength(1000);
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasIndex(x => new { x.PaymentId, x.AttemptNumber }).IsUnique();
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RefundConfiguration : IEntityTypeConfiguration<Refund>
{
    public void Configure(EntityTypeBuilder<Refund> b)
    {
        b.ToTable("refunds", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.Amount).HasPrecision(18, 2);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.ProviderRefundId).HasMaxLength(200);
        b.Property(x => x.ErrorMessage).HasMaxLength(1000);
        b.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
        b.HasIndex(x => x.IdempotencyKey).IsUnique();
        b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ProviderWebhookEventConfiguration : IEntityTypeConfiguration<ProviderWebhookEvent>
{
    public void Configure(EntityTypeBuilder<ProviderWebhookEvent> b)
    {
        b.ToTable("provider_webhook_events", "billing");
        b.HasKey(x => x.Id);
        b.Property(x => x.Provider).HasMaxLength(40).IsRequired();
        b.Property(x => x.EventKey).HasMaxLength(200).IsRequired();
        b.Property(x => x.PayloadHash).HasMaxLength(128);
        b.Property(x => x.Payload).HasColumnType("text");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(40).IsRequired();
        b.Property(x => x.ErrorMessage).HasMaxLength(1000);
        b.HasIndex(x => new { x.Provider, x.EventKey }).IsUnique();
    }
}
