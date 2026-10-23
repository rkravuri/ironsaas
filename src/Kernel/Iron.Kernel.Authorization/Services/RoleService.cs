using System.Text.Json;
using Iron.Kernel.Api.Permissions;
using Iron.Kernel.Api.Problems;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Authorization.Data;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Authorization.Services;

/// <summary>What a tenant role is made of when it is created or changed; the code is fixed at creation.</summary>
public sealed record RoleInput(string Name, string Description, PrincipalKind PrincipalType, IReadOnlyList<string> Permissions);

/// <summary>The export of a tenant's roles (TF-032): a format version and, per role, what another tenant needs to recreate it.</summary>
public sealed record RoleExport(int FormatVersion, IReadOnlyList<RoleDocument> Roles)
{
    public const int CurrentFormat = 1;
}

public sealed record RoleDocument(string Code, string Name, string Description, PrincipalKind PrincipalType, IReadOnlyList<string> Permissions);

public sealed record RoleChange(string Code, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, IReadOnlyList<string> Fields);

public sealed record UnknownPermissions(string Code, IReadOnlyList<string> Permissions);

/// <summary>A role as a pending change shows it before and after (ADR-0052): its code, words, type and permissions in order.</summary>
public sealed record RoleState(string Code, string Name, string Description, string PrincipalType, IReadOnlyList<string> Permissions);

/// <summary>What a held role change needs to apply: the role's id (none for a new one), its code, and what to save (none for a deletion).</summary>
public sealed record RolePayload(Guid? Id, string Code, RoleInput? Input);

/// <summary>What an import does or would do: the roles it creates, changes (with the permissions added and removed), leaves as they are, and cannot take.</summary>
public sealed record ImportDiff(
    bool DryRun,
    bool Applied,
    IReadOnlyList<string> Created,
    IReadOnlyList<RoleChange> Changed,
    IReadOnlyList<string> Unchanged,
    IReadOnlyList<UnknownPermissions> Unknown,
    IReadOnlyList<string> SkippedSystem);

