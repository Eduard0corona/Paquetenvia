using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NpgsqlTypes;
using Organizations.Application.Auditing;
using Organizations.Infrastructure.Persistence;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Organizations.Infrastructure.Auditing;

public sealed class PostgreSqlPlatformAdminTenantActivationAudit(
    TenantTransactionContext<OrganizationsDbContext> transactionContext,
    IAppendOnlyAuditWriter auditWriter,
    IClock clock)
    : IPlatformAdminTenantActivationAudit
{
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
                await auditWriter.WriteAsync(
                    connection,
                    transaction,
                    new AuditEntry(
                        Guid.NewGuid(),
                        organizationId,
                        actorUserId,
                        "TENANT_CONTEXT_ACTIVATED",
                        "ORGANIZATION",
                        organizationId,
                        requestId,
                        RedactedAuditPayload.Empty,
                        clock.UtcNow),
                    token);
                return true;
            },
            cancellationToken);
    }
}
