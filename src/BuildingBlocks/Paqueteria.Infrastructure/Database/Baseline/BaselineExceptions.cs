namespace Paqueteria.Infrastructure.Database.Baseline;

public sealed class BaselineVerificationException(string message) : InvalidOperationException(message);

public sealed class PartialDatabaseBaselineException(DatabaseBaselineState state)
    : InvalidOperationException($"Database baseline is partial or unknown. Present: {string.Join(", ", state.PresentCriticalObjects)}. Missing: {string.Join(", ", state.MissingCriticalObjects)}.")
{
    public DatabaseBaselineState State { get; } = state;
}

public sealed class DatabaseAssertionException(IReadOnlyList<string> violations)
    : InvalidOperationException($"Database baseline assertions failed:{Environment.NewLine}- {string.Join($"{Environment.NewLine}- ", violations)}")
{
    public IReadOnlyList<string> Violations { get; } = violations;
}
