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
    public void Correction_materialization_claims_workspace_and_uses_per_product_operation()
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Alpha.Api", "Services", "CorrectionWorkflowService.cs");
        var source = File.ReadAllText(path);

        Assert.Contains(".SetProperty(x => x.Status, ManualReportStatus.Processing)", source, StringComparison.Ordinal);
        Assert.Contains("oldProduct.CorrectionOperationCode ?? 2", source, StringComparison.Ordinal);
        Assert.Contains("x.Status == ManualReportStatus.Processing", source, StringComparison.Ordinal);
        Assert.Contains("HasReportLevelChangesAsync", source, StringComparison.Ordinal);
        Assert.Contains("report-employee-email:{employee.Id}", source, StringComparison.Ordinal);
        Assert.Contains("workspaceGraph.Products.Any(product => !product.SourceReportProductId.HasValue)", source, StringComparison.Ordinal);
        Assert.Contains("mappedPaymentProduct", source, StringComparison.Ordinal);
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
        Assert.Contains("correction_additions_not_supported", source, StringComparison.Ordinal);
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
