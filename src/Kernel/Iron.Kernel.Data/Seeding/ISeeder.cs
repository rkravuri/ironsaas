namespace Iron.Kernel.Data.Seeding;

/// <summary>
/// Reference data a module needs before it can serve: a role, a system principal, a code list. Registered as a scoped
/// service by the module; the <c>seed</c> command runs every seeder in <see cref="Order"/>, lowest first.
/// Seeders must be safe to run again: insert what is missing, leave what exists.
/// </summary>
public interface ISeeder
{
    /// <summary>Lower runs first. Use 100 for data without dependencies, higher for data that needs it.</summary>
    int Order { get; }

    Task SeedAsync(CancellationToken cancellationToken);
}
