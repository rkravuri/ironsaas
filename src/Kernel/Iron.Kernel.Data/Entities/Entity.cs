namespace Iron.Kernel.Data.Entities;

/// <summary>
/// Base of every table row: a UUID version 7 primary key, set in code when the object is created (ADR-0007).
/// Give a concrete entity a private parameterless constructor for Entity Framework and public constructors
/// for your code; EF binds constructor parameters to properties with the same name.
/// </summary>
public abstract class Entity
{
    protected Entity()
        : this(Ids.New())
    {
    }

    protected Entity(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("The id is empty.", nameof(id));
        }

        Id = id;
    }

    public Guid Id { get; private set; }
}
