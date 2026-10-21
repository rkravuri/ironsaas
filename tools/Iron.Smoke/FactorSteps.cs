using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// The smoke's step for S3 T3 (ADR-0047): a person of the run enrols an authenticator app in My profile, gets ten recovery
/// codes, and from then on their password alone signs them in no more. A wrong code is refused, the app's code signs them
/// in, and a recovery code signs them in once and asks for a new factor. The person is deactivated at the end, whatever
/// the result, so runs leave nobody behind.
/// </summary>
public sealed class FactorSteps(Api api, SmokeOptions options, SignIn signIn, Func<CancellationToken, Task<string>> signInAdministrator, string run)
{
    public async Task<string> AuthenticatorAppAsync(CancellationToken cancellationToken)
    {
        var loginId = $"smoke-factor-{run}@{options.Hostname}";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        string? personId = null;
        var failures = new List<string>();
        var finished = false;
        try
        {
            var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
            var tree = (await TenantAsync(HttpMethod.Get, "/api/v1/organisation-units", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the tree");
            var root = tree.Body!.Value.GetProperty("data").EnumerateArray().First(unit => unit.GetProperty("parent_id").ValueKind == JsonValueKind.Null).GetProperty("id").GetString();
            var created = (await TenantAsync(HttpMethod.Post, "/api/v1/users", admin, new { login_id = loginId, display_name = $"Smoke factor {run}", home_unit_id = root }, cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.Created, "invite the person");
            personId = created.Body!.Value.GetProperty("user").GetProperty("id").GetString();
            var link = created.Body!.Value.GetProperty("invitation_link").GetString() ?? throw new SmokeException("the invitation answer has no link");
            await signIn.SignOutAsync(admin, cancellationToken).ConfigureAwait(false);
            (await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/invitations/accept", host: options.Hostname, json: new { token = link[(link.IndexOf('#', StringComparison.Ordinal) + 1)..], password }, cancellationToken: cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.OK, "accept the invitation");

            // No role, so no factor is required: the password signs them in, and they add an app in My profile.
            var session = await signIn.AsAsync(loginId, password, null, cancellationToken).ConfigureAwait(false);
            var setup = (await TenantAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app", session, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "start an authenticator app");
            var app = new Authenticator(setup.Text("secret") ?? throw new SmokeException("the authenticator app came without its key"));
            var enrolled = (await TenantAsync(HttpMethod.Post, "/api/v1/me/factors/authenticator-app/confirm", session, new { code = await app.NextCodeAsync(cancellationToken).ConfigureAwait(false), name = "Smoke phone" }, cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.OK, "confirm the authenticator app");
            var codes = enrolled.Body!.Value.GetProperty("recovery_codes").EnumerateArray().Select(code => code.GetString()!).ToList();
            if (codes.Count != 10)
            {
                throw new SmokeException($"the first factor came with {codes.Count} recovery codes, not ten");
            }

            await signIn.SignOutAsync(session, cancellationToken).ConfigureAwait(false);

            // The password alone now opens no session: the second step, where a wrong code is refused and the app's signs in.
            var step = (await signIn.PasswordAsync(loginId, password, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the password step");
            if (SignIn.StepOf(step) != "second_factor" || step.SetCookie?.StartsWith("iron.session=", StringComparison.Ordinal) == true)
            {
                throw new SmokeException($"the password alone answered {SignIn.StepOf(step) ?? "a session"} for a person with a factor");
            }

            var wrong = (await TenantAsync(HttpMethod.Post, "/auth/second-factor", step.SetCookie!, new { code = app.WrongCode() }, cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.Unauthorized, "a wrong code");
            var signedIn = await signIn.SecondFactorAsync(loginId, step.SetCookie!, app, cancellationToken).ConfigureAwait(false);
            var me = (await TenantAsync(HttpMethod.Get, "/api/v1/me", signedIn, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "/me after the second step");
            await signIn.SignOutAsync(signedIn, cancellationToken).ConfigureAwait(false);

            // A recovery code signs in once and asks for a new factor; the same code again is refused.
            var recovery = (await signIn.PasswordAsync(loginId, password, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the password step");
            var recovered = (await TenantAsync(HttpMethod.Post, "/auth/second-factor", recovery.SetCookie!, new { recovery_code = codes[0] }, cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.OK, "sign in with a recovery code");
            if (!recovered.Body!.Value.GetProperty("enrol_new_factor").GetBoolean())
            {
                throw new SmokeException("a sign-in by recovery code did not ask for a new factor");
            }

            await signIn.SignOutAsync(recovered.SetCookie!, cancellationToken).ConfigureAwait(false);
            var again = (await signIn.PasswordAsync(loginId, password, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the password step");
            var reused = (await TenantAsync(HttpMethod.Post, "/auth/second-factor", again.SetCookie!, new { recovery_code = codes[0] }, cancellationToken).ConfigureAwait(false))
                .Expect(HttpStatusCode.Unauthorized, "the recovery code used twice");
            finished = true;
            return $"{loginId} enrolled an app with ten recovery codes; the password alone answers the second step, a wrong code {wrong.Text("error_code")}, the app's code signs in as {me.Text("login_id")}, a recovery code signs in once and asks for a new factor, and twice answers {reused.Text("error_code")}";
        }
        finally
        {
            if (personId is not null)
            {
                try
                {
                    // A fresh sign-in, so deactivating, a high-risk action, needs no confirmation (TF-248).
                    var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
                    (await TenantAsync(HttpMethod.Post, $"/api/v1/users/{personId}/deactivate", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "deactivate the person");
                    await signIn.SignOutAsync(admin, cancellationToken).ConfigureAwait(false);
                }
                catch (SmokeException exception)
                {
                    failures.Add(exception.Message);
                }
            }

            // A failure to restore fails a step that passed; a step that failed already says why, and this follows it.
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

    private Task<Answer> TenantAsync(HttpMethod method, string path, string cookie, object? json = null, CancellationToken cancellationToken = default) =>
        api.SendAsync(method, options.IngressUrl, path, host: options.Hostname, cookie: cookie, json: json, cancellationToken: cancellationToken);
}
