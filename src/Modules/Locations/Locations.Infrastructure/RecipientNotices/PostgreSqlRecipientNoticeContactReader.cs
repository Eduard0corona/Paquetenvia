using Paqueteria.Application.RecipientNotices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Locations.Infrastructure.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.contact.read_at_send</c>; AI-08 NTF-005): the notice copies
/// of a destination location, read by the Worker (<c>paqueteria_worker</c>, NOBYPASSRLS) in one short tenant transaction
/// of the owner organization - BEGIN, <c>set_config('app.current_org_ids', ..., true)</c> with only that organization,
/// <c>SET LOCAL ROLE paqueteria_worker</c> - and committed before the caller decrypts anything. Only the three notice
/// columns are read: never the address, the name, the PII-key phone or e-mail, or the PII key version.
/// </summary>
internal sealed class PostgreSqlRecipientNoticeContactReader(
    [FromKeyedServices(RuntimeDatabaseRole.Worker)] RuntimeDataSource pool) : IRecipientNoticeContactReader
{
    private const int CommandTimeoutSeconds = 10;

    public async Task<RecipientNoticeContactCopies?> ReadAsync(
        Guid ownerOrganizationId,
        Guid locationId,
        CancellationToken cancellationToken)
    {
        if (ownerOrganizationId == Guid.Empty || locationId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await pool.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var context = new NpgsqlCommand(
            """
            SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true);
            SET LOCAL ROLE paqueteria_worker;
            """,
            connection,
            transaction))
        {
            context.Parameters.Add(new NpgsqlParameter<Guid[]>("organization_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid)
            {
                TypedValue = [ownerOrganizationId],
            });
            await context.ExecuteNonQueryAsync(cancellationToken);
        }

        RecipientNoticeContactCopies? copies = null;
        await using (var command = new NpgsqlCommand(
            """
            SELECT notice_key_version,notice_phone_ciphertext,notice_email_ciphertext
            FROM locations.locations
            WHERE id=@location_id AND owner_org_id=@owner_org_id
            """,
            connection,
            transaction)
        {
            CommandTimeout = CommandTimeoutSeconds,
        })
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("location_id", NpgsqlDbType.Uuid) { TypedValue = locationId });
            command.Parameters.Add(new NpgsqlParameter<Guid>("owner_org_id", NpgsqlDbType.Uuid)
            {
                TypedValue = ownerOrganizationId,
            });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                copies = new RecipientNoticeContactCopies(
                    ownerOrganizationId,
                    locationId,
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1),
                    reader.IsDBNull(2) ? null : reader.GetFieldValue<byte[]>(2));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return copies;
    }
}

public static class RecipientNoticeContactDependencyInjection
{
    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-005) Worker composition: the notice contact reader on the Worker pool.
    /// It registers nothing else of the Locations module - no PII key, no sealer, no service and no endpoint.
    /// </summary>
    public static IServiceCollection AddLocationsRecipientNoticeContactReader(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Worker);
        // Replaces the unavailable fallback the Notifications lane registers for hosts without it.
        services.RemoveAll<IRecipientNoticeContactReader>();
        services.AddSingleton<IRecipientNoticeContactReader, PostgreSqlRecipientNoticeContactReader>();
        return services;
    }
}
