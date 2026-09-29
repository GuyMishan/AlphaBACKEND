using Xunit;
using Alpha.Api.Validation;
using Alpha.Domain.Employees;

namespace Alpha.Api.Tests;

public sealed class EmployeeIdentifierValidationTests
{
    [Fact]
    public void Israeli_id_uses_checksum_validation()
    {
        Assert.Null(ApiInputValidation.Employee(PersonIdentifierType.IsraeliId, "123456782", "ישראל", "ישראלי", "1", new DateOnly(2020, 1, 1)));
        Assert.Contains("תעודת הזהות", ApiInputValidation.Employee(PersonIdentifierType.IsraeliId, "123456789", "ישראל", "ישראלי", "1", new DateOnly(2020, 1, 1)));
    }

    [Theory]
    [InlineData("AB1234567")]
    [InlineData("P123456789012345")]
    public void Passport_accepts_non_empty_identifier_up_to_16_characters(string passport)
    {
        Assert.Null(ApiInputValidation.Employee(PersonIdentifierType.Passport, passport, "John", "Smith", "1", new DateOnly(2020, 1, 1)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AB 123")]
    [InlineData("12345678901234567")]
    public void Passport_rejects_invalid_wire_values(string passport)
    {
        Assert.NotNull(ApiInputValidation.Employee(PersonIdentifierType.Passport, passport, "John", "Smith", "1", new DateOnly(2020, 1, 1)));
    }
}
