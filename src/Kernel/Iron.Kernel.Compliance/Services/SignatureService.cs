using System.Globalization;
using System.Text.Json;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Compliance.Data;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Compliance.Services;

/// <summary>Every document type the host's modules let people sign, by record type.</summary>
public sealed class SignableCatalogue(IEnumerable<ISignableRecords> sources)
{
    private readonly List<ISignableRecords> _sources = [.. sources];

    public IReadOnlyList<SignableType> Types => [.. _sources.SelectMany(s => s.Types).OrderBy(t => t.Name, StringComparer.Ordinal)];

    public SignableType? Find(string recordType) => _sources.SelectMany(s => s.Types).FirstOrDefault(t => string.Equals(t.RecordType, recordType, StringComparison.Ordinal));

    public ISignableRecords? SourceOf(string recordType) => _sources.FirstOrDefault(s => s.Types.Any(t => string.Equals(t.RecordType, recordType, StringComparison.Ordinal)));
}

/// <summary>
/// The tenant's signing policy per document type (TF-063, ADR-0051): the setting's entry for the type over its module's
/// default; an always-signed type keeps its signature and its re-authentication.
/// </summary>
public sealed class SigningPolicies(ISettings settings, SignableCatalogue catalogue)
{
    public async Task<SigningPolicy> ForAsync(string recordType, CancellationToken cancellationToken)
    {
        var type = catalogue.Find(recordType);
        var policy = type?.Default ?? new SigningPolicy(false, SignatureMeanings.All, true);
        var all = await settings.GetAsync(ComplianceSettings.Policies, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        if (all.ValueKind == JsonValueKind.Object && all.TryGetProperty(recordType, out var set) && set.ValueKind == JsonValueKind.Object)
        {
            policy = new SigningPolicy(
                set.TryGetProperty("required", out var required) ? required.GetBoolean() : policy.Required,
                set.TryGetProperty("meanings", out var meanings) ? [.. meanings.EnumerateArray().Select(m => m.GetString()!).Where(m => type is null || type.Meanings.Contains(m, StringComparer.Ordinal))] : policy.Meanings,
                set.TryGetProperty("reauthenticate", out var reauthenticate) ? reauthenticate.GetBoolean() : policy.Reauthenticate);
        }

        if (policy.Meanings.Count == 0)
        {
            policy = policy with { Meanings = type?.Meanings ?? SignatureMeanings.All };
        }

        return type is { AlwaysSigned: true } ? policy with { Required = true, Reauthenticate = true } : policy;
    }
}

/// <summary>The proofs this request's signer earned, each taken once: a module never makes one of its own.</summary>
public sealed class SignatureProofs
{
    private readonly HashSet<Guid> _issued = [];

    public Guid Issue()
    {
        var token = Ids.New();
        _issued.Add(token);
        return token;
    }

    public bool Holds(Guid token) => _issued.Contains(token);

