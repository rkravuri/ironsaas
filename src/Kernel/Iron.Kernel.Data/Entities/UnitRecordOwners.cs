using System.Reflection;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Data.Entities;

/// <summary>The records of one type a unit owns, by id: the type's code, and its display name for people to read.</summary>
public sealed record UnitRecords(string RecordType, string DisplayName, IReadOnlyList<Guid> RecordIds);

/// <summary>
/// The business records a unit owns, for a merge (TF-223 AC1, ADR-0038): what will move, and moving it to the surviving
/// unit inside the merge's transaction. Every module with business records registers one, most simply the kernel's
/// <see cref="ContextRecordOwners{TContext}"/> with <c>AddUnitRecordOwners&lt;TContext&gt;(module)</c>.
/// </summary>
public interface IUnitRecordOwners
{
    /// <summary>The module, as the merge preview names it.</summary>
    string Module { get; }

    Task<IReadOnlyList<UnitRecords>> OwnedByAsync(Guid unitId, CancellationToken cancellationToken);

    /// <summary>Every record owned by the first unit is owned by the second; answers what moved. Joins the caller's transaction.</summary>
    Task<IReadOnlyList<UnitRecords>> MoveAsync(Guid fromUnitId, Guid toUnitId, CancellationToken cancellationToken);
}

/// <summary>
/// Every business record type of one module context, in one statement per type, deleted records included, since a
/// merged unit must own nothing. The record types are named as their policies name them.
/// </summary>
public sealed class ContextRecordOwners<TContext>(TContext context, IEnumerable<RecordPolicyRegistration> registrations, TimeProvider time, ICurrentPrincipal actor, string module) : IUnitRecordOwners
    where TContext : ModuleDbContext
{
    private static readonly MethodInfo IdsOf = typeof(ContextRecordOwners<TContext>).GetMethod(nameof(IdsAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo MoveOf = typeof(ContextRecordOwners<TContext>).GetMethod(nameof(MoveTypeAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public string Module => module;

    public async Task<IReadOnlyList<UnitRecords>> OwnedByAsync(Guid unitId, CancellationToken cancellationToken)
    {
        var owned = new List<UnitRecords>();
        foreach (var type in RecordTypes())
        {
            var ids = await ((Task<List<Guid>>)IdsOf.MakeGenericMethod(type).Invoke(this, [unitId, cancellationToken])!).ConfigureAwait(false);
            if (ids.Count > 0)
            {
                owned.Add(Records(type, ids));
            }
        }

        return owned;
    }

    public async Task<IReadOnlyList<UnitRecords>> MoveAsync(Guid fromUnitId, Guid toUnitId, CancellationToken cancellationToken)
    {
        var moved = new List<UnitRecords>();
        foreach (var type in RecordTypes())
        {
            var ids = await ((Task<List<Guid>>)MoveOf.MakeGenericMethod(type).Invoke(this, [fromUnitId, toUnitId, cancellationToken])!).ConfigureAwait(false);
            if (ids.Count > 0)
            {
                moved.Add(Records(type, ids));
            }
        }

        return moved;
    }

    private IEnumerable<Type> RecordTypes() =>
        context.Model.GetEntityTypes().Select(e => e.ClrType).Where(t => typeof(BusinessRecord).IsAssignableFrom(t)).OrderBy(t => t.Name, StringComparer.Ordinal);

    // A type with no policy fails the architecture test; its CLR name stands in so a merge still lists it.
    private UnitRecords Records(Type type, List<Guid> ids) =>
        registrations.FirstOrDefault(r => r.RecordType == type) is { } registration
            ? new UnitRecords(registration.Name, registration.DisplayName, ids)
            : new UnitRecords(type.Name, type.Name, ids);

    private async Task<List<Guid>> IdsAsync<T>(Guid unitId, CancellationToken cancellationToken)
        where T : BusinessRecord =>
        await context.Set<T>().IgnoreQueryFilters().AsNoTracking().Where(r => r.OwningOrganisationId == unitId).OrderBy(r => r.Id).Select(r => r.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<List<Guid>> MoveTypeAsync<T>(Guid fromUnitId, Guid toUnitId, CancellationToken cancellationToken)
        where T : BusinessRecord
    {
        var ids = await IdsAsync<T>(fromUnitId, cancellationToken).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return ids;
        }

        // One statement for the type; the stamps by hand, since an update in the database passes the save interceptors.
        var now = time.GetUtcNow();
        var by = actor.Id;
        await context.Set<T>().IgnoreQueryFilters()
            .Where(r => r.OwningOrganisationId == fromUnitId)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(r => r.OwningOrganisationId, toUnitId)
                    .SetProperty(r => r.UpdatedAt, now)
                    .SetProperty(r => r.UpdatedBy, by)
                    .SetProperty(r => r.Version, r => r.Version + 1),
                cancellationToken)
            .ConfigureAwait(false);
        return ids;
    }
}

public static class UnitRecordOwnersServiceCollectionExtensions
{
    /// <summary>The business records of a module's context, for merging units (ADR-0038): one line in the module's AddServices.</summary>
    public static IServiceCollection AddUnitRecordOwners<TContext>(this IServiceCollection services, string module)
        where TContext : ModuleDbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        services.AddScoped<IUnitRecordOwners>(provider => new ContextRecordOwners<TContext>(
            provider.GetRequiredService<TContext>(),
            provider.GetServices<RecordPolicyRegistration>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ICurrentPrincipal>(),
            module));
        return services;
    }
}
