using System.Net;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// The smoke's step for V2 (TF-244, ADR-0059): the tenant's suites as the control plane records them, then the unit's
/// answer. Where the tenant has the eProcurement Suite, its About page names the suite, the installation with the job that
/// made it, the plan, and the Purchasing module in force, and the context names the suite for the shell; where it does
/// not, the page answers not found and the context names no suite. It changes nothing, so it leaves nothing behind.
/// </summary>
public sealed class SuiteSteps(Api api, SmokeOptions options, Func<CancellationToken, Task<string>> signInAdministrator, string operatorToken)
{
    private const string Procurement = "procurement";

    public async Task<string> AboutAsync(CancellationToken cancellationToken)
    {
        var recorded = (await api.SendAsync(HttpMethod.Get, options.ControlUrl, $"/api/v1/tenants/{options.Tenant}/suites", bearer: operatorToken, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.OK, "the tenant's suites in the control plane");
        var suites = recorded.Body!.Value.EnumerateArray().Select(s => s.GetProperty("suite").GetString()).ToList();
        var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
        var about = await TenantAsync("/api/v1/suites/procurement/about", admin, cancellationToken).ConfigureAwait(false);
        var context = (await TenantAsync("/api/v1/me/context", admin, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the context");
        var named = context.Body!.Value.GetProperty("entitlements").TryGetProperty("suites", out var listed)
            ? listed.EnumerateArray().Select(s => s.GetProperty("code").GetString()).ToList()
            : [];

        if (!suites.Contains(Procurement))
        {
            about.Expect(HttpStatusCode.NotFound, "the About page of a suite the tenant does not have");
            if (named.Count > 0)
            {
                throw new SmokeException($"the context names {string.Join(", ", named)} for a tenant with no suite");
            }

            return $"{options.Tenant} has no suite: the eProcurement Suite's About page answers 404 and the context names none";
        }

        var page = about.Expect(HttpStatusCode.OK, "the eProcurement Suite's About page").Body!.Value;
        if (!named.Contains(Procurement))
        {
            throw new SmokeException($"the context names {(named.Count == 0 ? "no suite" : string.Join(", ", named))} for a tenant with the eProcurement Suite");
        }

        var installation = page.GetProperty("installation");
        if (installation.ValueKind != JsonValueKind.Object)
        {
            throw new SmokeException("the About page records no installation: the suite's seed did not run");
        }

        var plan = page.GetProperty("plan").GetString() ?? throw new SmokeException("the About page names no plan of the suite");
        var purchasing = page.GetProperty("modules").EnumerateArray().FirstOrDefault(m => m.GetProperty("code").GetString() == "purchasing");
        if (purchasing.ValueKind != JsonValueKind.Object || purchasing.GetProperty("status").GetString() != "enabled")
        {
            throw new SmokeException("the Purchasing module is not in force for a tenant with the suite's plan");
        }

        return $"{options.Tenant} has the eProcurement Suite under {plan}: installed {installation.GetProperty("installed_at").GetString()} by job {installation.GetProperty("installed_by_job").GetString() ?? "none"}, release {page.GetProperty("version").GetString()}; Purchasing enabled; the context names the suite";
    }

    private Task<Answer> TenantAsync(string path, string cookie, CancellationToken cancellationToken) =>
        api.SendAsync(HttpMethod.Get, options.IngressUrl, path, host: options.Hostname, cookie: cookie, cancellationToken: cancellationToken);
}
