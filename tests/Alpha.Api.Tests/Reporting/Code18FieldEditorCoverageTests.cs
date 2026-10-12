using Alpha.Infrastructure.Persistence;
using Xunit;

namespace Alpha.Api.Tests.Reporting;

/// <summary>
/// Official workbook contract for numbered code-18 fields. A valid field number
/// is not by itself permission to edit or to change another, similarly named field.
/// </summary>
public sealed class Code18FieldEditorCoverageTests
{
    [Theory]
    [InlineData(35, "reportTransfer")]
    [InlineData(37, "reportTransfer")]
    [InlineData(38, "reportTransfer")]
    [InlineData(40, "reportTransfer")]
    [InlineData(41, "reportTransfer")]
    [InlineData(42, "reportTransfer")]
    [InlineData(44, "reportTransfer")]
    [InlineData(45, "reportTransfer")]
    [InlineData(46, "reportTransfer")]
    [InlineData(49, "reportTransfer")]
    [InlineData(50, "reportTransfer")]
    [InlineData(51, "reportTransfer")]
    [InlineData(52, "reportTransfer")]
    [InlineData(53, "reportTransfer")]
    [InlineData(59, "depositProduct")]
    [InlineData(60, "depositProduct")]
    [InlineData(70, "depositEmployeeStatus")]
    [InlineData(71, "depositEmployeeStatus")]
    [InlineData(72, "depositEmployeeStatus")]
    [InlineData(73, "depositEmployeeStatus")]
    [InlineData(74, "depositEmployeeStatus")]
    [InlineData(75, "depositEmployeeStatus")]
    [InlineData(76, "depositEmployeeStatus")]
    [InlineData(77, "depositEmployeeStatus")]
    [InlineData(78, "depositEmployeeStatus")]
    [InlineData(79, "depositContributions")]
    [InlineData(80, "depositContributions")]
    [InlineData(81, "depositContributions")]
    [InlineData(82, "depositContributions")]
    public void Supported_editable_field_numbers_belong_to_the_expected_official_source_block(int number, string surface)
    {
        Assert.NotNull(EmployerInterface006FieldMapInitializer.Find(12, number));
        Assert.Equal(surface, EmployerInterface006FieldMapInitializer.EditSurface(12, number));
    }

    [Theory]
    [InlineData(1, "fileHeader")]
    [InlineData(20, "integrationIdentity")]
    [InlineData(61, "depositProduct")]
    [InlineData(62, "depositProduct")]
    [InlineData(69, "depositProduct")]
    [InlineData(83, "depositContributions")]
    [InlineData(84, "depositContributions")]
    [InlineData(90, "fileClosing")]
    [InlineData(95, "fileClosing")]
    public void Other_mapped_fields_are_not_assumed_to_have_a_user_editor(int number, string surface)
    {
        Assert.NotNull(EmployerInterface006FieldMapInitializer.Find(12, number));
        Assert.Equal(surface, EmployerInterface006FieldMapInitializer.EditSurface(12, number));
    }

    [Fact]
    public void Invalid_missing_field_numbers_never_route_to_a_field_editor()
    {
        Assert.Null(EmployerInterface006FieldMapInitializer.Code18FieldNumber(null));
        Assert.Null(EmployerInterface006FieldMapInitializer.Code18FieldNumber(0));
        Assert.Null(EmployerInterface006FieldMapInitializer.Code18FieldNumber(79.5m));
        Assert.Null(EmployerInterface006FieldMapInitializer.Code18FieldNumber(-1));
        Assert.Null(EmployerInterface006FieldMapInitializer.Find(12, 999));
        Assert.Equal("unknown", EmployerInterface006FieldMapInitializer.EditSurface(14, 79));
    }
}
