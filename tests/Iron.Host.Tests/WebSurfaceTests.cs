using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using DotNet.Testcontainers.Builders;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;

namespace Iron.Host.Tests;

/// <summary>
/// The web surface of every host (TF-070, ADR-0053): the headers each answer carries, whatever the path and the status,
/// from the API and the control plane in memory and from the shell's own nginx configuration in its image's server.
/// </summary>
public partial class WebSurfaceTests
{
    /// <summary>The shell's web server: the image the shell's Dockerfile builds on, with the repository's configuration.</summary>
    private const string ShellServerImage = "nginxinc/nginx-unprivileged:1.31-alpine";

    private static readonly string[] Headers =
    [
        "Content-Security-Policy", "Reporting-Endpoints", "Strict-Transport-Security", "X-Frame-Options", "X-Content-Type-Options", "Referrer-Policy", "Permissions-Policy",
    ];

    static WebSurfaceTests()
    {
        Environment.SetEnvironmentVariable("Tenant__Id", "acme");
        Environment.SetEnvironmentVariable("Tenant__Hostnames__0", "acme.localhost");
        Environment.SetEnvironmentVariable("ConnectionStrings__Database", "Host=localhost;Port=1;Database=nowhere;Username=nobody;Password=nothing;Timeout=1");
        Environment.SetEnvironmentVariable("Telemetry__ExportToCollector", "false");
        Environment.SetEnvironmentVariable("Heartbeat__Interval", "01:00:00");
        Environment.SetEnvironmentVariable("Storage__ConnectionString", "UseDevelopmentStorage=true");
        Environment.SetEnvironmentVariable("Storage__Container", "acme");
        Environment.SetEnvironmentVariable("Fleet__PollSeconds", "3600");
    }

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-070 AC1", "ADR-0053")]
    public async Task EveryAnswerOfTheApiAndTheControlPlaneCarriesTheHeadersWithAPolicyWithoutInlineCode()
    {
        await using var api = new WebApplicationFactory<Iron.Host.Api.ApiHost>().WithWebHostBuilder(host => host.UseEnvironment(Environments.Production));
        await using var controlPlane = new WebApplicationFactory<Iron.Host.ControlPlane.ControlPlaneHost>().WithWebHostBuilder(host => host.UseEnvironment(Environments.Production));
        var problems = new List<string>();
        foreach (var (name, client) in new[] { ("api", api.CreateClient()), ("control plane", controlPlane.CreateClient()) })
        {
            using (client)
            {
                // A success, a refusal, a path nothing serves and a method the path does not take.
                foreach (var (method, path) in new[] { (HttpMethod.Get, "/health/live"), (HttpMethod.Get, "/api/v1/me"), (HttpMethod.Get, "/api/v1/nothing-here"), (HttpMethod.Delete, "/auth/login") })
                {
                    using var request = new HttpRequestMessage(method, path);
                    request.Headers.Host = name == "api" ? "acme.localhost" : "localhost";
                    using var response = await client.SendAsync(request, Cancel);
                    problems.AddRange(Missing(response).Select(missing => $"{name} {method} {path} ({(int)response.StatusCode}): {missing}"));
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    [Proves("TF-070 AC1", "ADR-0053")]
    public async Task TheShellsServerSendsTheHeadersAFreshStyleNonceEachTimeAndAnswers404ForWhatItDoesNotServe()
    {
        var root = RepositoryRoot();
        var page = Path.Combine(Path.GetTempPath(), $"shell-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(page, "assets"));
        await File.WriteAllTextAsync(Path.Combine(page, "index.html"), """<!doctype html><html><head><meta property="csp-nonce" nonce="CSP_NONCE"><script type="module" nonce="CSP_NONCE" src="/assets/app.js"></script></head><body></body></html>""", Cancel);
        await File.WriteAllTextAsync(Path.Combine(page, "assets", "app.js"), "export const shell = true;", Cancel);
        var container = new ContainerBuilder(ShellServerImage)
            .WithResourceMapping(new FileInfo(Path.Combine(root, "shell", "nginx.conf")), "/etc/nginx/conf.d/")
            .WithResourceMapping(new FileInfo(Path.Combine(root, "shell", "security-headers.conf")), "/etc/nginx/snippets/")
            .WithResourceMapping(new DirectoryInfo(page), "/usr/share/nginx/html/")
            .WithCommand("sh", "-c", "mv /etc/nginx/conf.d/nginx.conf /etc/nginx/conf.d/default.conf && exec nginx -g 'daemon off;'")
            .WithPortBinding(8080, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(8080).ForPath("/healthz")))
            .Build();
        await using (container)
        {
            await container.StartAsync(Cancel);
            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{container.GetMappedPublicPort(8080)}") };

            var problems = new List<string>();
            var nonces = new List<string>();
            foreach (var (path, status) in new[]
            {
                ("/", HttpStatusCode.OK), ("/console/units", HttpStatusCode.OK), ("/assets/app.js", HttpStatusCode.OK),
                ("/api/v1/me", HttpStatusCode.NotFound), ("/health/ready", HttpStatusCode.NotFound), ("/auth/login", HttpStatusCode.NotFound),
                ("/connect/token", HttpStatusCode.NotFound), ("/.well-known/jwks.json", HttpStatusCode.NotFound), ("/missing.js", HttpStatusCode.NotFound),
            })
            {
                using var response = await client.GetAsync(new Uri(path, UriKind.Relative), Cancel);
                var body = await response.Content.ReadAsStringAsync(Cancel);
                if (response.StatusCode != status)
                {
                    problems.Add($"{path}: {(int)response.StatusCode}, not {(int)status}");
                }

                problems.AddRange(Missing(response).Select(missing => $"{path}: {missing}"));
                if (path is "/" or "/console/units")
                {
                    // The page's nonce is the one the policy names for style elements, and it is never the placeholder.
                    var named = StyleNonce().Match(response.Headers.GetValues("Content-Security-Policy").Single()).Groups[1].Value;
                    if (named.Length < 16 || !body.Contains($"nonce=\"{named}\"", StringComparison.Ordinal) || body.Contains("CSP_NONCE", StringComparison.Ordinal))
                    {
                        problems.Add($"{path}: the page's nonce is not the policy's ({named})");
                    }

                    nonces.Add(named);
                }

                if (status == HttpStatusCode.NotFound && body.Contains("csp-nonce", StringComparison.Ordinal))
                {
                    problems.Add($"{path}: answered the shell's page");
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
            Assert.Equal(nonces.Count, nonces.Distinct(StringComparer.Ordinal).Count());
        }

        Directory.Delete(page, recursive: true);
    }

    [Fact]
    [Proves("TF-070 AC4", "ADR-0053")]
    public void NoCodeWritesACookieAroundTheCookiePolicy()
    {
        // Every cookie goes through Response.Cookies, where the policy makes it Secure, HttpOnly and SameSite=Strict; a
        // header written by hand would pass it by.
        var offenders = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => File.ReadAllText(file).Contains("\"Set-Cookie\"", StringComparison.OrdinalIgnoreCase) || File.ReadAllText(file).Contains("Headers.SetCookie", StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(RepositoryRoot(), file))
            .ToList();

        Assert.Empty(offenders);
    }

    private static IEnumerable<string> Missing(HttpResponseMessage response)
    {
        foreach (var header in Headers)
        {
            if (!response.Headers.Contains(header))
            {
                yield return $"no {header}";
            }
        }

        if (response.Headers.TryGetValues("Content-Security-Policy", out var policies))
        {
            var policy = string.Join(";", policies);
            if (policy.Contains("unsafe-inline", StringComparison.Ordinal) || !policy.Contains("report-uri /api/v1/csp-reports", StringComparison.Ordinal) || !policy.Contains("report-to csp", StringComparison.Ordinal))
            {
                yield return $"a policy that allows inline code or reports nowhere: {policy}";
            }
        }

        if (response.Headers.TryGetValues("X-Frame-Options", out var frames) && frames.Single() != "DENY")
        {
            yield return "frames allowed";
        }
    }

    [GeneratedRegex("style-src 'self' 'nonce-([A-Za-z0-9]+)'")]
    private static partial Regex StyleNonce();

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Foundation.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Foundation.slnx was not found above the test's folder.");
    }
}
