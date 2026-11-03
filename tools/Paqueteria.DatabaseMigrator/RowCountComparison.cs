using System.Data;
using Npgsql;

/// <summary>
/// REL-001 restore vigente (Z9, Z18 "Exacto, en el job (Recommended)"): during the ENV-001 restore drill, <c>assert</c>
/// on the restored server also reads the server in service and compares four counts, organizations, orders, order
/// events and the PICKUP_PHOTO and DELIVERY_PHOTO proofs. Each side is read in one explicit READ ONLY transaction
/// (REPEATABLE READ, so the four counts are one snapshot). Any difference fails the command. Only the counts are
/// printed, never a value, a host or a login. Both sides must be read by a login that sees every row (superuser or
/// BYPASSRLS, like the deployment login of the verify job); otherwise row-level security could hide rows on both sides
/// and two empty answers would match.
/// </summary>
internal static class RowCountComparison
{
    /// <summary>The exit code of a comparison that did not match or could not be made.</summary>
    internal const int MismatchExitCode = 13;

    internal static readonly string[] Names = ["organizations", "orders", "order_events", "proof_photos"];

    private const string CountsSql =
        """
        SELECT
          (SELECT rolsuper OR rolbypassrls FROM pg_catalog.pg_roles WHERE rolname = current_user),
          (SELECT count(*) FROM organizations.organizations),
          (SELECT count(*) FROM orders.orders),
          (SELECT count(*) FROM orders.order_events),
          (SELECT count(*) FROM custody.proofs WHERE proof_type IN ('PICKUP_PHOTO','DELIVERY_PHOTO'))
        """;

    internal static async Task<IReadOnlyList<long>> CompareAsync(
        string sourceConnectionString,
        string restoredConnectionString,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (SameDatabase(sourceConnectionString, restoredConnectionString))
        {
            throw new RowCountComparisonException(
                "ROW_COUNTS_SAME_DATABASE: the source and the restored connection name the same database.");
        }

        var source = await CountAsync(sourceConnectionString, "source", cancellationToken).ConfigureAwait(false);
        var restored = await CountAsync(restoredConnectionString, "restored", cancellationToken).ConfigureAwait(false);
        if (!source.SequenceEqual(restored))
        {
            output.WriteLine($"ROW_COUNTS_DIFFER source {Format(source)} restored {Format(restored)}");
            throw new RowCountComparisonException(
                "ROW_COUNTS_DIFFER: the restored server does not hold the counts of the server in service.");
        }

        output.WriteLine($"ROW_COUNTS_MATCHED {Format(restored)}");
        return restored;
    }

    internal static string Format(IReadOnlyList<long> counts) =>
        string.Join(' ', Names.Select((name, index) => $"{name}={counts[index]}"));

    private static bool SameDatabase(string left, string right)
    {
        var a = new NpgsqlConnectionStringBuilder(left);
        var b = new NpgsqlConnectionStringBuilder(right);
        return string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase) &&
               a.Port == b.Port &&
               string.Equals(a.Database, b.Database, StringComparison.Ordinal);
    }

    private static async Task<IReadOnlyList<long>> CountAsync(
        string connectionString,
        string side,
        CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken)
            .ConfigureAwait(false);
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
        {
            await readOnly.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var command = new NpgsqlCommand(CountsSql, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new RowCountComparisonException($"ROW_COUNTS_UNREADABLE: the {side} counts could not be read.");
        }

        if (reader.IsDBNull(0) || !reader.GetBoolean(0))
        {
            throw new RowCountComparisonException(
                $"ROW_COUNTS_LOGIN_CANNOT_SEE_ALL_ROWS: the {side} login is neither superuser nor BYPASSRLS.");
        }

        var counts = Enumerable.Range(1, Names.Length).Select(reader.GetInt64).ToArray();
        await reader.DisposeAsync().ConfigureAwait(false);
        await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        return counts;
    }
}

/// <summary>A restore drill row-count comparison that did not match or could not be made. Carries no value.</summary>
internal sealed class RowCountComparisonException(string message) : Exception(message);
