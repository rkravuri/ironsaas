using Iron.Kernel.Api.Modules;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Entitlements.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Entitlements.Services;

/// <summary>A per-person module's seats: how many the subscription has, how many are assigned, how many are left (TF-041 AC3).</summary>
public sealed record SeatUsage(string Module, int? Limit, int Assigned, int? Remaining);

/// <summary>
/// The seats of per-person modules (TF-041): an administrator assigns one to a person within the licensed count, and
/// removes it. Above the count the assignment is refused with the count in the message. Every change writes its audit
/// event in the same transaction; a transaction-scoped advisory lock per module keeps two assignments from passing
/// the count together.
/// </summary>
public sealed class SeatService(
    EntitlementsDbContext context,
    EntitlementCache cache,
    EntitlementsOptions options,
    LicensingCatalogue catalogue,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal current,
    TimeProvider time,
    IPrincipals? principals = null)
{
    /// <summary>The advisory lock keys of seat changes start here; the module's code makes the rest.</summary>
    public const long SeatLockBase = 0x5EA7_0000_0000;

    public Task<bool> HoldsAsync(string module, Guid principalId, CancellationToken cancellationToken) =>
        context.Seats.AsNoTracking().AnyAsync(s => s.ModuleCode == module && s.PrincipalId == principalId, cancellationToken);

    public async Task<Result<SeatAssignment>> AssignAsync(string module, Guid principalId, CancellationToken cancellationToken)
    {
        var declared = catalogue.FindModule(module);
        if (declared is not { SubscriptionType: SubscriptionType.PerPerson })
        {
            return Error.Validation(EntitlementsErrors.SeatNotPerPerson, $"'{module}' is not a module licensed per person, so it has no seats.", new FieldError("module", "not_per_person", "Choose a module licensed per person."));
        }

        var licensed = cache.Usable(time.GetUtcNow(), options.StaleAfter)?.Document.FindModule(module);
        if (licensed is null)
        {
            return Error.Conflict(EntitlementsErrors.SeatNotLicensed, $"Your organisation's subscription does not include '{module}', so no seat can be assigned. Ask your administrator about the plan.");
        }

        if (principals is not null)
        {
            var found = await principals.FindAsync([principalId], cancellationToken).ConfigureAwait(false);
            if (!found.TryGetValue(principalId, out var person) || person.Type != PrincipalKind.User)
            {
                return Error.Validation(EntitlementsErrors.SeatPrincipal, "A seat is for a person of this tenant. Choose a user.", new FieldError("principal_id", "not_a_user", "Choose a user of this tenant."));
            }
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({LockKey(module)})", cancellationToken).ConfigureAwait(false);
        var existing = await context.Seats.SingleOrDefaultAsync(s => s.ModuleCode == module && s.PrincipalId == principalId, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var assigned = await context.Seats.CountAsync(s => s.ModuleCode == module, cancellationToken).ConfigureAwait(false);
        var limit = licensed.UserLimit ?? 0;
        if (assigned >= limit)
        {
            return Error.Conflict(EntitlementsErrors.SeatLimit, $"All {limit} seats of '{module}' are assigned ({assigned} of {limit}). Remove a seat, or ask your administrator for more, before you assign another.");
        }

        var seat = new SeatAssignment(module, principalId);
        context.Seats.Add(seat);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(EntitlementsAuditEvents.SeatAssigned, seat, assigned + 1, limit, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return seat;
    }

    public async Task<Result> UnassignAsync(Guid seatId, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var seat = await context.Seats.SingleOrDefaultAsync(s => s.Id == seatId, cancellationToken).ConfigureAwait(false);
        if (seat is null)
        {
            return Error.NotFound(EntitlementsErrors.SeatNotFound, "There is no seat with this id.");
        }

        await context.Database.ExecuteSqlAsync($"select pg_advisory_xact_lock({LockKey(seat.ModuleCode)})", cancellationToken).ConfigureAwait(false);
        context.Seats.Remove(seat);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var assigned = await context.Seats.CountAsync(s => s.ModuleCode == seat.ModuleCode, cancellationToken).ConfigureAwait(false);
        var limit = cache.Current?.Document.FindModule(seat.ModuleCode)?.UserLimit ?? 0;
        await WriteAsync(EntitlementsAuditEvents.SeatUnassigned, seat, assigned, limit, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Every per-person module this build declares, with its licensed count and the seats assigned.</summary>
    public async Task<IReadOnlyList<SeatUsage>> UsageAsync(CancellationToken cancellationToken)
    {
        var counts = await context.Seats.AsNoTracking().GroupBy(s => s.ModuleCode).Select(g => new { Module = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Module, g => g.Count, cancellationToken).ConfigureAwait(false);
        var document = cache.Current?.Document;
        return catalogue.Modules
            .Where(m => m.SubscriptionType == SubscriptionType.PerPerson)
            .Select(m =>
            {
                var limit = document?.FindModule(m.Code)?.UserLimit;
                var assigned = counts.GetValueOrDefault(m.Code);
                return new SeatUsage(m.Code, limit, assigned, limit is null ? null : Math.Max(0, limit.Value - assigned));
            })
            .ToList();
    }

    /// <summary>A stable key per module code: the base plus a hash of the code, the same in every process.</summary>
    public static long LockKey(string module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var hash = 17L;
        foreach (var character in module)
        {
            hash = unchecked((hash * 31) + character) & 0xFFFF_FFFF;
        }

        return SeatLockBase + hash;
    }

    private async Task WriteAsync(string action, SeatAssignment seat, int assigned, int limit, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["module"] = seat.ModuleCode,
            ["principal_id"] = seat.PrincipalId.ToString("D"),
            ["assigned"] = assigned.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        await audit.WriteAsync(new AuditEvent(action, AuditOutcome.Success, current.Id, seat.ModuleCode, details, "seat", seat.Id), cancellationToken).ConfigureAwait(false);
    }
}
