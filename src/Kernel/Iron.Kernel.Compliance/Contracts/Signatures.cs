using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Iron.Kernel.History;

namespace Iron.Kernel.Compliance.Contracts;

/// <summary>What a signature means (TF-063, ADR-0051). A document type allows some of them; nothing else is a meaning.</summary>
public static class SignatureMeanings
{
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string Acknowledged = "acknowledged";
    public const string Signed = "signed";
    public const string Reviewed = "reviewed";

    public static IReadOnlyList<string> All => [Approved, Rejected, Acknowledged, Signed, Reviewed];
}

/// <summary>
/// How the signer proved who they are at signing (TF-063, ADR-0051): their password, with the second factor they gave;
/// a fresh sign-in at their company login; or, for a document type whose policy does not ask for it, their session alone.
/// </summary>
public static class SignatureMethods
{
    public const string Password = "password";
    public const string PasswordAndApp = "password_and_app";
    public const string PasswordAndPasskey = "password_and_passkey";
    public const string PasswordAndRecoveryCode = "password_and_recovery_code";
    public const string CompanyLogin = "company_login";
    public const string Session = "session";

    public static IReadOnlyList<string> All => [Password, PasswordAndApp, PasswordAndPasskey, PasswordAndRecoveryCode, CompanyLogin, Session];
}

/// <summary>
/// The credentials a signature carries, as the signing dialog asks for them: the login identifier at the first signature
/// of a session, the password, and the second factor (an app's code, a recovery code, or a passkey's assertion).
/// </summary>
public sealed record SigningCredentials(string? LoginId, string? Password, string? Code, string? RecoveryCode, JsonElement? Passkey)
{
    public static SigningCredentials None { get; } = new(null, null, null, null, null);
}

/// <summary>
/// What the next signature asks of the signed-in person (ADR-0051, Part 11 section 11.200): the first in a session, the
/// login identifier, the password and the second factor they have; a later one, the password, and the second factor when
/// the tenant asks for it; a person who signs in through their company login, a fresh sign-in there.
/// </summary>
public sealed record SignerChallenge(bool FirstInSession, bool LoginId, bool Password, bool SecondFactor, IReadOnlyList<string> Methods, string? PasskeyOptions)
{
    /// <summary>The company login to sign in at again, for a person without a password.</summary>
    public Guid? ProviderId { get; init; }

    public string? ProviderName { get; init; }

    /// <summary>True when the provider sign-in this signature needs has just happened.</summary>
    public bool ProviderConfirmed { get; init; }

    /// <summary>False when the document type's policy does not ask for re-authentication: the session is enough.</summary>
    public bool Reauthenticate { get; init; } = true;
}

/// <summary>Who signs, as Identity proved them: their id, full name and login identifier at signing, and the method.</summary>
public sealed record SignerIdentity(Guid SignerId, string FullName, string LoginId, string Method, Guid? SessionId);

/// <summary>
/// The proof of the signer's identity (ADR-0051). Identity declares how it is made, by implementing this interface; the
/// kernel's Compliance module asks it at every signature and never reads credentials itself. Every refusal is a 403: the
/// session stays. A signature is refused, whatever the credentials, during impersonation, while acting for someone, for a
/// token or a service account, and for an operator's break-glass account.
/// </summary>
public interface ISignerAuthentication
{
    /// <summary>What the next signature asks of the person signed in now; refused for those who may never sign.</summary>
    Task<Result<SignerChallenge>> ChallengeAsync(CancellationToken cancellationToken);

    /// <summary>Checks the credentials against the components rule and, when they prove the person, says who signs and how.</summary>
    Task<Result<SignerIdentity>> AuthenticateAsync(SigningCredentials credentials, CancellationToken cancellationToken);

    /// <summary>For a document type whose policy does not ask for re-authentication: who signs, with the same refusals.</summary>
    Task<Result<SignerIdentity>> IdentifyAsync(CancellationToken cancellationToken);
}

/// <summary>A host without Identity has nobody to sign: every signature is refused.</summary>
public sealed class NoSignerAuthentication : ISignerAuthentication
{
    private static Error Refusal => Error.Forbidden(ComplianceErrors.NoSigner, "Signing needs a signed-in person, and this host has none.");

