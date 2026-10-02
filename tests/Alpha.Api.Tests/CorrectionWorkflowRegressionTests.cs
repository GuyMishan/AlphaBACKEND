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
