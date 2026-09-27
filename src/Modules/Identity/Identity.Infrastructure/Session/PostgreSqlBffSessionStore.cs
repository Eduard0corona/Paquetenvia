using System.Globalization;
using Identity.Application.Bootstrap;
using Identity.Application.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Identity.Infrastructure.Session;

/// <summary>
/// BFF-SESSION-TABLE-SHAPE adapter. Each call is one short transaction that assumes
/// <c>paqueteria_app</c> (the API login is NOINHERIT) and invokes one <c>security.*_bff_session</c>
/// function; no tenant context is set because the store is pre-tenant. Only hashes, protected tickets
/// and counts cross the boundary, and the log never carries a key, subject, sid or ticket.
/// </summary>
public sealed partial class PostgreSqlBffSessionStore(
    NpgsqlDataSource dataSource,
    IOptions<IdentityBootstrapOptions> options,
    ILogger<PostgreSqlBffSessionStore> logger) : IBffSessionStore
{
    private const string RuntimeRole = "paqueteria_app";

    public Task CreateAsync(BffSessionRecord session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHash(session.KeyHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(session.Subject);
        ArgumentNullException.ThrowIfNull(session.ProtectedTicket);
        return ExecuteAsync(
            "SELECT security.create_bff_session(@key_hash, @subject, @sid, @ticket, @expires_at);",
            command =>
            {
                command.Parameters.Add(Bytes("key_hash", session.KeyHash));
                command.Parameters.Add(Text("subject", session.Subject));
                command.Parameters.Add(new NpgsqlParameter("sid", NpgsqlDbType.Text)
                {
                    Value = (object?)session.AuthCenterSessionId ?? DBNull.Value,
                });
                command.Parameters.Add(Bytes("ticket", session.ProtectedTicket));
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("expires_at", NpgsqlDbType.TimestampTz)
                {
                    TypedValue = session.ExpiresAt.ToUniversalTime(),
                });
            },
            static _ => 0,
            "create",
            cancellationToken);
    }

    public Task<byte[]?> ResolveAsync(byte[] keyHash, CancellationToken cancellationToken)
    {
        RequireHash(keyHash);
        return ExecuteAsync(
            "SELECT security.resolve_bff_session(@key_hash);",
            command => command.Parameters.Add(Bytes("key_hash", keyHash)),
            static value => value as byte[],
            "resolve",
            cancellationToken);
    }

    public Task<int> RevokeAsync(byte[] keyHash, CancellationToken cancellationToken)
    {
        RequireHash(keyHash);
        return ExecuteAsync(
            "SELECT security.revoke_bff_session(@key_hash);",
            command => command.Parameters.Add(Bytes("key_hash", keyHash)),
            Count,
            "revoke",
            cancellationToken);
    }

    public Task<int> RevokeByAuthCenterSessionAsync(string authCenterSessionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(authCenterSessionId);
        return ExecuteAsync(
            "SELECT security.revoke_bff_session(@sid);",
            command => command.Parameters.Add(Text("sid", authCenterSessionId)),
            Count,
            "revoke_sid",
            cancellationToken);
    }

    public Task<int> RevokeBySubjectAsync(string subject, DateTimeOffset issuedBefore, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        return ExecuteAsync(
            "SELECT security.revoke_bff_session(@subject, @issued_before);",
            command =>
            {
                command.Parameters.Add(Text("subject", subject));
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("issued_before", NpgsqlDbType.TimestampTz)
                {
                    TypedValue = issuedBefore.ToUniversalTime(),
                });
            },
            Count,
            "revoke_subject",
            cancellationToken);
    }

    private async Task<T> ExecuteAsync<T>(
        string sql,
        Action<NpgsqlCommand> bind,
        Func<object?, T> read,
        string operation,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using (var role = new NpgsqlCommand($"SET LOCAL ROLE {RuntimeRole};", connection, transaction)
            {
                CommandTimeout = options.Value.CommandTimeoutSeconds,
            })
            {
                await role.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = new NpgsqlCommand(sql, connection, transaction)
            {
                CommandTimeout = options.Value.CommandTimeoutSeconds,
            };
            bind(command);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return read(value is DBNull ? null : value);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            LogFailed(logger, operation, exception is PostgresException postgres ? postgres.SqlState : "transport");
            throw new IdentityContextInfrastructureException("The BFF session store is unavailable.", exception);
        }
    }

    private static int Count(object? value) =>
        Convert.ToInt32(value ?? throw new InvalidOperationException("BFF session revocation returned no count."),
            CultureInfo.InvariantCulture);

    private static void RequireHash(byte[] keyHash)
    {
        ArgumentNullException.ThrowIfNull(keyHash);
        if (keyHash.Length != IBffSessionStore.KeyHashLength)
        {
            throw new ArgumentException("A BFF session key hash is exactly 32 bytes.", nameof(keyHash));
        }
    }

    private static NpgsqlParameter<byte[]> Bytes(string name, byte[] value) =>
        new(name, NpgsqlDbType.Bytea) { TypedValue = value };

    private static NpgsqlParameter<string> Text(string name, string value) =>
        new(name, NpgsqlDbType.Text) { TypedValue = value };

    [LoggerMessage(EventId = 4106, Level = LogLevel.Error,
        Message = "BFF session store {Operation} failed ({ErrorClass}).")]
    private static partial void LogFailed(ILogger logger, string operation, string errorClass);
}
