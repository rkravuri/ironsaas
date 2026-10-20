using System.Reflection;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Policies;
using Iron.Kernel.Data.Transactions;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Data.Sharing;

/// <summary>A record's sharing as its page shows it: the mode, the units it is listed for, and whether the reader may change it.</summary>
public sealed record RecordSharingView(string RecordType, Guid RecordId, Guid OwningUnitId, SharingMode Mode, IReadOnlyList<Guid> Units, bool MayChange);

/// <summary>The error codes of changing a record's sharing (ADR-0038).</summary>
public static class SharingErrors
{
    public const string RecordTypeUnknown = "data.sharing.record_type_unknown";
    public const string UnitsRequired = "data.sharing.units_required";
    public const string UnitNotActive = "data.sharing.unit_not_active";
}

/// <summary>
/// Who sees a business record beyond its unit (TF-222, ADR-0038), for any record type with a policy, named as
/// record-scoped assignments name it. Reading the sharing needs the record to be readable; changing it is the action
/// <c>share</c>, which the module's policy decides. A change replaces the listed units and writes its audit event with
/// the mode and the units before and after, in one transaction.
/// </summary>
public interface IRecordSharing
{
    Task<Result<RecordSharingView>> GetAsync(string recordType, Guid recordId, CancellationToken cancellationToken);

    /// <summary>Private and tenant list no units; shared lists at least one active unit other than the record's own.</summary>
    Task<Result<RecordSharingView>> ChangeAsync(string recordType, Guid recordId, SharingMode mode, IReadOnlyList<Guid> units, CancellationToken cancellationToken);
}

