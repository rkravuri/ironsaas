namespace Iron.Kernel.Compliance.Contracts;

/// <summary>
/// The five documents of an evidence pack (TF-067 AC1, ADR-0052), in the order the pack numbers them: what auditors ask
/// for again and again, each as CSV and as one readable page.
/// </summary>
public static class EvidenceDocuments
{
    /// <summary>Every person and service account with their roles and scopes at the period's end, and the grants and removals in it.</summary>
    public const string AccessList = "access_list";

    /// <summary>The audit events of administrative types in the period, with actor, impersonator and reason.</summary>
    public const string AdministrativeActions = "administrative_actions";

    /// <summary>The security settings, company logins, emergency accounts, legal holds and conflict rules as they stood at the period's end.</summary>
    public const string SecuritySettings = "security_settings";

    /// <summary>Every verification of the audit chain in the period, and the restores operators accepted.</summary>
    public const string ChainVerification = "chain_verification";

    /// <summary>The backup and restore drills of the period.</summary>
    public const string BackupDrills = "backup_drills";

    public static IReadOnlyList<string> All { get; } = [AccessList, AdministrativeActions, SecuritySettings, ChainVerification, BackupDrills];

    public static string TitleOf(string document) => document switch
    {
        AccessList => "Access list",
        AdministrativeActions => "Administrative actions",
        SecuritySettings => "Security settings",
        ChainVerification => "Audit chain verification",
        BackupDrills => "Backup and restore drills",
        _ => document,
    };

    /// <summary>The folder of a document in the pack: its number and its code, <c>1-access_list</c>.</summary>
    public static string FolderOf(string document) => $"{All.ToList().IndexOf(document) + 1}-{document}";
}

/// <summary>
/// The period a pack covers (TF-067 AC1): from <paramref name="From"/> up to, not including, <paramref name="To"/>, both
/// server times in UTC. "At the period's end" is the state at <paramref name="To"/>.
/// </summary>
public sealed record EvidencePeriod(DateTimeOffset From, DateTimeOffset To);

/// <summary>A column of an evidence table: its key in the CSV and its header on the readable page.</summary>
public sealed record EvidenceColumn(string Key, string Header);

/// <summary>
/// One table a module contributes to a document of the pack (ADR-0052): its name in the pack (the CSV's file name), its
/// title on the page, its columns, its rows as text, and a note the page shows above it, such as how far the state at
/// the period's end could be rebuilt.
/// </summary>
public sealed record EvidenceTable(string Document, string Name, string Title, IReadOnlyList<EvidenceColumn> Columns, IReadOnlyList<IReadOnlyList<string?>> Rows, string? Note = null);

/// <summary>
/// What a module contributes to an evidence pack (TF-067, ADR-0052). Each module answers for its own records, read by the
/// pack's job as the system, so the pack holds the whole tenant whatever its requester may read elsewhere: the permission
/// to export evidence is high risk for that reason.
/// </summary>
public interface IEvidenceSource
{
    Task<IReadOnlyList<EvidenceTable>> TablesAsync(EvidencePeriod period, CancellationToken cancellationToken);
}

/// <summary>An anchor of the audit chain in the control plane, as a pack's manifest names it.</summary>
public sealed record EvidenceAnchor(Guid? Id, int Segment, long Position, string Hash, DateTimeOffset At, DateTimeOffset ReceivedAt);

/// <summary>
/// The audit chain's head when a pack was made (ADR-0052): the segment, the latest position the control plane anchored,
/// its hash and when it was sealed, and that anchor, so an auditor matches the pack to the sealed trail. Without a
/// control plane, as on a host run from source, the head is the latest seal and no anchor stands for it.
/// </summary>
public sealed record EvidenceChainHead(int Segment, long Position, string Hash, DateTimeOffset SealedAt, EvidenceAnchor? Anchor);

/// <summary>The audit chain's head for a pack's manifest; the Audit module answers it.</summary>
public interface IEvidenceChain
{
    Task<EvidenceChainHead?> HeadAsync(CancellationToken cancellationToken);
}

/// <summary>The states of an evidence pack: asked for, being made by the unit's worker, ready to download, or failed with the reason.</summary>
public static class EvidencePackStates
{
    public const string Requested = "requested";
    public const string Building = "building";
    public const string Ready = "ready";
    public const string Failed = "failed";
}
