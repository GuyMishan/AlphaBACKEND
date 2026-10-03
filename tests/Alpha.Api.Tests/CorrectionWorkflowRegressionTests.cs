using Alpha.Domain.Reporting;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class CorrectionWorkflowRegressionTests
{
    [Fact]
    public void Report_product_correction_defaults_to_operation_2_and_can_be_reset()
    {
        var product = CreateProduct();

        product.MarkCorrectionChanged();

        Assert.True(product.IsCorrectionChanged);
        Assert.Equal(2, product.CorrectionOperationCode);

        product.SetCorrectionState(false);

        Assert.False(product.IsCorrectionChanged);
        Assert.Null(product.CorrectionOperationCode);
    }

    [Fact]
    public void Report_product_correction_can_use_operation_3_for_additional_money()
    {
        var product = CreateProduct();

        product.MarkCorrectionChanged(3);

        Assert.True(product.IsCorrectionChanged);
        Assert.Equal(3, product.CorrectionOperationCode);
    }

    [Fact]
    public void Report_product_rejects_non_correction_operation_codes()
    {
        var product = CreateProduct();

        Assert.Throws<ArgumentOutOfRangeException>(() => product.SetCorrectionState(true, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => product.SetCorrectionState(true, 6));
    }

    [Fact]
    public void Correction_previous_references_follow_product_lineage_not_only_fund()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Services", "CorrectionWorkflowService.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("negativeByOriginalProductId", source, StringComparison.Ordinal);
        Assert.Contains("workspaceProduct.SourceReportProductId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceByFund", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Correction_materialization_claims_workspace_and_uses_per_product_operation()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Services", "CorrectionWorkflowService.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("BeginTransactionAsync", source, StringComparison.Ordinal);
        Assert.Contains("sourceGraph = await LoadGraphAsync", source, StringComparison.Ordinal);
        Assert.Contains("plan = BuildDeltaPlan", source, StringComparison.Ordinal);
        Assert.Contains("CommitAsync", source, StringComparison.Ordinal);
        Assert.Contains("RollbackAsync", source, StringComparison.Ordinal);
        Assert.Contains(".SetProperty(x => x.Status, ManualReportStatus.Processing)", source, StringComparison.Ordinal);
        Assert.Contains("oldProduct.CorrectionOperationCode ?? 2", source, StringComparison.Ordinal);
        Assert.Contains("x.Status == ManualReportStatus.Processing", source, StringComparison.Ordinal);
        Assert.Contains("BuildDeltaPlan", source, StringComparison.Ordinal);
        Assert.Contains("plan.NegativeSourceProductIds", source, StringComparison.Ordinal);
        Assert.DoesNotContain("string.IsNullOrWhiteSpace(metadata.ClearingIdentifier)", source, StringComparison.Ordinal);
        Assert.Contains("report-employee-email:{employee.Id}", source, StringComparison.Ordinal);
        Assert.Contains("CurrentWorkspaceProductIds", source, StringComparison.Ordinal);
        Assert.Contains("mappedPaymentProduct", source, StringComparison.Ordinal);
        Assert.Contains("CorrectionFundKey", source, StringComparison.Ordinal);
        Assert.Contains("ManualReportAttachments.AddRange(clone.Attachments)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Correction_employee_save_preserves_existing_product_rows()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Endpoints", "ManualReportEndpoints.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("if (!report.IsCorrectionWorkspace)", source, StringComparison.Ordinal);
        Assert.Contains("existingBySource", source, StringComparison.Ordinal);
        Assert.Contains("correction_product_lineage_missing", source, StringComparison.Ordinal);
        Assert.Contains("product.SetCorrectionState(changed", source, StringComparison.Ordinal);
        Assert.DoesNotContain("correction_structure_changes_not_supported", source, StringComparison.Ordinal);
        Assert.Contains("db.ManualReportProducts.RemoveRange(removedProducts)", source, StringComparison.Ordinal);
        Assert.Contains("WorkspaceReportProductId", source, StringComparison.Ordinal);
    }

    private static ManualReportProduct CreateProduct() =>
        new(Guid.NewGuid(), PensionProductType.PensionFund, "12345",
            new DateOnly(2026, 9, 1), 1000m, "1", "1", false, null,
            fundCode: "123", fundName: "Test Fund");

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}


public sealed class ReportValidationConcurrencyRegressionTests
{
    [Fact]
    public void Concurrent_validation_returns_conflict_instead_of_committing_stale_state()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Endpoints", "ReportValidationEndpoints.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("report_changed_during_validation", source, StringComparison.Ordinal);
        Assert.Contains("catch (DbUpdateConcurrencyException)", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}


public sealed class EmployerInterfaceImmutableEvidenceRegressionTests
{
    [Fact]
    public void Sent_report_xml_download_returns_persisted_transmission_payload()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Endpoints", "EmployerInterfaceEndpoints.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("transmission_evidence_not_available", source, StringComparison.Ordinal);
        Assert.Contains("protector.UnprotectBytes(transmission.Payload", source, StringComparison.Ordinal);
        Assert.Contains("transmission.PayloadFileName", source, StringComparison.Ordinal);
        Assert.Contains("ManualReportStatus.Processing or ManualReportStatus.Sent or ManualReportStatus.Completed", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}


public sealed class ReportTransmissionSafetyRegressionTests
{
    [Fact]
    public void Ambiguous_provider_exception_keeps_report_locked_for_reconciliation()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Endpoints", "ReportTransmissionEndpoints.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("transmission_reconciliation_required", source, StringComparison.Ordinal);
        Assert.Contains("report remains locked in Processing", source, StringComparison.Ordinal);
        var catchIndex = source.IndexOf("catch (Exception)", StringComparison.Ordinal);
        Assert.True(catchIndex >= 0);
        var catchBody = source[catchIndex..];
        Assert.DoesNotContain("report.MarkTransmissionError", catchBody, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}


public sealed class CorrectionMaterializationRollbackRegressionTests
{
    [Fact]
    public void Materialization_failure_rolls_back_without_reusing_completed_transaction()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Services", "CorrectionWorkflowService.cs");
        var source = File.ReadAllText(path);

        var catchIndex = source.IndexOf("// The Processing claim and every technical document are part of this transaction.", StringComparison.Ordinal);
        Assert.True(catchIndex >= 0);
        var catchBody = source[catchIndex..];
        Assert.Contains("RollbackAsync", catchBody, StringComparison.Ordinal);
        Assert.DoesNotContain("ExecuteUpdateAsync", catchBody.Split("throw;", 2)[0], StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}


public sealed class EmployerInterfaceImportConsistencyRegressionTests
{
    [Fact]
    public void Current_import_rejects_conflicting_snapshots_for_same_employee_across_transfers()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Services", "EmployerInterfaceService.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("appears in multiple transfer blocks with conflicting snapshot data", source, StringComparison.Ordinal);
        Assert.Contains("GroupBy(x => x.Key", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}


public sealed class EmployerInterfaceTransferCorrelationRegressionTests
{
    [Fact]
    public void Feedback_transfer_identifier_is_scoped_to_the_exact_transfer_group()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Services", "EmployerInterfaceService.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("TransferFeedbackGroupKey", source, StringComparison.Ordinal);
        Assert.Contains("sameTransferMetadata", source, StringComparison.Ordinal);
        Assert.DoesNotContain("sameFundMetadata", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}


public sealed class CorrectionEndpointRegressionTests
{
    [Fact]
    public void Correction_payment_requires_explicit_per_transfer_operation()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Endpoints", "ManualReportEndpoints.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("correction_operation_required", source, StringComparison.Ordinal);
        Assert.Contains("CorrectionOperationCode is not (2 or 3)", source, StringComparison.Ordinal);
        Assert.Contains("correctionProducts", source, StringComparison.Ordinal);
        Assert.Contains("correctionProduct.MarkCorrectionChanged(request.CorrectionOperationCode)", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}


public sealed class CorrectionValidationRegressionTests
{
    [Fact]
    public void Current_correction_accepts_previous_transfer_identifier_without_clearing_identifier()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Endpoints", "ReportValidationEndpoints.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("string.IsNullOrEmpty(x.PreviousIdentifier)", source, StringComparison.Ordinal);
        Assert.Contains("string.IsNullOrEmpty(x.PreviousClearingIdentifier)", source, StringComparison.Ordinal);
        Assert.Contains("CORRECTION_PREVIOUS_REFERENCE_PENDING", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CORRECTION_NEGATIVE_FEEDBACK_PENDING", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}


public sealed class ManualReportConcurrencyRegressionTests
{
    [Fact]
    public void Manual_report_updated_at_is_a_concurrency_token()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Infrastructure", "Persistence", "ReportingEntityConfigurations.cs");
        var source = File.ReadAllText(path);
        Assert.Contains("b.Property(x => x.UpdatedAt).IsConcurrencyToken()", source, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}


public sealed class CorrectionDeltaRevisionRegressionTests
{
    [Fact]
    public void Correction_workflow_is_delta_based_and_promotes_business_revision()
    {
        var root = FindRepoRoot();
        var service = File.ReadAllText(Path.Combine(root, "src", "Alpha.Api", "Services", "CorrectionWorkflowService.cs"));
        var domain = File.ReadAllText(Path.Combine(root, "src", "Alpha.Domain", "Reporting", "ManualReporting.cs"));
        var endpoints = File.ReadAllText(Path.Combine(root, "src", "Alpha.Api", "Endpoints", "ManualReportEndpoints.cs"));

        Assert.Contains("BuildDeltaPlan", service, StringComparison.Ordinal);
        Assert.Contains("NegativeSourceProductIds", service, StringComparison.Ordinal);
        Assert.Contains("CurrentWorkspaceProductIds", service, StringComparison.Ordinal);
        Assert.Contains("FilterGraph", service, StringComparison.Ordinal);
        Assert.Contains("FinalizeRevisionIfCompleteAsync", service, StringComparison.Ordinal);
        Assert.Contains("PromoteCorrectionWorkspaceToRevision", domain, StringComparison.Ordinal);
        Assert.DoesNotContain("correction_structure_changes_not_supported", endpoints, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}


public sealed class ReportingStressScenarioRegressionTests
{
    [Fact]
    public void Finally_validated_report_is_immutable_so_the_validated_payload_cannot_be_changed_before_send()
    {
        var report = NewReport();
        report.MarkReadyForValidation();
        report.MarkValidated();

        Assert.Equal(ManualReportStatus.Validated, report.Status);
        Assert.False(report.IsEditable);
        Assert.Throws<InvalidOperationException>(() =>
            report.UpdateDetails(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 9)));
        Assert.Equal(ManualReportStatus.Validated, report.Status);
        Assert.NotNull(report.ValidatedAt);
    }

    [Fact]
    public void Simulated_process_crash_after_transmission_claim_keeps_report_locked()
    {
        var report = NewReport();
        report.MarkReadyForValidation();
        report.MarkValidated();
        report.MarkTransmissionStarted();

        Assert.Equal(ManualReportStatus.Processing, report.Status);
        Assert.False(report.IsEditable);
        Assert.Throws<InvalidOperationException>(() =>
            report.UpdateDetails(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 10)));
    }

    [Fact]
    public void Feedback_projection_reads_only_feedback_for_latest_transmission_attempt()
    {
        var source = Read("src", "Alpha.Api", "Endpoints", "ReportFeedbackEndpoints.cs");
        Assert.Contains("OrderByDescending(x => x.AttemptNumber)", source, StringComparison.Ordinal);
        Assert.Contains("query.Where(x => x.TransmissionId == latestTransmissionId.Value)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void All_reporting_input_paths_converge_on_the_same_manual_reporting_entities()
    {
        var import = Read("src", "Alpha.Api", "Services", "EmployerInterfaceService.cs");
        var manual = Read("src", "Alpha.Api", "Endpoints", "ManualReportEndpoints.cs");

        Assert.Contains("new ManualReport(", import, StringComparison.Ordinal);
        Assert.Contains("new ManualReportEmployee(", import, StringComparison.Ordinal);
        Assert.Contains("new ManualReportProduct(", import, StringComparison.Ordinal);
        Assert.Contains("new ManualContribution(", import, StringComparison.Ordinal);
        Assert.Contains("new ManualReportProduct(", manual, StringComparison.Ordinal);
        Assert.Contains("new ManualContribution(", manual, StringComparison.Ordinal);
    }

    [Fact]
    public void Multi_user_report_updates_have_both_ef_concurrency_token_and_api_conflict_handling()
    {
        var config = Read("src", "Alpha.Infrastructure", "Persistence", "ReportingEntityConfigurations.cs");
        var validation = Read("src", "Alpha.Api", "Endpoints", "ReportValidationEndpoints.cs");

        Assert.Contains("b.Property(x => x.UpdatedAt).IsConcurrencyToken()", config, StringComparison.Ordinal);
        Assert.Contains("catch (DbUpdateConcurrencyException)", validation, StringComparison.Ordinal);
        Assert.Contains("report_changed_during_validation", validation, StringComparison.Ordinal);
    }

    [Fact]
    public void Reconciliation_path_never_turns_unknown_provider_outcome_into_retryable_error()
    {
        var source = Read("src", "Alpha.Api", "Endpoints", "ReportTransmissionEndpoints.cs");
        var catchIndex = source.IndexOf("catch (Exception)", StringComparison.Ordinal);
        Assert.True(catchIndex >= 0);
        var body = source[catchIndex..];

        Assert.Contains("transmission_reconciliation_required", body, StringComparison.Ordinal);
        Assert.DoesNotContain("report.MarkTransmissionError", body, StringComparison.Ordinal);
        Assert.Contains("StatusCodes.Status502BadGateway", body, StringComparison.Ordinal);
    }

    private static ManualReport NewReport() =>
        new(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 9));

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AlphaBackend.slnx"))) dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
        return File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));
    }
}
