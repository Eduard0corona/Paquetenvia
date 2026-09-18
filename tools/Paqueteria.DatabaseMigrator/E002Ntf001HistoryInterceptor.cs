using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Paqueteria.Infrastructure.Database.Baseline;

internal sealed class E002Ntf001HistoryInterceptor : DbCommandInterceptor
{
    internal bool TargetAssertionExecuted { get; private set; }
    internal (string SqlState, string Message)? FirstPostgresFailure { get; private set; }

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (FirstPostgresFailure is null && eventData.Exception is PostgresException postgres)
        {
            FirstPostgresFailure = (postgres.SqlState, postgres.MessageText);
        }

        return base.CommandFailedAsync(command, eventData, cancellationToken);
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (IsNtf001HistoryInsert(command))
        {
            if (TargetAssertionExecuted || command.Connection is not NpgsqlConnection connection ||
                command.Transaction is not NpgsqlTransaction transaction)
            {
                throw new InvalidOperationException(
                    "E002_ROUTINE_MAP_TRANSITION_CONTEXT_INVALID; STOP_FOR_CONTRACT_REVIEW");
            }

            await new E002SemanticAssertions().AssertNtf001TargetAsync(
                connection, transaction, cancellationToken).ConfigureAwait(false);
            TargetAssertionExecuted = true;
        }

        return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool IsNtf001HistoryInsert(DbCommand command)
    {
        var sql = command.CommandText.TrimStart();
        if (!sql.StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase) ||
            !sql.Contains("__ef_migrations_history_notifications", StringComparison.Ordinal))
        {
            return false;
        }

        return sql.Contains(E002NotificationStateReader.Ntf001MigrationId, StringComparison.Ordinal) ||
               command.Parameters.Cast<DbParameter>().Any(parameter =>
                   string.Equals(Convert.ToString(parameter.Value),
                       E002NotificationStateReader.Ntf001MigrationId, StringComparison.Ordinal));
    }
}
