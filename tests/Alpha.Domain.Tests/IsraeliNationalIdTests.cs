using Alpha.Domain.Common;
using Xunit;

namespace Alpha.Domain.Tests;

public sealed class IsraeliNationalIdTests
{
    [Theory]
    [InlineData("123456782")]
    [InlineData("000000018")]
    [InlineData("18")]
    public void Accepts_valid_check_digits_including_short_left_padded_values(string value) =>
        Assert.True(IsraeliNationalId.IsValid(value));

    [Theory]
    [InlineData("123456789")]
    [InlineData("1234567820")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(null)]
    [InlineData("12345678a")]
    [InlineData(" 123456782")]
    public void Rejects_invalid_check_digits_lengths_and_characters(string? value) =>
        Assert.False(IsraeliNationalId.IsValid(value));

    [Fact]
    public void Rejects_non_ascii_digits()
    {
        // U+0660..U+0669 are Arabic-Indic digits: char.IsDigit is true (four of six former copies of this check used it),
        // but they are not identity numbers and corrupt the weighted sum.
        Assert.False(IsraeliNationalId.IsValid("١٢٣٤٥٦٧٨٢"));
        Assert.False(IsraeliNationalId.IsValid("12345678٢"));
    }
}
