using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Drivers.Application.Locations;
using Drivers.Domain.Location;
using Drivers.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;

namespace Drivers.Infrastructure.Locations;

public sealed class PostgreSqlDriverLocationIngestionService(
    TenantTransactionContext<DriversDbContext> transactionContext,
    IDriverLocationAuthorizer authorizer,
    IDriverLocationFailureInjector failureInjector,
    IClock clock,
    IOptions<DriversOptions> options,
    ILogger<PostgreSqlDriverLocationIngestionService> logger) : IDriverLocationIngestionService
{
    internal const string LocationOutboxTopic = "drivers.location-updated";
    internal const string PayloadSchemaVersion = "driver-location-updated-v1";
    private static readonly Meter Meter = new("Paqueteria.Drivers.Location", "1.0.0");
    private static readonly Counter<long> Batches = Meter.CreateCounter<long>("drivers.location.batches");
    private static readonly Counter<long> Positions = Meter.CreateCounter<long>("drivers.location.positions");
    private static readonly Counter<long> Publications = Meter.CreateCounter<long>("drivers.location.publications");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("drivers.location.persistence_failures");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>(
        "drivers.location.batch_duration", "ms");

    public async Task<DriverLocationBatchResult> PublishAsync(
        PublishDriverLocationBatchCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.ActorId == Guid.Empty ||
            command.OrganizationId == Guid.Empty ||
            command.Positions is not { Count: >= 1 and <= 20 })
        {
            throw new ArgumentException("The driver location batch is structurally invalid.", nameof(command));
        }

        var started = Stopwatch.GetTimestamp();
        Batches.Add(1, new KeyValuePair<string, object?>("provider", "postgresql"));
        try
        {
            var result = await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                (dbContext, token) => ExecuteBatchAsync(dbContext, command, token),
                cancellationToken);
            RecordResult(result);
            logger.LogInformation("driver_location_batch_completed");
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("outcome", "cancelled"));
            logger.LogWarning("driver_location_batch_failed");
            throw;
        }
        catch (DriverLocationForbiddenException)
        {
            throw;
        }
        catch (DriverLocationNotFoundException)
        {
            throw;
        }
        catch (DbException exception)
        {
            Failures.Add(1, new KeyValuePair<string, object?>("outcome", "database"));
            logger.LogWarning("driver_location_batch_failed");
            throw new DriverLocationInfrastructureException(exception);
        }
        finally
        {
            Duration.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", "postgresql"));
        }
    }

    private async Task<DriverLocationBatchResult> ExecuteBatchAsync(
        DriversDbContext dbContext,
        PublishDriverLocationBatchCommand command,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();

        var authorization = await ReadAuthorizationAsync(
            connection,
            transaction,
            command.ActorId,
            command.OrganizationId,
            cancellationToken);
        if (!authorizer.IsAuthorized(authorization))
        {
            throw new DriverLocationForbiddenException();
        }

        await failureInjector.OnStageAsync(
            DriverLocationTransactionStage.AuthorizationCompleted,
            cancellationToken);

        var driver = await ResolveDriverAsync(
            connection,
            transaction,
            command.ActorId,
            command.OrganizationId,
            cancellationToken) ?? throw new DriverLocationNotFoundException();

        await AcquireDriverLockAsync(connection, transaction, driver.Id, cancellationToken);
        await failureInjector.OnStageAsync(DriverLocationTransactionStage.DriverLocked, cancellationToken);

        var validations = command.Positions.Select(value =>
            DriverLocationValidationPolicy.Validate(new DriverLocationInput(
                value.ClientEventId,
                value.Latitude,
                value.Longitude,
                value.AccuracyMeters,
                value.CapturedAt,
                value.HeadingDegrees,
                value.SpeedMetersPerSecond))).ToArray();
        var validEventIds = validations
            .Where(value => value.Location is not null)
            .Select(value => value.Location!.ClientEventId)
            .Distinct()
            .ToArray();
        var duplicates = await ReadDuplicatesAsync(
            connection,
            transaction,
            driver.Id,
            validEventIds,
            cancellationToken);
        await failureInjector.OnStageAsync(DriverLocationTransactionStage.DuplicatesRead, cancellationToken);

        var items = new DriverLocationItemResult[command.Positions.Count];
        var newPositions = new List<PendingPosition>();
        var known = new Dictionary<Guid, Guid>(duplicates);
        for (var index = 0; index < validations.Length; index++)
        {
            var validation = validations[index];
            var suppliedEventId = command.Positions[index].ClientEventId;
            if (suppliedEventId != Guid.Empty &&
                known.TryGetValue(suppliedEventId, out var repeatedPositionId))
            {
                items[index] = new DriverLocationItemResult(
                    suppliedEventId,
                    repeatedPositionId,
                    DriverLocationItemStatus.Duplicate,
                    null);
                continue;
            }

            if (validation.Location is not { } location)
            {
                items[index] = new DriverLocationItemResult(
                    command.Positions[index].ClientEventId,
                    null,
                    DriverLocationItemStatus.Rejected,
                    validation.RejectionCode);
                continue;
            }

            location = location with
            {
                CapturedAt = UtcMicrosecondPrecision.Normalize(location.CapturedAt),
            };
            var positionId = Guid.NewGuid();
            known.Add(location.ClientEventId, positionId);
            items[index] = new DriverLocationItemResult(
                location.ClientEventId,
                positionId,
                DriverLocationItemStatus.Accepted,
                null);
            newPositions.Add(new PendingPosition(index, positionId, location));
        }

        var baseline = await ReadLatestPublishedPositionAsync(
            connection,
            transaction,
            driver.Id,
            cancellationToken);
        var publicationPolicy = options.Value.LocationTelemetry.ToPolicy();
        foreach (var pending in newPositions
                     .OrderBy(value => value.Location.CapturedAt)
                     .ThenBy(value => value.OriginalIndex)
                     .ThenBy(value => value.Location.ClientEventId))
        {
            pending.PublishRealtime = DriverLocationPublicationPolicy.ShouldPublish(
                baseline,
                pending.Location,
                publicationPolicy);
            if (pending.PublishRealtime)
            {
                baseline = new PublishedLocationBaseline(
                    pending.PositionId,
                    pending.Location.Latitude,
                    pending.Location.Longitude,
                    pending.Location.CapturedAt);
            }
        }

        var receivedAt = UtcMicrosecondPrecision.Normalize(clock.UtcNow);
        foreach (var pending in newPositions.OrderBy(value => value.OriginalIndex))
        {
            await InsertPositionAsync(
                connection,
                transaction,
                driver,
                pending,
                receivedAt,
                cancellationToken);
            await failureInjector.OnStageAsync(
                DriverLocationTransactionStage.PositionInserted,
                cancellationToken);
            if (pending.PublishRealtime)
            {
                await InsertLocationOutboxAsync(
                    connection,
                    transaction,
                    driver,
                    pending,
                    receivedAt,
                    cancellationToken);
                await failureInjector.OnStageAsync(
                    DriverLocationTransactionStage.LocationOutboxInserted,
                    cancellationToken);
            }
        }

        await failureInjector.OnStageAsync(DriverLocationTransactionStage.BeforeCommit, cancellationToken);
        return new DriverLocationBatchResult(items, newPositions.Count(value => value.PublishRealtime));
    }

    private static async Task<DriverLocationAuthorizationSnapshot> ReadAuthorizationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT u.status,
                   m.status,
                   m.role
            FROM identity.users u
            LEFT JOIN organizations.organization_memberships m
              ON m.user_id=u.id AND m.organization_id=@organization_id
            WHERE u.id=@actor_id;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddUuid(command, "actor_id", actorId);
        AddUuid(command, "organization_id", organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new(false, false, null);
        }

        return new(
            string.Equals(reader.GetString(0), "ACTIVE", StringComparison.Ordinal),
            !reader.IsDBNull(1) && string.Equals(reader.GetString(1), "ACTIVE", StringComparison.Ordinal),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    private static async Task<DriverIdentity?> ResolveDriverAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid actorId,
        Guid organizationId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT id,org_id,home_city_id
            FROM drivers.driver_profiles
            WHERE user_id=@actor_id
              AND org_id=@organization_id
              AND driver_type='OWN'
              AND status='ACTIVE';
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddUuid(command, "actor_id", actorId);
        AddUuid(command, "organization_id", organizationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new DriverIdentity(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2))
            : null;
    }

    private static async Task AcquireDriverLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid driverId,
        CancellationToken cancellationToken)
    {
        var material = Encoding.UTF8.GetBytes($"DRV-003:{driverId:D}");
        var lockKey = BitConverter.ToInt64(SHA256.HashData(material), 0);
        await using var command = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(@lock_key);",
            connection,
            transaction);
        command.Parameters.Add(new NpgsqlParameter<long>("lock_key", NpgsqlDbType.Bigint)
        {
            TypedValue = lockKey,
        });
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static async Task<Dictionary<Guid, Guid>> ReadDuplicatesAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid driverId,
        Guid[] clientEventIds,
        CancellationToken cancellationToken)
    {
        if (clientEventIds.Length == 0)
        {
            return [];
        }

        const string sql =
            """
            SELECT client_event_id,id
            FROM drivers.driver_positions
            WHERE driver_id=@driver_id
              AND client_event_id=ANY(@client_event_ids);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddUuid(command, "driver_id", driverId);
        command.Parameters.Add(new NpgsqlParameter<Guid[]>(
            "client_event_ids",
            NpgsqlDbType.Array | NpgsqlDbType.Uuid)
        {
            TypedValue = clientEventIds,
        });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new Dictionary<Guid, Guid>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetGuid(0), reader.GetGuid(1));
        }

        return result;
    }

    private static async Task<PublishedLocationBaseline?> ReadLatestPublishedPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid driverId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT id,ST_Y(point),ST_X(point),captured_at
            FROM drivers.driver_positions
            WHERE driver_id=@driver_id AND publish_realtime=true
            ORDER BY captured_at DESC,received_at DESC,id DESC
            LIMIT 1;
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddUuid(command, "driver_id", driverId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new PublishedLocationBaseline(
                reader.GetGuid(0),
                reader.GetDouble(1),
                reader.GetDouble(2),
                reader.GetFieldValue<DateTimeOffset>(3))
            : null;
    }

    private static async Task InsertPositionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DriverIdentity driver,
        PendingPosition pending,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO drivers.driver_positions
              (id,driver_id,org_id,city_id,client_event_id,point,accuracy_m,
               heading_degrees,speed_mps,captured_at,received_at,publish_realtime)
            VALUES
              (@id,@driver_id,@org_id,@city_id,@client_event_id,
               ST_SetSRID(ST_MakePoint(@longitude,@latitude),4326),
               @accuracy_m,@heading_degrees,@speed_mps,@captured_at,@received_at,@publish_realtime);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddUuid(command, "id", pending.PositionId);
        AddUuid(command, "driver_id", driver.Id);
        AddUuid(command, "org_id", driver.OrganizationId);
        AddUuid(command, "city_id", driver.CityId);
        AddUuid(command, "client_event_id", pending.Location.ClientEventId);
        command.Parameters.AddWithValue("longitude", NpgsqlDbType.Double, pending.Location.Longitude);
        command.Parameters.AddWithValue("latitude", NpgsqlDbType.Double, pending.Location.Latitude);
        command.Parameters.AddWithValue("accuracy_m", NpgsqlDbType.Numeric, pending.Location.AccuracyMeters);
        command.Parameters.Add(new NpgsqlParameter<decimal?>("heading_degrees", NpgsqlDbType.Numeric)
        {
            TypedValue = pending.Location.HeadingDegrees,
        });
        command.Parameters.Add(new NpgsqlParameter<decimal?>("speed_mps", NpgsqlDbType.Numeric)
        {
            TypedValue = pending.Location.SpeedMetersPerSecond,
        });
        command.Parameters.AddWithValue("captured_at", NpgsqlDbType.TimestampTz, pending.Location.CapturedAt);
        command.Parameters.AddWithValue("received_at", NpgsqlDbType.TimestampTz, receivedAt);
        command.Parameters.AddWithValue("publish_realtime", NpgsqlDbType.Boolean, pending.PublishRealtime);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertLocationOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        DriverIdentity driver,
        PendingPosition pending,
        DateTimeOffset receivedAt,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            INSERT INTO platform.location_outbox_events
              (id,owner_org_id,driver_position_id,topic,payload,status,attempts,available_at,
               locked_at,locked_by,lease_token,lease_expires_at,last_error,created_at,processed_at)
            VALUES
              (@id,@owner_org_id,@driver_position_id,@topic,@payload::jsonb,'PENDING',0,@available_at,
               NULL,NULL,NULL,NULL,NULL,@created_at,NULL);
            """;
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        AddUuid(command, "id", Guid.NewGuid());
        AddUuid(command, "owner_org_id", driver.OrganizationId);
        AddUuid(command, "driver_position_id", pending.PositionId);
        command.Parameters.AddWithValue("topic", NpgsqlDbType.Text, LocationOutboxTopic);
        command.Parameters.AddWithValue(
            "payload",
            NpgsqlDbType.Text,
            CreatePayload(driver.Id, pending));
        command.Parameters.AddWithValue("available_at", NpgsqlDbType.TimestampTz, receivedAt);
        command.Parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, receivedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreatePayload(Guid driverId, PendingPosition pending)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema_version", PayloadSchemaVersion);
            writer.WriteString("driver_position_id", pending.PositionId);
            writer.WriteString("driver_id", driverId);
            writer.WriteNumber("lat", pending.Location.Latitude);
            writer.WriteNumber("lng", pending.Location.Longitude);
            writer.WriteNumber("accuracy_m", pending.Location.AccuracyMeters);
            writer.WriteString(
                "captured_at",
                pending.Location.CapturedAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void AddUuid(NpgsqlCommand command, string name, Guid value) =>
        command.Parameters.Add(new NpgsqlParameter<Guid>(name, NpgsqlDbType.Uuid)
        {
            TypedValue = value,
        });

    private static void RecordResult(DriverLocationBatchResult result)
    {
        Positions.Add(result.AcceptedCount, new KeyValuePair<string, object?>("outcome", "accepted"));
        Positions.Add(result.DuplicateCount, new KeyValuePair<string, object?>("outcome", "duplicate"));
        foreach (var rejected in result.Items.Where(item => item.Status == DriverLocationItemStatus.Rejected))
        {
            Positions.Add(
                1,
                new KeyValuePair<string, object?>("outcome", "rejected"),
                new KeyValuePair<string, object?>("rejection_code", rejected.ErrorCode));
        }
        Publications.Add(
            result.PublishedCount,
            new KeyValuePair<string, object?>("publication_decision", "selected"));
        Publications.Add(
            result.SuppressedCount,
            new KeyValuePair<string, object?>("publication_decision", "suppressed"));
    }

    private sealed record DriverIdentity(Guid Id, Guid OrganizationId, Guid CityId);

    private sealed class PendingPosition(
        int originalIndex,
        Guid positionId,
        ValidatedDriverLocation location)
    {
        public int OriginalIndex { get; } = originalIndex;
        public Guid PositionId { get; } = positionId;
        public ValidatedDriverLocation Location { get; } = location;
        public bool PublishRealtime { get; set; }
    }
}
