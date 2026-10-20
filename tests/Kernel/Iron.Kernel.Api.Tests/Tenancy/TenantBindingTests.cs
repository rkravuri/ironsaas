using Iron.Kernel.Api.Tenancy;
using Microsoft.Extensions.Configuration;

namespace Iron.Kernel.Api.Tests.Tenancy;

public class TenantBindingTests
{
    [Fact]
    [Proves("ADR-0011")]
    public void BindsTheTenantAndItsHostnamesFromConfiguration()
    {
        var configuration = Build(("Tenant:Id", "acme"), ("Tenant:Hostnames:0", "acme.localhost"), ("Tenant:Hostnames:1", "Acme.Example.COM:443"));

        var tenant = TenantBinding.FromConfiguration(configuration);

        Assert.Equal("acme", tenant.Id.Value);
        Assert.True(tenant.ServesHost("acme.localhost"));
        Assert.True(tenant.ServesHost("acme.example.com"));
        Assert.False(tenant.ServesHost("bravo.localhost"));
    }

    [Theory]
    [Proves("ADR-0011")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Acme Corp")]
    [InlineData("-acme")]
    public void RefusesAMissingOrInvalidTenantId(string? id)
    {
        var configuration = Build(("Tenant:Id", id), ("Tenant:Hostnames:0", "acme.localhost"));

        var exception = Assert.Throws<InvalidOperationException>(() => TenantBinding.FromConfiguration(configuration));

        Assert.Contains("Tenant:Id", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("ADR-0019")]
    public void AcceptsACommaSeparatedHostnameListAsWell()
    {
        var configuration = Build(("Tenant:Id", "acme"), ("Tenant:HostnameList", "acme.localhost, Acme.Example.COM"));

        var tenant = TenantBinding.FromConfiguration(configuration);

        Assert.Equal(["acme.localhost", "acme.example.com"], tenant.Hostnames);
    }

    [Fact]
    [Proves("ADR-0011")]
    public void RefusesAnEmptyHostnameList()
    {
        var configuration = Build(("Tenant:Id", "acme"));

        var exception = Assert.Throws<InvalidOperationException>(() => TenantBinding.FromConfiguration(configuration));

        Assert.Contains("Tenant:Hostnames", exception.Message, StringComparison.Ordinal);
    }

    private static IConfiguration Build(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();
}