/// <summary>
/// Tenant roles (TF-032, ADR-0031): create, change and delete, export and import. System roles are read-only; a role
/// still assigned is not deleted; a role is saved only with permissions its editor holds tenant-wide, and only with
/// permissions the catalogue knows. A change of permissions is checked against the conflict rules for everyone holding
/// the role (ADR-0039). Every change writes its audit event, with the permissions before and after, in the same
/// transaction.
/// </summary>
public sealed class RoleService(
    AuthorizationDbContext context,
    AccessGrants grants,
    PermissionCatalogue catalogue,
    ConflictService segregation,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    IFourEyes? fourEyes = null)
{
    public const string RecordType = "role";

    /// <summary>The kinds of change a role's save becomes while it waits for a second administrator (TF-068, ADR-0052).</summary>
    public const string CreateKind = "authz.role.create";

    public const string UpdateKind = "authz.role.update";
    public const string DeleteKind = "authz.role.delete";
    public const string ImportKind = "authz.role.import";

    public async Task<Role?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await context.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);

    public async Task<Result<Role>> CreateAsync(string code, RoleInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalised = (code ?? string.Empty).Trim().ToLowerInvariant();
        if (!Role.IsValidCode(normalised))
        {
            return Error.Validation(ApiErrors.Validation, "The code is not valid.", new FieldError("code", "invalid", "Use lowercase letters, digits and hyphens, starting with a letter."));
        }

        var invalid = Check(input) ?? await EscalationAsync(input.Permissions, cancellationToken).ConfigureAwait(false);
        if (invalid is not null)
        {
            return invalid;
        }

        if (await context.Roles.AnyAsync(r => r.Code == normalised, cancellationToken).ConfigureAwait(false))
        {
            return new Error(ErrorKind.Conflict, AuthorizationErrors.RoleCodeTaken, $"A role with the code '{normalised}' exists already.", [new FieldError("code", "taken", "Another role has this code. Choose another.")]);
        }

        if (await HeldAsync(CreateKind, $"role:new:{normalised}", $"Create the role {input.Name.Trim()}", ChangeJson.Nothing, State(normalised, input), new RolePayload(null, normalised, input), cancellationToken).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        var role = new Role(normalised, input.Name, input.Description, isSystem: false, input.PrincipalType, input.Permissions);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.Roles.Add(role);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.RoleCreated, role, [], [], cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return role;
    }

    public async Task<Result<Role>> UpdateAsync(Guid id, RoleInput input, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        var role = await context.Roles.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return NotFound();
        }

        if (role.IsSystem)
        {
            return SystemRole(role);
        }

        var invalid = Check(input) ?? await EscalationAsync(input.Permissions, cancellationToken).ConfigureAwait(false);
        if (invalid is not null)
        {
            return invalid;
        }

        if (input.PrincipalType != role.PrincipalType && await InUseAsync(role.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(AuthorizationErrors.RoleInUse, $"'{role.Name}' is assigned; its principal type cannot change while it is. Remove its assignments first.");
        }

        var permissions = Role.Normalise(input.Permissions);
        var permissionsChange = !permissions.ToHashSet(StringComparer.Ordinal).SetEquals(role.Permissions);
        if (permissionsChange && await BlockedAsync(new Dictionary<Guid, IReadOnlyCollection<string>> { [role.Id] = permissions }, cancellationToken).ConfigureAwait(false) is { } blocked)
        {
            return blocked;
        }

        if (await HeldAsync(UpdateKind, $"role:{role.Id:N}", $"Change the role {role.Name}", State(role), State(role.Code, input), new RolePayload(role.Id, role.Code, input), cancellationToken).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        var before = role.Permissions.ToList();
        var fields = ChangedFields(role, input.Name, input.Description, input.PrincipalType);
        role.Change(input.Name, input.Description, input.PrincipalType, input.Permissions);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(AuthorizationAuditEvents.RoleChanged, role, before, fields, cancellationToken).ConfigureAwait(false);
        if (permissionsChange)
        {
            await segregation.RefreshHoldersAsync([role.Id], cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await ForgetHoldersAsync(role.Id, cancellationToken).ConfigureAwait(false);
        return role;
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var role = await context.Roles.SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false);
        if (role is null)
        {
            return NotFound();
        }

        if (role.IsSystem)
        {
            return SystemRole(role);
        }

        if (await InUseAsync(role.Id, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(AuthorizationErrors.RoleInUse, $"'{role.Name}' is still assigned. Remove its assignments before you delete it.");
        }

        if (await HeldAsync(DeleteKind, $"role:{role.Id:N}", $"Delete the role {role.Name}", State(role), ChangeJson.Nothing, new RolePayload(role.Id, role.Code, null), cancellationToken).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        var before = role.Permissions.ToList();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.Roles.Remove(role);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["name"] = role.Name, ["permissions_before"] = string.Join(',', before), ["permissions_after"] = string.Empty };
        await audit.WriteAsync(new AuditEvent(AuthorizationAuditEvents.RoleDeleted, AuditOutcome.Success, actor.Id, role.Code, details, RecordType, role.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>The tenant roles as a document another tenant can import; system roles are every tenant's already.</summary>
    public async Task<RoleExport> ExportAsync(CancellationToken cancellationToken)
    {
        var roles = await context.Roles.AsNoTracking().Where(r => !r.IsSystem).OrderBy(r => r.Code).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new RoleExport(RoleExport.CurrentFormat, roles.Select(r => new RoleDocument(r.Code, r.Name, r.Description, r.PrincipalType, r.Permissions)).ToList());
    }

    /// <summary>
    /// Compares a document with this tenant's roles and, unless it is a dry run, applies it (TF-032 AC3, ADR-0031): roles
    /// are created or changed by code, system role codes are skipped, an apply with unknown permissions or with
    /// permissions the importer lacks tenant-wide is refused, and each role applied writes its own audit event.
    /// </summary>
    public async Task<Result<ImportDiff>> ImportAsync(RoleExport document, bool dryRun, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        var shape = CheckDocument(document);
        if (shape is not null)
        {
            return shape;
        }

        var existing = await context.Roles.ToDictionaryAsync(r => r.Code, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        var created = new List<RoleDocument>();
        var changed = new List<(RoleDocument Document, Role Role, RoleChange Change)>();
        var unchanged = new List<string>();
        var unknown = new List<UnknownPermissions>();
        var skipped = new List<string>();
        foreach (var item in document.Roles.OrderBy(r => r.Code, StringComparer.Ordinal))
        {
            if (SystemRoles.All.Contains(item.Code, StringComparer.Ordinal) || (existing.TryGetValue(item.Code, out var system) && system.IsSystem))
            {
                skipped.Add(item.Code);
                continue;
            }

            var permissions = Role.Normalise(item.Permissions);
            var notKnown = permissions.Where(p => !catalogue.Contains(p)).ToList();
            if (notKnown.Count > 0)
            {
                unknown.Add(new UnknownPermissions(item.Code, notKnown));
            }

            if (!existing.TryGetValue(item.Code, out var role))
            {
                created.Add(item);
                continue;
            }

            var added = permissions.Except(role.Permissions, StringComparer.Ordinal).ToList();
            var removed = role.Permissions.Except(permissions, StringComparer.Ordinal).ToList();
            var fields = ChangedFields(role, item.Name, item.Description, item.PrincipalType);
            if (added.Count == 0 && removed.Count == 0 && fields.Count == 0)
            {
                unchanged.Add(item.Code);
            }
            else
            {
                changed.Add((item, role, new RoleChange(item.Code, added, removed, fields)));
            }
        }

        var diff = new ImportDiff(dryRun, Applied: false, created.Select(r => r.Code).ToList(), changed.Select(c => c.Change).ToList(), unchanged, unknown, skipped);
        if (dryRun)
        {
            return diff;
        }

        if (unknown.Count > 0)
        {
            var names = string.Join("; ", unknown.Select(u => $"{u.Code}: {string.Join(", ", u.Permissions)}"));
            return Error.Validation(AuthorizationErrors.ImportUnknownPermissions, $"The document holds permissions this tenant does not know ({names}). Remove them or import into a tenant that has the modules.");
        }

        var escalation = await EscalationAsync(created.Concat(changed.Select(c => c.Document)).SelectMany(r => r.Permissions), cancellationToken).ConfigureAwait(false);
        if (escalation is not null)
        {
            return escalation;
        }

        foreach (var (item, role, _) in changed)
        {
            if (item.PrincipalType != role.PrincipalType && await InUseAsync(role.Id, cancellationToken).ConfigureAwait(false))
            {
                return Error.Conflict(AuthorizationErrors.RoleInUse, $"'{role.Code}' is assigned; the import cannot change its principal type.");
            }
        }

        // The roles whose permissions change are checked together: two changes may complete one pair.
        var permissionChanges = changed
            .Where(c => c.Change.Added.Count > 0 || c.Change.Removed.Count > 0)
            .ToDictionary(c => c.Role.Id, c => (IReadOnlyCollection<string>)Role.Normalise(c.Document.Permissions));
        if (permissionChanges.Count > 0 && await BlockedAsync(permissionChanges, cancellationToken).ConfigureAwait(false) is { } blocked)
        {
            return blocked;
        }

        if (created.Count + changed.Count > 0
            && await HeldAsync(ImportKind, "roles:import", $"Import {created.Count + changed.Count} roles", await ImportStateAsync(document, cancellationToken).ConfigureAwait(false), ChangeJson.Of(ImportAfter(document)), document, cancellationToken).ConfigureAwait(false) is { } held)
        {
            return held;
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        foreach (var item in created)
        {
            var role = new Role(item.Code, item.Name, item.Description, isSystem: false, item.PrincipalType, item.Permissions);
            context.Roles.Add(role);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(AuthorizationAuditEvents.RoleCreated, role, [], [], cancellationToken, "import").ConfigureAwait(false);
        }

        foreach (var (item, role, change) in changed)
        {
            var before = role.Permissions.ToList();
            role.Change(item.Name, item.Description, item.PrincipalType, item.Permissions);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(AuthorizationAuditEvents.RoleChanged, role, before, change.Fields, cancellationToken, "import").ConfigureAwait(false);
        }

        if (permissionChanges.Count > 0)
        {
            await segregation.RefreshHoldersAsync([.. permissionChanges.Keys], cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        foreach (var (_, role, _) in changed)
        {
            await ForgetHoldersAsync(role.Id, cancellationToken).ConfigureAwait(false);
        }

        return diff with { Applied = true };
    }

    private Error? Check(RoleInput input)
    {
        if (!Role.IsValidName(input.Name))
        {
            return Error.Validation(ApiErrors.Validation, "The name is not valid.", new FieldError("name", "invalid", $"Enter a name of 1 to {Role.NameMaxLength} characters."));
        }

        if ((input.Description ?? string.Empty).Trim().Length > Role.DescriptionMaxLength)
        {
            return Error.Validation(ApiErrors.Validation, "The description is too long.", new FieldError("description", "too_long", $"Use at most {Role.DescriptionMaxLength} characters."));
        }

        if (input.PrincipalType is not (PrincipalKind.User or PrincipalKind.Service))
        {
            return Error.Validation(AuthorizationErrors.RolePrincipalType, "A tenant role is for people or for service accounts.", new FieldError("principal_type", "invalid", "Choose people or service accounts."));
        }

        var unknown = Role.Normalise(input.Permissions).Where(p => !catalogue.Contains(p)).ToList();
        return unknown.Count == 0
            ? null
            : Error.Validation(AuthorizationErrors.RolePermissionUnknown, $"This tenant does not know {string.Join(", ", unknown)}.", new FieldError("permissions", "unknown", $"Unknown: {string.Join(", ", unknown)}."));
    }

    private static Error? CheckDocument(RoleExport document)
    {
        if (document.FormatVersion != RoleExport.CurrentFormat || document.Roles is null)
        {
            return Error.Validation(AuthorizationErrors.ImportFormat, $"This is not a role export of format {RoleExport.CurrentFormat}.");
        }

        var duplicate = document.Roles.GroupBy(r => r.Code, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            return Error.Validation(AuthorizationErrors.ImportFormat, $"The document lists the role '{duplicate.Key}' twice.");
        }

        var bad = document.Roles.FirstOrDefault(r => !Role.IsValidCode(r.Code) || !Role.IsValidName(r.Name) || r.PrincipalType is not (PrincipalKind.User or PrincipalKind.Service) || r.Permissions is null);
        return bad is null
            ? null
            : Error.Validation(AuthorizationErrors.ImportFormat, $"The role '{bad.Code}' in the document has a code, a name or a principal type this tenant cannot take.");
    }

    /// <summary>ADR-0031 rule 2: a role holds only permissions its editor holds tenant-wide.</summary>
    private async Task<Error?> EscalationAsync(IEnumerable<string> permissions, CancellationToken cancellationToken)
    {
        var held = (await grants.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false)).TenantWide;
        var missing = Role.Normalise(permissions).Where(p => !held.Contains(p)).ToList();
        return missing.Count == 0
            ? null
            : Error.Forbidden(AuthorizationErrors.RoleEscalation, $"A role can only hold permissions you hold for the whole tenant. You do not hold: {string.Join(", ", missing)}.");
    }

    /// <summary>
    /// A change of roles' permissions checked against the conflict rules at once (TF-036, ADR-0039): refused when a block
    /// rule would find a new conflict for anyone holding one of the roles, with the rule named and how many people it
    /// would affect. Under a warn rule the change is saved, and the holders' rows of the report are computed again.
    /// </summary>
    private async Task<Error?> BlockedAsync(IReadOnlyDictionary<Guid, IReadOnlyCollection<string>> changes, CancellationToken cancellationToken)
    {
        var blocking = (await segregation.ForRoleChangesAsync(changes, cancellationToken).ConfigureAwait(false)).Where(c => c.Conflict.Rule.Mode == ConflictModes.Block).ToList();
        return blocking.Count == 0 ? null : Error.Conflict(AuthorizationErrors.SodConflict, ConflictService.RoleChangeRefusal(blocking, [.. changes.Keys]));
    }

    private async Task<bool> InUseAsync(Guid roleId, CancellationToken cancellationToken) =>
        await context.RoleAssignments.AnyAsync(a => a.RoleId == roleId, cancellationToken).ConfigureAwait(false);

    private async Task ForgetHoldersAsync(Guid roleId, CancellationToken cancellationToken)
    {
        var holders = await context.RoleAssignments.AsNoTracking().Where(a => a.RoleId == roleId).Select(a => a.PrincipalId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var holder in holders)
        {
            grants.Forget(holder);
        }
    }

    private static List<string> ChangedFields(Role role, string name, string description, PrincipalKind principalType)
    {
        var fields = new List<string>();
        if (!string.Equals(role.Name, (name ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            fields.Add("name");
        }

        if (!string.Equals(role.Description, (description ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            fields.Add("description");
        }

        if (role.PrincipalType != principalType)
        {
            fields.Add("principal_type");
        }

        return fields;
    }

    private Task WriteAsync(string action, Role role, IReadOnlyList<string> before, IReadOnlyList<string> fields, CancellationToken cancellationToken, string? reason = null)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["name"] = role.Name,
            ["principal_type"] = role.PrincipalType.ToString().ToLowerInvariant(),
            ["permissions_before"] = string.Join(',', before),
            ["permissions_after"] = string.Join(',', role.Permissions),
        };
        if (fields.Count > 0)
        {
            details["fields"] = string.Join(',', fields);
        }

        return audit.WriteAsync(new AuditEvent(action, AuditOutcome.Success, actor.Id, role.Code, details, RecordType, role.Id, reason), cancellationToken);
    }

    /// <summary>
    /// The state of what a held role change changes, now (ADR-0052): the role by its id, or by its code for a role not
    /// created yet, or for an import every role the document names. Compared with the change's before on approval.
    /// </summary>
    public async Task<JsonElement> StateAsync(HeldChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Kind == ImportKind)
        {
            return await ImportStateAsync(Import(change), cancellationToken).ConfigureAwait(false);
        }

        var payload = Payload(change);
        var role = payload.Id is { } id
            ? await context.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id, cancellationToken).ConfigureAwait(false)
            : await context.Roles.AsNoTracking().SingleOrDefaultAsync(r => r.Code == payload.Code, cancellationToken).ConfigureAwait(false);
        return role is null ? ChangeJson.Nothing : State(role);
    }

    /// <summary>Applies an approved role change through the same path a direct save takes, as the approver (ADR-0052).</summary>
    public async Task<Result> ApplyAsync(HeldChange change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (change.Kind == ImportKind)
        {
            var imported = await ImportAsync(Import(change), dryRun: false, cancellationToken).ConfigureAwait(false);
            return imported.IsFailure ? imported.Error : Result.Success();
        }

        var payload = Payload(change);
        switch (change.Kind)
        {
            case CreateKind:
                var created = await CreateAsync(payload.Code, payload.Input!, cancellationToken).ConfigureAwait(false);
                return created.IsFailure ? created.Error : Result.Success();
            case UpdateKind:
                var updated = await UpdateAsync(payload.Id!.Value, payload.Input!, cancellationToken).ConfigureAwait(false);
                return updated.IsFailure ? updated.Error : Result.Success();
            case DeleteKind:
                return await DeleteAsync(payload.Id!.Value, cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException($"The role service does not apply changes of the kind {change.Kind}.");
        }
    }

    /// <summary>A save of a role waits for a second administrator when the tenant lists roles for four-eyes (TF-068 AC1).</summary>
    private async Task<Error?> HeldAsync(string kind, string subject, string title, JsonElement before, JsonElement after, object payload, CancellationToken cancellationToken)
    {
        if (fourEyes is null || !await fourEyes.AppliesAsync(ConfigurationAreas.Roles, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return await fourEyes.HoldAsync(new ChangeProposal(ConfigurationAreas.Roles, kind, subject, title, before, after, ChangeJson.Of(payload), AuthorizationPermissions.RolesManage.Name, AccessScope.Tenant), cancellationToken).ConfigureAwait(false);
    }

    private static JsonElement State(Role role) => ChangeJson.Of(new RoleState(role.Code, role.Name, role.Description, role.PrincipalType.ToString().ToLowerInvariant(), Role.Normalise(role.Permissions)));

    private static JsonElement State(string code, RoleInput input) =>
        ChangeJson.Of(new RoleState(code, (input.Name ?? string.Empty).Trim(), (input.Description ?? string.Empty).Trim(), input.PrincipalType.ToString().ToLowerInvariant(), Role.Normalise(input.Permissions)));

    private async Task<JsonElement> ImportStateAsync(RoleExport document, CancellationToken cancellationToken)
    {
        var codes = document.Roles.Select(r => r.Code).ToArray();
        var roles = await context.Roles.AsNoTracking().Where(r => codes.Contains(r.Code)).ToDictionaryAsync(r => r.Code, StringComparer.Ordinal, cancellationToken).ConfigureAwait(false);
        return ChangeJson.Of(codes.Order(StringComparer.Ordinal).ToDictionary(code => code, code => roles.TryGetValue(code, out var role) ? State(role) : ChangeJson.Nothing, StringComparer.Ordinal));
    }

    private static Dictionary<string, JsonElement> ImportAfter(RoleExport document) =>
        document.Roles.OrderBy(r => r.Code, StringComparer.Ordinal).ToDictionary(
            r => r.Code,
            r => State(r.Code, new RoleInput(r.Name, r.Description, r.PrincipalType, r.Permissions)),
            StringComparer.Ordinal);

    private static RolePayload Payload(HeldChange change) =>
        change.Payload.Deserialize<RolePayload>(ChangeJson.Options) ?? throw new InvalidOperationException($"The change {change.Id} carries no role.");

    private static RoleExport Import(HeldChange change) =>
        change.Payload.Deserialize<RoleExport>(ChangeJson.Options) ?? throw new InvalidOperationException($"The change {change.Id} carries no import.");

    private static Error NotFound() => Error.NotFound(AuthorizationErrors.RoleNotFound, "There is no role with this id.");

    private static Error SystemRole(Role role) =>
        Error.Conflict(AuthorizationErrors.RoleSystem, $"'{role.Name}' is a system role. System roles follow the permission catalogue and cannot be changed or deleted.");
}
