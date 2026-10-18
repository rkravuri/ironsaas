namespace Iron.Data.Commands;

/// <summary>One module context registered with AddIronDbContext, so the commands can find every schema of the unit.</summary>
public sealed record RegisteredContext(Type ContextType, string Schema);
