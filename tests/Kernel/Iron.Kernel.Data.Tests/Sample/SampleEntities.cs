using Iron.Kernel.Data.Entities;

namespace Iron.Kernel.Data.Tests.Sample;

/// <summary>A business record that can be soft-deleted: the common case for master data.</summary>
public sealed class Widget : BusinessRecord, ISoftDeletable
{
    private Widget()
    {
    }

    public Widget(Guid owningOrganisationId, string name)
        : base(owningOrganisationId)
    {
        Name = name;
    }

    public string Name { get; private set; } = string.Empty;

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public void Rename(string name)
    {
        Name = name;
    }
}

/// <summary>Platform data that changes but belongs to no organisation.</summary>
public sealed class Preference : AuditedEntity
{
    private Preference()
    {
    }

    public Preference(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public void Change(string value)
    {
        Value = value;
    }
}

/// <summary>A row that is written once.</summary>
public sealed class WidgetEvent : AppendOnlyEntity
{
    private WidgetEvent()
    {
    }

    public WidgetEvent(Guid widgetId, string note)
    {
        WidgetId = widgetId;
        Note = note;
    }

    public Guid WidgetId { get; private set; }

    public string Note { get; private set; } = string.Empty;

    public void Edit(string note)
    {
        Note = note;
    }
}
