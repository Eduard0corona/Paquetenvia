using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Organizations.Application.Auditing;
using Organizations.Infrastructure.Persistence;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Organizations.Infrastructure.Auditing;

public sealed class PostgreSqlPlatformAdminTenantActivationAudit(
    TenantTransactionContext<OrganizationsDbContext> transactionContext)
    : IPlatformAdminTenantActivationAudit
{
    private const string InsertSql =
        """
        INSERT INTO platform.audit_logs
          (id, org_id, actor_id, action, entity_type, entity_id, request_id, payload_redacted, occurred_at)
        VALUES
          (@id, @org_id, @actor_id, 'TENANT_CONTEXT_ACTIVATED', 'ORGANIZATION', @entity_id, @request_id, '{}'::jsonb, @occurred_at);
        """;

    public async Task RecordAsync(
        Guid actorUserId,
        Guid organizationId,
        string? requestId,
        CancellationToken cancellationToken)
    {
        await transactionContext.ExecuteAsync<bool>(
            new TenantDatabaseExecutionContext(actorUserId, [organizationId]),
            async (dbContext, token) =>
            {
                var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
                var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
                await using var command = new NpgsqlCommand(InsertSql, connection, transaction);
                command.Parameters.Add(new NpgsqlParameter<Guid>("id", NpgsqlDbType.Uuid) { TypedValue = Guid.NewGuid() });
                command.Parameters.Add(new NpgsqlParameter<Guid>("org_id", NpgsqlDbType.Uuid) { TypedValue = organizationId });
                command.Parameters.Add(new NpgsqlParameter<Guid>("actor_id", NpgsqlDbType.Uuid) { TypedValue = actorUserId });
                command.Parameters.Add(new NpgsqlParameter<Guid>("entity_id", NpgsqlDbType.Uuid) { TypedValue = organizationId });
                command.Parameters.Add(new NpgsqlParameter<string?>("request_id", NpgsqlDbType.Text) { TypedValue = requestId });
                command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("occurred_at", NpgsqlDbType.TimestampTz)
                {
                    TypedValue = DateTimeOffset.UtcNow,
                });
                await command.ExecuteNonQueryAsync(token);
                return true;
            },
            cancellationToken);
    }
}
