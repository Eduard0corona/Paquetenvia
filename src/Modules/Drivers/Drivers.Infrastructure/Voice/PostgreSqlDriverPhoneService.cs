using Drivers.Application.Voice;
using Drivers.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Tenancy;
using Paqueteria.Application.Voice;
using Paqueteria.Infrastructure.Tenancy;
using static Drivers.Infrastructure.Voice.DriverVoiceSql;

namespace Drivers.Infrastructure.Voice;

/// <summary>
/// VOICE-001: the driver's own mobile number for the masked bridge. Reading and removing work whatever the mode (a
/// driver can always see that a number is stored and erase it); registering requires an enabled bridge. Protection
/// (ADP-001 Key Vault envelope in live mode) runs between two tenant transactions, so no Key Vault call is made while
/// a database transaction is held. The number is never returned, logged or audited: the audit row records only that
/// a number was registered or removed and the consent version.
/// </summary>
public sealed class PostgreSqlDriverPhoneService(
    TenantTransactionContext<DriversDbContext> transactionContext,
    IVoiceBridgeStatus voiceStatus,
    IDriverPhoneProtector protector,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IClock clock,
    ILogger<PostgreSqlDriverPhoneService> logger) : IDriverPhoneService
{
    public const string RegisteredAction = "DRIVER_PHONE_REGISTERED";
    public const string RemovedAction = "DRIVER_PHONE_REMOVED";
    public const string EntityType = "DriverProfile";

    public Task<DriverPhoneStatusResult> GetAsync(Guid actorId, Guid organizationId, CancellationToken cancellationToken) =>
        RunAsync(actorId, organizationId, (connection, transaction, driverId, token) =>
            ReadStatusAsync(connection, transaction, driverId, token), cancellationToken);

    public async Task<DriverPhoneStatusResult> RegisterAsync(
        RegisterDriverPhoneCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (voiceStatus.Mode == VoiceBridgeMode.Disabled)
        {
            // Data minimization: no phone is collected while the bridge cannot use it.
            throw new DriverPhoneUnavailableException();
        }

        if (!DriverPhonePolicy.TryNormalize(command.PhoneDigits, out var digits) ||
            !string.Equals(command.ConsentVersion, DriverPhoneConsent.CurrentVersion, StringComparison.Ordinal))
        {
            throw new ArgumentException("The driver phone command is invalid.", nameof(command));
        }

        // First transaction: who is the driver (the envelope binds the ciphertext to this row).
        var driverId = await RunAsync(
            command.ActorId,
            command.OrganizationId,
            (_, _, driver, _) => Task.FromResult(driver),
            cancellationToken).ConfigureAwait(false);

        var protectedPhone = await protector.ProtectAsync(command.OrganizationId, driverId, digits, cancellationToken)
            .ConfigureAwait(false);
        var now = UtcMicrosecondPrecision.Normalize(clock.UtcNow);

        return await RunAsync(command.ActorId, command.OrganizationId, async (connection, transaction, driver, token) =>
        {
            if (driver != driverId)
            {
                throw new DriverPhoneForbiddenException();
            }

            bool replaced;
            await using (var read = Command(
                connection,
                transaction,
                "SELECT phone_ciphertext IS NOT NULL FROM drivers.driver_profiles WHERE id=@driver FOR UPDATE"))
            {
                read.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
                replaced = await read.ExecuteScalarAsync(token).ConfigureAwait(false) is true;
            }

            await using (var update = Command(
                connection,
                transaction,
                """
                UPDATE drivers.driver_profiles
                SET phone_ciphertext=@ciphertext,phone_pii_key_version=@key_version,
                    phone_consent_version=@consent_version,phone_consented_at=@now
                WHERE id=@driver AND org_id=@organization
                """))
            {
                update.Parameters.Add(P("ciphertext", NpgsqlDbType.Bytea, protectedPhone.Ciphertext));
                update.Parameters.Add(P("key_version", NpgsqlDbType.Text, protectedPhone.KeyVersion));
                update.Parameters.Add(P("consent_version", NpgsqlDbType.Text, DriverPhoneConsent.CurrentVersion));
                update.Parameters.Add(P("now", NpgsqlDbType.TimestampTz, now));
                update.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
                update.Parameters.Add(P("organization", NpgsqlDbType.Uuid, command.OrganizationId));
                if (await update.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1)
                {
                    throw new DriverPhoneUnavailableException();
                }
            }

            await WriteAuditAsync(
                auditWriter,
                auditRedactor,
                connection,
                transaction,
                command.OrganizationId,
                command.ActorId,
                RegisteredAction,
                EntityType,
                driverId,
                command.RequestId,
                new { driver_id = driverId, consent_version = DriverPhoneConsent.CurrentVersion, replaced },
                now,
                token).ConfigureAwait(false);
            return await ReadStatusAsync(connection, transaction, driverId, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<DriverPhoneStatusResult> RemoveAsync(
        Guid actorId,
        Guid organizationId,
        string? requestId,
        CancellationToken cancellationToken) =>
        RunAsync(actorId, organizationId, async (connection, transaction, driverId, token) =>
        {
            int removed;
            await using (var update = Command(
                connection,
                transaction,
                """
                UPDATE drivers.driver_profiles
                SET phone_ciphertext=NULL,phone_pii_key_version=NULL,phone_consent_version=NULL,phone_consented_at=NULL
                WHERE id=@driver AND org_id=@organization AND phone_ciphertext IS NOT NULL
                """))
            {
                update.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
                update.Parameters.Add(P("organization", NpgsqlDbType.Uuid, organizationId));
                removed = await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            // Removing a number that is not there is a no-op and writes nothing.
            if (removed == 1)
            {
                await WriteAuditAsync(
                    auditWriter,
                    auditRedactor,
                    connection,
                    transaction,
                    organizationId,
                    actorId,
                    RemovedAction,
                    EntityType,
                    driverId,
                    requestId,
                    new { driver_id = driverId },
                    UtcMicrosecondPrecision.Normalize(clock.UtcNow),
                    token).ConfigureAwait(false);
            }

            return await ReadStatusAsync(connection, transaction, driverId, token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task<T> RunAsync<T>(
        Guid actorId,
        Guid organizationId,
        Func<NpgsqlConnection, NpgsqlTransaction, Guid, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || organizationId == Guid.Empty)
        {
            throw new DriverPhoneForbiddenException();
        }

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(actorId, [organizationId]),
                async (dbContext, token) =>
                {
                    var (connection, transaction) = Database(dbContext);
                    var driverId = await ResolveActiveDriverAsync(connection, transaction, actorId, organizationId, token)
                        .ConfigureAwait(false) ?? throw new DriverPhoneForbiddenException();
                    return await operation(connection, transaction, driverId, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsStoreFailure(exception))
        {
            logger.LogWarning("Driver phone store unavailable with error {ErrorType}.", exception.GetType().Name);
            throw new DriverPhoneUnavailableException(exception);
        }
    }

    private async Task<DriverPhoneStatusResult> ReadStatusAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid driverId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            """
            SELECT phone_ciphertext IS NOT NULL,phone_consent_version,phone_consented_at
            FROM drivers.driver_profiles WHERE id=@driver
            """);
        command.Parameters.Add(P("driver", NpgsqlDbType.Uuid, driverId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new DriverPhoneForbiddenException();
        }

        var registered = reader.GetBoolean(0);
        return new DriverPhoneStatusResult(
            voiceStatus.Mode != VoiceBridgeMode.Disabled,
            registered,
            registered ? reader.GetString(1) : null,
            registered ? reader.GetFieldValue<DateTimeOffset>(2) : null);
    }
}
