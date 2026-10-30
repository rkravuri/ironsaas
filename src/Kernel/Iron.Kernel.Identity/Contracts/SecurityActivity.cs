namespace Iron.Kernel.Identity.Contracts;

/// <summary>The record type of people and service accounts in their field history and their audit events (ADR-0051).</summary>
public static class IdentityRecordTypes
{
    public const string Principal = "principal";
}

/// <summary>
/// A change another module makes to a person's access, noted in the person's security activity (TF-019, ADR-0046), which
/// My profile and the user record show: their visibility tags, for one (ADR-0057). Only the row is written: the module
/// that made the change writes its own audit event, in the same transaction. Identity implements it.
/// </summary>
public interface ISecurityActivity
{
    /// <summary>A security event of the kind for the principal, made by the request's principal, inside the caller's transaction.</summary>
    Task NoteAsync(Guid principalId, string kind, CancellationToken cancellationToken);
}
