using System.Globalization;
using System.Text.Json;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Security.Secrets;
using Iron.Kernel.Tenancy.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Iron.Kernel.Fleet.Services;

/// <summary>
/// The control plane's own database and chain (ADR-0053), named once: its database, its three roles (the owner that
/// migrates, the app role it runs with, the read-only role that verifies), the secrets that hold them, and the container
/// of its anchors. Its verifications are recorded beside the tenants', under its own tenant id.
/// </summary>
public static class ControlPlaneNames
{
    public const string Slug = "control";
    public const string Database = "control";
    public const string OwnerRole = "control_owner";
    public const string AppRole = "control_app";
    public const string ReaderRole = "control_audit";
    public const string DatabaseSecretPath = "control/database";
    public const string UnitSecretPath = "control/unit";
    public const string MigrateSecretPath = "control-migrate/unit";
    public const string AnchorsContainer = "control-audit-anchors";
}

/// <summary>Where the control plane's anchors are (<c>ControlAudit</c>, ADR-0053).</summary>
public sealed class ControlAuditOptions
{
    public const string SectionName = "ControlAudit";

    /// <summary>
    /// The anchors' container as a URL with a SAS that may read, list and create blobs, and never write over or delete
    /// one: on the laptop the container in Azurite, in Azure a container with an immutability policy.
    /// </summary>
    public string? AnchorsSasUrl { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AnchorsSasUrl);
}

