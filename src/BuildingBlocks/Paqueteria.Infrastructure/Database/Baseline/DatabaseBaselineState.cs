namespace Paqueteria.Infrastructure.Database.Baseline;

public enum DatabaseBaselineStatus
{
    Clean,
    Applied,
    Partial,
}

public sealed record DatabaseBaselineState(
    DatabaseBaselineStatus Status,
    IReadOnlyList<string> PresentCriticalObjects,
    IReadOnlyList<string> MissingCriticalObjects);

public enum DatabaseBaselineApplyStatus
{
    Applied,
    AlreadyApplied,
}

public sealed record DatabaseBaselineTimings(
    TimeSpan Schema,
    TimeSpan Roles,
    TimeSpan Assertions);

public sealed record DatabaseBaselineApplyResult(
    DatabaseBaselineApplyStatus Status,
    DatabaseBaselineState InitialState,
    DatabaseAssertionReport Assertions,
    DatabaseBaselineTimings Timings);

public sealed record DatabaseBaselinePlan(
    string Baseline,
    string SanitizedTarget,
    DatabaseBaselineState State,
    IReadOnlyList<VerifiedBaselineStep> Steps,
    IReadOnlyList<string> Assertions);
