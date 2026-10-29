using Iron.Kernel.Audit.Contracts;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.Data.Visibility;
using Iron.Kernel.Visibility.Contracts;
using Iron.Kernel.Visibility.Data;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Visibility.Services;

/// <summary>
/// The tenant's visibility tags (TF-241 rule 1, ADR-0057): created, renamed and described, retired and reactivated, each
/// change with its audit event in the same transaction and in the tag's field history. A code is never changed and
/// never reused, since records and people carry it.
/// </summary>
public sealed class TagService(VisibilityDbContext context, IUnitOfWork unitOfWork, IAuditWriter audit, ICurrentPrincipal current)
{
    public async Task<Result<VisibilityTag>> CreateAsync(string code, string name, string? description, CancellationToken cancellationToken)
    {
        var normalised = RecordTags.Normalise(code ?? string.Empty);
        if (!VisibilityTag.IsCode(normalised))
        {
            return Error.Validation(VisibilityErrors.TagCodeInvalid, $"'{code}' is not a tag code.", new FieldError("code", "invalid", "Use capitals, digits, hyphens and underscores, at most 32, starting with a capital or a digit."));
        }

        if (await context.Tags.AnyAsync(t => t.Code == normalised, cancellationToken).ConfigureAwait(false))
        {
            return Error.Conflict(VisibilityErrors.TagCodeTaken, $"The code {normalised} is a tag's already. A code stays with its tag, retired or not, so choose another.");
        }

        var tag = new VisibilityTag(normalised, name, description);
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false))
        {
            context.Tags.Add(tag);
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(VisibilityAuditEvents.TagCreated, tag, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        return tag;
    }

    public async Task<Result<VisibilityTag>> ChangeAsync(Guid id, string name, string? description, CancellationToken cancellationToken)
    {
        var tag = await context.Tags.SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        if (tag is null)
        {
            return NotFound();
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        tag.Change(name, description);
        if (context.Entry(tag).State == EntityState.Modified)
        {
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await WriteAsync(VisibilityAuditEvents.TagChanged, tag, cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return tag;
    }

    /// <summary>Retires a tag: it stays on the records and people that carry it, and is put on nothing new.</summary>
    public async Task<Result<VisibilityTag>> RetireAsync(Guid id, CancellationToken cancellationToken)
    {
        var tag = await context.Tags.SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        if (tag is null)
        {
            return NotFound();
        }

        if (!tag.IsActive)
        {
            return Error.Conflict(VisibilityErrors.TagRetired, $"The tag {tag.Code} is retired already.");
        }

        return await MoveAsync(tag, VisibilityAuditEvents.TagRetired, tag.Retire, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<VisibilityTag>> ReactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var tag = await context.Tags.SingleOrDefaultAsync(t => t.Id == id, cancellationToken).ConfigureAwait(false);
        if (tag is null)
        {
            return NotFound();
        }

        if (tag.IsActive)
        {
            return Error.Conflict(VisibilityErrors.TagActive, $"The tag {tag.Code} is active already.");
        }

        return await MoveAsync(tag, VisibilityAuditEvents.TagReactivated, tag.Reactivate, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<VisibilityTag>> MoveAsync(VisibilityTag tag, string action, Action change, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken).ConfigureAwait(false);
        change();
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await WriteAsync(action, tag, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return tag;
    }

    private Task WriteAsync(string action, VisibilityTag tag, CancellationToken cancellationToken)
    {
        var details = new Dictionary<string, string>(StringComparer.Ordinal) { ["code"] = tag.Code, ["name"] = tag.Name, ["status"] = tag.IsActive ? "active" : "retired" };
        return audit.WriteAsync(new AuditEvent(action, AuditOutcome.Success, current.Id, tag.Code, details, VisibilityTag.RecordType, tag.Id), cancellationToken);
    }

    private static Error NotFound() => Error.NotFound(VisibilityErrors.TagNotFound, "There is no visibility tag with this id.");
}
