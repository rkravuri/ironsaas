using System.Security.Cryptography;
using System.Text;
using Iron.Kernel.Data.Checks;
using Iron.Kernel.Data.Contexts;
using Iron.Kernel.Data.Encryption;
using Iron.Kernel.Data.Tests.Sample;
using Iron.Kernel.Security.Keys;
using Iron.Kernel.Security.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Iron.Kernel.Data.Tests.Encryption;

/// <summary>OpenBao's transit engine for the encrypted fields, with a key family of its own per test (tests/Iron.Testing).</summary>
public sealed class TransitFixture : IAsyncLifetime
{
    private TestOpenBao? _store;

    public TestOpenBao Store => _store ?? throw new InvalidOperationException("The fixture is not initialised.");

    public async ValueTask InitializeAsync() => _store = await TestOpenBao.StartAsync("data", TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (_store is not null)
        {
            await _store.DisposeAsync();
        }
    }

    /// <summary>A new tenant's keys, made as the fleet makes them, and the key service the unit would use.</summary>
    public async Task<TenantKeys> NewTenantAsync()
    {
        var prefix = $"tenant-d{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}";
        var keys = new TransitKeyService(new SecretStoreOptions { Address = Store.Address, Token = Store.RootToken, Mount = Store.Mount, TransitMount = Store.TransitMount }, new KeyOptions { Prefix = prefix });
        foreach (var purpose in KeyPurposes.All)
        {
            await keys.EnsureAsync(KeyPurposes.KeyName(prefix, purpose), KeyKinds.Of(purpose), TestContext.Current.CancellationToken);
        }

        return new TenantKeys(keys, prefix);
    }
}

/// <summary>A test tenant's key service and its key family.</summary>
public sealed class TenantKeys(TransitKeyService keys, string prefix) : IDisposable
{
    public TransitKeyService Service { get; } = keys;

    public string Prefix { get; } = prefix;

    public string Name(KeyPurpose purpose) => KeyPurposes.KeyName(Prefix, purpose);

    public void Dispose() => Service.Dispose();
}

