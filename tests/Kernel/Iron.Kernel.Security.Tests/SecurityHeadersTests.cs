using System.Net;

namespace Iron.Kernel.Security.Tests;

[Collection(SecurityCollection.Name)]
public class SecurityHeadersTests(SecurityFixture fixture)
{
    [Fact]
    public async Task EveryResponseCarriesTheSecurityHeaders()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/ok", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Contains("camera=()", Header(response, "Permissions-Policy"), StringComparison.Ordinal);
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
        Assert.Equal(SecurityHeadersMiddleware.HstsValue, Header(response, "Strict-Transport-Security"));

        var csp = Header(response, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HstsIsNotSentOverPlainHttp()
    {
        using var response = await fixture.Client.GetAsync(new Uri("http://localhost/ok"), TestContext.Current.CancellationToken);

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task ApiResponsesAreNeverCached()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/things", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("no-store", Header(response, "Cache-Control"));
    }

    [Fact]
    public async Task ExemptPathsGetNoContentSecurityPolicy()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/docs/page", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    [Fact]
    public async Task CookiesAreSecureHttpOnlyAndStrict()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/cookie", UriKind.Relative), TestContext.Current.CancellationToken);

        var setCookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AConfiguredOriginIsAllowedAndOthersAreNot()
    {
        using var allowed = new HttpRequestMessage(HttpMethod.Get, new Uri("/ok", UriKind.Relative));
        allowed.Headers.Add("Origin", "https://allowed.example");
        using var allowedResponse = await fixture.Client.SendAsync(allowed, TestContext.Current.CancellationToken);

        Assert.Equal("https://allowed.example", Header(allowedResponse, "Access-Control-Allow-Origin"));
        Assert.Contains("X-Correlation-Id", Header(allowedResponse, "Access-Control-Expose-Headers"), StringComparison.Ordinal);

        using var other = new HttpRequestMessage(HttpMethod.Get, new Uri("/ok", UriKind.Relative));
        other.Headers.Add("Origin", "https://evil.example");
        using var otherResponse = await fixture.Client.SendAsync(other, TestContext.Current.CancellationToken);

        Assert.False(otherResponse.Headers.Contains("Access-Control-Allow-Origin"));
    }

    private static string Header(HttpResponseMessage response, string name)
        => string.Join(", ", response.Headers.TryGetValues(name, out var values) ? values : response.Content.Headers.TryGetValues(name, out var contentValues) ? contentValues : []);
}
