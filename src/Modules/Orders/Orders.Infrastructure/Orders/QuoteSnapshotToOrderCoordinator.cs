using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Orders;
using Orders.Domain;
using Orders.Infrastructure.Persistence;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using Paqueteria.Contracts.Legal;
using Paqueteria.Infrastructure.Tenancy;

namespace Orders.Infrastructure.Orders;

public enum OrderCreationStage
{
    ReservationCreated,
    QuoteLocked,
    OrderInserted,
    PackageItemsInserted,
    AcceptanceInserted,
    EventInserted,
    OutboxInserted,
    AuditInserted,
    QuoteConsumed,
    BeforeIdempotencyCompletion,
}

public interface IOrderCreationFailureInjector
{
    Task OnStageAsync(OrderCreationStage stage, CancellationToken cancellationToken);
}

public sealed class NoOpOrderCreationFailureInjector : IOrderCreationFailureInjector
{
    public Task OnStageAsync(OrderCreationStage stage, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class QuoteSnapshotToOrderCoordinator(
    TenantTransactionContext<OrdersDbContext> transactionContext,
    IOrderPublicIdGenerator publicIdGenerator,
    IOrderCreationFailureInjector failureInjector,
    IAppendOnlyAuditWriter auditWriter,
    IAuditPayloadRedactor auditRedactor,
    IOptions<OrdersOptions> options,
    IClock clock) : IOrderService
{
    internal const string IdempotencyScope = "ORD-001:CREATE_ORDER";
    private const string PublicIdUniqueConstraint = "orders_public_id_key";
    private const string QuoteIdUniqueConstraint = "orders_quote_id_key";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
    private static readonly HashSet<string> OrderStatuses =
    [
        "DRAFT", "CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP", "PICKED_UP",
        "IN_TRANSIT", "DELIVERING", "FAILED_ATTEMPT", "RESCHEDULED", "RETURNING", "RETURNED",
        "DELIVERED", "CLOSED", "CLAIM_OPEN", "CLAIM_RESOLVED", "CANCELLED",
    ];

    public async Task<OrderResult> CreateAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);
        _ = OrderInputPolicy.TryParsePayerType(command.PayerType, out var payerType);
        var requestHash = ComputeRequestHash(command);
        var maximumAttempts = checked(options.Value.PublicIdCollisionRetryCount + 1);

        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var publicId = publicIdGenerator.Create();
            if (!OrderPublicIdPolicy.IsValid(publicId))
            {
                throw new OrderServiceUnavailableException("The public order identifier generator returned an invalid value.");
            }

            try
            {
                return await transactionContext.ExecuteAsync(
                    new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
                    (dbContext, token) => CreateWithinTransactionAsync(
                        dbContext,
                        command,
                        payerType,
                        requestHash,
                        publicId,
                        token),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (PublicIdCollisionException) when (attempt + 1 < maximumAttempts)
            {
                continue;
            }
            catch (PublicIdCollisionException exception)
            {
                throw new OrderServiceUnavailableException(
                    "The public order identifier could not be allocated safely.",
                    exception);
            }
            catch (OrderConflictException)
            {
                throw;
            }
            catch (OrderServiceUnavailableException)
            {
                throw;
            }
            catch (PostgresException exception) when (
                exception.SqlState == PostgresErrorCodes.UniqueViolation &&
                string.Equals(exception.ConstraintName, QuoteIdUniqueConstraint, StringComparison.Ordinal))
            {
                throw new OrderConflictException(OrderConflictCode.QuoteUnavailable);
            }
            catch (Exception exception) when (exception is PostgresException or NpgsqlException or DbUpdateException)
            {
                throw new OrderServiceUnavailableException("The order operation failed safely.", exception);
            }
        }

        throw new OrderServiceUnavailableException("The public order identifier could not be allocated safely.");
    }

    public Task<OrderPageResult> ListAsync(
        Guid actorId,
        Guid organizationId,
        string? status,
        Guid? ownerOrganizationId,
        string? cursor,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || organizationId == Guid.Empty ||
            (status is not null && !OrderStatuses.Contains(status)) ||
            (ownerOrganizationId is { } owner && owner != organizationId))
        {
            return Task.FromResult(new OrderPageResult([], null));
        }

        DateTimeOffset cursorCreatedAt = default;
        Guid cursorId = default;
        var hasCursor = cursor is not null;
        if (hasCursor && !OrderCursorCodec.TryDecode(cursor, out cursorCreatedAt, out cursorId))
        {
            return Task.FromResult(new OrderPageResult([], null));
        }

        return transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(actorId, [organizationId]),
            (dbContext, token) => ListWithinTransactionAsync(
                dbContext,
                status,
                hasCursor,
                cursorCreatedAt,
                cursorId,
                token),
            cancellationToken);
    }

    public Task<OrderDetailResult> GetAsync(
        Guid actorId,
        Guid organizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || organizationId == Guid.Empty || orderId == Guid.Empty)
        {
            throw new OrderNotFoundException();
        }

        return transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(actorId, [organizationId]),
            (dbContext, token) => GetWithinTransactionAsync(dbContext, orderId, token),
            cancellationToken);
    }

    internal static byte[] ComputeRequestHash(CreateOrderCommand command)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("tenant", command.OrganizationId);
            writer.WriteString("quote_id", command.QuoteId);
            writer.WriteString("payer_type", command.PayerType);
            writer.WriteString("terms_version", command.Acceptance.TermsVersion);
            writer.WriteString("privacy_version", command.Acceptance.PrivacyVersion);
            writer.WriteString(
                "accepted_at",
                command.Acceptance.AcceptedAt.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                    CultureInfo.InvariantCulture));
            writer.WriteString("acceptance_channel", command.Acceptance.AcceptanceChannel);
            writer.WriteEndObject();
        }

        return SHA256.HashData(stream.ToArray());
    }

    private async Task<OrderResult> CreateWithinTransactionAsync(
        OrdersDbContext dbContext,
        CreateOrderCommand command,
        PayerType payerType,
        byte[] requestHash,
        string publicId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
        await AcquireIdempotencyLockAsync(connection, transaction, command, cancellationToken);
        var idempotency = await FindIdempotencyAsync(connection, transaction, command, cancellationToken);
        if (idempotency is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(idempotency.RequestHash, requestHash))
            {
                throw new OrderConflictException(OrderConflictCode.IdempotencyConflict);
            }

            var replay = ReadCompletedResponse(idempotency);
            if (replay is not null)
            {
                return replay;
            }
        }
        else
        {
            var reservedAt = clock.UtcNow;
            await InsertIdempotencyReservationAsync(
                connection,
                transaction,
                command,
                requestHash,
                reservedAt,
                reservedAt.AddMinutes(options.Value.IdempotencyLifetimeMinutes),
                cancellationToken);
            await failureInjector.OnStageAsync(OrderCreationStage.ReservationCreated, cancellationToken);
        }

        var quote = await ReadQuoteForUpdateAsync(connection, transaction, command.QuoteId, cancellationToken);
        var now = clock.UtcNow;
        if (quote is null ||
            quote.OwnerOrganizationId != command.OrganizationId ||
            quote.Status != "ACTIVE" ||
            quote.ExpiresAt <= now ||
            quote.ConsumedAt is not null ||
            quote.CityId == Guid.Empty ||
            quote.OriginLocationId == Guid.Empty ||
            quote.DestinationLocationId == Guid.Empty ||
            quote.Currency != "MXN" ||
            quote.SubtotalCents < 0 ||
            quote.DiscountCents < 0 ||
            quote.TaxCents < 0 ||
            quote.TotalCents != checked(quote.SubtotalCents - quote.DiscountCents + quote.TaxCents) ||
            quote.MinimumTotalCentsSnapshot < 0)
        {
            throw new OrderConflictException(OrderConflictCode.QuoteUnavailable);
        }

        await failureInjector.OnStageAsync(OrderCreationStage.QuoteLocked, cancellationToken);
        var packages = ParsePackages(quote.PackageSnapshot);
        var orderId = Guid.NewGuid();
        var order = Order.Create(
            orderId,
            publicId,
            quote.Id,
            quote.OwnerOrganizationId,
            quote.ClientAccountId,
            quote.CityId,
            quote.ServiceAreaId,
            quote.OriginLocationId,
            quote.DestinationLocationId,
            quote.ServiceType,
            quote.PricingTier,
            quote.ConsolidatedRoute,
            payerType,
            quote.SubtotalCents,
            quote.DiscountCents,
            quote.TaxCents,
            quote.TotalCents,
            quote.MinimumTotalCentsSnapshot,
            quote.Currency,
            quote.PricingPolicyVersion,
            quote.PackageSnapshot,
            quote.FinancialOverride,
            now);

        await InsertOrderAsync(connection, transaction, order, cancellationToken);
        await failureInjector.OnStageAsync(OrderCreationStage.OrderInserted, cancellationToken);
        await InsertPackageItemsAsync(connection, transaction, order, packages, cancellationToken);
        await failureInjector.OnStageAsync(OrderCreationStage.PackageItemsInserted, cancellationToken);

        var evidence = new OrderAcceptanceEvidence(
            order.Id,
            order.QuoteId,
            order.OwnerOrganizationId,
            command.ActorId,
            command.Acceptance.TermsVersion,
            command.Acceptance.PrivacyVersion,
            command.Acceptance.AcceptedAt,
            command.Acceptance.AcceptanceChannel);
        await InsertAcceptanceAsync(
            connection,
            transaction,
            Guid.NewGuid(),
            evidence,
            now,
            OrderAcceptanceCanonicalizer.ComputeSha256(evidence),
            cancellationToken);
        await failureInjector.OnStageAsync(OrderCreationStage.AcceptanceInserted, cancellationToken);

        await InsertInitialEventAsync(
            connection,
            transaction,
            Guid.NewGuid(),
            order,
            command.ActorId,
            now,
            cancellationToken);
        await failureInjector.OnStageAsync(OrderCreationStage.EventInserted, cancellationToken);

        await InsertOutboxAsync(
            connection,
            transaction,
            Guid.NewGuid(),
            order,
            now,
            cancellationToken);
        await failureInjector.OnStageAsync(OrderCreationStage.OutboxInserted, cancellationToken);

        var auditPayload = JsonSerializer.SerializeToElement(new
        {
            order_id = order.Id,
            quote_id = order.QuoteId,
            payer_type = order.PayerType.ToContractValue(),
            pricing_tier = order.PricingTier,
            total_cents = order.TotalCents,
            request_id = command.RequestId,
        }, JsonOptions);
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                order.OwnerOrganizationId,
                command.ActorId,
                "ORDER_CREATED",
                "Order",
                order.Id,
                command.RequestId,
                auditRedactor.Redact(auditPayload),
                now),
            cancellationToken);
        await failureInjector.OnStageAsync(OrderCreationStage.AuditInserted, cancellationToken);

        await ConsumeQuoteAsync(connection, transaction, quote.Id, now, cancellationToken);
        await failureInjector.OnStageAsync(OrderCreationStage.QuoteConsumed, cancellationToken);
        var result = ToResult(order);
        var responseJson = JsonSerializer.Serialize(result, JsonOptions);
        await failureInjector.OnStageAsync(OrderCreationStage.BeforeIdempotencyCompletion, cancellationToken);
        await CompleteIdempotencyAsync(
            connection,
            transaction,
            command,
            requestHash,
            order.Id,
            responseJson,
            now.AddMinutes(options.Value.IdempotencyLifetimeMinutes),
            cancellationToken);
        return result;
    }

    private async Task<OrderPageResult> ListWithinTransactionAsync(
        OrdersDbContext dbContext,
        string? status,
        bool hasCursor,
        DateTimeOffset cursorCreatedAt,
        Guid cursorId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id,public_id,owner_org_id,operator_org_id,status,subtotal_cents,discount_cents,currency,version,
                   origin_location_id,destination_location_id,service_type,quote_id,city_id,service_area_id,
                   pricing_tier,total_cents,claim_window_ends_at,finalized_at,created_at
            FROM orders.orders
            WHERE (@status IS NULL OR status=@status)
              AND (@has_cursor=false OR created_at<@cursor_created_at OR (created_at=@cursor_created_at AND id<@cursor_id))
            ORDER BY created_at DESC,id DESC
            LIMIT @take
            """);
        command.Parameters.Add(P("status", NpgsqlDbType.Text, status));
        command.Parameters.Add(P("has_cursor", NpgsqlDbType.Boolean, hasCursor));
        command.Parameters.Add(P("cursor_created_at", NpgsqlDbType.TimestampTz, cursorCreatedAt));
        command.Parameters.Add(P("cursor_id", NpgsqlDbType.Uuid, cursorId));
        command.Parameters.Add(P("take", NpgsqlDbType.Integer, checked(options.Value.PageSize + 1)));

        var rows = new List<OrderReadRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadOrder(reader));
        }

        var hasMore = rows.Count > options.Value.PageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var nextCursor = hasMore && rows.Count > 0
            ? OrderCursorCodec.Encode(rows[^1].CreatedAt, rows[^1].Result.Id)
            : null;
        return new OrderPageResult(rows.Select(value => value.Result).ToArray(), nextCursor);
    }

    private async Task<OrderDetailResult> GetWithinTransactionAsync(
        OrdersDbContext dbContext,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id,public_id,owner_org_id,operator_org_id,status,subtotal_cents,discount_cents,currency,version,
                   origin_location_id,destination_location_id,service_type,quote_id,city_id,service_area_id,
                   pricing_tier,total_cents,claim_window_ends_at,finalized_at,created_at
            FROM orders.orders WHERE id=@id
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, orderId));
        OrderReadRow? order = null;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                order = ReadOrder(reader);
            }
        }

        if (order is null)
        {
            throw new OrderNotFoundException();
        }

        await using var timelineCommand = CreateCommand(
            connection,
            transaction,
            "SELECT event_type,occurred_at FROM orders.order_events WHERE order_id=@id ORDER BY occurred_at,id;");
        timelineCommand.Parameters.Add(P("id", NpgsqlDbType.Uuid, orderId));
        var timeline = new List<OrderTimelineItem>();
        await using var timelineReader = await timelineCommand.ExecuteReaderAsync(cancellationToken);
        while (await timelineReader.ReadAsync(cancellationToken))
        {
            timeline.Add(new OrderTimelineItem(
                timelineReader.GetString(0),
                timelineReader.GetFieldValue<DateTimeOffset>(1)));
        }

        return new OrderDetailResult(order.Result, timeline);
    }

    private static void Validate(CreateOrderCommand command)
    {
        if (command.ActorId == Guid.Empty ||
            command.OrganizationId == Guid.Empty ||
            command.QuoteId == Guid.Empty ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey) ||
            !OrderInputPolicy.TryParsePayerType(command.PayerType, out _) ||
            command.Acceptance is null ||
            string.IsNullOrWhiteSpace(command.Acceptance.TermsVersion) ||
            string.IsNullOrWhiteSpace(command.Acceptance.PrivacyVersion) ||
            !OrderInputPolicy.IsAcceptanceChannel(command.Acceptance.AcceptanceChannel))
        {
            throw new OrderConflictException(OrderConflictCode.InvalidRequest);
        }
    }

    private static IReadOnlyList<ParsedPackage> ParsePackages(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array ||
                document.RootElement.GetArrayLength() == 0)
            {
                throw new OrderConflictException(OrderConflictCode.QuoteUnavailable);
            }

            var packages = new List<ParsedPackage>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("description", out var description) ||
                    description.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(description.GetString()) ||
                    description.GetString()!.Length > 250 ||
                    !item.TryGetProperty("weight_grams", out var weight) ||
                    !weight.TryGetInt32(out var weightGrams) ||
                    weightGrams <= 0 ||
                    !item.TryGetProperty("declared_value_cents", out var declared) ||
                    !declared.TryGetInt64(out var declaredValueCents) ||
                    declaredValueCents < 0)
                {
                    throw new OrderConflictException(OrderConflictCode.QuoteUnavailable);
                }

                var length = ReadNullablePositiveInt(item, "length_mm");
                var width = ReadNullablePositiveInt(item, "width_mm");
                var height = ReadNullablePositiveInt(item, "height_mm");
                var dimensions = JsonSerializer.Serialize(new
                {
                    length_mm = length,
                    width_mm = width,
                    height_mm = height,
                }, JsonOptions);
                packages.Add(new ParsedPackage(
                    description.GetString()!,
                    weightGrams,
                    declaredValueCents,
                    dimensions));
            }

            return packages;
        }
        catch (OrderConflictException)
        {
            throw;
        }
        catch (JsonException)
        {
            throw new OrderConflictException(OrderConflictCode.QuoteUnavailable);
        }
    }

    private static int? ReadNullablePositiveInt(JsonElement item, string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (!value.TryGetInt32(out var result) || result <= 0)
        {
            throw new OrderConflictException(OrderConflictCode.QuoteUnavailable);
        }

        return result;
    }

    private async Task AcquireIdempotencyLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateOrderCommand create,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT pg_advisory_xact_lock(hashtextextended(@lock_key,0));");
        command.Parameters.Add(P(
            "lock_key",
            NpgsqlDbType.Text,
            $"{create.OrganizationId:D}:{IdempotencyScope}:{create.IdempotencyKey}"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IdempotencyRow?> FindIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateOrderCommand create,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT request_hash,response_status,response_body::text,resource_id
            FROM platform.idempotency_keys
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key
            """);
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, create.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, create.IdempotencyKey));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new IdempotencyRow(
            reader.GetFieldValue<byte[]>(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetGuid(3));
    }

    private static OrderResult? ReadCompletedResponse(IdempotencyRow row)
    {
        if (row.ResponseStatus is null && row.ResponseBody is null && row.ResourceId is null)
        {
            return null;
        }

        if (row.ResponseStatus != 201 ||
            string.IsNullOrWhiteSpace(row.ResponseBody) ||
            row.ResourceId is null)
        {
            throw new OrderServiceUnavailableException("The idempotency record is inconsistent.");
        }

        var result = JsonSerializer.Deserialize<OrderResult>(row.ResponseBody, JsonOptions)
            ?? throw new OrderServiceUnavailableException("The idempotency response is invalid.");
        if (result.Id != row.ResourceId)
        {
            throw new OrderServiceUnavailableException("The idempotency resource is inconsistent.");
        }

        return result;
    }

    private async Task InsertIdempotencyReservationAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateOrderCommand create,
        byte[] requestHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,resource_id,created_at,expires_at)
            VALUES (@owner,@scope,@key,@hash,NULL,NULL,NULL,@created,@expires)
            """);
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, create.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, create.IdempotencyKey));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        command.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, createdAt));
        command.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz, expiresAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "The idempotency reservation was not inserted.");
    }

    private async Task<QuoteSnapshot?> ReadQuoteForUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid quoteId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            SELECT id,owner_org_id,client_account_id,city_id,service_area_id,origin_location_id,destination_location_id,
                   service_type,pricing_tier,consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
                   minimum_total_cents_snapshot,currency,pricing_policy_version,package_snapshot::text,
                   financial_override::text,status,expires_at,consumed_at
            FROM pricing.quotes WHERE id=@id FOR UPDATE
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, quoteId));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new QuoteSnapshot(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetGuid(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.GetGuid(5),
            reader.GetGuid(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetBoolean(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reader.GetInt64(13),
            reader.GetInt64(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetString(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.GetString(19),
            reader.GetFieldValue<DateTimeOffset>(20),
            reader.IsDBNull(21) ? null : reader.GetFieldValue<DateTimeOffset>(21));
    }

    private async Task InsertOrderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Order order,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO orders.orders(
              id,public_id,quote_id,owner_org_id,operator_org_id,client_account_id,city_id,service_area_id,
              origin_location_id,destination_location_id,service_type,pricing_tier,consolidated_route,payer_type,status,
              subtotal_cents,discount_cents,tax_cents,total_cents,minimum_total_cents_snapshot,currency,
              pricing_policy_version,package_snapshot,financial_override,cod_expected_cents,version,
              claim_window_ends_at,finalized_at,archived_at,created_at,updated_at)
            VALUES (
              @id,@public_id,@quote_id,@owner,NULL,@client,@city,@area,@origin,@destination,@service,@tier,@consolidated,
              @payer,'DRAFT',@subtotal,@discount,@tax,@total,@minimum,@currency,@policy,@packages,@override,0,1,
              NULL,NULL,NULL,@created,@updated)
            """);
        AddOrderParameters(command, order);
        try
        {
            RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "The order was not inserted.");
        }
        catch (PostgresException exception) when (
            exception.SqlState == PostgresErrorCodes.UniqueViolation &&
            string.Equals(exception.ConstraintName, PublicIdUniqueConstraint, StringComparison.Ordinal))
        {
            throw new PublicIdCollisionException(exception);
        }
    }

    private async Task InsertPackageItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Order order,
        IReadOnlyList<ParsedPackage> packages,
        CancellationToken cancellationToken)
    {
        foreach (var package in packages)
        {
            await using var command = CreateCommand(
                connection,
                transaction,
                """
                INSERT INTO orders.package_items(
                  id,order_id,owner_org_id,operator_org_id,description,weight_grams,declared_value_cents,dimensions_mm)
                VALUES (@id,@order,@owner,NULL,@description,@weight,@declared,@dimensions)
                """);
            command.Parameters.Add(P("id", NpgsqlDbType.Uuid, Guid.NewGuid()));
            command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
            command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
            command.Parameters.Add(P("description", NpgsqlDbType.Text, package.Description));
            command.Parameters.Add(P("weight", NpgsqlDbType.Integer, package.WeightGrams));
            command.Parameters.Add(P("declared", NpgsqlDbType.Bigint, package.DeclaredValueCents));
            command.Parameters.Add(P("dimensions", NpgsqlDbType.Jsonb, package.DimensionsMm));
            RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "A package item was not inserted.");
        }
    }

    private async Task InsertAcceptanceAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid acceptanceId,
        OrderAcceptanceEvidence evidence,
        DateTimeOffset recordedAt,
        byte[] evidenceHash,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO orders.order_acceptances(
              id,order_id,quote_id,owner_org_id,actor_id,terms_version,privacy_version,accepted_at_client,
              recorded_at_server,acceptance_channel,evidence_schema_version,evidence_hash)
            VALUES (@id,@order,@quote,@owner,@actor,@terms,@privacy,@accepted,@recorded,@channel,@schema,@hash)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, acceptanceId));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, evidence.OrderId));
        command.Parameters.Add(P("quote", NpgsqlDbType.Uuid, evidence.QuoteId));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, evidence.OwnerOrganizationId));
        command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, evidence.ActorId));
        command.Parameters.Add(P("terms", NpgsqlDbType.Text, evidence.TermsVersion));
        command.Parameters.Add(P("privacy", NpgsqlDbType.Text, evidence.PrivacyVersion));
        command.Parameters.Add(P("accepted", NpgsqlDbType.TimestampTz, evidence.AcceptedAtClient.ToUniversalTime()));
        command.Parameters.Add(P("recorded", NpgsqlDbType.TimestampTz, recordedAt));
        command.Parameters.Add(P("channel", NpgsqlDbType.Text, evidence.AcceptanceChannel));
        command.Parameters.Add(P("schema", NpgsqlDbType.Text, OrderAcceptanceCanonicalizer.SchemaVersion));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, evidenceHash));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "The order acceptance was not inserted.");
    }

    private async Task InsertInitialEventAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid eventId,
        Order order,
        Guid actorId,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            order_id = order.Id,
            quote_id = order.QuoteId,
            status = "DRAFT",
        }, JsonOptions);
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO orders.order_events(
              id,order_id,owner_org_id,operator_org_id,aggregate_version,event_type,public_event_code,payload,actor_id,occurred_at)
            VALUES (@id,@order,@owner,NULL,1,'ORDER_CREATED','ORDER_CREATED',@payload,@actor,@occurred)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, eventId));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        command.Parameters.Add(P("payload", NpgsqlDbType.Jsonb, payload));
        command.Parameters.Add(P("actor", NpgsqlDbType.Uuid, actorId));
        command.Parameters.Add(P("occurred", NpgsqlDbType.TimestampTz, occurredAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "The initial order event was not inserted.");
    }

    private async Task InsertOutboxAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid outboxId,
        Order order,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var tenantContext = JsonSerializer.Serialize(new
        {
            organization_ids = new[] { order.OwnerOrganizationId },
        }, JsonOptions);
        var payload = JsonSerializer.Serialize(new
        {
            order_id = order.Id,
            public_id = order.PublicId,
            status = "DRAFT",
        }, JsonOptions);
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,payload,priority,status,
              attempts,available_at,locked_at,locked_by,lease_token,lease_expires_at,last_error,created_at,processed_at)
            VALUES (@id,@owner,@tenant,'orders.created','Order',@order,1,@payload,50,'PENDING',0,@available,
                    NULL,NULL,NULL,NULL,NULL,@created,NULL)
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, outboxId));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        command.Parameters.Add(P("tenant", NpgsqlDbType.Jsonb, tenantContext));
        command.Parameters.Add(P("order", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("payload", NpgsqlDbType.Jsonb, payload));
        command.Parameters.Add(P("available", NpgsqlDbType.TimestampTz, occurredAt));
        command.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, occurredAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "The order outbox event was not inserted.");
    }

    private async Task ConsumeQuoteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid quoteId,
        DateTimeOffset consumedAt,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE pricing.quotes SET status='USED',consumed_at=@consumed
            WHERE id=@id AND status='ACTIVE' AND consumed_at IS NULL AND expires_at>@consumed
            """);
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, quoteId));
        command.Parameters.Add(P("consumed", NpgsqlDbType.TimestampTz, consumedAt));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new OrderConflictException(OrderConflictCode.QuoteUnavailable);
        }
    }

    private async Task CompleteIdempotencyAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CreateOrderCommand create,
        byte[] requestHash,
        Guid orderId,
        string responseJson,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            """
            UPDATE platform.idempotency_keys
            SET response_status=201,response_body=@response,resource_id=@resource,expires_at=@expires
            WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key AND request_hash=@hash
              AND response_status IS NULL AND response_body IS NULL AND resource_id IS NULL
            """);
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, create.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("key", NpgsqlDbType.Text, create.IdempotencyKey));
        command.Parameters.Add(P("hash", NpgsqlDbType.Bytea, requestHash));
        command.Parameters.Add(P("response", NpgsqlDbType.Jsonb, responseJson));
        command.Parameters.Add(P("resource", NpgsqlDbType.Uuid, orderId));
        command.Parameters.Add(P("expires", NpgsqlDbType.TimestampTz, expiresAt));
        RequireOne(await command.ExecuteNonQueryAsync(cancellationToken), "The idempotency record was not completed.");
    }

    private void AddOrderParameters(NpgsqlCommand command, Order order)
    {
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, order.Id));
        command.Parameters.Add(P("public_id", NpgsqlDbType.Text, order.PublicId));
        command.Parameters.Add(P("quote_id", NpgsqlDbType.Uuid, order.QuoteId));
        command.Parameters.Add(P("owner", NpgsqlDbType.Uuid, order.OwnerOrganizationId));
        command.Parameters.Add(P("client", NpgsqlDbType.Uuid, order.ClientAccountId));
        command.Parameters.Add(P("city", NpgsqlDbType.Uuid, order.CityId));
        command.Parameters.Add(P("area", NpgsqlDbType.Uuid, order.ServiceAreaId));
        command.Parameters.Add(P("origin", NpgsqlDbType.Uuid, order.OriginLocationId));
        command.Parameters.Add(P("destination", NpgsqlDbType.Uuid, order.DestinationLocationId));
        command.Parameters.Add(P("service", NpgsqlDbType.Text, order.ServiceType));
        command.Parameters.Add(P("tier", NpgsqlDbType.Text, order.PricingTier));
        command.Parameters.Add(P("consolidated", NpgsqlDbType.Boolean, order.ConsolidatedRoute));
        command.Parameters.Add(P("payer", NpgsqlDbType.Text, order.PayerType.ToContractValue()));
        command.Parameters.Add(P("subtotal", NpgsqlDbType.Bigint, order.SubtotalCents));
        command.Parameters.Add(P("discount", NpgsqlDbType.Bigint, order.DiscountCents));
        command.Parameters.Add(P("tax", NpgsqlDbType.Bigint, order.TaxCents));
        command.Parameters.Add(P("total", NpgsqlDbType.Bigint, order.TotalCents));
        command.Parameters.Add(P("minimum", NpgsqlDbType.Bigint, order.MinimumTotalCentsSnapshot));
        command.Parameters.Add(P("currency", NpgsqlDbType.Char, order.Currency));
        command.Parameters.Add(P("policy", NpgsqlDbType.Text, order.PricingPolicyVersion));
        command.Parameters.Add(P("packages", NpgsqlDbType.Jsonb, order.PackageSnapshot));
        command.Parameters.Add(P("override", NpgsqlDbType.Jsonb, order.FinancialOverride));
        command.Parameters.Add(P("created", NpgsqlDbType.TimestampTz, order.CreatedAt));
        command.Parameters.Add(P("updated", NpgsqlDbType.TimestampTz, order.UpdatedAt));
    }

    private NpgsqlCommand CreateCommand(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql) => new(sql, connection, transaction)
        {
            CommandTimeout = options.Value.CommandTimeoutSeconds,
        };

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };

    private static void RequireOne(int affected, string message)
    {
        if (affected != 1)
        {
            throw new OrderServiceUnavailableException(message);
        }
    }

    private static OrderResult ToResult(Order order) => new(
        order.Id,
        order.PublicId,
        order.OwnerOrganizationId,
        order.OperatorOrganizationId,
        order.Status.ToContractValue(),
        new MoneyResult(order.Currency, checked(order.SubtotalCents - order.DiscountCents)),
        order.Version,
        order.OriginLocationId,
        order.DestinationLocationId,
        order.ServiceType,
        order.QuoteId,
        order.CityId,
        order.ServiceAreaId,
        order.PricingTier,
        new MoneyResult(order.Currency, order.TotalCents),
        order.ClaimWindowEndsAt,
        order.FinalizedAt);

    private static OrderReadRow ReadOrder(NpgsqlDataReader reader)
    {
        var currency = reader.GetString(7);
        return new OrderReadRow(
            new OrderResult(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetGuid(2),
                reader.IsDBNull(3) ? null : reader.GetGuid(3),
                reader.GetString(4),
                new MoneyResult(currency, checked(reader.GetInt64(5) - reader.GetInt64(6))),
                reader.GetInt32(8),
                reader.GetGuid(9),
                reader.GetGuid(10),
                reader.GetString(11),
                reader.GetGuid(12),
                reader.GetGuid(13),
                reader.IsDBNull(14) ? null : reader.GetGuid(14),
                reader.GetString(15),
                new MoneyResult(currency, reader.GetInt64(16)),
                reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
                reader.IsDBNull(18) ? null : reader.GetFieldValue<DateTimeOffset>(18)),
            reader.GetFieldValue<DateTimeOffset>(19));
    }

    private sealed record IdempotencyRow(
        byte[] RequestHash,
        int? ResponseStatus,
        string? ResponseBody,
        Guid? ResourceId);

    private sealed record QuoteSnapshot(
        Guid Id,
        Guid OwnerOrganizationId,
        Guid? ClientAccountId,
        Guid CityId,
        Guid? ServiceAreaId,
        Guid OriginLocationId,
        Guid DestinationLocationId,
        string ServiceType,
        string PricingTier,
        bool ConsolidatedRoute,
        long SubtotalCents,
        long DiscountCents,
        long TaxCents,
        long TotalCents,
        long MinimumTotalCentsSnapshot,
        string Currency,
        string PricingPolicyVersion,
        string PackageSnapshot,
        string? FinancialOverride,
        string Status,
        DateTimeOffset ExpiresAt,
        DateTimeOffset? ConsumedAt);

    private sealed record ParsedPackage(
        string Description,
        int WeightGrams,
        long DeclaredValueCents,
        string DimensionsMm);

    private sealed record OrderReadRow(OrderResult Result, DateTimeOffset CreatedAt);

    private sealed class PublicIdCollisionException(Exception innerException)
        : Exception("The generated public order identifier collided.", innerException);
}
