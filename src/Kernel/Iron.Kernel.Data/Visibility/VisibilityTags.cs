using System.Linq.Expressions;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Data.Visibility;

/// <summary>One visibility tag as the data library needs it: its code, its name, and whether it may still be put on anything.</summary>
public sealed record VisibilityTagInfo(string Code, string Name, bool Active);

/// <summary>
/// What the data library asks of the visibility tags (TF-241, ADR-0057), which Iron.Kernel.Visibility keeps: the tags a
/// principal holds and the tags the tenant defined. The library declares it so it references no module (ADR-0029);
/// without it nobody holds a tag, the filter narrows nothing, and no record can be tagged.
/// </summary>
public interface IVisibilityTags
{
    /// <summary>
    /// The codes the principal holds, retired ones included, so a retired tag never widens what they see; while the
    /// request's principal acts for someone, the delegator's (ADR-0037). Empty when they hold none.
    /// </summary>
    Task<IReadOnlyList<string>> HeldAsync(Guid principalId, CancellationToken cancellationToken);

    /// <summary>The tags with these codes that exist, by code; an unknown code is left out.</summary>
    Task<IReadOnlyDictionary<string, VisibilityTagInfo>> FindAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>Every active tag, in code order: what a person who holds none may put on a record.</summary>
    Task<IReadOnlyList<VisibilityTagInfo>> ActiveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The tag filter (TF-241 rules 2 to 4): a reader who holds no tags sees every record; one who holds tags sees a record
/// without tags, and a record that carries at least one of theirs. One more condition in the policy's query, after the
/// scope rule and the module's policy, so it only narrows and a list's total counts what the reader sees (ADR-0057).
/// </summary>
public static class VisibilityFilter
{
    public static Expression<Func<T, bool>> For<T>(IReadOnlyCollection<string> held)
        where T : BusinessRecord
    {
        ArgumentNullException.ThrowIfNull(held);
        if (held.Count == 0)
        {
            return _ => true;
        }

        var codes = held.ToArray();
        return record => record.VisibilityTags.Count == 0 || record.VisibilityTags.Any(code => codes.Contains(code));
    }

    /// <summary>The same rule for one record already read.</summary>
    public static bool Sees(IReadOnlyCollection<string> held, IReadOnlyCollection<string> tags)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(tags);
        return held.Count == 0 || tags.Count == 0 || tags.Any(held.Contains);
    }
}

/// <summary>
/// The tag check of one record for one reader, as an explanation names it (ADR-0039, ADR-0057): the record's tags, the
/// reader's, the ones they share, and whether the reader sees the record by its tags.
/// </summary>
public sealed record RecordTagCheck(IReadOnlyList<string> RecordTags, IReadOnlyList<string> HeldTags, IReadOnlyList<string> SharedTags, bool Passed)
{
    public static RecordTagCheck Of(IReadOnlyCollection<string> held, IReadOnlyCollection<string> tags)
    {
        ArgumentNullException.ThrowIfNull(held);
        ArgumentNullException.ThrowIfNull(tags);
        var ordered = held.Order(StringComparer.Ordinal).ToList();
        return new RecordTagCheck([.. tags.Order(StringComparer.Ordinal)], ordered, [.. ordered.Where(tags.Contains)], VisibilityFilter.Sees(held, tags));
    }
}
