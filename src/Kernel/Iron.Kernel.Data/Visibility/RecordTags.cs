using System.Reflection;
using Iron.Kernel.Data.Commands;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.Policies;

namespace Iron.Kernel.Data.Visibility;

/// <summary>
/// A record's visibility tags as its page shows them: the tags it carries, whether the reader may change them, and the
/// tags the reader may put on it (rule 5): the active tags they hold, or every active tag when they hold none.
/// </summary>
public sealed record RecordTagsView(string RecordType, Guid RecordId, IReadOnlyList<VisibilityTagInfo> Tags, bool MayChange, IReadOnlyList<VisibilityTagInfo> Choices);

/// <summary>The error codes of changing a record's visibility tags (TF-241, ADR-0057).</summary>
public static class VisibilityErrors
{
    public const string RecordTypeUnknown = "data.visibility.record_type_unknown";

    /// <summary>No tag with this code exists.</summary>
    public const string TagUnknown = "data.visibility.tag_unknown";

    /// <summary>A retired tag stays where it is, and is put on nothing new.</summary>
    public const string TagRetired = "data.visibility.tag_retired";

    /// <summary>Rule 5: a person puts on a record, or takes off it, only tags they hold, unless they hold none.</summary>
    public const string TagNotHeld = "data.visibility.tag_not_held";
}

/// <summary>
/// The visibility tags of any business record with a policy (TF-241, ADR-0057), named as record-scoped assignments name
/// its type. Reading them needs the record to be readable; changing them is the action <c>tag</c>, which the module's
/// policy decides, and rule 5 decides which tags: a person puts on a record, and takes off it, only tags they hold,
/// unless they hold none, and a retired tag is put on nothing. The save records the change in the record's field
/// history, before and after, in its own transaction (ADR-0051).
/// </summary>
public interface IRecordTags
{
    Task<Result<RecordTagsView>> GetAsync(string recordType, Guid recordId, CancellationToken cancellationToken);

    /// <summary>Replaces the record's tags with these codes; the refusal of rule 5 names the first tag it refuses.</summary>
    Task<Result<RecordTagsView>> ChangeAsync(string recordType, Guid recordId, IReadOnlyList<string> codes, CancellationToken cancellationToken);
}