/// <summary>
/// The control plane's anchors in object storage (TF-061, ADR-0053): one blob per anchor, named by its kind and position,
/// created and never replaced. The SAS the control plane holds may create a blob but not write over or delete one, so
/// the host whose chain the anchors stand for cannot change them; an anchor sent again finds itself there.
/// </summary>
public sealed class BlobAuditAnchorStore(BlobContainerClient container) : IAuditAnchorStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public BlobAuditAnchorStore(ControlAuditOptions options)
        : this(new BlobContainerClient(new Uri(options?.AnchorsSasUrl ?? throw new ArgumentNullException(nameof(options)))))
    {
    }

    public async Task<long> LatestSealAsync(CancellationToken cancellationToken)
    {
        var latest = 0L;
        await foreach (var blob in container.GetBlobsAsync(BlobTraits.None, BlobStates.None, AuditAnchorKinds.Seal + "/", cancellationToken).ConfigureAwait(false))
        {
            if (PositionOf(blob.Name) is { } position && position > latest)
            {
                latest = position;
            }
        }

        return latest;
    }

    public async Task<AuditAnchorAppend> AppendAsync(AuditAnchor anchor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        var blob = container.GetBlobClient(NameOf(anchor));
        try
        {
            var upload = new BlobUploadOptions
            {
                Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All },
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/json" },
            };
            await blob.UploadAsync(BinaryData.FromObjectAsJson(anchor, Json), upload, cancellationToken).ConfigureAwait(false);
            return AuditAnchorAppend.Added;
        }
        catch (RequestFailedException exception) when (exception.Status is 409 or 412 or 403)
        {
            // There already: the same anchor from an earlier pass, or another hash, which stands.
            if (!await blob.ExistsAsync(cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            var held = await ReadOneAsync(blob, cancellationToken).ConfigureAwait(false);
            return string.Equals(held.Hash, anchor.Hash, StringComparison.Ordinal) ? AuditAnchorAppend.AlreadyThere : AuditAnchorAppend.Conflict;
        }
    }

    public async Task<IReadOnlyList<AuditAnchor>> ReadAsync(long fromPosition, CancellationToken cancellationToken)
    {
        var anchors = new List<AuditAnchor>();
        await foreach (var blob in container.GetBlobsAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            if (PositionOf(blob.Name) is { } position && position >= fromPosition)
            {
                anchors.Add(await ReadOneAsync(container.GetBlobClient(blob.Name), cancellationToken).ConfigureAwait(false));
            }
        }

        return [.. anchors.OrderBy(a => a.Position).ThenBy(a => a.Kind, StringComparer.Ordinal)];
    }

    /// <summary>The blob of an anchor: <c>seal/0000000000000000042.json</c>, so names sort as positions do.</summary>
    public static string NameOf(AuditAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        return $"{anchor.Kind}/{anchor.Position.ToString("D19", CultureInfo.InvariantCulture)}.json";
    }

    private static long? PositionOf(string name)
    {
        var slash = name.IndexOf('/', StringComparison.Ordinal);
        var dot = name.LastIndexOf('.');
        return slash > 0 && dot > slash && long.TryParse(name.AsSpan(slash + 1, dot - slash - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var position) ? position : null;
    }

    private static async Task<AuditAnchor> ReadOneAsync(BlobClient blob, CancellationToken cancellationToken)
    {
        var content = await blob.DownloadContentAsync(cancellationToken).ConfigureAwait(false);
        return content.Value.Content.ToObjectFromJson<AuditAnchor>(Json) ?? throw new InvalidOperationException($"The anchor {blob.Name} is empty.");
    }
}

/// <summary>
/// The verification of the control plane's own chain (TF-061, ADR-0053), as the fleet verifies a tenant's: with the
/// control plane's read-only role, which can read the chain's tables and nothing else, against the anchors in object
/// storage, outside its database. A daily run checks what was sealed since the last one that passed; a full run checks
/// from the first position. The result is recorded beside the tenants' and raises the same alert when it does not pass.
/// </summary>
public sealed class ControlAuditVerifier(ITenantAuditChains chains, ISecretStore secrets, FleetOptions options, IAuditAnchorStore anchors, TimeProvider time)
{
    public async Task<AuditVerificationInfo> VerifyAsync(bool full, CancellationToken cancellationToken)
    {
        var start = 1L;
        var previous = AuditCanonicalForm.Genesis;
        if (!full)
        {
            var last = await chains.LatestVerificationAsync(ControlPlaneNames.Slug, false, cancellationToken).ConfigureAwait(false);
            if (last is { Result: AuditVerificationResults.Passed, ToHash: { } verifiedHash })
            {
                start = last.ToPosition + 1;
                previous = verifiedHash;
            }
            else
            {
                full = true;
            }
        }

        var held = await anchors.ReadAsync(start, cancellationToken).ConfigureAwait(false);
        await using var dataSource = await ReaderAsync(cancellationToken).ConfigureAwait(false);
        var events = await AuditChainVerifier.ReadOnlineAsync(dataSource, start, cancellationToken).ConfigureAwait(false);
        var result = AuditChainCheck.Check(start, previous, events, held, []);
        var verification = new AuditVerificationInfo(
            ControlPlaneNames.Slug,
            null,
            full,
            result.FromPosition,
            result.ToPosition,
            result.ToHash,
            AuditChainVerifier.ResultOf(result),
            result.EventsChecked,
            [.. result.Problems.Select(p => new AuditChainProblemInfo(p.Kind, p.Position, p.EventId, p.Message))],
            result.RestorePosition,
            result.RestoreAt,
            time.GetUtcNow());
        await chains.RecordVerificationAsync(verification, null, cancellationToken).ConfigureAwait(false);
        return verification;
    }

    /// <summary>The control plane's database as its read-only role reaches it, on the fleet's own route to the server.</summary>
    private async Task<NpgsqlDataSource> ReaderAsync(CancellationToken cancellationToken)
    {
        var admin = options.Database.AdminConnectionString ?? throw new InvalidOperationException("Fleet:Database:AdminConnectionString is required to verify the control plane's audit chain.");
        var database = await secrets.ReadAsync(ControlPlaneNames.DatabaseSecretPath, cancellationToken).ConfigureAwait(false);
        var password = database?.GetValueOrDefault(ControlPlaneDatabase.ReaderPasswordKey)
            ?? throw new InvalidOperationException("The control plane has no read-only role yet: make up-control (or make secret-control) makes it.");
        var builder = new NpgsqlConnectionStringBuilder(admin)
        {
            Database = ControlPlaneNames.Database,
            Username = ControlPlaneNames.ReaderRole,
            Password = password,
            Pooling = false,
        };
        return NpgsqlDataSource.Create(builder.ConnectionString);
    }
}

/// <summary>
/// The fleet's daily verification of the control plane's chain (TF-061 AC1, ADR-0053): once a day, in full once a month;
/// a run it could not make is tried again an hour later.
/// </summary>
public sealed class ControlAuditVerificationWorker(IServiceScopeFactory scopes, FleetOptions options, TimeProvider time, ILogger<ControlAuditVerificationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Daily = TimeSpan.FromDays(1);
    private static readonly TimeSpan Monthly = TimeSpan.FromDays(30);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Audit.PollSeconds)), time);
        var waitUntil = DateTimeOffset.MinValue;
        do
        {
            var now = time.GetUtcNow();
            if (now < waitUntil)
            {
                continue;
            }

            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (scope.ServiceProvider.GetService<IAuditAnchorStore>() is null)
                {
                    // No anchors configured (ControlAudit:AnchorsSasUrl), as on a fleet run without the control plane's.
                    continue;
                }

                var chains = scope.ServiceProvider.GetRequiredService<ITenantAuditChains>();
                var last = await chains.LatestVerificationAsync(ControlPlaneNames.Slug, false, stoppingToken).ConfigureAwait(false);
                if (last is not null && now - last.At < Daily)
                {
                    continue;
                }

                var lastFull = await chains.LatestVerificationAsync(ControlPlaneNames.Slug, true, stoppingToken).ConfigureAwait(false);
                var verification = await scope.ServiceProvider.GetRequiredService<ControlAuditVerifier>()
                    .VerifyAsync(lastFull is null || now - lastFull.At >= Monthly, stoppingToken).ConfigureAwait(false);
                logger.LogInformation("The control plane's audit chain was verified: {Result} from position {From} to {To}.", verification.Result, verification.FromPosition, verification.ToPosition);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                waitUntil = now + RetryAfter;
                logger.LogWarning("The control plane's audit chain could not be verified; it is tried again in an hour: {Message}", exception.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

/// <summary>
/// The fleet image's command <c>verify-control-audit [full]</c> (ADR-0053): the daily verification of the control plane's
/// chain, by hand. It prints the result and exits 0 when the chain passed, 2 when it did not.
/// </summary>
public sealed class VerifyControlAuditCommand(IServiceProvider services) : IUnitCommand
{
    public string Name => DatabaseCommands.VerifyControlAudit;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (services.GetService<ControlAuditVerifier>() is not { } verifier)
        {
            await Console.Error.WriteLineAsync("ControlAudit:AnchorsSasUrl is not set, so the control plane's chain has no anchors to verify against: make up-control (or make secret-control) prepares them.").ConfigureAwait(false);
            return 2;
        }

        var verification = await verifier.VerifyAsync(args.Contains("full", StringComparer.Ordinal), cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync($"The control plane's audit chain: {verification.Result}, {(verification.Full ? "in full" : "since the last verification")}, positions {verification.FromPosition} to {verification.ToPosition}, {verification.EventsChecked} events.").ConfigureAwait(false);
        foreach (var problem in verification.Problems.Take(20))
        {
            await Console.Out.WriteLineAsync($"  {problem.Kind} at position {problem.Position}{(problem.EventId is { } id ? $", event {id:D}" : string.Empty)}: {problem.Message}").ConfigureAwait(false);
        }

        return verification.Result == AuditVerificationResults.Passed ? 0 : 2;
    }
}
