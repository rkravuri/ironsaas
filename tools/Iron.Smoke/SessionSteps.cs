using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Iron.Smoke;

/// <summary>
/// The smoke's steps for S3 T5 (ADR-0049): an administrator ends a session of a person of the run with a reason, whose
/// next request is refused and whose end the audit trail names with who and why; and revokes everything of another,
/// whose every session stops at once. Each person is deactivated at the end, whatever the result, so runs leave nobody
/// behind.
/// </summary>
public sealed class SessionSteps(Api api, SmokeOptions options, SignIn signIn, Func<CancellationToken, Task<string>> signInAdministrator, string run)
{
    public async Task<string> EndSessionAsync(CancellationToken cancellationToken)
    {
        var reason = $"Smoke run {run}: a session ended by an administrator";
        string? personId = null;
        var finished = false;
        try
        {
            var (id, loginId, password) = await PersonAsync("smoke-session", cancellationToken).ConfigureAwait(false);
            personId = id;
            var session = await signIn.AsAsync(loginId, password, null, cancellationToken).ConfigureAwait(false);
            (await TenantAsync(HttpMethod.Get, "/api/v1/me", session, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the person's /me");

            // A fresh sign-in of the administrator: ending a session is behind a recent authentication (TF-248).
            var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
            var listed = (await TenantAsync(HttpMethod.Get, $"/api/v1/sessions?filter=principal_id:eq:{id}", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the person's sessions in the console");
            var sessionId = listed.Body!.Value.GetProperty("data").EnumerateArray().Single().GetProperty("id").GetString();
            (await TenantAsync(HttpMethod.Post, $"/api/v1/sessions/{sessionId}/end", admin, new { reason }, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.NoContent, "end the session");
            var refused = (await TenantAsync(HttpMethod.Get, "/api/v1/me", session, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.Unauthorized, "the ended session's next request");
            if (refused.Text("error_code") != "auth.session_ended")
            {
                throw new SmokeException($"the ended session was refused with {refused.Text("error_code")}, not auth.session_ended");
            }

            var audit = (await TenantAsync(HttpMethod.Get, $"/api/v1/audit-events?filter=record_id:eq:{id}&sort=-created_at&page_size=20", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the audit trail");
            var ended = audit.Body!.Value.GetProperty("data").EnumerateArray().FirstOrDefault(e => e.GetProperty("event_type").GetString() == "identity.session_ended");
            if (ended.ValueKind != JsonValueKind.Object || ended.GetProperty("reason").GetString() != reason)
            {
                throw new SmokeException("the audit trail has no session_ended event with the administrator's reason");
            }

            await signIn.SignOutAsync(admin, cancellationToken).ConfigureAwait(false);
            finished = true;
            return $"{loginId}'s session ended by an administrator: its next request answers {refused.Text("error_code")}, and the audit trail names {ended.GetProperty("actor_name").GetString()} and the reason";
        }
        finally
        {
            await DeactivateAsync(personId, finished, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> RevokePersonAsync(CancellationToken cancellationToken)
    {
        var reason = $"Smoke run {run}: everything of a person revoked";
        string? personId = null;
        var finished = false;
        try
        {
            var (id, loginId, password) = await PersonAsync("smoke-revoke", cancellationToken).ConfigureAwait(false);
            personId = id;
            var first = await signIn.AsAsync(loginId, password, null, cancellationToken).ConfigureAwait(false);
            var second = await signIn.AsAsync(loginId, password, null, cancellationToken).ConfigureAwait(false);

            var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
            var revoked = (await TenantAsync(HttpMethod.Post, $"/api/v1/users/{id}/revoke", admin, new { reason }, cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "revoke everything of the person");
            var count = revoked.Body!.Value.GetProperty("sessions_ended").GetInt32();
            var codes = new List<string?>();
            foreach (var session in new[] { first, second })
            {
                codes.Add((await TenantAsync(HttpMethod.Get, "/api/v1/me", session, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.Unauthorized, "a revoked session's next request").Text("error_code"));
            }

            if (count != 2 || codes.Any(code => code != "auth.session_revoked"))
            {
                throw new SmokeException($"revoking ended {count} session(s), and their next requests answered {string.Join(", ", codes)}");
            }

            await signIn.SignOutAsync(admin, cancellationToken).ConfigureAwait(false);
            finished = true;
            return $"everything of {loginId} revoked: {count} sessions ended, each answering auth.session_revoked at its next request";
        }
        finally
        {
            await DeactivateAsync(personId, finished, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A person of the run in the root unit, with no role, who signs in with a password made here.</summary>
    private async Task<(string Id, string LoginId, string Password)> PersonAsync(string prefix, CancellationToken cancellationToken)
    {
        var loginId = $"{prefix}-{run}@{options.Hostname}";
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
        var tree = (await TenantAsync(HttpMethod.Get, "/api/v1/organisation-units", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "the tree");
        var root = tree.Body!.Value.GetProperty("data").EnumerateArray().First(unit => unit.GetProperty("parent_id").ValueKind == JsonValueKind.Null).GetProperty("id").GetString();
        var created = (await TenantAsync(HttpMethod.Post, "/api/v1/users", admin, new { login_id = loginId, display_name = $"Smoke {prefix} {run}", home_unit_id = root }, cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.Created, "invite the person");
        var id = created.Body!.Value.GetProperty("user").GetProperty("id").GetString() ?? throw new SmokeException("the invitation answer has no id");
        var link = created.Body!.Value.GetProperty("invitation_link").GetString() ?? throw new SmokeException("the invitation answer has no link");
        await signIn.SignOutAsync(admin, cancellationToken).ConfigureAwait(false);
        (await api.SendAsync(HttpMethod.Post, options.IngressUrl, "/auth/invitations/accept", host: options.Hostname, json: new { token = link[(link.IndexOf('#', StringComparison.Ordinal) + 1)..], password }, cancellationToken: cancellationToken).ConfigureAwait(false))
            .Expect(HttpStatusCode.OK, "accept the invitation");
        return (id, loginId, password);
    }

    /// <summary>Deactivates the person with a fresh sign-in of the administrator; a failure to restore fails a step that passed.</summary>
    private async Task DeactivateAsync(string? personId, bool finished, CancellationToken cancellationToken)
    {
        if (personId is null)
        {
            return;
        }

        try
        {
            var admin = await signInAdministrator(cancellationToken).ConfigureAwait(false);
            (await TenantAsync(HttpMethod.Post, $"/api/v1/users/{personId}/deactivate", admin, cancellationToken: cancellationToken).ConfigureAwait(false)).Expect(HttpStatusCode.OK, "deactivate the person");
            await signIn.SignOutAsync(admin, cancellationToken).ConfigureAwait(false);
        }
        catch (SmokeException exception) when (!finished)
        {
            Console.Error.WriteLine($"  could not restore: {exception.Message}");
        }
    }

    private Task<Answer> TenantAsync(HttpMethod method, string path, string cookie, object? json = null, CancellationToken cancellationToken = default) =>
        api.SendAsync(method, options.IngressUrl, path, host: options.Hostname, cookie: cookie, json: json, cancellationToken: cancellationToken);
}
