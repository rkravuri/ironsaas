using System.Reflection;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Policies;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Data.Visibility;

/// <summary>
/// Rule 6 of TF-241 as a declaration (ADR-0057): a record type that takes the visibility tags of its master when it is
/// created, the master named by its record type as its policy registers it (another module's, through no reference to
/// it) and by the record's own field that holds the master's id. Each product decides, in its own PRD, which of its
/// record types declare it; the kernel declares none.
/// </summary>
public sealed record TagsFromMaster(Type RecordType, string MasterRecordType, Func<BusinessRecord, Guid?> MasterId);

public static class TagsFromMasterServiceCollectionExtensions
{
    /// <summary>
    /// A new record of <typeparamref name="T"/> without tags of its own takes, when it is saved, the tags its master
    /// carries then. Later changes of the master's tags do not follow: the record's tags are its own from then on.
    /// </summary>
    public static IServiceCollection AddTagsFromMaster<T>(this IServiceCollection services, string masterRecordType, Func<T, Guid?> masterId)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(masterRecordType);
        ArgumentNullException.ThrowIfNull(masterId);
        if (services.Any(d => d.ImplementationInstance is TagsFromMaster declared && declared.RecordType == typeof(T)))
        {
            throw new InvalidOperationException($"{typeof(T).Name} takes its tags from a master already; a record type has one master for its tags.");
        }

        services.AddSingleton(new TagsFromMaster(typeof(T), masterRecordType, record => masterId((T)record)));
        return services;
    }
}

/// <summary>
/// Gives a new record its master's tags before the save (rule 6), so the record's creation, with its tags, is one event
/// of its field history. Runs before the stamps and the history; a record that carries tags of its own keeps them. The
/// master is read as it is, without the creator's policy: the product's rule decides, not the reader.
/// </summary>
public sealed class MasterTagsInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    private static readonly MethodInfo TagsOfMaster = typeof(MasterTagsInterceptor).GetMethod(nameof(TagsOfAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        TakeAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await TakeAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task TakeAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return;
        }

        var declared = services.GetServices<TagsFromMaster>().ToDictionary(d => d.RecordType);
        if (declared.Count == 0)
        {
            return;
        }

        var created = context.ChangeTracker.Entries<BusinessRecord>()
            .Where(e => e.State == EntityState.Added && e.Entity.VisibilityTags.Count == 0 && declared.ContainsKey(e.Entity.GetType()))
            .Select(e => e.Entity)
            .ToList();
        foreach (var record in created)
        {
            var declaration = declared[record.GetType()];
            if (declaration.MasterId(record) is not { } masterId)
            {
                continue;
            }

            var master = services.GetServices<RecordPolicyRegistration>().FirstOrDefault(r => string.Equals(r.Name, declaration.MasterRecordType, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"{record.GetType().Name} takes its tags from {declaration.MasterRecordType}, which has no record policy in this host.");
            var masterContext = ModuleContexts.Of(master.RecordType, services, services.GetServices<RegisteredContext>());
            var tags = await ((Task<IReadOnlyList<string>?>)TagsOfMaster.MakeGenericMethod(master.RecordType).Invoke(null, [masterContext, masterId, cancellationToken])!).ConfigureAwait(false);
            if (tags is { Count: > 0 })
            {
                record.ChangeVisibilityTags(tags);
            }
        }
    }

    private static async Task<IReadOnlyList<string>?> TagsOfAsync<TMaster>(DbContext context, Guid masterId, CancellationToken cancellationToken)
        where TMaster : BusinessRecord =>
        await context.Set<TMaster>().AsNoTracking().Where(m => m.Id == masterId).Select(m => m.VisibilityTags).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
}
