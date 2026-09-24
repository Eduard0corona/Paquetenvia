using Npgsql;
using NpgsqlTypes;
using Finance.Application;

namespace Finance.Infrastructure.Persistence;

internal static class FinanceSql
{
    internal static NpgsqlCommand Create(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        int commandTimeoutSeconds) => new(sql, connection, transaction)
        {
            CommandTimeout = commandTimeoutSeconds,
        };

    internal static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };

    internal static void RequireOne(int affected, string message)
    {
        if (affected != 1)
        {
            throw new FinanceUnavailableException(message);
        }
    }

    internal static FinanceConflictException Conflict(
        FinanceConflictCode code,
        Exception? exception = null) => new(code, exception);
}
