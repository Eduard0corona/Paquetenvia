using System.Text.Json;
using Custody.Application.ProofUploads;
using Custody.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Custody.Infrastructure.Proofs;

public sealed class PostgreSqlProofDownloadService(
    TenantTransactionContext<CustodyDbContext> transactionContext,
    IProofObjectStorage storage,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor redactor,
    IClock clock) : IProofDownloadService
{
    public async Task<ProofDownloadResult> GetInternalDownloadAsync(
        GetProofDownloadCommand command,
        CancellationToken cancellationToken)
    {
        if (!storage.IsEnabled)
        {
            throw new ProofStorageUnavailableException();
        }

        var proofEvidence = await transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
            async (dbContext, token) =>
            {
                var (connection, transaction) = CustodySql.Database(dbContext);
                await using var proof = new NpgsqlCommand(
                    """
                    SELECT order_id,object_key
                    FROM custody.proofs
                    WHERE id=@proof
                    """,
                    connection,
                    transaction);
                proof.Parameters.Add(CustodySql.P("proof", NpgsqlDbType.Uuid, command.ProofId));
                await using var reader = await proof.ExecuteReaderAsync(token);
                if (!await reader.ReadAsync(token))
                {
                    throw new ProofNotFoundException();
                }

                var orderId = reader.GetGuid(0);
                var result = reader.GetString(1);
                await reader.DisposeAsync();
                _ = await CustodySql.ReadAuthorizedOrderAsync(
                    dbContext,
                    orderId,
                    command.ActorId,
                    command.OrganizationId,
                    command.MfaSatisfied,
                    token) ?? throw new ProofNotFoundException();
                return new DownloadEvidence(orderId, result);
            },
            cancellationToken);

        var url = await storage.CreateInternalDownloadUrlAsync(
            proofEvidence.ObjectKey,
            cancellationToken);
        await transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
            async (dbContext, token) =>
            {
                _ = await CustodySql.ReadAuthorizedOrderAsync(
                    dbContext,
                    proofEvidence.OrderId,
                    command.ActorId,
                    command.OrganizationId,
                    command.MfaSatisfied,
                    token) ?? throw new ProofNotFoundException();
                using var payload = JsonDocument.Parse("{}");
                var (connection, transaction) = CustodySql.Database(dbContext);
                await auditWriter.WriteAsync(
                    connection,
                    transaction,
                    new AuditEntry(
                        Guid.NewGuid(),
                        command.OrganizationId,
                        command.ActorId,
                        "custody.proof.download_url_issued",
                        "proof",
                        command.ProofId,
                        command.RequestId,
                        redactor.Redact(payload.RootElement),
                        clock.UtcNow),
                    token);
                return true;
            },
            cancellationToken);
        return new ProofDownloadResult(command.ProofId, url);
    }

    private sealed record DownloadEvidence(Guid OrderId, string ObjectKey);
}
