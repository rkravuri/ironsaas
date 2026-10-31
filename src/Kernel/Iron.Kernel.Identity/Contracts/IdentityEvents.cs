namespace Iron.Kernel.Identity.Contracts;

/// <summary>A person was deactivated (TF-010 AC3): they cannot sign in, and every session of theirs ended.</summary>
public sealed record PersonDeactivated([property: PersonalData] Guid PersonId, int SessionsEnded);

/// <summary>The domain events Identity publishes (TF-110, ADR-0060), which other modules subscribe to through these declarations.</summary>
public static class IdentityEvents
{
    public const string Owner = "Identity module";

    /// <summary>Authorization ends the person's delegations on it, since nobody acts for or as a deactivated person.</summary>
    public static readonly EventType<PersonDeactivated> PersonDeactivated = new(
        "identity.person.deactivated", 1, IdentityRecordTypes.Principal, "A person was deactivated: they cannot sign in, and every session of theirs ended.", Owner);

    public static IReadOnlyList<EventType> All => [PersonDeactivated];
}
