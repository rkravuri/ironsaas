using System.Text.Json;

namespace Iron.Kernel.Authorization.Services;

/// <summary>
/// Applies approved changes of roles and assignments (TF-068, ADR-0052) through the services a direct save uses, so the
/// approver's own rules hold: nobody grants more than they hold, the conflict rules, the last administrator.
/// </summary>
public sealed class AuthorizationChangeApplier(RoleService roles, AssignmentService assignments) : IConfigurationChangeApplier
{
    public bool Handles(string kind) => kind is RoleService.CreateKind or RoleService.UpdateKind or RoleService.DeleteKind or RoleService.ImportKind or AssignmentService.GrantKind or AssignmentService.RemoveKind;

    public Task<JsonElement> CurrentAsync(HeldChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return IsAssignment(change) ? assignments.StateAsync(change, cancellationToken) : roles.StateAsync(change, cancellationToken);
    }

    public Task<Result> ApplyAsync(HeldChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return IsAssignment(change) ? assignments.ApplyAsync(change, cancellationToken) : roles.ApplyAsync(change, cancellationToken);
    }

    private static bool IsAssignment(HeldChange change) => change.Kind is AssignmentService.GrantKind or AssignmentService.RemoveKind;
}
