using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Changes.Contracts;
using Iron.Kernel.Changes.Data;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Settings.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Changes.Services;

/// <summary>
/// Whether the scope is applying an approved change (ADR-0052): the owning module's save then runs through its own rules
/// without asking for a second administrator again. Scoped: one approval's application per scope.
/// </summary>
public sealed class ChangeApplication
{
    public Guid? Applying { get; set; }
}

/// <summary>
/// The four-eyes answer of a tenant (TF-068 AC1, ADR-0052): a save in an area the tenant lists becomes a pending change
/// with its before and after, its requester and reason, audited, and nothing applies yet. The four-eyes setting itself
/// is governed whenever any area is listed.
/// </summary>
public sealed class FourEyes(
    ChangesDbContext context,
    ISettings settings,
    ICurrentPrincipal current,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    ChangeApplication application,
    TimeProvider time,
    IPrincipals? principals = null) : IFourEyes
{
    public async Task<bool> AppliesAsync(string area, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(area);
        if (application.Applying is not null || current.Id == SystemPrincipal.SystemId || current.IsAnonymous())
        {
            return false;
        }

        var areas = ChangesSettings.AreasOf(await settings.GetAsync(ChangesSettings.Areas, SettingContext.Tenant, cancellationToken).ConfigureAwait(false));
        return area == ConfigurationAreas.FourEyes ? areas.Count > 0 : areas.Contains(area, StringComparer.Ordinal);
    }

    public async Task<Error> HoldAsync(ChangeProposal proposal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        if (proposal.Reason is { Length: > PendingChange.ReasonMaxLength })
        {
            return Error.Validation(ChangesErrors.ReasonRequired, $"A reason is at most {PendingChange.ReasonMaxLength} characters.", new FieldError("reason", "too_long", $"At most {PendingChange.ReasonMaxLength} characters."));
        }

        var waiting = await context.Changes.AsNoTracking().FirstOrDefaultAsync(c => c.Subject == proposal.Subject && c.Status == PendingChangeStates.Pending, cancellationToken).ConfigureAwait(false);
        if (waiting is not null)
        {
            return AlreadyPending(waiting);
        }

        var days = await settings.GetAsync(ChangesSettings.ExpiryDays, SettingContext.Tenant, cancellationToken).ConfigureAwait(false);
        var name = await NameAsync(current.Id, cancellationToken).ConfigureAwait(false);
        var change = new PendingChange(proposal, current.Id, name, time.GetUtcNow().AddDays(days));
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.Changes.Add(change);
        try
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Two saves of one subject at once: the unique index keeps the first; this one answers as the second.
            return Error.Conflict(ChangesErrors.AlreadyPending, $"A change of this is already waiting for approval: {proposal.Title}. Wait until it is decided, or withdraw it if it is yours.");
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["area"] = change.Area,
            ["kind"] = change.Kind,
            ["subject"] = change.Subject,
            ["title"] = change.Title,
            ["expires_at"] = change.ExpiresAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
        };
        await audit.WriteAsync(new AuditEvent(ChangesAuditEvents.ChangeRequested, AuditOutcome.Success, current.Id, change.Title, details, PendingChange.RecordType, change.Id, change.Reason), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new Error(ErrorKind.Held, ChangesErrors.Held, $"Your change waits for a second administrator's approval: {change.Title}. Nothing changes until it is approved.")
        {
            Extensions = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["change_id"] = change.Id,
                ["expires_at"] = change.ExpiresAt,
            },
        };
    }

    public static Error AlreadyPending(PendingChange waiting)
    {
        ArgumentNullException.ThrowIfNull(waiting);
        return new Error(ErrorKind.Conflict, ChangesErrors.AlreadyPending, $"A change of this is already waiting for approval, asked by {waiting.RequesterName}: {waiting.Title}. Wait until it is decided, or withdraw it if it is yours.")
        {
            Extensions = new Dictionary<string, object>(StringComparer.Ordinal) { ["change_id"] = waiting.Id },
        };
    }

    private async Task<string> NameAsync(Guid id, CancellationToken cancellationToken)
    {
        if (principals is null)
        {
            return id.ToString("D");
        }

        var found = await principals.FindAsync([id], cancellationToken).ConfigureAwait(false);
        return found.TryGetValue(id, out var summary) ? summary.DisplayName : id.ToString("D");
    }
}
