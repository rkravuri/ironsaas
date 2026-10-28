// The test of csharp.yaml beside it (ADR-0055): each "ruleid" line names the rule the next line must raise, each "ok"
// line a rule the next line must not. make security runs the scanner's test mode over this folder. Not compiled.
using System.Diagnostics;
using System.Net.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

public sealed class RuleExamples
{
    public HttpClient Clients()
    {
        // ruleid: iron-tls-validation-disabled
        var any = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        var handler = new HttpClientHandler();
        // ruleid: iron-tls-validation-disabled
        handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true;
        // ok: iron-tls-validation-disabled
        handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => certificate is not null && trusted.Contains(certificate.Thumbprint);
        return new HttpClient(handler);
    }

    public void Logs(ILogger logger, LoginRequest request, string token, int tokenCount, string password)
    {
        // ruleid: iron-secret-in-log
        logger.LogInformation("Signing in {Login} with {Password}", request.Login, request.Password);
        // ruleid: iron-secret-in-log
        logger.LogDebug("Token {Token}", token);
        // ruleid: iron-secret-in-log
        logger.LogError("Could not connect with {Password}", password);
        // ok: iron-secret-in-log
        logger.LogInformation("Issued {Count} tokens to {Login}", tokenCount, request.Login);
        // ok: iron-secret-in-log
        logger.LogInformation("The password of {Login} changed", request.Login);
    }

    public void Processes(string image, string name)
    {
        // ruleid: iron-process-arguments-as-text
        var start = new ProcessStartInfo("docker", $"run --name {name} {image}");
        // ruleid: iron-process-arguments-as-text
        var other = new ProcessStartInfo("docker") { Arguments = "pull " + image };
        // ruleid: iron-process-arguments-as-text
        Process.Start("docker", "pull " + image);
        // ok: iron-process-arguments-as-text
        var safe = new ProcessStartInfo("docker") { RedirectStandardOutput = true };
        safe.ArgumentList.Add("pull");
        safe.ArgumentList.Add(image);
    }

    public IResult Answers(HttpContext context)
    {
        try
        {
            return Results.Ok();
        }
        catch (InvalidOperationException exception)
        {
            // ruleid: iron-exception-text-in-answer
            return Results.Problem(exception.Message);
        }
        catch (Exception failure)
        {
            // ruleid: iron-exception-text-in-answer
            _ = context.Response.WriteAsync(failure.ToString());
            // ok: iron-exception-text-in-answer
            return Results.Problem("The request could not be completed.");
        }
    }

    public void Cookies(HttpResponse response)
    {
        // ruleid: iron-cookie-without-protection
        response.Cookies.Append("a", "b", new CookieOptions { Secure = false });
        // ruleid: iron-cookie-without-protection
        response.Cookies.Append("a", "b", new CookieOptions { HttpOnly = false, Secure = true });
        // ruleid: iron-cookie-without-protection
        response.Cookies.Append("a", "b", new CookieOptions { SameSite = SameSiteMode.None });
        // ok: iron-cookie-without-protection
        response.Cookies.Append("a", "b", new CookieOptions { Secure = true, HttpOnly = true, SameSite = SameSiteMode.Strict });
    }

    public void Store(HttpClient client, SecretsConfiguration configuration)
    {
        // ruleid: iron-secret-store-outside-security
        var store = new OpenBaoSecretStore(client, configuration);
        // ruleid: iron-secret-store-outside-security
        _ = client.GetAsync("/v1/secret/data/tenants/acme");
        // ok: iron-secret-store-outside-security
        _ = client.GetAsync("/api/v1/me");
    }
}
