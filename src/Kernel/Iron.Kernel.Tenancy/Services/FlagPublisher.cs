using Iron.Kernel.Flags.Contracts;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>The revisions of the store paths: a change marks its paths behind, inside the caller's transaction.</summary>
public sealed class FlagPublications(TenancyDbContext context)
{
    /// <summary>The next revision of a path; the row is made the first time a path changes.</summary>
    public async Task<int> TouchAsync(string path, CancellationToken cancellationToken)
    {
        var publication = context.FlagPublications.Local.FirstOrDefault(p => p.Path == path)
            ?? await context.FlagPublications.SingleOrDefaultAsync(p => p.Path == path, cancellationToken).ConfigureAwait(false);
        if (publication is null)
        {
            publication = new FlagPublication(path);
            context.FlagPublications.Add(publication);
        }

        return publication.Touch();
    }
}

/// <summary>
/// Writes the flag rules to the store after a committed change (ADR-0035): <c>platform/flags</c> with every global and
/// region rule, <c>tenants/&lt;slug&gt;/flags</c> with the tenant's tenant, unit and user rules and its region and plan. The
/// revision the store holds follows. A store that cannot take them is logged and left to the fleet's reconcile, which
/// runs every minute; nothing here throws.
/// </summary>
public sealed class FlagPublisher(TenancyDbContext context, ISecretStore store, TimeProvider time, ILogger<FlagPublisher> logger) : ITenantFlags
{
    public async Task<bool> PublishAsync(string slug, CancellationToken cancellationToken)
    {
        var platform = await PublishPathAsync(FlagRulesDocument.PlatformPath, cancellationToken).ConfigureAwait(false);
        var tenant = await PublishPathAsync(FlagRulesDocument.TenantPath(slug), cancellationToken).ConfigureAwait(false);
        return platform && tenant;
    }

    /// <summary>Writes one path at its current revision; a path that never changed is written at revision 0 so its unit reads an answer.</summary>
    public async Task<bool> PublishPathAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var publication = await context.FlagPublications.AsNoTracking().SingleOrDefaultAsync(p => p.Path == path, cancellationToken).ConfigureAwait(false);
                var document = await DocumentAsync(path, publication?.Revision ?? 0, cancellationToken).ConfigureAwait(false);
                if (document is null)
                {
                    return false;
                }

                await store.WriteAsync(path, document.ToSecret(), cancellationToken).ConfigureAwait(false);
                if (publication is null)
                {
                    return true;
                }

                var tracked = await context.FlagPublications.SingleAsync(p => p.Path == path, cancellationToken).ConfigureAwait(false);
                await context.Entry(tracked).ReloadAsync(cancellationToken).ConfigureAwait(false);
                tracked.Published(document.Revision);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (tracked.Revision <= document.Revision)
                {
                    logger.LogInformation("Wrote revision {Revision} of the flag rules at {Path} to the store.", document.Revision, path);
                    return true;
                }
            }

            return false;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("The flag rules at {Path} could not be written to the store ({Error}); the fleet writes them within a minute.", path, exception.Message);
            return false;
        }
    }

    /// <summary>The document of a path from the rules in the database; null for a tenant that does not exist.</summary>
    public async Task<FlagRulesDocument?> DocumentAsync(string path, int revision, CancellationToken cancellationToken)
    {
        if (path == FlagRulesDocument.PlatformPath)
        {
            var shared = await context.FlagRules.AsNoTracking().Where(r => r.Scope == FlagScope.Global || r.Scope == FlagScope.Region).OrderBy(r => r.FlagKey).ThenBy(r => r.Position).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new FlagRulesDocument(FlagRulesDocument.CurrentFormat, path, revision, time.GetUtcNow(), null, [.. shared.Select(r => r.ToEntry())]);
        }

        var slug = path.Split('/')[1];
        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return null;
        }

        var own = await context.FlagRules.AsNoTracking().Where(r => r.TenantSlug == slug && (r.Scope == FlagScope.Tenant || r.Scope == FlagScope.Unit || r.Scope == FlagScope.User)).OrderBy(r => r.FlagKey).ThenBy(r => r.Position).ToListAsync(cancellationToken).ConfigureAwait(false);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal) { [FlagAttributes.Region] = tenant.Region, [FlagAttributes.Plan] = tenant.Plan };
        return new FlagRulesDocument(FlagRulesDocument.CurrentFormat, path, revision, time.GetUtcNow(), attributes, [.. own.Select(r => r.ToEntry())]);
    }

    /// <summary>One pass of the reconcile: every path whose store copy is behind is written again. The number written.</summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var behind = await context.FlagPublications.AsNoTracking().Where(p => p.PublishedRevision < p.Revision).Select(p => p.Path).ToListAsync(cancellationToken).ConfigureAwait(false);
        var written = 0;
        foreach (var path in behind)
        {
            if (await PublishPathAsync(path, cancellationToken).ConfigureAwait(false))
            {
                written++;
            }
        }

        if (behind.Count > 0)
        {
            logger.LogInformation("Reconciled {Written} of {Behind} flag path(s) whose store copy was behind.", written, behind.Count);
        }

        return written;
    }
}

/// <summary>The fleet worker's minute loop for the flag paths behind (ADR-0035), beside the entitlements' reconcile.</summary>
public sealed class FlagReconciler(IServiceScopeFactory scopes, ReconcileOptions options, TimeProvider time, ILogger<FlagReconciler> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Seconds)), time);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<FlagPublisher>().ReconcileAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "The fleet could not reconcile the flag rules.");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
    }
}
