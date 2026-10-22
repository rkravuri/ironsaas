using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Identity.Data;

/// <summary>
/// A person linked to a provider (ADR-0048): by the provider's subject identifier (<c>sub</c>), which never changes and is
/// never reused, so a later sign-in finds the person even when their email changed at the provider. Made at their first
/// sign-in through it, when their verified email matched, or when the provider created them.
/// </summary>
public sealed class SsoLink : AuditedEntity, IOwnAccessRule
{
    public const int SubjectMaxLength = 255;

    private SsoLink()
    {
        Subject = string.Empty;
        Email = string.Empty;
    }

    public SsoLink(Guid principalId, Guid providerId, string subject, string email, DateTimeOffset now)
    {
        if (principalId == Guid.Empty || providerId == Guid.Empty)
        {
            throw new ArgumentException("A link names a person and a provider.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        PrincipalId = principalId;
        ProviderId = providerId;
        Subject = subject;
        Email = email;
        LastSignInAt = now;
    }

    public Guid PrincipalId { get; private set; }

    public Guid ProviderId { get; private set; }

    /// <summary>The provider's <c>sub</c> for the person.</summary>
    public string Subject { get; private set; }

    /// <summary>The verified email of the last sign-in through the provider.</summary>
    public string Email { get; private set; }

    public DateTimeOffset LastSignInAt { get; private set; }

    public void SignedIn(string email, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        Email = email;
        LastSignInAt = now;
    }
}
