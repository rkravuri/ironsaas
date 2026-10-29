using Iron.Kernel.Data.Encryption;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>The kinds of second factor (TF-013, ADR-0047), as the API and the tenant's policy name them.</summary>
public static class FactorKinds
{
    /// <summary>A time-based one-time code from an authenticator app (RFC 6238).</summary>
    public const string AuthenticatorApp = "authenticator_app";

    /// <summary>A WebAuthn credential that verifies its user: a full second factor.</summary>
    public const string Passkey = "passkey";

    public static IReadOnlyList<string> All => [AuthenticatorApp, Passkey];
}

/// <summary>
/// A second factor of a person's (TF-013, ADR-0047): an authenticator app or a passkey, saved only once it has been used
/// to prove itself. An authenticator app's secret is kept encrypted with the tenant's data key (TF-073, ADR-0054), never plain, and
/// <see cref="LastUsedStep"/> keeps a code from being accepted twice. A passkey keeps its credential id, its public key and
/// its signature counter; nothing of it is secret. Each person reads and changes only their own; an administrator may
/// remove them all (a reset), never read them.
/// </summary>
public sealed class Factor : AuditedEntity, IOwnAccessRule
{
    public const int NameMaxLength = 100;
    public const int KindMaxLength = 20;

    private Factor()
    {
        Kind = string.Empty;
        Name = string.Empty;
    }

    private Factor(Guid principalId, string kind, string name)
        : this()
    {
        if (principalId == Guid.Empty)
        {
            throw new ArgumentException("The principal id is empty.", nameof(principalId));
        }

        PrincipalId = principalId;
        Kind = kind;
        Rename(name);
    }

    public Guid PrincipalId { get; private set; }

    /// <summary>One of <see cref="FactorKinds"/>.</summary>
    public string Kind { get; private set; }

    /// <summary>The name the person chose, so they can tell their passkeys apart.</summary>
    public string Name { get; private set; }

    /// <summary>An authenticator app's secret, encrypted with the tenant's data key (<c>FactorSecrets</c>); null for a passkey.</summary>
    public EncryptedValue? Secret { get; private set; }

    /// <summary>The time step of the last code accepted: a code of this step or an earlier one is refused (RFC 6238 section 5.2).</summary>
    public long? LastUsedStep { get; private set; }

    /// <summary>A passkey's credential id, as the authenticator gave it; unique in the tenant.</summary>
    public byte[]? CredentialId { get; private set; }

    /// <summary>A passkey's public key, COSE-encoded.</summary>
    public byte[]? PublicKey { get; private set; }

    /// <summary>A passkey's signature counter: an assertion whose counter does not grow suggests a cloned authenticator.</summary>
    public long SignCount { get; private set; }

    /// <summary>The user handle the passkey was registered with, which a sign-in without a password names the person by.</summary>
    public byte[]? UserHandle { get; private set; }

    /// <summary>The authenticator's model, when it said; zeros when it did not.</summary>
    public Guid? AaGuid { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    public bool IsPasskey => Kind == FactorKinds.Passkey;

    public bool IsAuthenticatorApp => Kind == FactorKinds.AuthenticatorApp;

    public static Factor AuthenticatorApp(Guid principalId, string name, EncryptedValue secret, long confirmedStep, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return new Factor(principalId, FactorKinds.AuthenticatorApp, name)
        {
            Secret = secret,
            LastUsedStep = confirmedStep,
            LastUsedAt = now,
        };
    }

    public static Factor Passkey(Guid principalId, string name, byte[] credentialId, byte[] publicKey, long signCount, byte[] userHandle, Guid? aaGuid)
    {
        ArgumentNullException.ThrowIfNull(credentialId);
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(userHandle);
        return new Factor(principalId, FactorKinds.Passkey, name)
        {
            CredentialId = credentialId,
            PublicKey = publicKey,
            SignCount = signCount,
            UserHandle = userHandle,
            AaGuid = aaGuid,
        };
    }

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var trimmed = name.Trim();
        Name = trimmed.Length > NameMaxLength ? trimmed[..NameMaxLength] : trimmed;
    }

    /// <summary>A code of <paramref name="step"/> was accepted: no code of this step or before it is accepted again.</summary>
    public void UsedCode(long step, DateTimeOffset now)
    {
        LastUsedStep = step;
        LastUsedAt = now;
    }

    /// <summary>The passkey signed an assertion with this counter.</summary>
    public void UsedPasskey(long signCount, DateTimeOffset now)
    {
        SignCount = signCount;
        LastUsedAt = now;
    }
}
