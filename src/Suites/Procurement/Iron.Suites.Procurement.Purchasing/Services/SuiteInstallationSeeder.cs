using System.Globalization;
using Iron.Kernel;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Seeding;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Events.Contracts;
using Iron.Suites.Procurement.Purchasing.Contracts;
using Iron.Suites.Procurement.Purchasing.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Iron.Suites.Procurement.Purchasing.Services;

/// <summary>
/// Records the suite's installation in this tenant (TF-244, ADR-0059): the first seed writes when, by which job and with
/// which release; every seed after it, which an update runs, writes the same for the latest. The fleet's seed step names
/// its job in <c>Seed:JobId</c>, and the image its release in <c>Release:Version</c>. Each is audited as the system's,
/// in the same transaction (ADR-0013). It runs after every Foundation seeder.
/// </summary>
public sealed class SuiteInstallationSeeder(
    PurchasingDbContext context,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    TimeProvider time,
    IConfiguration configuration,
    IEventPublisher events,
    ILogger<SuiteInstallationSeeder> logger) : ISeeder
{
    public const string JobIdKey = "Seed:JobId";
    public const string VersionKey = "Release:Version";
    public const string Development = "development";
    public const string RecordType = "suite_installation";

    public int Order => 1000;

    /// <summary>The release this image is, or <c>development</c> for one built on a laptop.</summary>
    public static string VersionOf(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration[VersionKey] is { Length: > 0 } version && !string.IsNullOrWhiteSpace(version) ? version.Trim() : Development;
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var job = Guid.TryParse(configuration[JobIdKey], out var id) && id != Guid.Empty ? id : (Guid?)null;
        var version = VersionOf(configuration);
        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var installation = await context.Installations.SingleOrDefaultAsync(i => i.Suite == ProcurementSuite.ShortName, cancellationToken).ConfigureAwait(false);
        var first = installation is null;
        if (installation is null)
        {
            installation = new SuiteInstallation(ProcurementSuite.ShortName, now, job, version);
            context.Installations.Add(installation);
        }
        else
        {
            installation.Seeded(now, job, version);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["version"] = version,
            ["job_id"] = job?.ToString("D") ?? string.Empty,
            ["installed_at"] = installation.InstalledAt.ToString("O", CultureInfo.InvariantCulture),
        };
        var action = first ? PurchasingAuditEvents.SuiteInstalled : PurchasingAuditEvents.SuiteSeeded;
        await audit.WriteAsync(new AuditEvent(action, AuditOutcome.Success, SystemPrincipal.SystemId, ProcurementSuite.ShortName, details, RecordType, installation.Id, "seed"), cancellationToken).ConfigureAwait(false);
        if (first)
        {
            // The suite's own event (ADR-0060): only a unit that runs the suite declares and stores it.
            await events.PublishAsync(PurchasingEvents.SuiteInstalled, installation.Id, new SuiteInstalled(installation.Id, ProcurementSuite.ShortName, version, job), cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(first ? "The {Suite} suite is installed with release {Version} by job {Job}." : "The {Suite} suite is seeded again with release {Version} by job {Job}.", ProcurementSuite.ShortName, version, job?.ToString("D") ?? "none");
    }
}
