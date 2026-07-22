using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

public sealed class DatabaseBaselineDeployer(
    DatabaseBaselineStateDetector? stateDetector = null,
    DatabaseBaselineAssertions? assertions = null)
{
    private readonly DatabaseBaselineStateDetector _stateDetector = stateDetector ?? new DatabaseBaselineStateDetector();
    private readonly DatabaseBaselineAssertions _assertions = assertions ?? new DatabaseBaselineAssertions();

    public async Task<DatabaseBaselinePlan> PlanAsync(
        VerifiedDatabaseBaseline baseline,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var state = await _stateDetector.DetectAsync(connection, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new DatabaseBaselinePlan(
            baseline.Version,
            SanitizeTarget(connectionString),
            state,
            baseline.Steps,
            DatabaseBaselineAssertions.AssertionNames);
    }

    public async Task<DatabaseBaselineApplyResult> ApplyAsync(
        VerifiedDatabaseBaseline baseline,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await ExecuteNonQueryAsync(
                connection,
                transaction,
                "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
                cancellationToken,
                new NpgsqlParameter<long>("key", CanonicalBaselineContract.AdvisoryLockKey)).ConfigureAwait(false);

            var initialState = await _stateDetector.DetectAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (initialState.Status == DatabaseBaselineStatus.Partial)
            {
                throw new PartialDatabaseBaselineException(initialState);
            }

            if (initialState.Status == DatabaseBaselineStatus.Applied)
            {
                var alreadyAppliedAssertions = await _assertions.AssertAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return new DatabaseBaselineApplyResult(
                    DatabaseBaselineApplyStatus.AlreadyApplied,
                    initialState,
                    alreadyAppliedAssertions,
                    new DatabaseBaselineTimings(TimeSpan.Zero, TimeSpan.Zero, alreadyAppliedAssertions.Duration));
            }

            var postgresVersionNumber = await ExecuteScalarAsync<int>(
                connection,
                transaction,
                "SELECT current_setting('server_version_num')::integer",
                cancellationToken).ConfigureAwait(false);
            if (postgresVersionNumber / 10_000 != 18)
            {
                throw new InvalidOperationException($"PostgreSQL 18 is required; server_version_num is {postgresVersionNumber}.");
            }

            var schemaDuration = await ExecuteStepAsync(connection, transaction, baseline.Steps[0], cancellationToken).ConfigureAwait(false);
            var rolesDuration = await ExecuteStepAsync(connection, transaction, baseline.Steps[1], cancellationToken).ConfigureAwait(false);
            var assertionReport = await _assertions.AssertAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new DatabaseBaselineApplyResult(
                DatabaseBaselineApplyStatus.Applied,
                initialState,
                assertionReport,
                new DatabaseBaselineTimings(schemaDuration, rolesDuration, assertionReport.Duration));
        }
        catch
        {
            if (transaction.Connection is not null)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            }

            throw;
        }
    }

    public async Task<DatabaseAssertionReport> AssertAsync(
        VerifiedDatabaseBaseline baseline,
        string connectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var state = await _stateDetector.DetectAsync(connection, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (state.Status != DatabaseBaselineStatus.Applied)
        {
            throw new InvalidOperationException($"Assertions require an applied baseline; detected {state.Status}.");
        }

        return await _assertions.AssertAsync(connection, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public static string SanitizeTarget(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        var host = string.IsNullOrWhiteSpace(builder.Host) ? "<unspecified-host>" : builder.Host;
        var database = string.IsNullOrWhiteSpace(builder.Database) ? "<unspecified-database>" : builder.Database;
        var username = string.IsNullOrWhiteSpace(builder.Username) ? "<unspecified-user>" : builder.Username;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{host}:{builder.Port}/{database} as {username}");
    }

    private static async Task<TimeSpan> ExecuteStepAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        VerifiedBaselineStep step,
        CancellationToken cancellationToken)
    {
        var sql = await File.ReadAllTextAsync(step.AbsolutePath, cancellationToken).ConfigureAwait(false);
        var stopwatch = Stopwatch.StartNew();
        await using var command = new NpgsqlCommand(sql, connection, transaction)
        {
            CommandTimeout = 180,
        };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();
        return stopwatch.Elapsed;
    }

    private static async Task<T> ExecuteScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is T typed ? typed : (T)Convert.ChangeType(value!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteNonQueryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params NpgsqlParameter[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
