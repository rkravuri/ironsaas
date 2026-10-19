using Iron.Kernel.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Iron.Kernel.Data.Conventions;

/// <summary>
/// Runs before every save: a business record that is added, or whose owning organisation changed, must name an active
/// organisation unit (TF-034, TF-220). Services check first and answer with an error; this is the guarantee behind
/// them. The check is resolved when a save needs it, not when the context is built, because Authorization's own context
/// carries this interceptor too.
/// </summary>
public sealed class OwningOrganisationInterceptor(IServiceProvider services) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        CheckAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
        return base.SavingChanges(eventData, result);
    }

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        await CheckAsync(eventData.Context, cancellationToken).ConfigureAwait(false);
        return await base.SavingChangesAsync(eventData, result, cancellationToken).ConfigureAwait(false);
    }

    private async Task CheckAsync(DbContext? context, CancellationToken cancellationToken)
    {
        if (context is null)
        {
            return;
        }

        var units = context.ChangeTracker.Entries<BusinessRecord>()
            .Where(entry => entry.State == EntityState.Added
                || (entry.State == EntityState.Modified && entry.Property(record => record.OwningOrganisationId).IsModified))
            .Select(entry => entry.Entity.OwningOrganisationId)
            .Distinct()
            .ToList();
        if (units.Count == 0)
        {
            return;
        }

        var check = services.GetService<IOrganisationUnitCheck>();
        if (check is null)
        {
            return;
        }

        var notActive = await check.NotActiveAsync(units, cancellationToken).ConfigureAwait(false);
        if (notActive.Count > 0)
        {
            throw new OwningOrganisationException(notActive);
        }
    }
}
