using System.Globalization;
using System.Security.Cryptography;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Authorization.Contracts;
using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Compliance.Data;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Files.Contracts;
using Iron.Kernel.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Compliance.Services;

/// <summary>A short-lived link to download a pack: its path, which carries the token, and when it stops working.</summary>
public sealed record EvidenceLink(string Url, DateTimeOffset ExpiresAt);

/// <summary>A pack opened through its link: the pack and its verified bytes.</summary>
public sealed record OpenedPack(EvidencePack Pack, byte[] Content);

/// <summary>
/// Evidence packs (TF-067, ADR-0052). A person with <c>compliance.evidence.export</c> asks for a period (AC3 refuses the
/// rest at the endpoint); the unit's worker makes the pack as the system, on the requester's behalf, from every module's
/// <see cref="IEvidenceSource"/>, with the chain's head from <see cref="IEvidenceChain"/>, into the tenant's container;
/// the person downloads it through a link that works once, for five minutes, for them alone. Asking, making and
/// downloading are audit events naming the requester (AC2).
/// </summary>
public sealed class EvidencePacks(
    ComplianceDbContext context,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal current,
    IObjectStorage storage,
    TenantContext tenant,
    TimeProvider time,
    IServiceProvider services,
    ILogger<EvidencePacks> logger,
    IPrincipals? principals = null)
{
    public const string DownloadPath = "/api/v1/evidence-packs/downloads/";

    /// <summary>The longest period a pack covers: ten years, longer than any retention an auditor asks about.</summary>
    public static readonly TimeSpan LongestPeriod = TimeSpan.FromDays(3660);

    public async Task<Result<EvidencePack>> RequestAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (to <= from)
        {
            return Error.Validation(ComplianceErrors.EvidencePeriodInvalid, "The period ends before it starts. Choose an end after the start.", new FieldError("to", "before_start", "Choose an end after the start."));
        }

        if (from >= now)
        {
            return Error.Validation(ComplianceErrors.EvidencePeriodInvalid, "The period starts in the future. Choose a start that has passed.", new FieldError("from", "future", "Choose a start that has passed."));
        }

        if (to - from > LongestPeriod)
        {
            return Error.Validation(ComplianceErrors.EvidencePeriodInvalid, "A pack covers at most ten years. Choose a shorter period.", new FieldError("from", "too_long", "Choose a period of at most ten years."));
        }

        // A period that ends later than now ends now: "at the period's end" is never a guess about the future.
        var end = to > now ? now : to;
        var name = await NameAsync(current.Id, cancellationToken).ConfigureAwait(false);
        var pack = new EvidencePack(from, end, current.Id, name);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        context.EvidencePacks.Add(pack);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(ComplianceAuditEvents.EvidencePackRequested, AuditOutcome.Success, current.Id, Title(pack), Period(pack), EvidencePack.RecordType, pack.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return pack;
    }

    public async Task<Page<EvidencePack>> ListAsync(PageRequest page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(page);
        var query = context.EvidencePacks.AsNoTracking().OrderByDescending(p => p.CreatedAt);
        var total = await query.LongCountAsync(cancellationToken).ConfigureAwait(false);
        var items = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        return new Page<EvidencePack>(items, page, total);
    }

    public Task<EvidencePack?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        context.EvidencePacks.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    /// <summary>Makes the oldest pack asked for, if any; answers whether one was made or failed.</summary>
    public async Task<bool> BuildNextAsync(CancellationToken cancellationToken)
    {
        var pack = await context.EvidencePacks.Where(p => p.Status == EvidencePackStates.Requested).OrderBy(p => p.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (pack is null)
        {
            return false;
        }

        pack.Start();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await BuildAsync(pack, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Makes a pack: every module's tables for its period, the chain's head, the ZIP into the tenant's container.</summary>
    public async Task BuildAsync(EvidencePack pack, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pack);
        try
        {
            var period = new EvidencePeriod(pack.PeriodFrom, pack.PeriodTo);
            var tables = new List<EvidenceTable>();
            foreach (var source in services.GetServices<IEvidenceSource>())
            {
                tables.AddRange(await source.TablesAsync(period, cancellationToken).ConfigureAwait(false));
            }

            var head = services.GetService<IEvidenceChain>() is { } chain ? await chain.HeadAsync(cancellationToken).ConfigureAwait(false) : null;
            var historySince = services.GetService<IRecordHistory>() is { } history ? await history.KeptSinceAsync(cancellationToken).ConfigureAwait(false) : null;
            var heading = new EvidencePackHeading(tenant.Id.Value, period, pack.RequestedById, pack.RequesterName, pack.CreatedAt, time.GetUtcNow()) { FieldHistorySince = historySince };
            var written = EvidencePackWriter.Write(heading, tables, head);
            var key = $"evidence/{pack.Id:N}.zip";
            await using (var stream = new MemoryStream(written.Zip))
            {
                await storage.PutAsync(key, stream, "application/zip", cancellationToken).ConfigureAwait(false);
            }

            await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
            pack.Made(key, written.Zip.LongLength, written.Sha256, written.ManifestSha256, head, time.GetUtcNow());
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            var details = Period(pack);
            details["sha256"] = written.Sha256;
            details["manifest_sha256"] = written.ManifestSha256;
            details["files"] = written.Files.Count.ToString(CultureInfo.InvariantCulture);
            if (head is not null)
            {
                details["chain_segment"] = head.Segment.ToString(CultureInfo.InvariantCulture);
                details["chain_position"] = head.Position.ToString(CultureInfo.InvariantCulture);
                details["chain_hash"] = head.Hash;
                details["anchor_id"] = head.Anchor?.Id?.ToString("D") ?? "none";
            }

            await audit.WriteAsync(new AuditEvent(ComplianceAuditEvents.EvidencePackMade, AuditOutcome.Success, SystemPrincipal.SystemId, Title(pack), details, EvidencePack.RecordType, pack.Id, OnBehalfOfId: pack.RequestedById), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Made evidence pack {PackId}: {Files} files, {Bytes} bytes.", pack.Id, written.Files.Count, written.Zip.LongLength);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Evidence pack {PackId} could not be made.", pack.Id);
            await using var transaction = await unitOfWork.BeginAsync(CancellationToken.None).ConfigureAwait(false);
            pack.Fail("The pack could not be made. Ask for it again; if it fails again, give the support team the time it failed.", time.GetUtcNow());
            await context.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            await audit.WriteAsync(new AuditEvent(ComplianceAuditEvents.EvidencePackFailed, AuditOutcome.Failure, SystemPrincipal.SystemId, Title(pack), Period(pack), EvidencePack.RecordType, pack.Id, OnBehalfOfId: pack.RequestedById), CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>A link to download a ready pack, for the person asking, that works once within five minutes.</summary>
    public async Task<Result<EvidenceLink>> LinkAsync(Guid id, CancellationToken cancellationToken)
    {
        var pack = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        if (pack is null)
        {
            return Error.NotFound(ComplianceErrors.EvidencePackNotFound, "There is no such evidence pack.");
        }

        if (pack.Status != EvidencePackStates.Ready)
        {
            return Error.Conflict(ComplianceErrors.EvidencePackNotReady, pack.Status == EvidencePackStates.Failed ? "This pack could not be made. Ask for a new one." : "This pack is still being made. Try again in a moment.");
        }

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var expires = time.GetUtcNow().Add(EvidenceDownloadLink.Lifetime);
        context.EvidenceDownloadLinks.Add(new EvidenceDownloadLink(pack.Id, current.Id, Hash(token), expires));
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new EvidenceLink(DownloadPath + token, expires);
    }

    /// <summary>
    /// Opens a pack through its link (TF-067 AC2): the person who asked for the link, within its five minutes, once; the
    /// bytes checked against the pack's hash; the download audited with the downloader and the pack's requester.
    /// </summary>
    public async Task<Result<OpenedPack>> OpenAsync(string token, CancellationToken cancellationToken)
    {
        var hash = Hash(token ?? string.Empty);
        var link = await context.EvidenceDownloadLinks.SingleOrDefaultAsync(l => l.TokenHash == hash, cancellationToken).ConfigureAwait(false);
        if (link is null || link.PrincipalId != current.Id || link.UsedAt is not null || link.ExpiresAt <= time.GetUtcNow())
        {
            return Error.NotFound(ComplianceErrors.EvidenceLinkInvalid, "This download link has expired or was used already. Download the pack again from the list.");
        }

        var pack = await context.EvidencePacks.AsNoTracking().SingleAsync(p => p.Id == link.PackId, cancellationToken).ConfigureAwait(false);
        var stored = pack.BlobKey is null ? null : await storage.GetAsync(pack.BlobKey, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return Error.NotFound(ComplianceErrors.EvidencePackNotFound, "The pack's file is missing from the tenant's storage. Ask for a new pack.");
        }

        byte[] content;
        using (stored)
        await using (var buffer = new MemoryStream())
        {
            await stored.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            content = buffer.ToArray();
        }

        if (!string.Equals(EvidencePackWriter.Sha256(content), pack.Sha256, StringComparison.Ordinal))
        {
            logger.LogError("ALERT: evidence pack {PackId} in the tenant's storage does not match the hash it was made with.", pack.Id);
            return Error.Unexpected(ComplianceErrors.EvidencePackNotFound, "The pack's file no longer matches the hash it was made with, so it is not given out. Ask for a new pack.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        link.Use(time.GetUtcNow());
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = Period(pack);
        details["sha256"] = pack.Sha256!;
        details["requested_by"] = pack.RequestedById.ToString("D");
        details["requester_name"] = pack.RequesterName;
        await audit.WriteAsync(new AuditEvent(ComplianceAuditEvents.EvidencePackDownloaded, AuditOutcome.Success, current.Id, Title(pack), details, EvidencePack.RecordType, pack.Id), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new OpenedPack(pack, content);
    }

    public static string Title(EvidencePack pack)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return $"Evidence pack {pack.PeriodFrom.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} to {pack.PeriodTo.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
    }

    private static Dictionary<string, string> Period(EvidencePack pack) => new(StringComparer.Ordinal)
    {
        ["period_from"] = CanonicalJson.Time(pack.PeriodFrom),
        ["period_to"] = CanonicalJson.Time(pack.PeriodTo),
        ["requested_by"] = pack.RequestedById.ToString("D"),
    };

    private static string Hash(string token) => EvidencePackWriter.Sha256(System.Text.Encoding.UTF8.GetBytes(token));

    private async Task<string> NameAsync(Guid id, CancellationToken cancellationToken)
    {
        if (principals is null)
        {
            return id.ToString("D");
        }

        var found = await principals.FindAsync([id], cancellationToken).ConfigureAwait(false);
        return found.TryGetValue(id, out var person) ? person.DisplayName : id.ToString("D");
    }
}

/// <summary>Makes the evidence packs people asked for, a few seconds after they ask (ADR-0052), in the unit's worker.</summary>
public sealed class EvidencePackWorker(IServiceScopeFactory scopes, TimeProvider time, ILogger<EvidencePackWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval, time);
            do
            {
                try
                {
                    // One pack per scope: each reads the whole tenant, and a fresh scope keeps its memory short-lived.
                    bool made;
                    do
                    {
                        await using var scope = scopes.CreateAsyncScope();
                        made = await scope.ServiceProvider.GetRequiredService<EvidencePacks>().BuildNextAsync(stoppingToken).ConfigureAwait(false);
                    }
                    while (made);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    logger.LogError(exception, "The evidence pack pass failed; it runs again in {Interval}.", Interval);
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
