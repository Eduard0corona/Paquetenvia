using System.Data.Common;
using System.Text.Json;
using Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application.Auditing;

namespace Drivers.Infrastructure.Voice;

/// <summary>SQL shared by the VOICE-001 services. Every statement runs inside the caller's tenant transaction.</summary>
internal static class DriverVoiceSql
{
    public const int CommandTimeoutSeconds = 30;

    private static readonly JsonSerializerOptions AuditJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>
    /// The actor's own driver profile in the selected organization, under the rules of the DRV-001 stops query: an
    /// ACTIVE OWN or EXTERNAL profile of an ACTIVE user who holds an ACTIVE DRIVER membership there.
    /// </summary>
    public static async Task<Guid?> ResolveActiveDriverAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT p.id
            FROM drivers.driver_profiles p
            JOIN identity.users u ON u.id=p.user_id AND u.status='ACTIVE'
            WHERE p.user_id=@actor AND p.org_id=@organization
              AND p.driver_type IN ('OWN','EXTERNAL') AND p.status='ACTIVE'
              AND EXISTS (
                SELECT 1 FROM organizations.organization_memberships m
                WHERE m.user_id=p.user_id AND m.organization_id=p.org_id
                  AND m.role='DRIVER' AND m.status='ACTIVE'
              )
            """;
        await using var command = Command(connection, transaction, sql);
        command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is Guid id ? id : null;
    }

    public static async Task WriteAuditAsync(
        IAppendOnlyAuditWriter writer,
        IAuditPayloadRedactor redactor,
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid organizationId,
        Guid? actorId,
        string action,
        string entityType,
        Guid entityId,
        string? requestId,
        object payload,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var element = JsonSerializer.SerializeToElement(payload, AuditJsonOptions);
        await writer.WriteAsync(
                connection,
                transaction,
                new AuditEntry(
                    Guid.NewGuid(),
                    organizationId,
                    actorId,
                    action,
                    entityType,
                    entityId,
                    requestId,
                    redactor.Redact(element),
                    occurredAt),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A store failure (connection, timeout, exhausted retries or a refused write): the caller answers 503.</summary>
    public static bool IsStoreFailure(Exception exception) =>
        exception is DbException or TimeoutException or RetryLimitExceededException or InvalidOperationException;

    public static (NpgsqlConnection Connection, NpgsqlTransaction Transaction) Database(DriversDbContext dbContext) =>
        ((NpgsqlConnection)dbContext.Database.GetDbConnection(),
         (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction());

    public static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql) =>
        new(sql, connection, transaction) { CommandTimeout = CommandTimeoutSeconds };

    public static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) =>
        new(name, type) { Value = value ?? DBNull.Value };
}
