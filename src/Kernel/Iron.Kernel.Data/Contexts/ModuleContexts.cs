using System.Collections.Concurrent;
using Iron.Kernel.Data.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Data.Contexts;

/// <summary>Finds the module context that maps an entity type: the record policies, the sharing and the tags need it.</summary>
internal static class ModuleContexts
{
    // An entity type is mapped by one module context for the life of the process; found once, then remembered.
    private static readonly ConcurrentDictionary<Type, Type> ContextOfEntity = new();

    public static DbContext Of<T>(IServiceProvider services, IEnumerable<RegisteredContext> contexts)
        where T : class => Of(typeof(T), services, contexts);

    public static DbContext Of(Type entityType, IServiceProvider services, IEnumerable<RegisteredContext> contexts)
    {
        var contextType = ContextOfEntity.GetOrAdd(entityType, _ =>
            contexts.Select(c => c.ContextType).FirstOrDefault(type => ((DbContext)services.GetRequiredService(type)).Model.FindEntityType(entityType) is not null)
            ?? throw new InvalidOperationException($"No module context maps {entityType.Name}; register the module's context with AddIronDbContext."));
        return (DbContext)services.GetRequiredService(contextType);
    }
}
