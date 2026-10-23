namespace Iron.Kernel.Data.Entities;

/// <summary>
/// An entity whose field-level changes are recorded on every save (TF-062, ADR-0051): one audit event per changed
/// record, with every changed field's old and new value, the actor, the time and the reason when one was given. Every
/// business record must carry it (the model check fails, naming the entity), and so do the kernel's records people
/// change. A field marked <see cref="SensitiveAttribute"/> is kept masked, <see cref="SecretAttribute"/> only as
/// changed, and <see cref="NotTrackedAttribute"/> not at all.
/// </summary>
/// <remarks>
/// The record's type in its history is the name <see cref="TrackedAsAttribute"/> gives, else the class's public constant
/// <c>RecordType</c> when it has one (the name its policies and audit events use), otherwise its table's name.
/// </remarks>
public interface ITrackedChanges;

/// <summary>A field whose history keeps its last <see cref="Keep"/> characters and masks the rest (TF-062 AC3).</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SensitiveAttribute(int keep = 4) : Attribute
{
    public int Keep { get; } = keep;
}

/// <summary>A field whose history records only that it changed, never a value: a password's hash, a token.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;

/// <summary>
/// A field the history leaves out: bookkeeping the system changes on its own, such as a count of failed sign-ins or the
/// time of the last one. A save that changes only such fields records nothing.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotTrackedAttribute : Attribute;

/// <summary>
/// The record's type in its history, for an entity that cannot hold a <c>RecordType</c> constant because a property
/// has that name, such as a legal hold, which names the record it holds.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class TrackedAsAttribute(string recordType) : Attribute
{
    public string RecordType { get; } = recordType;
}