public sealed class RecordTags(
    IServiceProvider services,
    IEnumerable<RegisteredContext> contexts,
    IRecordPolicies policies,
    IEnumerable<RecordPolicyRegistration> registrations,
    ICurrentPrincipal current,
    IVisibilityTags? tags = null) : IRecordTags
{
    private static readonly MethodInfo GetOf = typeof(RecordTags).GetMethod(nameof(GetCoreAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly MethodInfo ChangeOf = typeof(RecordTags).GetMethod(nameof(ChangeCoreAsync), BindingFlags.NonPublic | BindingFlags.Instance)!;

    /// <summary>A tag's code as it is kept: trimmed and in capitals, as the Visibility module makes it.</summary>
    public static string Normalise(string code)
    {
        ArgumentNullException.ThrowIfNull(code);
        return code.Trim().ToUpperInvariant();
    }

    public Task<Result<RecordTagsView>> GetAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        var registration = Registration(recordType);
        return registration is null
            ? Task.FromResult<Result<RecordTagsView>>(UnknownType(recordType))
            : (Task<Result<RecordTagsView>>)GetOf.MakeGenericMethod(registration.RecordType).Invoke(this, [registration.Name, recordId, cancellationToken])!;
    }

    public Task<Result<RecordTagsView>> ChangeAsync(string recordType, Guid recordId, IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(codes);
        var registration = Registration(recordType);
        return registration is null
            ? Task.FromResult<Result<RecordTagsView>>(UnknownType(recordType))
            : (Task<Result<RecordTagsView>>)ChangeOf.MakeGenericMethod(registration.RecordType).Invoke(this, [registration.Name, recordId, codes, cancellationToken])!;
    }

    private async Task<Result<RecordTagsView>> GetCoreAsync<T>(string name, Guid recordId, CancellationToken cancellationToken)
        where T : BusinessRecord
    {
        var found = await policies.FindAsync<T>(recordId, RecordActions.Read, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var mayChange = tags is not null && await policies.For<T>(current.Id).AllowsAsync(found.Value, RecordActions.Tag, cancellationToken).ConfigureAwait(false);
        return await ViewAsync(name, found.Value, mayChange, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<RecordTagsView>> ChangeCoreAsync<T>(string name, Guid recordId, IReadOnlyList<string> codes, CancellationToken cancellationToken)
        where T : BusinessRecord
    {
        var found = await policies.FindAsync<T>(recordId, RecordActions.Tag, cancellationToken).ConfigureAwait(false);
        if (found.IsFailure)
        {
            return found.Error;
        }

        var record = found.Value;
        var wanted = codes.Where(c => !string.IsNullOrWhiteSpace(c)).Select(Normalise).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var before = record.VisibilityTags;
        var added = wanted.Where(c => !before.Contains(c)).ToList();
        var removed = before.Where(c => !wanted.Contains(c)).ToList();
        if (added.Count == 0 && removed.Count == 0)
        {
            return await ViewAsync(name, record, mayChange: true, cancellationToken).ConfigureAwait(false);
        }

        if (tags is null)
        {
            return Error.Validation(VisibilityErrors.TagUnknown, $"There is no visibility tag {added.FirstOrDefault() ?? removed[0]}.", new FieldError("tags", "unknown", "Choose tags this tenant defined."));
        }

        var known = await tags.FindAsync(added, cancellationToken).ConfigureAwait(false);
        if (added.FirstOrDefault(c => !known.ContainsKey(c)) is { } unknown)
        {
            return Error.Validation(VisibilityErrors.TagUnknown, $"There is no visibility tag {unknown}.", new FieldError("tags", "unknown", $"{unknown} is not a tag of this tenant."));
        }

        if (added.FirstOrDefault(c => !known[c].Active) is { } retired)
        {
            return Error.Validation(VisibilityErrors.TagRetired, $"The tag {retired} is retired, so it can no longer be put on a record.", new FieldError("tags", "retired", $"{retired} is retired."));
        }

        // Rule 5: whoever holds tags changes only those; whoever holds none may put any active tag.
        var held = await tags.HeldAsync(current.Id, cancellationToken).ConfigureAwait(false);
        if (held.Count > 0)
        {
            if (added.FirstOrDefault(c => !held.Contains(c)) is { } notHeld)
            {
                return Error.Forbidden(VisibilityErrors.TagNotHeld, $"You do not hold the tag {notHeld}, so you cannot put it on a record.");
            }

            if (removed.FirstOrDefault(c => !held.Contains(c)) is { } notYours)
            {
                return Error.Forbidden(VisibilityErrors.TagNotHeld, $"You do not hold the tag {notYours}, so you cannot take it off a record.");
            }
        }

        record.ChangeVisibilityTags(wanted);
        await ModuleContexts.Of<T>(services, contexts).SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return await ViewAsync(name, record, mayChange: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecordTagsView> ViewAsync(string name, BusinessRecord record, bool mayChange, CancellationToken cancellationToken)
    {
        if (tags is null)
        {
            return new RecordTagsView(name, record.Id, [.. record.VisibilityTags.Select(c => new VisibilityTagInfo(c, c, Active: false))], MayChange: false, []);
        }

        var known = await tags.FindAsync(record.VisibilityTags.ToList(), cancellationToken).ConfigureAwait(false);
        var carried = record.VisibilityTags.Select(c => known.GetValueOrDefault(c) ?? new VisibilityTagInfo(c, c, Active: false)).ToList();
        if (!mayChange)
        {
            return new RecordTagsView(name, record.Id, carried, MayChange: false, []);
        }

        var held = await tags.HeldAsync(current.Id, cancellationToken).ConfigureAwait(false);
        var active = await tags.ActiveAsync(cancellationToken).ConfigureAwait(false);
        return new RecordTagsView(name, record.Id, carried, MayChange: true, [.. active.Where(t => held.Count == 0 || held.Contains(t.Code))]);
    }

    private RecordPolicyRegistration? Registration(string recordType) => registrations.FirstOrDefault(r => string.Equals(r.Name, recordType, StringComparison.Ordinal));

    private static Error UnknownType(string recordType) => Error.NotFound(VisibilityErrors.RecordTypeUnknown, $"'{recordType}' is not a record type that carries visibility tags.");
}
