namespace Iron.Kernel.Authorization.Contracts;

/// <summary>A role at a scope that a source outside the tenant's administrators gives a principal: a single sign-on provider's group mapping (ADR-0048).</summary>
public sealed record ProvidedRole(string RoleCode, RoleScope Scope);

/// <summary>What a sync changed: the roles added and removed, each as <c>code@scope</c>, and those it could not apply, with why.</summary>
public sealed record ProvidedAssignmentChanges(IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Skipped);

/// <summary>
/// Role assignments that come from a source rather than from a person granting them (ADR-0048): a single sign-on
/// provider maps its groups to roles, and every sign-in through it makes the person's assignments from that provider
/// match their groups. Such an assignment is marked with its source, so the assignments people granted are never touched
/// by a sync, and the shell says where it came from. The rule that keeps a mapping honest is ADR-0031's: a mapping may
/// name only a role its author may grant at its scope, which <see cref="MissingToGrantAsync"/> answers.
/// </summary>
public interface IProvidedAssignments
{
    /// <summary>
    /// The permissions <paramref name="granterId"/> lacks to grant the role at the scope, as ADR-0031's rule 1 reads it,
    /// <c>authz.assignments.manage</c> included: empty when the grant would be allowed. A role or a unit that does not
    /// exist, or a role that is not for people, is an error.
    /// </summary>
    Task<Result<IReadOnlyList<string>>> MissingToGrantAsync(Guid granterId, string roleCode, RoleScope scope, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the principal's assignments from <paramref name="source"/> exactly <paramref name="wanted"/>: adds the missing
    /// ones, removes the others. A role the principal holds already at the scope, from any source, is not added again; the
    /// last active tenant administrator keeps the role; a role or unit gone since the mapping was saved is skipped and
    /// said so. Trusted, as the seed's path is: the rule was applied when the mapping was saved. Each change is audited
    /// with its source, in one transaction.
    /// </summary>
    Task<Result<ProvidedAssignmentChanges>> SyncAsync(Guid principalId, string source, IReadOnlyList<ProvidedRole> wanted, CancellationToken cancellationToken);

    /// <summary>Removes every assignment from <paramref name="source"/>, its provider being removed; answers how many went.</summary>
    Task<int> RemoveSourceAsync(string source, CancellationToken cancellationToken);
}