public sealed class RecordSharing(
    IServiceProvider services,
    IEnumerable<RegisteredContext> contexts,
    IRecordPolicies policies,
    IEnumerable<RecordPolicyRegistration> registrations,
    DataDbContext data,
    IUnitOfWork unitOfWork,
    ICurrentPrincipal current,
    IOrganisationUnitCheck? unitCheck = null,
    IRecordAudit? audit = null) : IRecordSharing
{
    private static readonly MethodInfo GetOf = typeof(RecordSharing).GetMethod(nameof(GetCoreAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo ChangeOf = typeof(RecordSharing).GetMethod(nameof(ChangeCoreAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public Task<Result<RecordSharingView>> GetAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        var registration = Registration(recordType);
        return registration is null
            ? Task.FromResult<Result<RecordSharingView>>(UnknownType(recordType))
            : (Task<Result<RecordSharingView>>)GetOf.MakeGenericMethod(registration.RecordType).Invoke(this, [registration.Name, recordId, cancellationToken])!;
    }

    public Task<Result<RecordSharingView>> ChangeAsync(string recordType, Guid recordId, SharingMode mode, IReadOnlyList<Guid> units, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(units);
        var registration = Registration(recordType);
        return registration is null
            ? Task.FromResult<Result<RecordSharingView>>(UnknownType(recordType))
            : (Task<Result<RecordSharingView>>)ChangeOf.MakeGenericMethod(registration.RecordType).Invoke(this, [registration.Name, recordId, mode, units, cancellationToken])!;
    }

    private async Task<Result<RecordSharingView>> GetCoreAsync<T>(string name, Guid recordId, CancellationToken cancellationToken)
        where T : BusinessRecord
    {
        var found = await policies.FindAsync<T>(recordId, RecordActions.Read, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var mayChange = await policies.For<T>(current.Id).AllowsAsync(found.Value, RecordActions.Share, cancellationToken).ConfigureAwait(false);
        return new RecordSharingView(name, recordId, found.Value.OwningOrganisationId, found.Value.SharingMode, await UnitsOfAsync(name, recordId, cancellationToken).ConfigureAwait(false), mayChange);
    }

    private async Task<Result<RecordSharingView>> ChangeCoreAsync<T>(string name, Guid recordId, SharingMode mode, IReadOnlyList<Guid> units, CancellationToken cancellationToken)
        where T : BusinessRecord
    {
        var found = await policies.FindAsync<T>(recordId, RecordActions.Share, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var record = found.Value;

        // The record's own unit sees it whatever the mode, so listing it would say nothing.
        var listed = mode == SharingMode.Shared ? units.Where(u => u != record.OwningOrganisationId).Distinct().Order().ToList() : [];
        if (mode == SharingMode.Shared && listed.Count == 0)
        {
            return Error.Validation(SharingErrors.UnitsRequired, "A shared record is listed for at least one unit besides its own.", new FieldError("units", "required", "Choose the units to share it with."));
        }

        if (listed.Count > 0 && unitCheck is not null && (await unitCheck.NotActiveAsync(listed, cancellationToken).ConfigureAwait(false)) is { Count: > 0 } inactive)
        {
            return Error.Validation(SharingErrors.UnitNotActive, $"{inactive.Count} of the units is not an active unit of this tenant.", new FieldError("units", "not_active", "Choose active units."));
        }

        var modeBefore = record.SharingMode;
        var existing = await data.Shares.Where(s => s.RecordType == name && s.RecordId == recordId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var unitsBefore = existing.Select(s => s.UnitId).Order().ToList();
        if (modeBefore == mode && unitsBefore.SequenceEqual(listed))
        {
            return new RecordSharingView(name, recordId, record.OwningOrganisationId, mode, listed, MayChange: true);
        }

        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            record.ChangeSharing(mode);
            await ModuleContexts.Of<T>(services, contexts).SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            data.Shares.RemoveRange(existing.Where(s => !listed.Contains(s.UnitId)));
            data.Shares.AddRange(listed.Where(u => !unitsBefore.Contains(u)).Select(u => new RecordShare(name, recordId, u)));
            await data.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (audit is not null)
            {
                await audit.SharingChangedAsync(new RecordSharingEvent(current.Id, name, recordId, record.OwningOrganisationId, modeBefore, unitsBefore, mode, listed), cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return new RecordSharingView(name, recordId, record.OwningOrganisationId, mode, listed, MayChange: true);
    }

    private async Task<IReadOnlyList<Guid>> UnitsOfAsync(string name, Guid recordId, CancellationToken cancellationToken) =>
        await data.Shares.AsNoTracking().Where(s => s.RecordType == name && s.RecordId == recordId).Select(s => s.UnitId).OrderBy(u => u).ToListAsync(cancellationToken).ConfigureAwait(false);

    private RecordPolicyRegistration? Registration(string recordType) => registrations.FirstOrDefault(r => string.Equals(r.Name, recordType, StringComparison.Ordinal));

    private static Error UnknownType(string recordType) => Error.NotFound(SharingErrors.RecordTypeUnknown, $"'{recordType}' is not a record type that can be shared.");
}

/// <summary>
/// A unit's shares, for a merge (TF-223, ADR-0038): which records list it, and listing the surviving unit instead, in the
/// caller's transaction. A record listed for both keeps one share.
/// </summary>
public interface IRecordShares
{
    Task<IReadOnlyList<RecordShare>> OfUnitAsync(Guid unitId, CancellationToken cancellationToken);

    Task<IReadOnlyList<RecordShare>> MoveAsync(Guid fromUnitId, Guid toUnitId, CancellationToken cancellationToken);
}

public sealed class RecordShares(DataDbContext data) : IRecordShares
{
    public async Task<IReadOnlyList<RecordShare>> OfUnitAsync(Guid unitId, CancellationToken cancellationToken) =>
        await data.Shares.AsNoTracking().Where(s => s.UnitId == unitId).OrderBy(s => s.RecordType).ThenBy(s => s.RecordId).ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<RecordShare>> MoveAsync(Guid fromUnitId, Guid toUnitId, CancellationToken cancellationToken)
    {
        var moving = await data.Shares.Where(s => s.UnitId == fromUnitId).ToListAsync(cancellationToken).ConfigureAwait(false);
        var recordIds = moving.Select(s => s.RecordId).ToArray();
        var already = await data.Shares.Where(s => s.UnitId == toUnitId && recordIds.Contains(s.RecordId)).Select(s => new { s.RecordType, s.RecordId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var share in moving)
        {
            if (already.Any(a => a.RecordType == share.RecordType && a.RecordId == share.RecordId))
            {
                data.Shares.Remove(share);
            }
            else
            {
                share.MoveTo(toUnitId);
            }
        }

        await data.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return moving;
    }
}
