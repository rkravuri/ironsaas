namespace Iron.Kernel.Authorization.Contracts;

/// <summary>One unit on a path from the root.</summary>
public sealed record UnitPathStep(Guid Id, string Name);

/// <summary>A unit a person may work for, with its path from the root (the unit last), for the header's menu on the tree.</summary>
public sealed record WorkingUnitChoice(Guid Id, string Name, string Code, int Depth, IReadOnlyList<UnitPathStep> Path);

/// <summary>
/// The units a person may choose to work for (TF-222, ADR-0038): their home unit and the units of their own
/// assignments, active ones only, in tree order; and whether they may choose all of them at once, the consolidated view.
/// </summary>
public sealed record WorkingUnitOptions(IReadOnlyList<WorkingUnitChoice> Units, bool AllUnitsAvailable);

/// <summary>What Identity asks of the tree for the session's working unit and for <c>/me</c>.</summary>
public interface IWorkingUnits
{
    Task<WorkingUnitOptions> OptionsAsync(Guid principalId, Guid? homeUnitId, CancellationToken cancellationToken);
}
