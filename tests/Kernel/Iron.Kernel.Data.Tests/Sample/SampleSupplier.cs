using Iron.Kernel.Data.Encryption;
using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Data.Tests.Sample;

/// <summary>
/// A supplier of the sample module with two encrypted fields (TF-074, ADR-0054): its tax identifier, found by exact
/// value through its keyed hash, and its bank account, encrypted only. Neither is ever stored as its value.
/// </summary>
public sealed class SampleSupplier : BusinessRecord, ITrackedChanges
{
    public const string RecordType = "sample_supplier";

    private SampleSupplier()
    {
    }

    public SampleSupplier(Guid owningOrganisationId, string name)
        : base(owningOrganisationId)
    {
        Name = name;
    }

    public string Name { get; private set; } = string.Empty;

    [Searchable]
    public EncryptedValue? TaxIdentifier { get; private set; }

    public SearchHash? TaxIdentifierHash { get; private set; }

    public EncryptedValue? BankAccount { get; private set; }

    public void IdentifyForTax(SearchableValue taxIdentifier)
    {
        ArgumentNullException.ThrowIfNull(taxIdentifier);
        TaxIdentifier = taxIdentifier.Value;
        TaxIdentifierHash = taxIdentifier.Hash;
    }

    public void PayTo(EncryptedValue bankAccount) => BankAccount = bankAccount;
}
