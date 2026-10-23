using Iron.Kernel.Compliance.Contracts;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Iron.Kernel.Compliance.Tests;

/// <summary>
/// The sample module's document: a business record, tracked (TF-062), with a sensitive field, that its module lets people
/// approve by signature (TF-063). As a business module's document will be in its slice.
/// </summary>
public sealed class SampleDocument : BusinessRecord, ITrackedChanges
{
    public const string RecordType = "sample_document";

    private SampleDocument()
    {
        Title = string.Empty;
        Body = string.Empty;
    }

    public SampleDocument(Guid owningOrganisationId, string title, string body, decimal amount, string bankAccount)
        : base(owningOrganisationId)
    {
        Title = title;
        Body = body;
        Amount = amount;
        BankAccount = bankAccount;
    }

    public string Title { get; private set; }

    public string Body { get; private set; }

    public decimal Amount { get; private set; }

    [Sensitive]
    public string? BankAccount { get; private set; }

    public void Revise(string body, decimal amount)
    {
        Body = body;
        Amount = amount;
    }
}

[Schema("kernel_sample")]
public sealed class SampleDocumentsDbContext(DbContextOptions<SampleDocumentsDbContext> options) : ModuleDbContext(options)
{
    public DbSet<SampleDocument> Documents => Set<SampleDocument>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SampleDocument>().Property(d => d.Amount).HasPrecision(18, 2);
    }
}

/// <summary>The sample module's documents as signable records: its snapshot is every field a person wrote, at the record's version.</summary>
public sealed class SampleDocuments(SampleDocumentsDbContext context) : ISignableRecords
{
    public static SignableType Type { get; } = new(
        SampleDocument.RecordType,
        "Sample document",
        [SignatureMeanings.Approved, SignatureMeanings.Rejected, SignatureMeanings.Acknowledged],
        new SigningPolicy(true, [SignatureMeanings.Approved, SignatureMeanings.Rejected, SignatureMeanings.Acknowledged], true));

    public IReadOnlyList<SignableType> Types => [Type];

    public static RecordSnapshot Snapshot(SampleDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return RecordSnapshot.Of(SampleDocument.RecordType, document.Id, document.Version, document.Title, new
        {
            document.Title,
            document.Body,
            document.Amount,
            document.BankAccount,
            OwningOrganisationId = document.OwningOrganisationId,
        });
    }

    public async Task<RecordSnapshot?> CurrentAsync(string recordType, Guid recordId, CancellationToken cancellationToken)
    {
        var document = await context.Documents.AsNoTracking().SingleOrDefaultAsync(d => d.Id == recordId, cancellationToken).ConfigureAwait(false);
        return document is null ? null : Snapshot(document);
    }

    public Task<bool> MayReadAsync(string recordType, Guid recordId, CancellationToken cancellationToken) => Task.FromResult(true);
}
