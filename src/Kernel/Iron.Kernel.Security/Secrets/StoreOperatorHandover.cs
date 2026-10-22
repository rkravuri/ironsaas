using System.Globalization;
using Iron.Kernel.Handover;

namespace Iron.Kernel.Security.Secrets;

/// <summary>
/// Bound from the <c>Handover</c> section, which only the fleet sets, for one image command (ADR-0046): the one-time place
/// for this job (<c>tenants/&lt;slug&gt;/handover/&lt;job id&gt;</c>) and a short-lived token that can write there and
/// nothing else.
/// </summary>
public sealed class HandoverOptions
{
    public const string SectionName = "Handover";

    public string? Path { get; set; }

    public string? Token { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Path) && !string.IsNullOrWhiteSpace(Token);
}

/// <summary>The keys of a handed-over item in the store, which the control plane reads back.</summary>
public static class HandoverKeys
{
    public const string Kind = "kind";
    public const string Subject = "subject";
    public const string LinkPath = "link_path";
    public const string ExpiresAt = "expires_at";

    /// <summary>What an item carries beyond a link: a break-glass review (ADR-0049).</summary>
    public const string Payload = "payload";
}

/// <summary>
/// Hands an item over through the store (ADR-0046): written once at the job's place with the token the fleet gave, and
/// deleted by the store when the link stops working. The unit's own token cannot write there; this one cannot read.
/// </summary>
public sealed class StoreOperatorHandover(SecretStoreOptions store, HandoverOptions handover, TimeProvider time) : IOperatorHandover
{
    public bool IsAvailable => handover.IsConfigured && !string.IsNullOrWhiteSpace(store.Address);

    public async Task DepositAsync(OperatorHandoverItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!IsAvailable)
        {
            throw new InvalidOperationException("No handover place is configured: the fleet sets Handover:Path and Handover:Token for the command it runs.");
        }

        var lifetime = item.ExpiresAt - time.GetUtcNow();
        using var place = new OpenBaoSecretStore(new SecretStoreOptions { Address = store.Address, Token = handover.Token, Mount = store.Mount });
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [HandoverKeys.Kind] = item.Kind,
            [HandoverKeys.Subject] = item.Subject,
            [HandoverKeys.LinkPath] = item.LinkPath,
            [HandoverKeys.ExpiresAt] = item.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
        };
        if (item.Payload is not null)
        {
            values[HandoverKeys.Payload] = item.Payload;
        }

        await place.WriteOnceAsync(
            handover.Path!,
            values,
            lifetime > TimeSpan.FromMinutes(1) ? lifetime : TimeSpan.FromMinutes(1),
            cancellationToken).ConfigureAwait(false);
    }
}
