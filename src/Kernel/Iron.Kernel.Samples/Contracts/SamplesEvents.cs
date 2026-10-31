namespace Iron.Kernel.Samples.Contracts;

/// <summary>A sample record was created in a unit (ADR-0057): the smoke's way to see an event published and handled.</summary>
public sealed record SampleRecordCreated(Guid RecordId, Guid UnitId);

/// <summary>The sample module's event (TF-110, ADR-0060), in units marked for testing only.</summary>
public static class SamplesEvents
{
    public static readonly EventType<SampleRecordCreated> RecordCreated = new(
        "samples.record.created", 1, "sample_record", "A sample record was created, in a unit marked for testing.", "Platform tests");

    public static IReadOnlyList<EventType> All => [RecordCreated];
}
