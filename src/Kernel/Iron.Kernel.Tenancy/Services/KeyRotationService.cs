using System.Globalization;
using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Tenancy.Contracts;
using Iron.Kernel.Tenancy.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Iron.Kernel.Tenancy.Services;

/// <summary>
/// The operator's bounds of key rotation (<c>Keys:Rotation</c>, ADR-0054): the days a tenant may choose between its data
/// and index keys' rotations, the signing key's and the database password's schedules, and how long an old signing key
/// stays published after a rotation, longer than any token it signed lives.
/// </summary>
public sealed class KeyRotationOptions
{
    public const string SectionName = "Keys:Rotation";

    public int DataDaysMinimum { get; set; } = 30;

    public int DataDaysMaximum { get; set; } = 730;

    /// <summary>A tenant's data and index keys rotate yearly until it says otherwise.</summary>
    public int DataDaysDefault { get; set; } = 365;

    /// <summary>Signing keys rotate yearly (TF-075).</summary>
    public int SigningDays { get; set; } = 365;

    public int DatabaseDays { get; set; } = 90;

    /// <summary>
    /// How long the old version of a signing key keeps validating after a rotation: longer than an access token lives (15
    /// minutes), plus the minute a unit may keep signing with it before it reads the new one, plus room for clocks.
    /// </summary>
    public int SigningOverlapMinutes { get; set; } = 30;

    public int DataDays(int asked) => Math.Clamp(asked, DataDaysMinimum, DataDaysMaximum);
}