    public Task<Result<SignerChallenge>> ChallengeAsync(CancellationToken cancellationToken) => Task.FromResult<Result<SignerChallenge>>(Refusal);

    public Task<Result<SignerIdentity>> AuthenticateAsync(SigningCredentials credentials, CancellationToken cancellationToken) => Task.FromResult<Result<SignerIdentity>>(Refusal);

    public Task<Result<SignerIdentity>> IdentifyAsync(CancellationToken cancellationToken) => Task.FromResult<Result<SignerIdentity>>(Refusal);
}

/// <summary>
/// A tenant's policy for one document type (TF-063): whether its decisions must be signed, which meanings a signature
/// may have, and whether signing asks for re-authentication (TF-063 AC1: refused without it).
/// </summary>
public sealed record SigningPolicy(bool Required, IReadOnlyList<string> Meanings, bool Reauthenticate);

/// <summary>
/// A document type that can be signed, declared by the module that owns it: its record type, its name, the meanings its
/// signatures may have, and the policy that applies until the tenant sets one. An always-signed type, such as the
/// audit-trail review, keeps its signature and its re-authentication whatever the tenant sets.
/// </summary>
public sealed record SignableType(string RecordType, string Name, IReadOnlyList<string> Meanings, SigningPolicy Default, bool AlwaysSigned = false);

/// <summary>
/// A record's content at one version, as its module writes it for a signature (ADR-0051): the record type, its id, its
/// version and its fields as a JSON object. The title is what a reader sees and is not hashed.
/// </summary>
public sealed record RecordSnapshot(string RecordType, Guid RecordId, int Version, string Title, JsonElement Content)
{
    private static readonly JsonSerializerOptions ContentJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    /// <summary>A snapshot from an object whose properties are the fields, named in snake_case.</summary>
    public static RecordSnapshot Of(string recordType, Guid recordId, int version, string title, object content) =>
        new(recordType, recordId, version, title, JsonSerializer.SerializeToElement(content, ContentJson));
}

/// <summary>
/// The records a module lets people sign (ADR-0051). The module declares its types, writes a record's current snapshot,
/// and says whether the person signed in may read a record and its signatures.
/// </summary>
public interface ISignableRecords
{
    IReadOnlyList<SignableType> Types { get; }

    /// <summary>The record's content at its current version, or null when there is no such record.</summary>
    Task<RecordSnapshot?> CurrentAsync(string recordType, Guid recordId, CancellationToken cancellationToken);

    /// <summary>Whether the person signed in may read this record, and so its signatures and their snapshots.</summary>
    Task<bool> MayReadAsync(string recordType, Guid recordId, CancellationToken cancellationToken);
}

/// <summary>
/// The canonical form of a signed snapshot, version 1 (ADR-0051), beside the audit event's in the Audit README: a JSON
/// array, written compactly by the kernel's canonical JSON, of the text <c>iron-snapshot-v1</c>, the record type, the
/// record id, the version as a whole number, and the content object. The hash is SHA-256 of its UTF-8 bytes, in
/// lowercase hexadecimal. The record type, the id and the version are inside the hash, so a signature can never apply to
/// another record or another version.
/// </summary>
public static class SnapshotCanonicalForm
{
    public const short Version = 1;
    public const string Label = "iron-snapshot-v1";

