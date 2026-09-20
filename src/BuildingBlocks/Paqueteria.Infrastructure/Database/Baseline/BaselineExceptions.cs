namespace Paqueteria.Infrastructure.Database.Baseline;

public sealed class BaselineVerificationException(string message) : InvalidOperationException(message);

// E-002 v0.9 Amendment 2: an invalid first-baseline prestate is E002_BASELINE_PRESTATE_PARTIAL (fail closed).
public sealed class PartialDatabaseBaselineException(DatabaseBaselineState state)
    : InvalidOperationException($"E002_BASELINE_PRESTATE_PARTIAL; STOP_FOR_CONTRACT_REVIEW; Database baseline is partial or unknown. Present: {string.Join(", ", state.PresentCriticalObjects)}. Missing: {string.Join(", ", state.MissingCriticalObjects)}.")
{
    public DatabaseBaselineState State { get; } = state;
}

public sealed class DatabaseAssertionException(IReadOnlyList<string> violations)
    : InvalidOperationException($"Database baseline assertions failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", violations)}")
{
    public IReadOnlyList<string> Violations { get; } = violations;
}
