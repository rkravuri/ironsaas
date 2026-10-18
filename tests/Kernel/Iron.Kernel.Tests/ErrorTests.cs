namespace Iron.Kernel.Tests;

public class ErrorTests
{
    [Theory]
    [InlineData("identity.login.invalid_credentials")]
    [InlineData("authz.permission_denied")]
    [InlineData("tenancy.hostname.taken")]
    [InlineData("a1.b2")]
    public void AcceptsWellFormedCodes(string code)
    {
        Assert.True(ErrorCodes.IsValid(code));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nodots")]
    [InlineData("Identity.Login")]
    [InlineData("identity..login")]
    [InlineData("identity.login.")]
    [InlineData(".identity.login")]
    [InlineData("identity.1login")]
    [InlineData("identity.log in")]
    [InlineData("identity.log-in")]
    public void RejectsBadlyFormedCodes(string code)
    {
        Assert.False(ErrorCodes.IsValid(code));
    }

    [Fact]
    public void TheConstructorRejectsABadCode()
    {
        Assert.Throws<ArgumentException>(() => Error.NotFound("BadCode", "message"));
    }

    [Fact]
    public void FactoriesSetTheKind()
    {
        Assert.Equal(ErrorKind.Validation, Error.Validation("tests.a.b", "m").Kind);
        Assert.Equal(ErrorKind.Unauthorized, Error.Unauthorized("tests.a.b", "m").Kind);
        Assert.Equal(ErrorKind.Forbidden, Error.Forbidden("tests.a.b", "m").Kind);
        Assert.Equal(ErrorKind.NotFound, Error.NotFound("tests.a.b", "m").Kind);
        Assert.Equal(ErrorKind.Conflict, Error.Conflict("tests.a.b", "m").Kind);
        Assert.Equal(ErrorKind.Unexpected, Error.Unexpected("tests.a.b", "m").Kind);
    }

    [Fact]
    public void FieldsAreEmptyUnlessGiven()
    {
        var plain = Error.Conflict("tests.a.b", "m");
        var withFields = Error.Validation("tests.a.b", "m", new FieldError("email", "required", "The email is required."));

        Assert.Empty(plain.Fields);
        var field = Assert.Single(withFields.Fields);
        Assert.Equal("email", field.Field);
        Assert.Equal("required", field.Code);
    }
}