    public static string Write(RecordSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var builder = new StringBuilder(512);
        builder.Append('[');
        CanonicalJson.Text(builder, Label);
        builder.Append(',');
        CanonicalJson.Text(builder, snapshot.RecordType);
        builder.Append(',');
        CanonicalJson.Identifier(builder, snapshot.RecordId);
        builder.Append(',');
        builder.Append(snapshot.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(',');
        CanonicalJson.Value(builder, snapshot.Content);
        builder.Append(']');
        return builder.ToString();
    }

    public static string Hash(string canonical)
    {
        ArgumentNullException.ThrowIfNull(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string Hash(RecordSnapshot snapshot) => Hash(Write(snapshot));
}

/// <summary>
/// The proof that the person signed in proved who they are for one signature (ADR-0051), made by
/// <see cref="ISignatures.AuthenticateAsync"/> and taken once by <see cref="ISignatures.RecordAsync"/> in the same
/// request. A module cannot make one: the token is the signature service's own.
/// </summary>
public sealed record SignerProof(Guid Token, string RecordType, SignerIdentity Signer);

/// <summary>
/// A signature as a reader sees it (TF-063 AC2, AC3): who, how, when, what it means, the version it applies to, and its
/// snapshot's hash, recomputed from the snapshot kept with it; and whether the record has changed since.
/// </summary>
public sealed record SignatureView(
    Guid Id,
    string RecordType,
    Guid RecordId,
    int RecordVersion,
    Guid SignerId,
    string SignerName,
    string SignerLoginId,
    string Method,
    string Meaning,
    string? Comment,
    DateTimeOffset SignedAt,
    string SnapshotHash,
    bool HashMatches,
    int? CurrentVersion,
    bool RecordChanged);

/// <summary>
/// A signature in a list of records (TF-219 AC1): who signed, what it means, when, and the version. Opening it checks it
/// against its snapshot and the record (<see cref="ISignatures.ForRecordAsync"/>).
/// </summary>
public sealed record SignatureSummary(Guid Id, Guid RecordId, int RecordVersion, Guid SignerId, string SignerName, string Meaning, string Method, DateTimeOffset SignedAt);

/// <summary>
/// Electronic signatures (TF-063, TF-219, ADR-0051). A module signs inside its own action: it asks the policy, proves the
/// signer with <see cref="AuthenticateAsync"/> before its transaction (a wrong password is counted whatever happens
/// next), then records the signature with the record's snapshot in its transaction, so the decision and its signature
/// commit together.
/// </summary>
public interface ISignatures
{
    Task<SigningPolicy> PolicyAsync(string recordType, CancellationToken cancellationToken);

    Task<Result<SignerChallenge>> ChallengeAsync(string recordType, CancellationToken cancellationToken);

    Task<Result<SignerProof>> AuthenticateAsync(string recordType, SigningCredentials credentials, CancellationToken cancellationToken);

    /// <summary>Records the signature of this snapshot with this meaning, in the caller's unit of work; an audit event with it.</summary>
    Task<Result<SignatureView>> RecordAsync(SignerProof proof, RecordSnapshot snapshot, string meaning, string? comment, CancellationToken cancellationToken);

    /// <summary>A record's signatures, oldest first, each checked against its snapshot and the record's current version.</summary>
    Task<IReadOnlyList<SignatureView>> ForRecordAsync(string recordType, Guid recordId, CancellationToken cancellationToken);

    /// <summary>The signatures of a page of records of one type, in one query, oldest first.</summary>
    Task<IReadOnlyList<SignatureSummary>> SummariesAsync(string recordType, IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken);
}

/// <summary>A host without the Compliance module: nothing needs a signature, and none can be made.</summary>
public sealed class NoSignatures : ISignatures
{
    private static Error Refusal => Error.Forbidden(ComplianceErrors.NoSigner, "Signing is not available on this host.");

    public Task<SigningPolicy> PolicyAsync(string recordType, CancellationToken cancellationToken) =>
        Task.FromResult(new SigningPolicy(false, SignatureMeanings.All, true));

    public Task<Result<SignerChallenge>> ChallengeAsync(string recordType, CancellationToken cancellationToken) => Task.FromResult<Result<SignerChallenge>>(Refusal);

    public Task<Result<SignerProof>> AuthenticateAsync(string recordType, SigningCredentials credentials, CancellationToken cancellationToken) => Task.FromResult<Result<SignerProof>>(Refusal);

    public Task<Result<SignatureView>> RecordAsync(SignerProof proof, RecordSnapshot snapshot, string meaning, string? comment, CancellationToken cancellationToken) => Task.FromResult<Result<SignatureView>>(Refusal);

    public Task<IReadOnlyList<SignatureView>> ForRecordAsync(string recordType, Guid recordId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SignatureView>>([]);

    public Task<IReadOnlyList<SignatureSummary>> SummariesAsync(string recordType, IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SignatureSummary>>([]);
}