    public bool Take(Guid token) => _issued.Remove(token);
}

/// <summary>
/// Electronic signatures (TF-063, TF-219, ADR-0051). Proving the signer is Identity's (<see cref="ISignerAuthentication"/>);
/// the policy, the snapshot's canonical form and hash, the signature's row and its audit event are this service's. A
/// signature never changes; a changed record shows it against the version it signed.
/// </summary>
public sealed class SignatureService(
    ComplianceDbContext context,
    ISignerAuthentication signer,
    SigningPolicies policies,
    SignableCatalogue catalogue,
    SignatureProofs proofs,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    ILogger<SignatureService> logger) : ISignatures
{
    /// <summary>The refusals no credential can lift: they are recorded as an attempt (ADR-0051).</summary>
    private static readonly HashSet<string> Never = new(StringComparer.Ordinal)
    {
        ComplianceErrors.Impersonation, ComplianceErrors.ActingFor, ComplianceErrors.Token, ComplianceErrors.ServiceAccount, ComplianceErrors.BreakGlass,
    };

    public Task<SigningPolicy> PolicyAsync(string recordType, CancellationToken cancellationToken) => policies.ForAsync(recordType, cancellationToken);

    public async Task<Result<SignerChallenge>> ChallengeAsync(string recordType, CancellationToken cancellationToken)
    {
        var policy = await policies.ForAsync(recordType, cancellationToken).ConfigureAwait(false);
        if (policy.Reauthenticate)
        {
            return await signer.ChallengeAsync(cancellationToken).ConfigureAwait(false);
        }

        var identified = await signer.IdentifyAsync(cancellationToken).ConfigureAwait(false);
        return identified.IsFailure ? identified.Error : new SignerChallenge(false, false, false, false, [], null) { Reauthenticate = false };
    }

    public async Task<Result<SignerProof>> AuthenticateAsync(string recordType, SigningCredentials credentials, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var policy = await policies.ForAsync(recordType, cancellationToken).ConfigureAwait(false);
        var proved = policy.Reauthenticate
            ? await signer.AuthenticateAsync(credentials, cancellationToken).ConfigureAwait(false)
            : await signer.IdentifyAsync(cancellationToken).ConfigureAwait(false);
        if (proved.IsFailure)
        {
            if (Never.Contains(proved.Error.Code))
            {
                var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["record_type"] = recordType, ["refusal"] = proved.Error.Code };
                await audit.WriteAsync(new AuditEvent(ComplianceAuditEvents.SignatureRefused, AuditOutcome.Denied, actor.Id, recordType, details, Reason: proved.Error.Code, Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
            }

            return proved.Error;
        }

        return new SignerProof(proofs.Issue(), recordType, proved.Value);
    }

    public async Task<Result<SignatureView>> RecordAsync(SignerProof proof, RecordSnapshot snapshot, string meaning, string? comment, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!string.Equals(proof.RecordType, snapshot.RecordType, StringComparison.Ordinal) || !proofs.Holds(proof.Token))
        {
            return Error.Forbidden(ComplianceErrors.ProofInvalid, "This signature was not proved in this request. Sign again.");
        }

        var policy = await policies.ForAsync(snapshot.RecordType, cancellationToken).ConfigureAwait(false);
        if (!policy.Meanings.Contains(meaning, StringComparer.Ordinal))
        {
            return Error.Validation(ComplianceErrors.MeaningNotAllowed, $"A signature here may mean {string.Join(", ", policy.Meanings)}; not {meaning}.");
        }

        // Taken only now: a refused meaning leaves the proof for the signature the person meant.
        proofs.Take(proof.Token);

        var canonical = SnapshotCanonicalForm.Write(snapshot);
        var hash = SnapshotCanonicalForm.Hash(canonical);
        var who = proof.Signer;
        var signature = new Signature(snapshot.RecordType, snapshot.RecordId, snapshot.Version, canonical, hash, who.SignerId, who.FullName, who.LoginId, who.Method, meaning, comment, who.SessionId);
        context.Signatures.Add(signature);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["signature_id"] = signature.Id.ToString("D"),
            ["meaning"] = meaning,
            ["method"] = who.Method,
            ["version"] = snapshot.Version.ToString(CultureInfo.InvariantCulture),
            ["snapshot_hash"] = hash,
        };
        if (signature.Comment is { } kept)
        {
            details["comment"] = kept;
        }

        await audit.WriteAsync(new AuditEvent(ComplianceAuditEvents.SignatureSigned, AuditOutcome.Success, who.SignerId, snapshot.Title, details, snapshot.RecordType, snapshot.RecordId), cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Principal {PrincipalId} signed {RecordType} {RecordId} version {Version} as {Meaning} by {Method}.", who.SignerId, snapshot.RecordType, snapshot.RecordId, snapshot.Version, meaning, who.Method);
        return View(signature, snapshot.Version, hash);
    }

    public async Task<IReadOnlyList<SignatureView>> ForRecordAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        var signatures = await context.Signatures.AsNoTracking()
            .Where(s => s.RecordType == recordType && s.RecordId == recordId)
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (signatures.Count == 0)
        {
            return [];
        }

        var current = catalogue.SourceOf(recordType) is { } source ? await source.CurrentAsync(recordType, recordId, cancellationToken).ConfigureAwait(false) : null;
        var currentHash = current is null ? null : SnapshotCanonicalForm.Hash(current);
        return [.. signatures.Select(s => View(s, current?.Version, currentHash))];
    }

