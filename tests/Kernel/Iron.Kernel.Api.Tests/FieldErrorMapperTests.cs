using Iron.Kernel.Api.Validation;

namespace Iron.Kernel.Api.Tests;

public class FieldErrorMapperTests
{
    [Theory]
    [Proves("ADR-0009")]
    [InlineData("Name", "name")]
    [InlineData("UnitPrice", "unit_price")]
    [InlineData("Items[0].UnitPrice", "items[0].unit_price")]
    [InlineData("Address.PostalCode", "address.postal_code")]
    [InlineData("", "")]
    public void PropertyPathsBecomeSnakeCaseJsonPaths(string propertyPath, string expected)
    {
        Assert.Equal(expected, FieldErrorMapper.ToFieldName(propertyPath));
    }

    [Theory]
    [Proves("ADR-0009")]
    [InlineData("NotEmptyValidator", "not_empty")]
    [InlineData("GreaterThanOrEqualValidator", "greater_than_or_equal")]
    [InlineData("MaximumLengthValidator", "maximum_length")]
    [InlineData("custom_rule", "custom_rule")]
    [InlineData("", "invalid")]
    [InlineData(null, "invalid")]
    public void ValidatorNamesBecomeShortCodes(string? errorCode, string expected)
    {
        Assert.Equal(expected, FieldErrorMapper.ToCode(errorCode));
    }
}