/// <summary>
/// Encrypted fields (TF-074 AC1, AC2, ADR-0054) and their re-encryption after a rotation (TF-075 AC1), on the sample
/// module's suppliers: a tax identifier found by exact value through its keyed hash, and a bank account encrypted only.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EncryptedFieldTests(PostgresFixture fixture, TransitFixture transit) : IClassFixture<TransitFixture>
{
    private static readonly Guid Organisation = Guid.Parse("01a14e12-0000-7000-8000-0000000000e1");

    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Fact]
    [Proves("TF-074 AC1", "ADR-0054")]
    public async Task ADumpOfTheTableShowsCiphertextAndNeverTheValues()
    {
        using var keys = await transit.NewTenantAsync();
        await using var provider = await BuildAsync(keys);
        var supplier = await SaveSupplierAsync(provider, "Northwind Valves", "12-3456789", "GB33BUKB20201555555555");

        var dump = await DumpAsync(supplier.Id);

        Assert.Contains("vault:v1:", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("3456789", dump, StringComparison.Ordinal);
        Assert.DoesNotContain("20201555555555", dump, StringComparison.Ordinal);
        Assert.Contains("Northwind Valves", dump, StringComparison.Ordinal);
    }

    [Fact]
    [Proves("TF-074 AC2", "TF-074 AC4", "ADR-0054")]
    public async Task AnExactValueIsFoundThroughItsKeyedHashAndAPartOfItFindsNothing()
    {
        using var keys = await transit.NewTenantAsync();
        await using var provider = await BuildAsync(keys);
        var wanted = await SaveSupplierAsync(provider, "Contoso Pumps", "98-7654321", "DE89370400440532013000");
        await SaveSupplierAsync(provider, "Fabrikam Seals", "11-2223334", "FR1420041010050500013M02606");

        Assert.Equal([wanted.Id], await FindByTaxIdentifierAsync(provider, "98-7654321"));
        Assert.Equal([wanted.Id], await FindByTaxIdentifierAsync(provider, " 987 654 321 "));
        Assert.Empty(await FindByTaxIdentifierAsync(provider, "7654321"));
        Assert.Empty(await FindByTaxIdentifierAsync(provider, "98-765432"));
    }

    [Fact]
    [Proves("TF-075 AC1", "ADR-0054")]
    public async Task AfterARotationTheJobMovesEveryValueAndOnlyThenIsTheOldVersionRetired()
    {
        using var keys = await transit.NewTenantAsync();
        await using var provider = await BuildAsync(keys);
        var suppliers = new List<SampleSupplier>();
        for (var i = 0; i < 7; i++)
        {
            suppliers.Add(await SaveSupplierAsync(provider, $"Rotation supplier {i}", $"55-00000{i}", $"NL91ABNA041716430{i}"));
        }

        var before = await CiphertextAsync(suppliers[0].Id);
        await keys.Service.RotateAsync(keys.Name(KeyPurpose.Data), Cancel);
        await keys.Service.RotateAsync(keys.Name(KeyPurpose.Index), Cancel);
        var reencryption = provider.GetRequiredService<FieldReencryption>();
        var reports = new List<ReencryptionProgress>();

        var data = await reencryption.RunAsync(KeyPurpose.Data, 2, (progress, _) => { reports.Add(progress); return Task.CompletedTask; }, Cancel, batchSize: 3);
        var index = await reencryption.RunAsync(KeyPurpose.Index, 2, null, Cancel, batchSize: 3);
        await keys.Service.RetireAsync(keys.Name(KeyPurpose.Data), 2, Cancel);
        await keys.Service.RetireAsync(keys.Name(KeyPurpose.Index), 2, Cancel);

        Assert.True(data.Done >= 14, $"moved {data.Done} values");
        Assert.True(index.Done >= 7, $"made {index.Done} hashes");
        Assert.Equal(0, await reencryption.RemainingAsync(KeyPurpose.Data, 2, Cancel));
        Assert.Equal(0, await reencryption.RemainingAsync(KeyPurpose.Index, 2, Cancel));
        Assert.Equal(reports[^1].Total, reports[^1].Done);
        await Assert.ThrowsAsync<KeyVersionRetiredException>(() => keys.Service.DecryptAsync(KeyPurpose.Data, before, Cancel));
        Assert.Equal([suppliers[3].Id], await FindByTaxIdentifierAsync(provider, "55-000003"));
        await using var scope = provider.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Suppliers.AsNoTracking().SingleAsync(s => s.Id == suppliers[6].Id, Cancel);
        Assert.Equal(2, stored.BankAccount!.KeyVersion);
        Assert.Equal("NL91ABNA0417164306", await scope.ServiceProvider.GetRequiredService<IFieldEncryption>().DecryptAsync(stored.BankAccount, Cancel));
    }

    [Fact]
    [Proves("TF-075 AC1", "ADR-0054")]
    public async Task AnInterruptedJobResumesWhereItStoppedAndFinishesTheRest()
    {
        using var keys = await transit.NewTenantAsync();
        await using var provider = await BuildAsync(keys);
        for (var i = 0; i < 5; i++)
        {
            await SaveSupplierAsync(provider, $"Interrupted supplier {i}", $"66-10000{i}", $"BE6853900754703{i}");
        }

        await keys.Service.RotateAsync(keys.Name(KeyPurpose.Data), Cancel);
        var reencryption = provider.GetRequiredService<FieldReencryption>();
        var total = await reencryption.RemainingAsync(KeyPurpose.Data, 2, Cancel);
        using var interrupt = CancellationTokenSource.CreateLinkedTokenSource(Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reencryption.RunAsync(KeyPurpose.Data, 2, (_, _) => { interrupt.Cancel(); return Task.CompletedTask; }, interrupt.Token, batchSize: 2));
        var left = await reencryption.RemainingAsync(KeyPurpose.Data, 2, Cancel);
        var resumed = await reencryption.RunAsync(KeyPurpose.Data, 2, null, Cancel, batchSize: 2);

        Assert.Equal(10, total);
        Assert.Equal(8, left);
        Assert.Equal(8, resumed.Done);
        Assert.Equal(0, await reencryption.RemainingAsync(KeyPurpose.Data, 2, Cancel));
    }

    [Fact]
    [Proves("ADR-0054")]
    public void TheModelCheckNamesASearchableFieldWithoutItsHashAndAHashOfNothing()
    {
        using var context = new ContextWithBrokenEncryption(DesignTime.OptionsFor<ContextWithBrokenEncryption>());

        var violations = ModelCheck.Check(context.Model, nameof(ContextWithBrokenEncryption), isBusinessModule: false);

        Assert.Contains("ContextWithBrokenEncryption: BrokenRecord.Identifier is searchable without its hash. Add a SearchHash property named IdentifierHash (ADR-0054).", violations);
        Assert.Contains(violations, v => v.StartsWith("ContextWithBrokenEncryption: BrokenRecord.StrayHash is a search hash of no searchable encrypted field.", StringComparison.Ordinal));
    }

    /// <summary>
    /// The data services with the tenant's key service, on an empty suppliers table: the collection's tests run one at a
    /// time, and every supplier a test left is encrypted under another test's keys.
    /// </summary>
    private async Task<ServiceProvider> BuildAsync(TenantKeys keys)
    {
        await using (var clear = fixture.DataSource.CreateCommand("DELETE FROM kernel_sample.suppliers"))
        {
            await clear.ExecuteNonQueryAsync(Cancel);
        }

        return fixture.BuildServices(new FakeTimeProvider(DateTimeOffset.UtcNow), new FixedPrincipal(Ids.New()), services => services.AddSingleton<IKeyService>(keys.Service));
    }

    private static async Task<SampleSupplier> SaveSupplierAsync(ServiceProvider provider, string name, string taxIdentifier, string bankAccount)
    {
        await using var scope = provider.CreateAsyncScope();
        var encryption = scope.ServiceProvider.GetRequiredService<IFieldEncryption>();
        var supplier = new SampleSupplier(Organisation, name);
        supplier.IdentifyForTax(await encryption.SealSearchableAsync(taxIdentifier, Cancel));
        supplier.PayTo(await encryption.EncryptAsync(bankAccount, Cancel));
        var context = scope.ServiceProvider.GetRequiredService<SampleDbContext>();
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync(Cancel);
        return supplier;
    }

    private static async Task<IReadOnlyList<Guid>> FindByTaxIdentifierAsync(ServiceProvider provider, string taxIdentifier)
    {
        await using var scope = provider.CreateAsyncScope();
        var hashes = (await scope.ServiceProvider.GetRequiredService<IFieldEncryption>().SearchHashesAsync(taxIdentifier, Cancel)).ToList();
        return await scope.ServiceProvider.GetRequiredService<SampleDbContext>().Suppliers.AsNoTracking()
            .Where(s => hashes.Contains(s.TaxIdentifierHash!))
            .Select(s => s.Id)
            .ToListAsync(Cancel);
    }

    /// <summary>The supplier's row as a dump writes it: COPY in text form, every column.</summary>
    private async Task<string> DumpAsync(Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Cancel);
        using var reader = await connection.BeginTextExportAsync($"COPY (SELECT * FROM kernel_sample.suppliers WHERE id = '{id:D}') TO STDOUT", Cancel);
        return await reader.ReadToEndAsync(Cancel);
    }

    private async Task<string> CiphertextAsync(Guid id)
    {
        await using var command = fixture.DataSource.CreateCommand("SELECT bank_account FROM kernel_sample.suppliers WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        return (string)(await command.ExecuteScalarAsync(Cancel))!;
    }
}

/// <summary>A searchable field without its hash, and a hash of nothing, for the model check's messages.</summary>
[Schema("kernel_broken")]
public sealed class ContextWithBrokenEncryption(DbContextOptions<ContextWithBrokenEncryption> options) : ModuleDbContext(options)
{
    public DbSet<BrokenRecord> Records => Set<BrokenRecord>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
    }
}

public sealed class BrokenRecord : Iron.Kernel.Data.Entities.AuditedEntity
{
    [Searchable]
    public EncryptedValue? Identifier { get; private set; }

    public SearchHash? StrayHash { get; private set; }
}
