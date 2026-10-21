namespace Iron.Kernel.Tests;

public class TenantContextTests
{
    private static readonly TenantId Acme = TenantId.Parse("acme");

    [Fact]
    [Proves("ADR-0011")]
    public void NormalisesHostnamesAndKeepsTheFirstAsPrimary()
    {
        var context = new TenantContext(Acme, ["Acme.localhost:443", "acme.example.com", "acme.localhost"]);

        Assert.Equal("acme.localhost", context.PrimaryHostname);
        string[] expected = ["acme.localhost", "acme.example.com"];
        Assert.Equal(expected, context.Hostnames);
    }

    [Theory]
    [Proves("ADR-0011")]
    [InlineData("acme.localhost", true)]
    [InlineData("ACME.LOCALHOST", true)]
    [InlineData("acme.localhost:443", true)]
    [InlineData("acme.example.com:8443", true)]
    [InlineData("bravo.localhost", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ServesOnlyItsOwnHostnames(string? host, bool expected)
    {
        var context = new TenantContext(Acme, ["acme.localhost", "acme.example.com"]);

        Assert.Equal(expected, context.ServesHost(host));
    }

    [Fact]
    [Proves("ADR-0011")]
    public void RejectsAnEmptyTenantOrNoHostnames()
    {
        Assert.Throws<ArgumentException>(() => new TenantContext(default, ["acme.localhost"]));
        Assert.Throws<ArgumentException>(() => new TenantContext(Acme, []));
        Assert.Throws<ArgumentException>(() => new TenantContext(Acme, [" "]));
    }

    [Fact]
    [Proves("ADR-0007")]
    public void KnowsWhenItIsTheControlPlane()
    {
        Assert.True(new TenantContext(TenantId.ControlPlane, ["control.localhost"]).IsControlPlane);
        Assert.False(new TenantContext(Acme, ["acme.localhost"]).IsControlPlane);
    }

    [Fact]
    [Proves("ADR-0011")]
    public void NormaliseHostKeepsAnIpv6LiteralIntact()
    {
        Assert.Equal("[::1]:443", TenantContext.NormaliseHost("[::1]:443"));
        Assert.Equal("acme.localhost", TenantContext.NormaliseHost(" Acme.Localhost:80 "));
    }
}
