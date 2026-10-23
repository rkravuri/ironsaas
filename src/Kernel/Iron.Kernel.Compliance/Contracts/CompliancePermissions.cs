using System.Text.Json;
using Iron.Kernel.History;

namespace Iron.Kernel.Compliance.Contracts;

public static class CompliancePermissions
{
    public static readonly Permission AuditReviewsRead = new("compliance.audit_reviews.read", "See the reviews of the audit trail: their periods, scopes, outcomes and signatures, and the periods nobody reviewed.", Risk.Medium);

    /// <summary>A review is signed with the meaning "reviewed" (TF-219 AC3): the reviewer's own credentials, never a token's.</summary>
    public static readonly Permission AuditReviewsRecord = new("compliance.audit_reviews.record", "Record and sign a review of the audit trail for a period.", Risk.High);

    public static IReadOnlyList<Permission> All => [AuditReviewsRead, AuditReviewsRecord];
}

public static class ComplianceAuditEvents
{
    /// <summary>A record was signed (TF-063): the meaning, the method, the version and its snapshot's hash.</summary>
    public const string SignatureSigned = "compliance.signature_signed";

    /// <summary>A signature was refused before any credential was checked: during impersonation, acting for someone, for a token, a service account or a break-glass account.</summary>
    public const string SignatureRefused = "compliance.signature_refused";

    /// <summary>An attempt to attach a signature to another record or version, refused because the hash does not match (TF-219 AC2).</summary>
    public const string SignatureAttachRefused = "compliance.signature_attach_refused";

    /// <summary>A review of the audit trail was recorded and signed (TF-219 AC3).</summary>
    public const string AuditReviewRecorded = "compliance.audit_review_recorded";

    /// <summary>Every event type above with the words it reads as in the audit trail, the History tab and the exports (ADR-0051).</summary>
    public static IReadOnlyList<AuditEventType> Types { get; } =
    [
        new(SignatureSigned, "Record signed"),
        new(SignatureRefused, "Signature refused"),
        new(SignatureAttachRefused, "Signature refused for another record"),
        new(AuditReviewRecorded, "Audit-trail review recorded"),
    ];
}

public static class ComplianceErrors
{
    public const string NoSigner = "compliance.signature.no_signer";
    public const string Required = "compliance.signature.required";
    public const string MeaningNotAllowed = "compliance.signature.meaning_not_allowed";
    public const string Impersonation = "compliance.signature.impersonation";
    public const string ActingFor = "compliance.signature.acting_for";
    public const string Token = "compliance.signature.token";
    public const string ServiceAccount = "compliance.signature.service_account";
    public const string BreakGlass = "compliance.signature.break_glass";
    public const string Invalid = "compliance.signature.invalid";
    public const string ProviderSignInRequired = "compliance.signature.provider_sign_in_required";
    public const string ProofInvalid = "compliance.signature.proof_invalid";
    public const string VersionChanged = "compliance.signature.version_changed";
    public const string HashMismatch = "compliance.signature.hash_mismatch";
    public const string NotFound = "compliance.signature.not_found";
    public const string TypeUnknown = "compliance.signature.type_unknown";
    public const string ReviewNotFound = "compliance.audit_review.not_found";
    public const string ReviewInvalid = "compliance.audit_review.invalid";
}

/// <summary>The tenant's signing policy (TF-063, ADR-0051). Changing it is a high-risk action, behind a recent authentication (TF-248).</summary>
public static class ComplianceSettings
{
    /// <summary>
    /// The policy per document type, as an object keyed by record type: <c>{"access_review_decision": {"required": true,
    /// "meanings": ["approved", "rejected"], "reauthenticate": true}}</c>. A type the object does not name keeps its
    /// module's default; an always-signed type keeps its signature and its re-authentication whatever the object says.
    /// </summary>
    public static readonly Setting<JsonElement> Policies = Setting.Json(
        "compliance.signing.policies",
        "The signing policy per document type: whether a signature is required, which meanings it may have, and whether signing asks for the password again.",
        "{}",
        SettingScopes.Tenant,
        SettingRule.Must(IsPolicyObject, "An object keyed by document type, each with required (true or false), meanings (a list of approved, rejected, acknowledged, signed and reviewed) and reauthenticate (true or false)."))
        .RequiringRecentAuthentication();

    public static readonly Setting<bool> LaterSecondFactor = Setting.Boolean(
        "compliance.signing.later_second_factor",
        "Ask for the second factor at every signature, not only at the first of a session. The password is always asked.",
        false,
        SettingScopes.Tenant)
        .RequiringRecentAuthentication();

    public static IReadOnlyList<SettingDefinition> All => [Policies, LaterSecondFactor];

    /// <summary>The policy object's shape: each value an object of known keys with values of the right kind.</summary>
    public static bool IsPolicyObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var type in value.EnumerateObject())
        {
            if (type.Value.ValueKind != JsonValueKind.Object || string.IsNullOrWhiteSpace(type.Name))
            {
                return false;
            }

            foreach (var field in type.Value.EnumerateObject())
            {
                var valid = field.Name switch
                {
                    "required" or "reauthenticate" => field.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                    "meanings" => field.Value.ValueKind == JsonValueKind.Array && field.Value.GetArrayLength() > 0
                        && field.Value.EnumerateArray().All(m => m.ValueKind == JsonValueKind.String && SignatureMeanings.All.Contains(m.GetString(), StringComparer.Ordinal)),
                    _ => false,
                };
                if (!valid)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
