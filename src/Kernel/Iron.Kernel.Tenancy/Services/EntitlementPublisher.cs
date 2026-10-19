using Iron.Kernel.Entitlements.Contracts;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>
/// Writes a tenant's effective entitlements to the store at <c>tenants/&lt;slug&gt;/entitlements</c> after a committed
/// change (ADR-0034), and marks the revision the store holds. A store that cannot take them is logged and left to the
/// fleet's reconcile, which runs every minute; nothing here throws. A change committed while a write runs is written
/// after it, so the store never ends on an older revision.
/// </summary>
public sealed class EntitlementPublisher(TenancyDbContext context, EntitlementService entitlements, ISecretStore store, ILogger<EntitlementPublisher> logger) : ITenantEntitlements
{
    public async Task<bool> PublishAsync(string slug, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var document = await entitlements.DocumentAsync(slug, cancellationToken).ConfigureAwait(false);
                if (document is null)
                {
                    return false;
                }

                await store.WriteAsync(EntitlementsDocument.PathOf(slug), document.ToSecret(), cancellationToken).ConfigureAwait(false);
                var tenant = await context.Tenants.SingleAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
                await context.Entry(tenant).ReloadAsync(cancellationToken).ConfigureAwait(false);
                tenant.Published(document.Revision);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (tenant.EntitlementsRevision <= document.Revision)
                {
                    logger.LogInformation("Wrote revision {Revision} of tenant {Slug}'s entitlements to the store.", document.Revision, slug);
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Tenant {Slug}'s entitlements could not be written to the store ({Error}); the fleet writes them within a minute.", slug, exception.Message);
            return false;
        }
    }
}

/// <summary>Bound from the <c>Tenancy</c> section of the fleet worker.</summary>
public sealed class ReconcileOptions
{
    public const string SectionName = "Tenancy:Reconcile";

    /// <summary>How often the fleet looks for tenants whose store copy is behind (ADR-0034).</summary>
    public int Seconds { get; set; } = 60;
}

/// <summary>
/// The fleet worker's loop (ADR-0034): every minute, every tenant whose entitlements the store does not hold at their
/// current revision is written again. Errors are logged, never thrown.
/// </summary>
public sealed class EntitlementReconciler(IServiceScopeFactory scopes, ReconcileOptions options, TimeProvider time, ILogger<EntitlementReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Seconds)), time);
        try
        {
            do
            {
                await ReconcileAsync(stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }

    /// <summary>One pass: the number of tenants written.</summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var behind = await context.Tenants.AsNoTracking()
                .Where(t => t.PublishedRevision < t.EntitlementsRevision && t.Status != TenantStatus.Decommissioned)
                .Select(t => t.Slug)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var written = 0;
            foreach (var slug in behind)
            {
                await using var tenantScope = scopes.CreateAsyncScope();
                if (await tenantScope.ServiceProvider.GetRequiredService<ITenantEntitlements>().PublishAsync(slug, cancellationToken).ConfigureAwait(false))
                {
                    written++;
                }
            }

            if (behind.Count > 0)
            {
                logger.LogInformation("Reconciled the entitlements of {Written} of {Behind} tenant(s) whose store copy was behind.", written, behind.Count);
            }

            return written;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "The fleet could not reconcile the tenants' entitlements.");
            return 0;
        }
    }
}
