using System.Net;

namespace Iron.Kernel.Security.Tests;

[Collection(SecurityCollection.Name)]
public class SecurityHeadersTests(SecurityFixture fixture)
{
    [Fact]
    [Proves("TF-070 AC1", "ADR-0010", "ADR-0053")]
    public async Task EveryResponseCarriesTheSecurityHeadersAndAPolicyThatReportsItsViolations()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/ok", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
        Assert.Equal("strict-origin-when-cross-origin", Header(response, "Referrer-Policy"));
        Assert.Contains("camera=()", Header(response, "Permissions-Policy"), StringComparison.Ordinal);
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Opener-Policy"));
        Assert.Equal(SecurityHeadersMiddleware.HstsValue, Header(response, "Strict-Transport-Security"));
        Assert.Equal("csp=\"/api/v1/csp-reports\"", Header(response, "Reporting-Endpoints"));

        var csp = Header(response, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp, StringComparison.Ordinal);
        Assert.Contains("frame-ancestors 'none'", csp, StringComparison.Ordinal);
        Assert.Contains("report-uri /api/v1/csp-reports", csp, StringComparison.Ordinal);
        Assert.Contains("report-to csp", csp, StringComparison.Ordinal);
        Assert.DoesNotContain("unsafe-inline", csp, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-070 AC1", "ADR-0053")]
    public async Task HstsIsSentEvenWhenTheIngressSpeaksHttpToTheHost()
    {
        using var response = await fixture.Client.GetAsync(new Uri("http://localhost/ok"), TestContext.Current.CancellationToken);

        Assert.Equal(SecurityHeadersMiddleware.HstsValue, Header(response, "Strict-Transport-Security"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    [Fact]
    [Proves("ADR-0010")]
    public async Task ApiResponsesAreNeverCached()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/things", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal("no-store", Header(response, "Cache-Control"));
    }

    [Fact]
    [Proves("ADR-0010")]
    public async Task ExemptPathsGetNoContentSecurityPolicy()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/docs/page", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.False(response.Headers.Contains("Content-Security-Policy"));
        Assert.Equal("DENY", Header(response, "X-Frame-Options"));
    }

    [Fact]
    [Proves("TF-070 AC4", "ADR-0010")]
    public async Task EveryCookieIsSecureHttpOnlyAndStrictWhateverItsCodeAsks()
    {
        using var response = await fixture.Client.GetAsync(new Uri("/cookie", UriKind.Relative), TestContext.Current.CancellationToken);

        var cookies = response.Headers.GetValues("Set-Cookie").ToList();
        Assert.Equal(3, cookies.Count);
        Assert.All(cookies, cookie =>
        {
            Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string Header(HttpResponseMessage response, string name)
        => string.Join(", ", response.Headers.TryGetValues(name, out var values) ? values : response.Content.Headers.TryGetValues(name, out var contentValues) ? contentValues : []);
}
