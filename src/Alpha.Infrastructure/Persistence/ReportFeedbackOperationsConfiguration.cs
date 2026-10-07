using Alpha.Domain.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Alpha.Infrastructure.Persistence;

public sealed class EmployerInterfaceTransferFeedbackConfiguration : IEntityTypeConfiguration<EmployerInterfaceTransferFeedback>
{
    public void Configure(EntityTypeBuilder<EmployerInterfaceTransferFeedback> b)
    {
        b.ToTable("employer_interface_transfer_feedback", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.TransferIdentifier).HasMaxLength(100).IsRequired();
        b.Property(x => x.ClearingIdentifier).HasMaxLength(100);
        b.Property(x => x.ReportedDepositAmount).HasPrecision(18, 2);
        b.Property(x => x.ActualReceivedAmount).HasPrecision(18, 2);
        b.Property(x => x.AllocatedAmount).HasPrecision(18, 2);
        b.Property(x => x.InTransitAmount).HasPrecision(18, 2);
        b.Property(x => x.ProactiveRefundAmount).HasPrecision(18, 2);
        b.Property(x => x.EmployerAccountRefundAmount).HasPrecision(18, 2);
        b.Property(x => x.StatusDetail).HasMaxLength(200);
        b.Property(x => x.PaymentReference).HasMaxLength(100);
        b.Property(x => x.CorrectnessTimestamp).HasMaxLength(32);
        b.HasIndex(x => new { x.ReportId, x.TransferIdentifier, x.ReceivedAt });
        b.HasIndex(x => new { x.ReportId, x.FeedbackId, x.TransferIdentifier, x.ReceivedAt });
        b.HasIndex(x => new { x.FeedbackId, x.TransferIdentifier }).IsUnique();
        b.HasIndex(x => x.FeedbackId);
        b.HasOne<EmployerInterfaceFeedback>().WithMany().HasForeignKey(x => x.FeedbackId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ManualReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class EmployerInterfaceContributionFeedbackConfiguration : IEntityTypeConfiguration<EmployerInterfaceContributionFeedback>
{
    public void Configure(EntityTypeBuilder<EmployerInterfaceContributionFeedback> b)
    {
        b.ToTable("employer_interface_contribution_feedback", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.RecordIdentifier).HasMaxLength(36).IsRequired();
        b.Property(x => x.ErrorDescription).HasMaxLength(2000);
        b.Property(x => x.ErrorAmount).HasPrecision(18, 2);
        b.Property(x => x.CalculatedSalary).HasPrecision(18, 2);
        b.Property(x => x.ContributionRate).HasPrecision(9, 4);
        b.Property(x => x.ContributionAmount).HasPrecision(18, 2);
        b.Property(x => x.PolicyNumber).HasMaxLength(100);
        b.Property(x => x.SourceFileName).HasMaxLength(260);
        b.HasIndex(x => new { x.ReportId, x.ReportProductId, x.ReceivedAt });
        b.HasIndex(x => new { x.ReportProductId, x.ReceivedAt });
        b.HasIndex(x => new { x.ReportId, x.FeedbackId, x.ContributionId, x.ReceivedAt });
        b.HasIndex(x => new { x.FeedbackId, x.RecordIdentifier, x.Sequence }).IsUnique();
        b.HasIndex(x => x.ContributionId);
        b.HasOne<EmployerInterfaceFeedback>().WithMany().HasForeignKey(x => x.FeedbackId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ManualReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ManualReportProduct>().WithMany().HasForeignKey(x => x.ReportProductId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ManualContribution>().WithMany().HasForeignKey(x => x.ContributionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class ReportProductTreatmentConfiguration : IEntityTypeConfiguration<ReportProductTreatment>
{
    public void Configure(EntityTypeBuilder<ReportProductTreatment> b)
    {
        b.ToTable("report_product_treatments", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.StatusCode).HasMaxLength(80).IsRequired();
        b.Property(x => x.Note).HasColumnType("text");
        b.Property(x => x.UpdatedAt).IsConcurrencyToken();
        b.HasIndex(x => x.ReportProductId).IsUnique();
        b.HasIndex(x => x.StatusCode);
        b.HasOne<ManualReportProduct>().WithOne().HasForeignKey<ReportProductTreatment>(x => x.ReportProductId).OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ReportProductTreatmentHistoryConfiguration : IEntityTypeConfiguration<ReportProductTreatmentHistory>
{
    public void Configure(EntityTypeBuilder<ReportProductTreatmentHistory> b)
    {
        b.ToTable("report_product_treatment_history", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.PreviousStatusCode).HasMaxLength(80);
        b.Property(x => x.StatusCode).HasMaxLength(80).IsRequired();
        b.Property(x => x.Note).HasColumnType("text");
        b.HasIndex(x => new { x.ReportProductId, x.CreatedAt });
        b.HasOne<ManualReportProduct>().WithMany().HasForeignKey(x => x.ReportProductId).OnDelete(DeleteBehavior.Cascade);
    }
}


public sealed class FeedbackProblemResolutionConfiguration : IEntityTypeConfiguration<FeedbackProblemResolution>
{
    public void Configure(EntityTypeBuilder<FeedbackProblemResolution> b)
    {
        b.ToTable("feedback_problem_resolutions", "reporting"); b.HasKey(x => x.Id); b.Property(x => x.ProblemId).HasMaxLength(180).IsRequired(); b.Property(x => x.ResolutionSource).HasMaxLength(80).IsRequired(); b.HasIndex(x => x.ProblemId).IsUnique(); b.HasIndex(x => new { x.ReportId, x.ErrorCode }); b.HasOne<EmployerInterfaceFeedback>().WithMany().HasForeignKey(x => x.FeedbackId).OnDelete(DeleteBehavior.Cascade); b.HasOne<ManualReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class FeedbackResolutionDocumentConfiguration : IEntityTypeConfiguration<FeedbackResolutionDocument>
{
    public void Configure(EntityTypeBuilder<FeedbackResolutionDocument> b)
    {
        b.ToTable("feedback_resolution_documents", "reporting"); b.HasKey(x => x.Id); b.Property(x => x.ProblemId).HasMaxLength(180).IsRequired(); b.Property(x => x.OriginalFileName).HasMaxLength(260).IsRequired(); b.Property(x => x.ContentType).HasMaxLength(100).IsRequired(); b.Property(x => x.Content).HasColumnType("bytea").IsRequired(); b.Property(x => x.Sha256).HasMaxLength(64).IsRequired(); b.HasIndex(x => new { x.ProblemId, x.CreatedAt }); b.HasOne<ManualReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
    }
}


public sealed class FeedbackProblemDecisionConfiguration : IEntityTypeConfiguration<FeedbackProblemDecision>
{
    public void Configure(EntityTypeBuilder<FeedbackProblemDecision> b)
    {
        b.ToTable("feedback_problem_decisions", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProblemId).HasMaxLength(180).IsRequired();
        b.Property(x => x.Outcome).HasMaxLength(40).IsRequired();
        b.Property(x => x.Note).HasColumnType("text");
        b.HasIndex(x => new { x.ProblemId, x.DecidedAt });
        b.HasIndex(x => new { x.ReportId, x.DecidedAt });
        b.HasOne<EmployerInterfaceFeedback>().WithMany().HasForeignKey(x => x.FeedbackId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ManualReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
    }
}


public sealed class FeedbackExternalCaseConfiguration : IEntityTypeConfiguration<FeedbackExternalCase>
{
    public void Configure(EntityTypeBuilder<FeedbackExternalCase> b)
    {
        b.ToTable("feedback_external_cases", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.CaseKey).HasMaxLength(300).IsRequired();
        b.Property(x => x.Status).HasMaxLength(20).IsRequired();
        b.Property(x => x.Destination).HasMaxLength(120).IsRequired();
        b.Property(x => x.Subject).HasMaxLength(300).IsRequired();
        b.Property(x => x.MessageTemplate).HasColumnType("text");
        b.HasIndex(x => new { x.EmployerId, x.CaseKey }).IsUnique();
        b.HasIndex(x => new { x.EmployerId, x.Status, x.UpdatedAt });
    }
}
public sealed class FeedbackExternalCaseProblemConfiguration : IEntityTypeConfiguration<FeedbackExternalCaseProblem>
{
    public void Configure(EntityTypeBuilder<FeedbackExternalCaseProblem> b)
    {
        b.ToTable("feedback_external_case_problems", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProblemId).HasMaxLength(180).IsRequired();
        b.HasIndex(x => new { x.CaseId, x.ProblemId }).IsUnique();
        b.HasIndex(x => x.ProblemId);
        b.HasOne<FeedbackExternalCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne<EmployerInterfaceFeedback>().WithMany().HasForeignKey(x => x.FeedbackId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ManualReport>().WithMany().HasForeignKey(x => x.ReportId).OnDelete(DeleteBehavior.Restrict);
    }
}
public sealed class FeedbackExternalCaseEventConfiguration : IEntityTypeConfiguration<FeedbackExternalCaseEvent>
{
    public void Configure(EntityTypeBuilder<FeedbackExternalCaseEvent> b)
    {
        b.ToTable("feedback_external_case_events", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(60).IsRequired();
        b.Property(x => x.Note).HasColumnType("text");
        b.HasIndex(x => new { x.CaseId, x.CreatedAt });
        b.HasOne<FeedbackExternalCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
    }
}
public sealed class FeedbackExternalCaseAttachmentConfiguration : IEntityTypeConfiguration<FeedbackExternalCaseAttachment>
{
    public void Configure(EntityTypeBuilder<FeedbackExternalCaseAttachment> b)
    {
        b.ToTable("feedback_external_case_attachments", "reporting");
        b.HasKey(x => x.Id);
        b.Property(x => x.OriginalFileName).HasMaxLength(260).IsRequired();
        b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
        b.Property(x => x.Content).HasColumnType("bytea").IsRequired();
        b.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.CaseId, x.CreatedAt });
        b.HasOne<FeedbackExternalCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
    }
}
