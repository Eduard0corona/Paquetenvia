using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Paqueteria.Infrastructure.Database.Baseline;

public sealed class DatabaseBaselineDeployer(
    DatabaseBaselineStateDetector? stateDetector = null,
    DatabaseBaselineAssertions? assertions = null,
    Action<string>? stageObserver = null)
{
    private readonly DatabaseBaselineStateDetector _stateDetector = stateDetector ?? new DatabaseBaselineStateDetector();
    private readonly DatabaseBaselineAssertions _assertions = assertions ?? new DatabaseBaselineAssertions();
    // Sanitized stage trace (E-002 v0.8 §36 "phase"); lets regression coverage prove the §18 order.
    private readonly Action<string>? _stageObserver = stageObserver;

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
        CancellationToken cancellationToken = default,
        AzureOwnershipBridgeSelection? ownershipBridge = null)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ownershipBridge?.AssertAllowed();
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

            // E-002 v0.8 §8 / v0.13 Amendment 3: the managed-service preflight runs before any mutation.
            if (ownershipBridge is not null)
            {
                _stageObserver?.Invoke("e002-platform-preflight");
                await AzureOwnershipBridge.PlatformPreflightAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
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

            // E-002 v0.8 §14/§18: canonical AI-06 executes first; the bridge prelude follows it.
            _stageObserver?.Invoke("ai06");
            var schemaDuration = await ExecuteStepAsync(connection, transaction, baseline.Steps[0], cancellationToken).ConfigureAwait(false);
            E002AclSnapshot? databaseAclBefore = null;
            if (ownershipBridge is not null)
            {
                _stageObserver?.Invoke("e002-prelude");
                databaseAclBefore = await AzureOwnershipBridge.PreludeAsync(
                    connection, transaction, _stageObserver, cancellationToken).ConfigureAwait(false);
            }

            _stageObserver?.Invoke("ai18");
            var rolesDuration = await ExecuteStepAsync(connection, transaction, baseline.Steps[1], cancellationToken).ConfigureAwait(false);
            if (databaseAclBefore is not null)
            {
                _stageObserver?.Invoke("e002-cleanup");
                await AzureOwnershipBridge.CleanupAndAssertAsync(
                    connection, transaction, databaseAclBefore, _stageObserver, cancellationToken).ConfigureAwait(false);
            }

            _stageObserver?.Invoke("canonical-assertions");
            var assertionReport = await _assertions.AssertAsync(connection, transaction, cancellationToken).ConfigureAwait(false);
            if (ownershipBridge is not null)
            {
                await new E002SemanticAssertions().AssertAsync(
                    connection, E002NotificationState.Pending, transaction, cancellationToken).ConfigureAwait(false);
            }

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
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception)
        {
            throw new InvalidOperationException(
                $"Baseline step {step.Id} failed (SQLSTATE {exception.SqlState}, position {exception.Position}, internal position {exception.InternalPosition}, context {exception.Where ?? "<none>"}).",
                exception);
        }
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
