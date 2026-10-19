namespace Iron.Kernel.Tests;

public class TenantIdTests
{
    [Theory]
    [InlineData("acme")]
    [InlineData("acme-uk")]
    [InlineData("tenant42")]
    [InlineData("abc")]
    [InlineData("a23456789012345678901234567890")]
    public void AcceptsValidSlugs(string value)
    {
        Assert.True(TenantId.IsValid(value));
        Assert.Equal(value, TenantId.Parse(value).Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("a234567890123456789012345678901")]
    [InlineData("Acme")]
    [InlineData("1acme")]
    [InlineData("-acme")]
    [InlineData("acme-")]
    [InlineData("acme--uk")]
    [InlineData("acme_uk")]
    [InlineData("acme.uk")]
    [InlineData("acme uk")]
    public void RejectsInvalidSlugs(string value)
    {
        Assert.False(TenantId.IsValid(value));
        Assert.False(TenantId.TryParse(value, out _));
        Assert.Throws<FormatException>(() => TenantId.Parse(value));
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.False(TenantId.IsValid(null));
        Assert.False(TenantId.TryParse(null, out _));
    }

    [Fact]
    public void EqualIdsAreEqual()
    {
        Assert.Equal(TenantId.Parse("acme"), TenantId.Parse("acme"));
        Assert.NotEqual(TenantId.Parse("acme"), TenantId.Parse("bravo"));
        Assert.Equal("acme", TenantId.Parse("acme").ToString());
    }

    [Fact]
    public void TheDefaultValueIsEmpty()
    {
        TenantId id = default;

        Assert.True(id.IsEmpty);
        Assert.Equal(string.Empty, id.Value);
        Assert.False(TenantId.ControlPlane.IsEmpty);
        Assert.Equal("control", TenantId.ControlPlane.Value);
    }
}