/// <summary>
/// Key rotation in the control plane (TF-073, TF-075, ADR-0054). A key officer asks for a rotation of a tenant's data,
/// index or signing key, or its database password, with a reason and a fresh token; the schedule asks for the ones due.
/// The fleet's key worker rotates in the key service, or rotates the password, and records it; the tenant's unit reads its
/// rotations, records them in its own audit trail, moves every value to the new version and reports its progress; the
/// fleet then retires the old version. Every step is audited here with who asked and why.
/// </summary>
public sealed class KeyRotationService(
    TenancyDbContext context,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    ICurrentPrincipal actor,
    OperatorNames names,
    AuditChainService chains,
    KeyRotationOptions options,
    TimeProvider time,
    ILogger<KeyRotationService> logger) : ITenantKeys
{
    /// <summary>How long a unit still reads a retired rotation, to record its retirement.</summary>
    public static readonly TimeSpan RetiredKeptFor = TimeSpan.FromDays(30);

    public const string ScheduleName = "schedule";

    /// <summary>A key officer's rotation (TF-075 AC3, AC4): the permission and the fresh token are the endpoint's to check.</summary>
    public async Task<Result<KeyRotationInfo>> RequestAsync(string slug, string key, string? reason, CancellationToken cancellationToken)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why) || why.Length > KeyRotationRecord.ReasonMaxLength)
        {
            return Error.Validation(TenancyErrors.KeyReasonRequired, "A rotation needs a reason, which both audit trails keep.", new FieldError("reason", "required", $"Say why the key is rotated, {KeyRotationRecord.ReasonMaxLength} characters at most."));
        }

        if (!TenantKeyNames.IsKnown(key))
        {
            return Error.NotFound(TenancyErrors.KeyUnknown, $"A tenant's keys are {string.Join(", ", TenantKeyNames.All)}; '{key}' is not one.");
        }

        var tenant = await context.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Slug == slug, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return Error.NotFound(TenancyErrors.TenantNotFound, $"There is no tenant '{slug}'.");
        }

        if (await OpenAsync(slug, key, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(TenancyErrors.KeyRotationInProgress, $"The {key} key of '{slug}' is being rotated already; it can be rotated again once its old version is retired.");
        }

        var operatorName = await names.OfAsync(actor.Id, cancellationToken).ConfigureAwait(false);
        return await AddAsync(slug, key, KeyRotationTriggers.Operator, actor.Id, operatorName, why, tenant.Id, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A tenant's keys and its latest rotations, newest first, for the operator.</summary>
    public async Task<(IReadOnlyList<TenantKeyInfo> Keys, IReadOnlyList<KeyRotationInfo> Rotations)> StatusAsync(string slug, CancellationToken cancellationToken)
    {
        var keys = await context.TenantKeys.AsNoTracking().Where(k => k.TenantSlug == slug).ToListAsync(cancellationToken).ConfigureAwait(false);
        var rotations = await context.KeyRotations.AsNoTracking().Where(r => r.TenantSlug == slug).OrderByDescending(r => r.CreatedAt).Take(20).ToListAsync(cancellationToken).ConfigureAwait(false);
        return ([.. keys.OrderBy(k => TenantKeyNames.All.ToList().IndexOf(k.Key)).Select(Info)], [.. rotations.Select(Info)]);
    }

    public async Task<KeyRotationInfo?> FindAsync(string slug, Guid id, CancellationToken cancellationToken)
    {
        var rotation = await context.KeyRotations.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.TenantSlug == slug, cancellationToken).ConfigureAwait(false);
        return rotation is null ? null : Info(rotation);
    }

    /// <summary>The calling unit's keys and its rotations that are open, or retired within a month.</summary>
    public async Task<Result<(IReadOnlyList<TenantKeyInfo> Keys, IReadOnlyList<KeyRotationInfo> Rotations)>> OfCallerAsync(CancellationToken cancellationToken)
    {
        var slug = await chains.TenantOfCallerAsync(cancellationToken).ConfigureAwait(false);
        if (slug is null)
        {
            return Error.Forbidden(TenancyErrors.KeyRotationNotAUnit, "Only a tenant's unit reads its own key rotations.");
        }

        var since = time.GetUtcNow() - RetiredKeptFor;
        var keys = await context.TenantKeys.AsNoTracking().Where(k => k.TenantSlug == slug).ToListAsync(cancellationToken).ConfigureAwait(false);
        var rotations = await context.KeyRotations.AsNoTracking()
            .Where(r => r.TenantSlug == slug && r.State != KeyRotationStates.Requested && (r.RetiredAt == null || r.RetiredAt >= since))
            .OrderBy(r => r.CreatedAt)
            .Take(50)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return ([.. keys.Select(Info)], [.. rotations.Select(Info)]);
    }

    /// <summary>The calling unit's re-encryption's progress; <paramref name="finished"/> when every value has moved (TF-075 AC1).</summary>
    public async Task<Result<KeyRotationInfo>> ProgressAsync(Guid id, long done, long total, bool finished, CancellationToken cancellationToken)
    {
        var slug = await chains.TenantOfCallerAsync(cancellationToken).ConfigureAwait(false);
        var rotation = slug is null ? null : await context.KeyRotations.SingleOrDefaultAsync(r => r.Id == id && r.TenantSlug == slug, cancellationToken).ConfigureAwait(false);
        if (rotation is null)
        {
            return Error.NotFound(TenancyErrors.KeyRotationNotFound, $"This unit has no key rotation {id}.");
        }

        if (rotation.State == KeyRotationStates.Rotated)
        {
            rotation.Progress(done, total);
            if (finished)
            {
                rotation.Finish(time.GetUtcNow());
            }

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return Info(rotation);
    }

    /// <summary>The calling unit's policy for its data and index keys, kept within the operator's bounds; the days kept.</summary>
    public async Task<Result<int>> PolicyAsync(int dataDays, CancellationToken cancellationToken)
    {
        var slug = await chains.TenantOfCallerAsync(cancellationToken).ConfigureAwait(false);
        if (slug is null)
        {
            return Error.Forbidden(TenancyErrors.KeyRotationNotAUnit, "Only a tenant's unit sets its own key policy.");
        }

        var days = options.DataDays(dataDays);
        var keys = await context.TenantKeys.Where(k => k.TenantSlug == slug && (k.Key == TenantKeyNames.Data || k.Key == TenantKeyNames.Index)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var key in keys.Where(k => k.RotationDays != days))
        {
            key.Every(days);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return days;
    }

    public async Task RecordKeyAsync(string slug, string key, int version, int minimumVersion, CancellationToken cancellationToken)
    {
        var record = await context.TenantKeys.SingleOrDefaultAsync(k => k.TenantSlug == slug && k.Key == key, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            context.TenantKeys.Add(new TenantKeyRecord(slug, key, version, minimumVersion, DaysOf(key)));
        }
        else
        {
            record.Versions(version, minimumVersion);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> ScheduleDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var active = await context.Tenants.AsNoTracking().Where(t => t.Status == TenantStatus.Active).Select(t => new { t.Slug, t.Id }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var slugs = active.Select(t => t.Slug).ToList();
        var keys = await context.TenantKeys.AsNoTracking().Where(k => slugs.Contains(k.TenantSlug)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var asked = 0;
        foreach (var key in keys.Where(k => k.NextRotationAt <= now))
        {
            if (await OpenAsync(key.TenantSlug, key.Key, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var tenantId = active.Single(t => t.Slug == key.TenantSlug).Id;
            var reason = string.Create(CultureInfo.InvariantCulture, $"Scheduled rotation, every {key.RotationDays} days.");
            if ((await AddAsync(key.TenantSlug, key.Key, KeyRotationTriggers.Schedule, null, ScheduleName, reason, tenantId, cancellationToken).ConfigureAwait(false)).IsSuccess)
            {
                asked++;
            }
        }

        return asked;
    }

    public async Task<IReadOnlyList<KeyRotationInfo>> RequestedAsync(CancellationToken cancellationToken) =>
        [.. (await context.KeyRotations.AsNoTracking().Where(r => r.State == KeyRotationStates.Requested).OrderBy(r => r.CreatedAt).ToListAsync(cancellationToken).ConfigureAwait(false)).Select(Info)];

    public async Task RotatedAsync(Guid rotationId, int fromVersion, int toVersion, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var rotation = await context.KeyRotations.SingleAsync(r => r.Id == rotationId, cancellationToken).ConfigureAwait(false);
        rotation.Rotate(fromVersion, toVersion, now);

        // A database password has no values to move and no old version that keeps working: done once it is set.
        if (rotation.Key == TenantKeyNames.Database)
        {
            rotation.Retire(now);
        }

        var key = await context.TenantKeys.SingleOrDefaultAsync(k => k.TenantSlug == rotation.TenantSlug && k.Key == rotation.Key, cancellationToken).ConfigureAwait(false);
        if (key is null)
        {
            key = new TenantKeyRecord(rotation.TenantSlug, rotation.Key, toVersion, rotation.Key == TenantKeyNames.Database ? toVersion : fromVersion, DaysOf(rotation.Key));
            context.TenantKeys.Add(key);
        }

        key.Rotated(toVersion, now);
        if (rotation.Key == TenantKeyNames.Database)
        {
            key.Retired(toVersion);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(Event(TenancyAuditEvents.KeyRotated, rotation, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["from_version"] = fromVersion.ToString(CultureInfo.InvariantCulture),
            ["to_version"] = toVersion.ToString(CultureInfo.InvariantCulture),
        }), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("The {Key} key of tenant {Slug} was rotated from version {From} to {To}, asked by {RequestedBy}.", rotation.Key, rotation.TenantSlug, fromVersion, toVersion, rotation.RequestedByName);
    }

    public async Task FailedAsync(Guid rotationId, string error, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var rotation = await context.KeyRotations.SingleAsync(r => r.Id == rotationId, cancellationToken).ConfigureAwait(false);
        rotation.Fail(error);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(Event(TenancyAuditEvents.KeyRotationFailed, rotation, new Dictionary<string, string>(StringComparer.Ordinal) { ["error"] = rotation.Error ?? string.Empty }, AuditOutcome.Failure, AuditSeverity.High), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogError("ALERT: the {Key} key of tenant {Slug} could not be rotated: {Error}", rotation.Key, rotation.TenantSlug, rotation.Error);
    }

    public async Task<IReadOnlyList<KeyRotationInfo>> RetirableAsync(CancellationToken cancellationToken)
    {
        var overlapEnded = time.GetUtcNow().AddMinutes(-options.SigningOverlapMinutes);
        var rotations = await context.KeyRotations.AsNoTracking()
            .Where(r => (r.State == KeyRotationStates.Finished && (r.Key == TenantKeyNames.Data || r.Key == TenantKeyNames.Index))
                || (r.State == KeyRotationStates.Rotated && r.Key == TenantKeyNames.Signing && r.RotatedAt <= overlapEnded))
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return [.. rotations.Select(Info)];
    }

    public async Task RetiredAsync(Guid rotationId, int minimumVersion, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var rotation = await context.KeyRotations.SingleAsync(r => r.Id == rotationId, cancellationToken).ConfigureAwait(false);
        rotation.Retire(now);
        var key = await context.TenantKeys.SingleOrDefaultAsync(k => k.TenantSlug == rotation.TenantSlug && k.Key == rotation.Key, cancellationToken).ConfigureAwait(false);
        key?.Retired(minimumVersion);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(Event(TenancyAuditEvents.KeyRetired, rotation, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minimum_version"] = minimumVersion.ToString(CultureInfo.InvariantCulture),
            ["values_moved"] = rotation.Done.ToString(CultureInfo.InvariantCulture),
        }), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("The {Key} key of tenant {Slug} has its versions below {Minimum} retired.", rotation.Key, rotation.TenantSlug, minimumVersion);
    }

    public static TenantKeyInfo Info(TenantKeyRecord key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return new(key.TenantSlug, key.Key, key.KeyVersion, key.MinimumVersion, key.RotatedAt, key.NextRotationAt, key.RotationDays);
    }

    public static KeyRotationInfo Info(KeyRotationRecord rotation)
    {
        ArgumentNullException.ThrowIfNull(rotation);
        return new(rotation.Id, rotation.TenantSlug, rotation.Key, rotation.Trigger, rotation.RequestedByName, rotation.Reason, rotation.State, rotation.FromVersion, rotation.ToVersion, rotation.CreatedAt, rotation.RotatedAt, rotation.Done, rotation.Total, rotation.FinishedAt, rotation.RetiredAt, rotation.Error);
    }

    private int DaysOf(string key) => key switch
    {
        TenantKeyNames.Signing => options.SigningDays,
        TenantKeyNames.Database => options.DatabaseDays,
        _ => options.DataDaysDefault,
    };

    private async Task<bool> OpenAsync(string slug, string key, CancellationToken cancellationToken) =>
        await context.KeyRotations.AnyAsync(r => r.TenantSlug == slug && r.Key == key && (r.State == KeyRotationStates.Requested || r.State == KeyRotationStates.Rotated || r.State == KeyRotationStates.Finished), cancellationToken).ConfigureAwait(false);

    private async Task<Result<KeyRotationInfo>> AddAsync(string slug, string key, string trigger, Guid? requesterId, string requesterName, string reason, Guid tenantId, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        var rotation = new KeyRotationRecord(slug, key, trigger, requesterId, requesterName, reason, KeyRotationStates.Requested);
        context.KeyRotations.Add(rotation);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await audit.WriteAsync(new AuditEvent(
            TenancyAuditEvents.KeyRotationRequested,
            AuditOutcome.Success,
            requesterId ?? SystemPrincipal.SystemId,
            slug,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["key"] = key, ["trigger"] = trigger, ["rotation_id"] = rotation.Id.ToString("D"), ["requested_by"] = requesterName },
            "tenant",
            tenantId,
            reason,
            Severity: trigger == KeyRotationTriggers.Operator ? AuditSeverity.High : AuditSeverity.Normal), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("A rotation of the {Key} key of tenant {Slug} was asked for by {RequestedBy}.", key, slug, requesterName);
        return Info(rotation);
    }

    private static AuditEvent Event(string type, KeyRotationRecord rotation, Dictionary<string, string> details, string outcome = AuditOutcome.Success, string? severity = null)
    {
        details["key"] = rotation.Key;
        details["rotation_id"] = rotation.Id.ToString("D");
        details["requested_by"] = rotation.RequestedByName;
        details["trigger"] = rotation.Trigger;
        return new AuditEvent(type, outcome, SystemPrincipal.SystemId, rotation.TenantSlug, details, "key_rotation", rotation.Id, rotation.Reason, Severity: severity);
    }
}
