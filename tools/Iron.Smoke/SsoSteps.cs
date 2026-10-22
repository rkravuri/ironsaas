using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// The smoke's step for S3 T4 (ADR-0048): the tenant's "Smoke test provider" on the test provider (navikt's
/// mock-oauth2-server, profile sso), its connection tested, its client secret set anew; a person of the run signs in
/// through it with the authorization code flow and PKCE, is created at that first sign-in, and is deactivated at the end,
/// whatever the result. The provider stays, under one issuer per tenant, so runs do not pile providers up.
/// </summary>
public sealed class SsoSteps(Api api, SmokeOptions options, SignIn signIn, Func<CancellationToken, Task<string>> signInAdministrator, string run)
{
    public const string ProviderName = "Smoke test provider";

    /// <summary>The test provider as the ingress serves it, to the browser, the units and the smoke alike.</summary>
    public const string ProviderHost = "idp.localhost";

    public async Task<string> SignInThroughAsync(CancellationToken cancellationToken)
    {
        var domain = $"sso.{options.Hostname}";
        var email = $"smoke-sso-{run}@{domain}";
        var discovery = $"http://{ProviderHost}/smoke-{options.Tenant}/.well-known/openid-configuration";
        string? personId = null;
        var failures = new List<string>();
        var finished = false;
        try
        {
            // Connecting a provider asks for a recent sign-in: the administrator's is fresh.
            var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
            var test = (await TenantAsync(HttpMethod.Post, "/api/v1/sso/test", admin, new { discovery_url = discovery }, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "test the connection");
            var checks = test.Body!.Value.GetProperty("items").GetArrayLength();
            if (!test.Body!.Value.GetProperty("passed").GetBoolean())
            {
                throw new SmokeException($"the connection test of {discovery} failed: {test.Body}");
            }

            var listed = (await TenantAsync(HttpMethod.Get, "/api/v1/sso/providers?page_size=200", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "list the providers");
            var existing = listed.Body!.Value.GetProperty("data").EnumerateArray().FirstOrDefault(p => p.GetProperty("name").GetString() == ProviderName);
            var body = new
            {
                name = ProviderName,
                preset = "other",
                discovery_url = discovery,
                client_id = $"iron-smoke-{options.Tenant}",
                client_secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)),
                allowed_domains = new[] { domain },
                just_in_time = true,
                default_role_code = (string?)null,
                default_home_unit_id = (string?)null,
                sso_only = false,
                trust_provider_mfa = true,
                role_mappings = Array.Empty<object>(),
            };
            var providerId = existing.ValueKind == JsonValueKind.Object
                ? (await TenantAsync(HttpMethod.Put, $"/api/v1/sso/providers/{existing.GetProperty("id").GetString()}", admin, body, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "update the provider").Text("id")
                : (await TenantAsync(HttpMethod.Post, "/api/v1/sso/providers", admin, body, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.Created, "connect the provider").Text("id");
            await signIn.SignOutAsync(admin, cancellationToken).ConfigureAwait(false);

            // The browser's part: the unit's authorization address, the provider's login page with this sign-in's claims,
            // and the provider's way back with the code and the state, which the shell's callback page posts.
            var start = (await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/sso/start", host: options.Hostname, json: new { provider_id = providerId, return_to = "/" }, cancellationToken: cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.OK, "start the sign-in");
            var authorization = new Uri(start.Text("authorization_url") ?? throw new SmokeException("the start answered no authorization address"));
            var query = Query(authorization);
            if (query.GetValueOrDefault("code_challenge_method") != "S256" || string.IsNullOrEmpty(query.GetValueOrDefault("state")) || string.IsNullOrEmpty(query.GetValueOrDefault("nonce")))
            {
                throw new SmokeException($"the authorization address lacks PKCE, the state or the nonce: {authorization}");
            }

            var claims = JsonSerializer.Serialize(new { email, email_verified = true, name = $"Smoke SSO {run}" });
            var form = $"username={Uri.EscapeDataString($"smoke-sso-{run}")}&claims={Uri.EscapeDataString(claims)}";
            var back = await api.SendAsync(HttpMethod.Post, options.IngressUrl, authorization.PathAndQuery, host: authorization.Authority, form: form, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (back.Status is not (HttpStatusCode.Found or HttpStatusCode.SeeOther) || back.Location is null)
            {
                throw new SmokeException($"the test provider did not send the browser back: {(int)back.Status}; is its profile up (make test-provider)?");
            }

            var answer = Query(back.Location);
            var callback = (await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/sso/callback", host: options.Hostname, cookie: start.SetCookie, json: new { code = answer.GetValueOrDefault("code"), state = answer.GetValueOrDefault("state") }, cancellationToken: cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.OK, "complete the sign-in");
            if (callback.SetCookie?.StartsWith("iron.session=", StringComparison.Ordinal) != true)
            {
                throw new SmokeException($"the sign-in through {ProviderName} answered {SignIn.StepOf(callback) ?? "no session"}");
            }

            var me = (await TenantAsync(HttpMethod.Get, "/api/v1/me", callback.SetCookie, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "/me after the sign-in");
            personId = me.Text("id");
            if (me.Text("login_id") != email)
            {
                throw new SmokeException($"the sign-in through {ProviderName} signed in {me.Text("login_id")}, not {email}");
            }

            await signIn.SignOutAsync(callback.SetCookie, cancellationToken).ConfigureAwait(false);
            finished = true;
            return $"{ProviderName} at {discovery} passed the connection test ({checks} checks); {email} signed in through it with the code flow and PKCE and was created at that first sign-in";
        }
        finally
        {
            if (personId is not null)
            {
                try
                {
                    var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
                    (await TenantAsync(HttpMethod.Post, $"/api/v1/users/{personId}/deactivate", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "deactivate the person");
                    await signIn.SignOutAsync(admin, cancellationToken).ConfigureAwait(false);
                }
                catch (SmokeException exception)
                {
                    failures.Add(exception.Message);
                }
            }

            if (failures.Count > 0 && finished)
            {
                throw new SmokeException($"could not restore: {string.Join("; ", failures)}");
            }

            if (failures.Count > 0)
            {
                Console.Error.WriteLine($"  could not restore: {string.Join("; ", failures)}");
            }
        }
    }

    private static Dictionary<string, string> Query(Uri address) =>
        address.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty, StringComparer.Ordinal);

    private Task<Answer> TenantAsync(HttpMethod method, string path, string cookie, object? json = null, CancellationToken cancellationToken = default) =>
        api.SendAsync(method, options.IngressUrl, path, host: options.Hostname, cookie: cookie, json: json, cancellationToken: cancellationToken);
}
