using Iron.Kernel.Data.Checks;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Entities;
using Iron.Kernel.Data.History;
using Iron.Kernel.Data.Tests.Sample;
using Iron.Kernel.Data.Transactions;
using Iron.Kernel.History;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Data.Tests;

/// <summary>
/// Field-level history (TF-062, ADR-0051): a save of a tracked record hands the recorder one change with every changed
/// field, in the save's transaction; sensitive fields are masked and secrets never kept; bookkeeping alone records
/// nothing; and the model check fails, naming the entity, for a business record that is not tracked.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ChangeHistoryTests(PostgresFixture fixture)
{
    private static readonly Guid Organisation = Guid.Parse("01a14e12-0000-7000-8000-00000000000a");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-062 AC1", "ADR-0051")]
    public async Task ARecordChangedInTwoFieldsGivesOneGroupWithTwoRowsAndItsVersion()
    {
        var recorder = new CapturingRecorder();
        await using var provider = Build(recorder);
        var proposal = new Proposal(Organisation, "Annual maintenance");
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Proposals.Add(proposal);
            await context.SaveChangesAsync(Cancel);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            var stored = await context.Proposals.SingleAsync(p => p.Id == proposal.Id, Cancel);
            stored.Retitle("Annual maintenance, three sites");
            stored.Submit();
            await context.SaveChangesAsync(Cancel);
        }

        var created = Assert.Single(recorder.Saves[0]);
        Assert.Equal((RecordChangeKinds.Created, "proposals", proposal.Id, 1), (created.Kind, created.RecordType, created.RecordId, created.Version));
        var changed = Assert.Single(recorder.Saves[1]);
        Assert.Equal((RecordChangeKinds.Changed, 2), (changed.Kind, changed.Version));
        Assert.Equal(
            [new FieldChange("status", "Draft", "Submitted"), new FieldChange("title", "Annual maintenance", "Annual maintenance, three sites")],
            changed.Fields.OrderBy(f => f.Field, StringComparer.Ordinal));
        Assert.All(recorder.TransactionOpen, Assert.True);
    }

    [Fact]
    [Proves("TF-062 AC3", "ADR-0051")]
    public async Task ASensitiveFieldIsKeptMaskedAndASecretOnlyAsChanged()
    {
        var recorder = new CapturingRecorder();
        await using var provider = Build(recorder);
        var order = new SampleOrder(Organisation, Ids.New(), "PO-77", DateTimeOffset.UtcNow);
        order.PayTo("GB33BUKB20201555555555");
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Orders.Add(order);
            await context.SaveChangesAsync(Cancel);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            var stored = await context.Orders.SingleAsync(o => o.Id == order.Id, Cancel);
            stored.PayTo("DE89370400440532013000");
            stored.ChangePortalKey("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08");
            await context.SaveChangesAsync(Cancel);
        }

        var fields = Assert.Single(recorder.Saves[1]).Fields.ToDictionary(f => f.Field);
        Assert.Equal(new FieldChange("bank_account", "****5555", "****3000", FieldTreatments.Masked), fields["bank_account"]);
        Assert.Equal(new FieldChange("portal_key_hash", null, null, FieldTreatments.Secret), fields["portal_key_hash"]);
        Assert.DoesNotContain(recorder.Changes.SelectMany(c => c.Fields), f => f.Old?.Contains("2020155", StringComparison.Ordinal) == true || f.New?.Contains("0532013", StringComparison.Ordinal) == true);
    }

    [Fact]
    [Proves("ADR-0054", "ADR-0051")]
    public async Task AnEncryptedFieldsHistorySaysOnlyThatItChangedAndItsHashSaysNothing()
    {
        var recorder = new CapturingRecorder();
        using var keys = new Iron.Kernel.Security.Keys.LocalKeyService(new Iron.Kernel.Security.Keys.KeyOptions());
        await using var provider = fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new FixedPrincipal(Ids.New()), services =>
        {
            services.AddScoped<IChangeRecorder>(p => recorder.In(p.GetRequiredService<IUnitOfWork>()));
            services.AddSingleton<IKeyService>(keys);
        });
        var supplier = new SampleSupplier(Organisation, "History supplier");
        await using (var scope = provider.CreateAsyncScope())
        {
            var encryption = scope.ServiceProvider.GetRequiredService<Iron.Kernel.Data.Encryption.IFieldEncryption>();
            supplier.IdentifyForTax(await encryption.SealSearchableAsync("77-1234567", Cancel));
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Suppliers.Add(supplier);
            await context.SaveChangesAsync(Cancel);
        }

        var fields = Assert.Single(recorder.Saves[0]).Fields.ToDictionary(f => f.Field);
        Assert.Equal(new FieldChange("tax_identifier", null, null, FieldTreatments.Secret), fields["tax_identifier"]);
        Assert.False(fields.ContainsKey("tax_identifier_hash"));
        Assert.DoesNotContain(recorder.Changes.SelectMany(c => c.Fields), f => f.New?.Contains("local:v", StringComparison.Ordinal) == true);
    }

    [Fact]
    [Proves("ADR-0051")]
    public async Task BookkeepingAloneRecordsNothingAndASoftDeleteRecordsTheDeletion()
    {
        var recorder = new CapturingRecorder();
        await using var provider = Build(recorder);
        var order = new SampleOrder(Organisation, Ids.New(), "PO-78", DateTimeOffset.UtcNow);
        var widget = new Widget(Organisation, "Valve");
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.AddRange(order, widget);
            await context.SaveChangesAsync(Cancel);
        }

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            (await context.Orders.SingleAsync(o => o.Id == order.Id, Cancel)).Viewed(DateTimeOffset.UtcNow);
            context.Widgets.Remove(await context.Widgets.SingleAsync(w => w.Id == widget.Id, Cancel));
            await context.SaveChangesAsync(Cancel);
        }

        var deleted = Assert.Single(recorder.Saves[1]);
        Assert.Equal((RecordChangeKinds.Deleted, widget.Id), (deleted.Kind, deleted.RecordId));
        Assert.Contains(new FieldChange("name", "Valve", null), deleted.Fields);
    }

    [Fact]
    [Proves("TF-062 AC4", "ADR-0051", "ADR-0013")]
    public async Task ASaveWhoseHistoryCannotBeWrittenIsRolledBack()
    {
        var recorder = new CapturingRecorder { Fail = true };
        await using var provider = Build(recorder);
        var proposal = new Proposal(Organisation, "Never stored");
        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
            context.Proposals.Add(proposal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync(Cancel));
        }

        await using var check = provider.CreateAsyncScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<SampleDbContext>().Proposals.AnyAsync(p => p.Id == proposal.Id, Cancel));
    }

    [Fact]
    [Proves("TF-062 AC2", "ADR-0051")]
    public void TheModelCheckFailsNamingABusinessRecordWithoutItsHistory()
    {
        var options = new DbContextOptionsBuilder<ContextWithAnUntrackedRecord>().UseNpgsql(fixture.ConnectionString).Options;
        using var context = new ContextWithAnUntrackedRecord(options);

        var violation = Assert.Single(ModelCheck.Check(context.Model, nameof(ContextWithAnUntrackedRecord), isBusinessModule: true));

        Assert.StartsWith("ContextWithAnUntrackedRecord: Invoice is a business record without its change history.", violation, StringComparison.Ordinal);
    }

    private ServiceProvider Build(CapturingRecorder recorder) =>
        fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new FixedPrincipal(Ids.New()), services =>
            services.AddScoped<IChangeRecorder>(provider => recorder.In(provider.GetRequiredService<IUnitOfWork>())));

    /// <summary>The Audit module's part, as a test sees it: each save's changes, and whether a transaction was open.</summary>
    private sealed class CapturingRecorder
    {
        public bool Fail { get; init; }

        public List<IReadOnlyList<RecordChange>> Saves { get; } = [];

        public IEnumerable<RecordChange> Changes => Saves.SelectMany(s => s);

        public List<bool> TransactionOpen { get; } = [];

        public IChangeRecorder In(IUnitOfWork unitOfWork) => new Recorder(this, unitOfWork);

        private sealed class Recorder(CapturingRecorder owner, IUnitOfWork unitOfWork) : IChangeRecorder
        {
            public Task RecordAsync(IReadOnlyList<RecordChange> changes, CancellationToken cancellationToken)
            {
                owner.TransactionOpen.Add(unitOfWork.IsActive);
                if (owner.Fail)
                {
                    throw new InvalidOperationException("The audit trail could not be written.");
                }

                owner.Saves.Add(changes);
                return Task.CompletedTask;
            }
        }
    }
}

/// <summary>A business record that forgot its history, for the model check's refusal (TF-062 AC2).</summary>
public sealed class Invoice : BusinessRecord
{
    private Invoice()
    {
    }

    public string Number { get; private set; } = string.Empty;
}

[Schema("kernel_untracked")]
public sealed class ContextWithAnUntrackedRecord(DbContextOptions<ContextWithAnUntrackedRecord> options) : ModuleDbContext(options)
{
    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
    }
}