    public async Task<IReadOnlyList<SignatureSummary>> SummariesAsync(string recordType, IReadOnlyCollection<Guid> recordIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recordIds);
        if (recordIds.Count == 0)
        {
            return [];
        }

        return await context.Signatures.AsNoTracking()
            .Where(s => s.RecordType == recordType && recordIds.Contains(s.RecordId))
            .OrderBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .Select(s => new SignatureSummary(s.Id, s.RecordId, s.RecordVersion, s.SignerId, s.SignerName, s.Meaning, s.Method, s.CreatedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>One signature by id, checked like a record's.</summary>
    public async Task<(Signature Signature, SignatureView View)?> FindAsync(Guid id, CancellationToken cancellationToken)
    {
        var signature = await context.Signatures.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, cancellationToken).ConfigureAwait(false);
        if (signature is null)
        {
            return null;
        }

        var current = catalogue.SourceOf(signature.RecordType) is { } source ? await source.CurrentAsync(signature.RecordType, signature.RecordId, cancellationToken).ConfigureAwait(false) : null;
        return (signature, View(signature, current?.Version, current is null ? null : SnapshotCanonicalForm.Hash(current)));
    }

    /// <summary>
    /// An attempt to attach a signature to a record (TF-219 AC2): it applies only where the record's current snapshot has
    /// the signature's hash, which holds the record type, the id and the version, so another record or another version is
    /// refused and the attempt recorded.
    /// </summary>
    public async Task<Result<SignatureView>> AttachAsync(Guid signatureId, string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        var found = await FindAsync(signatureId, cancellationToken).ConfigureAwait(false);
        if (found is not { } pair)
        {
            return Error.NotFound(ComplianceErrors.NotFound, "There is no signature with this id.");
        }

        var target = catalogue.SourceOf(recordType) is { } source ? await source.CurrentAsync(recordType, recordId, cancellationToken).ConfigureAwait(false) : null;
        var targetHash = target is null ? null : SnapshotCanonicalForm.Hash(target);
        if (!string.Equals(targetHash, pair.Signature.SnapshotHash, StringComparison.Ordinal))
        {
            var details = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["signature_id"] = signatureId.ToString("D"),
                ["signed_record_type"] = pair.Signature.RecordType,
                ["signed_record_id"] = pair.Signature.RecordId.ToString("D"),
                ["signed_version"] = pair.Signature.RecordVersion.ToString(CultureInfo.InvariantCulture),
                ["signed_hash"] = pair.Signature.SnapshotHash,
                ["target_hash"] = targetHash ?? "none",
            };
            await audit.WriteAsync(new AuditEvent(ComplianceAuditEvents.SignatureAttachRefused, AuditOutcome.Denied, actor.Id, target?.Title ?? recordType, details, recordType, recordId, "hash_mismatch", Severity: AuditSeverity.High), cancellationToken).ConfigureAwait(false);
            return Error.Conflict(ComplianceErrors.HashMismatch, "This signature cannot apply here: it was made for another record or another version, and its hash does not match this one's.");
        }

        return pair.View;
    }

    private static SignatureView View(Signature signature, int? currentVersion, string? currentHash) => new(
        signature.Id,
        signature.RecordType,
        signature.RecordId,
        signature.RecordVersion,
        signature.SignerId,
        signature.SignerName,
        signature.SignerLoginId,
        signature.Method,
        signature.Meaning,
        signature.Comment,
        signature.CreatedAt,
        signature.SnapshotHash,
        string.Equals(SnapshotCanonicalForm.Hash(signature.Snapshot), signature.SnapshotHash, StringComparison.Ordinal),
        currentVersion,
        currentHash is null || currentVersion != signature.RecordVersion || !string.Equals(currentHash, signature.SnapshotHash, StringComparison.Ordinal));
}
