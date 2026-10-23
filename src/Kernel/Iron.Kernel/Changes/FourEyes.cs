using System.Text.Json;

namespace Iron.Kernel;

/// <summary>
/// The areas of a tenant's configuration that may need a second administrator (TF-068, ADR-0052). A tenant lists the ones
/// it wants in <c>changes.four_eyes.areas</c>. Entitlements are the operator's, not the tenant's, so they are not here.
/// </summary>
public static class ConfigurationAreas
{
    /// <summary>Roles and the assignments of roles to people and service accounts.</summary>
    public const string Roles = "roles";

    /// <summary>The company logins (OpenID Connect providers) of the tenant.</summary>
    public const string SingleSignOn = "single_sign_on";

    /// <summary>How long each class of records and the audit trail are kept.</summary>
    public const string Retention = "retention";

    /// <summary>The MFA policy and the password policy, with its lockout.</summary>
    public const string MfaAndPasswordPolicies = "mfa_and_password_policies";

    /// <summary>Placing and releasing legal holds.</summary>
    public const string LegalHolds = "legal_holds";

    /// <summary>The signing policy per document type.</summary>
    public const string SigningPolicy = "signing_policy";

    /// <summary>
    /// The four-eyes setting itself. It needs a second administrator whenever any area does, so one administrator cannot
    /// switch the control off and then change alone; it is not offered in the list, as it follows the others.
    /// </summary>
    public const string FourEyes = "four_eyes";

    /// <summary>The areas a tenant may list, in the order the console offers them.</summary>
    public static IReadOnlyList<string> Listed { get; } = [Roles, SingleSignOn, Retention, MfaAndPasswordPolicies, LegalHolds, SigningPolicy];
}

/// <summary>
/// A save that waits for a second administrator (TF-068, ADR-0052): the module's own proposal, with the state it changes
/// as it is now (<paramref name="Before"/>) and as it would be (<paramref name="After"/>), both as people read them side by
/// side, and what the module needs to apply it later (<paramref name="Payload"/>). <paramref name="Subject"/> names what
/// it changes, such as <c>role:{id}</c>: one pending change per subject. The approver must hold
/// <paramref name="Permission"/> at <paramref name="Scope"/>, the same place the requester needed it.
/// </summary>
public sealed record ChangeProposal(
    string Area,
    string Kind,
    string Subject,
    string Title,
    JsonElement Before,
    JsonElement After,
    JsonElement Payload,
    string Permission,
    AccessScope Scope,
    string? Reason = null);

/// <summary>
/// The question every save in a sensitive area asks before it writes (TF-068, ADR-0052): must this change wait for a
/// second administrator? When it must, the module hands its proposal over and answers the <see cref="ErrorKind.Held"/>
/// error it gets back, which the API turns into 202 with the pending change. The Changes module answers it; without it,
/// as in a job, a seed or a host without the module, nothing waits.
/// </summary>
public interface IFourEyes
{
    /// <summary>
    /// True when the tenant lists the area and a person makes the change now; false while an approved change is being
    /// applied, for the system principal, and when the area is not listed.
    /// </summary>
    Task<bool> AppliesAsync(string area, CancellationToken cancellationToken);

    /// <summary>
    /// Keeps the proposal as a pending change, audited, and answers the held error with its id; or a refusal, such as a
    /// change of the same subject already waiting.
    /// </summary>
    Task<Error> HoldAsync(ChangeProposal proposal, CancellationToken cancellationToken);
}

/// <summary>The answer where no change waits: a job, a seed, a command, or a host without the Changes module.</summary>
public sealed class NoFourEyes : IFourEyes
{
    public static readonly NoFourEyes Instance = new();

    private NoFourEyes()
    {
    }

    public Task<bool> AppliesAsync(string area, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task<Error> HoldAsync(ChangeProposal proposal, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("No change waits for approval in this host: ask AppliesAsync first.");
}

/// <summary>A pending change as the module that applies it reads it (ADR-0052).</summary>
public sealed record HeldChange(
    Guid Id,
    string Area,
    string Kind,
    string Subject,
    JsonElement Before,
    JsonElement After,
    JsonElement Payload,
    Guid RequestedBy,
    string? Reason);

/// <summary>
/// Applies the approved changes of the kinds a module proposes (ADR-0052). The Changes module asks for the current state
/// first and compares it with the change's <see cref="HeldChange.Before"/>: when something else changed it meanwhile,
/// the change goes back for a new review instead of applying. Applying runs as the approver, in the approval's
/// transaction, through the module's own rules, without asking for a second administrator again.
/// </summary>
public interface IConfigurationChangeApplier
{
    bool Handles(string kind);

    /// <summary>The subject's state now, in the same form as the proposal's <see cref="ChangeProposal.Before"/>.</summary>
    Task<JsonElement> CurrentAsync(HeldChange change, CancellationToken cancellationToken);

    Task<Result> ApplyAsync(HeldChange change, CancellationToken cancellationToken);

    /// <summary>Forgets what the change kept outside its row, such as a staged secret, when it ends without applying.</summary>
    Task DiscardAsync(HeldChange change, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>JSON for proposals: snake_case names, as the API writes them, so the console shows the same words.</summary>
public static class ChangeJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>A value as a proposal's before, after or payload.</summary>
    public static JsonElement Of(object? value) => JsonSerializer.SerializeToElement(value, Options);

    /// <summary>Nothing: what a record that does not exist yet is before, or after it is gone.</summary>
    public static JsonElement Nothing { get; } = JsonSerializer.SerializeToElement<object?>(null);

    /// <summary>Two states are the same when their canonical JSON is (key order does not matter).</summary>
    public static bool Same(JsonElement first, JsonElement second) => string.Equals(Canonical(first), Canonical(second), StringComparison.Ordinal);

    private static string Canonical(JsonElement element)
    {
        var builder = new System.Text.StringBuilder();
        Iron.Kernel.History.CanonicalJson.Value(builder, element);
        return builder.ToString();
    }
}
