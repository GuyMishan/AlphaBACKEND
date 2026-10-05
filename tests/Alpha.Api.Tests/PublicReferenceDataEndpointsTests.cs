using Alpha.Api.Endpoints;
using Xunit;

namespace Alpha.Api.Tests;

public sealed class PublicReferenceDataEndpointsTests
{
    [Theory]
    [InlineData("964 · הפניקס השתלמות כללי · הפניקס פנסיה וגמל בע\"מ", "964")]
    [InlineData(" 964 · הפניקס השתלמות כללי ", "964")]
    [InlineData("הפניקס השתלמות", "הפניקס השתלמות")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void Pension_product_search_normalizes_selected_display_labels(string search, string? expected)
    {
        Assert.Equal(expected, PublicReferenceDataEndpoints.NormalizePensionProductSearch(search));
    }
}
