using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Paqueteria.Application;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.Idempotency;
using Paqueteria.Application.Tenancy;
using Paqueteria.Infrastructure.Tenancy;
using Pricing.Application.Quotes;
using Pricing.Domain;
using Pricing.Infrastructure.Persistence;

namespace Pricing.Infrastructure.Quotes;

public sealed class PostgreSqlQuoteService(
    TenantTransactionContext<PricingDbContext> transactionContext,
    IQuoteLocationResolver locationResolver,
    IAuditPayloadRedactor redactor,
    IAppendOnlyAuditWriter auditWriter,
    IOptions<PricingOptions> options,
    IClock clock,
    ILogger<PostgreSqlQuoteService> logger) : IQuoteService
{
    internal const string IdempotencyScope = "PRC-001:CREATE_QUOTE";

    /// <summary>LOW-PRICE-MANUAL-AUTH-2026-10-02: the append-only audit action of a manual low price authorization.</summary>
    internal const string LowPriceAuthorizedAuditAction = "QUOTE_LOW_PRICE_AUTHORIZED";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    public async Task<QuoteResult> CreateAsync(CreateQuoteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);
        ValidateLowPriceAuthorization(command);
        var serviceType = ParseServiceType(command.ServiceType);
        var inputHash = ComputeInputHash(command);

        QuoteResult? replay;
        try
        {
            replay = await ReserveOrReplayAsync(command, inputHash, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is QuoteValidationException or QuoteServiceUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or DbUpdateException)
        {
            throw new QuoteServiceUnavailableException("The quote idempotency preflight failed safely.", exception);
        }

        if (replay is not null)
        {
            return replay;
        }

        ResolveQuoteLocationResult origin;
        ResolveQuoteLocationResult destination;
        try
        {
            origin = await locationResolver.ResolveAsync(ToLocationCommand(command, command.Origin, QuoteLocationRole.Origin), cancellationToken);
            destination = await locationResolver.ResolveAsync(ToLocationCommand(command, command.Destination, QuoteLocationRole.Destination), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LocationPiiProtectionUnavailableException exception)
        {
            throw new QuoteServiceUnavailableException("PII protection is unavailable.", exception);
        }
        catch (LocationServiceUnavailableException exception)
        {
            throw new QuoteServiceUnavailableException("Location resolution is unavailable.", exception);
        }

        var originLocation = RequireResolved(origin);
        var destinationLocation = RequireResolved(destination);
        var geography = QuoteGeographyPolicy.Resolve(
            new PricingLocation(originLocation.CityId, originLocation.ServiceAreaId, originLocation.OperatingZoneId),
            new PricingLocation(destinationLocation.CityId, destinationLocation.ServiceAreaId, destinationLocation.OperatingZoneId));
        if (!geography.IsSameCity)
        {
            throw new QuoteValidationException(QuoteValidationCode.DifferentCities);
        }

        var sharedServiceAreaId = geography.SharedServiceAreaId;
        var sharedOperatingZoneId = geography.SharedOperatingZoneId;

        try
        {
            return await FinalizeAsync(
                command,
                inputHash,
                serviceType,
                originLocation,
                destinationLocation,
                sharedServiceAreaId,
                sharedOperatingZoneId,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is QuoteValidationException or QuoteConflictException or QuoteServiceUnavailableException)
        {
            throw;
        }
        catch (AuditRedactionException exception)
        {
            throw new QuoteServiceUnavailableException("The low price authorization audit could not be redacted.", exception);
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or DbUpdateException)
        {
            throw new QuoteServiceUnavailableException("The quote operation failed safely.", exception);
        }
    }

    private Task<QuoteResult?> ReserveOrReplayAsync(
        CreateQuoteCommand command,
        byte[] inputHash,
        CancellationToken cancellationToken) =>
        transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
            async (dbContext, token) =>
            {
                await AcquireIdempotencyLockAsync(dbContext, command, token);
                var existing = await FindIdempotencyRecordAsync(dbContext, command, token);
                if (existing is not null)
                {
                    EnsureMatchingHash(existing, inputHash);
                    return ReadCompletedResponse(existing);
                }

                var createdAt = clock.UtcNow;
                await InsertIdempotencyReservationAsync(
                    dbContext,
                    command,
                    inputHash,
                    createdAt,
                    CalculateReservationExpiration(createdAt, options.Value),
                    token);
                return null;
            },
            cancellationToken);

    private Task<QuoteResult> FinalizeAsync(
        CreateQuoteCommand command,
        byte[] inputHash,
        ServiceType serviceType,
        QuoteLocationResult originLocation,
        QuoteLocationResult destinationLocation,
        Guid? sharedServiceAreaId,
        Guid? sharedOperatingZoneId,
        CancellationToken cancellationToken) =>
        transactionContext.ExecuteAsync(
            new TenantDatabaseExecutionContext(command.ActorId, [command.OrganizationId]),
            async (dbContext, token) =>
            {
                await AcquireIdempotencyLockAsync(dbContext, command, token);
                var existing = await FindIdempotencyRecordAsync(dbContext, command, token)
                    ?? throw new QuoteServiceUnavailableException("The idempotency reservation is missing.");
                EnsureMatchingHash(existing, inputHash);
                var completed = ReadCompletedResponse(existing);
                if (completed is not null)
                {
                    return completed;
                }

                var now = clock.UtcNow;
                var (tier, privateTariffId) = await SelectPricingTierAsync(dbContext, command, token);
                var rules = await dbContext.TariffRules.AsNoTracking()
                    .Where(rule => rule.CityId == originLocation.CityId && rule.ServiceType == serviceType)
                    .ToArrayAsync(token);
                var authorizationInput = command.LowPriceAuthorization;
                var evaluation = new TariffRuleEvaluator().Evaluate(
                    new TariffEvaluationContext(
                        command.OrganizationId,
                        originLocation.CityId,
                        sharedServiceAreaId,
                        sharedOperatingZoneId,
                        tier,
                        serviceType,
                        command.ConsolidatedRoute,
                        now,
                        privateTariffId,
                        LowPriceAuthorized: authorizationInput is not null),
                    rules);
                ThrowIfFailed(evaluation, command.OrganizationId, originLocation.CityId, serviceType);

                // LOW-PRICE-MANUAL-AUTH-2026-10-02: an authorization that the selected price does not need is the
                // uniform 409, decided before anything is written; it is never stored silently.
                if (authorizationInput is not null &&
                    !LowPriceGuardPolicy.RequiresAuthorization(tier, command.ConsolidatedRoute, evaluation.Total.AmountCents))
                {
                    throw new QuoteConflictException();
                }

                var selectedRule = evaluation.Rule!;
                var expiresAt = QuoteExpirationPolicy.Calculate(
                    now,
                    TimeSpan.FromMinutes(options.Value.QuoteLifetimeMinutes),
                    selectedRule.ActiveTo);
                LowPriceAuthorization? authorization = null;
                if (authorizationInput is not null)
                {
                    // valid_until is the quote's own expiry. PostgreSQL keeps microseconds, so the expiry is
                    // truncated to them first and the stored valid_until equals the stored expires_at exactly.
                    expiresAt = TruncateToMicroseconds(expiresAt);
                    authorization = new LowPriceAuthorization(command.ActorId, authorizationInput.Reason, expiresAt);
                }

                var packageSnapshot = CreatePackageSnapshot(command.Packages);
                var requestSnapshot = CreateRequestSnapshot(
                    command,
                    originLocation,
                    destinationLocation,
                    sharedServiceAreaId);
                var breakdown = new[]
                {
                    new QuoteBreakdownLine(
                        "BASE_TARIFF",
                        selectedRule.Id,
                        selectedRule.AmountCents,
                        tier.ToContractValue(),
                        selectedRule.TaxMode.ToContractValue()),
                };
                var quote = Quote.Create(
                    Guid.NewGuid(),
                    command.OrganizationId,
                    command.ClientAccountId,
                    originLocation.CityId,
                    sharedServiceAreaId,
                    originLocation.LocationId,
                    destinationLocation.LocationId,
                    serviceType,
                    tier,
                    command.ConsolidatedRoute,
                    evaluation,
                    [selectedRule.Id],
                    JsonSerializer.Serialize(requestSnapshot, JsonOptions),
                    JsonSerializer.Serialize(packageSnapshot, JsonOptions),
                    JsonSerializer.Serialize(breakdown, JsonOptions),
                    inputHash,
                    expiresAt,
                    now,
                    authorization,
                    authorization is null ? null : SerializeFinancialOverride(authorization));

                var response = ToResult(quote);
                var responseJson = JsonSerializer.Serialize(response, JsonOptions);
                await InsertQuoteAsync(dbContext, quote, token);
                if (authorization is not null)
                {
                    await WriteLowPriceAuthorizationAuditAsync(dbContext, command, quote, authorization, now, token);
                }

                await CompleteIdempotencyReservationAsync(dbContext, command, inputHash, responseJson, quote, token);
                return response;
            },
            cancellationToken);

    public async Task<QuoteResult> GetAsync(
        Guid actorId,
        Guid organizationId,
        Guid quoteId,
        CancellationToken cancellationToken)
    {
        if (actorId == Guid.Empty || organizationId == Guid.Empty || quoteId == Guid.Empty)
        {
            throw new QuoteNotFoundException();
        }

        try
        {
            return await transactionContext.ExecuteAsync(
                new TenantDatabaseExecutionContext(actorId, [organizationId]),
                async (dbContext, token) =>
                {
                    var now = clock.UtcNow;
                    var quote = await dbContext.Quotes.AsNoTracking().SingleOrDefaultAsync(value =>
                        value.Id == quoteId &&
                        value.ExpiresAt > now &&
                        value.Status != QuoteStatus.Expired &&
                        value.Status != QuoteStatus.Revoked,
                        token);
                    return quote is null ? throw new QuoteNotFoundException() : ToResult(quote);
                },
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (QuoteNotFoundException)
        {
            throw;
        }
        catch (Exception exception) when (exception is PostgresException or NpgsqlException or DbUpdateException)
        {
            throw new QuoteServiceUnavailableException("The quote query failed safely.", exception);
        }
    }

    private static async Task<(PricingTier Tier, Guid? PrivateTariffId)> SelectPricingTierAsync(
        PricingDbContext dbContext,
        CreateQuoteCommand command,
        CancellationToken cancellationToken)
    {
        ClientAccountProjection? account = null;
        TariffRule? privateRule = null;
        if (command.ClientAccountId is { } accountId)
        {
            account = await dbContext.ClientAccounts.AsNoTracking()
                .SingleOrDefaultAsync(value => value.Id == accountId, cancellationToken);
            if (account?.PrivateTariffId is { } privateTariffId)
            {
                privateRule = await dbContext.TariffRules.AsNoTracking()
                    .SingleOrDefaultAsync(rule => rule.Id == privateTariffId, cancellationToken);
            }
        }

        var selection = new PricingTierSelector().Select(
            command.ClientAccountId,
            account is null ? null : new ClientPricingProfile(account.Status == "ACTIVE", account.PrivateTariffId),
            privateRule);
        return selection.Failure switch
        {
            PricingTierSelectionFailure.None => (selection.Tier, selection.PrivateTariffId),
            PricingTierSelectionFailure.ClientAccountUnavailable => throw new QuoteValidationException(QuoteValidationCode.ClientAccountUnavailable),
            PricingTierSelectionFailure.VolumePricingUnavailable => throw new QuoteValidationException(QuoteValidationCode.ClientAccountRequiresVolumePricing),
            _ => throw new QuoteValidationException(QuoteValidationCode.NoTariffRule),
        };
    }

    private void ThrowIfFailed(
        TariffEvaluationResult evaluation,
        Guid organizationId,
        Guid cityId,
        ServiceType serviceType)
    {
        if (evaluation.Failure == TariffEvaluationFailure.None)
        {
            return;
        }

        if (evaluation.Failure == TariffEvaluationFailure.AmbiguousRule)
        {
            logger.LogWarning(
                "Ambiguous tariff configuration for organization {OrganizationId}, city {CityId}, service {ServiceType}.",
                organizationId,
                cityId,
                serviceType.ToContractValue());
        }

        if (evaluation.Failure == TariffEvaluationFailure.PolicyVersionMissing)
        {
            logger.LogWarning(
                "Tariff rule without a pricing policy version for organization {OrganizationId}, city {CityId}, service {ServiceType}; the rule is not quotable until it carries one.",
                organizationId,
                cityId,
                serviceType.ToContractValue());
        }

        throw new QuoteValidationException(evaluation.Failure switch
        {
            TariffEvaluationFailure.NoRule => QuoteValidationCode.NoTariffRule,
            TariffEvaluationFailure.AmbiguousRule => QuoteValidationCode.AmbiguousTariffRule,
            TariffEvaluationFailure.PolicyVersionMissing => QuoteValidationCode.NoTariffRule,
            TariffEvaluationFailure.TaxModeBlocked => QuoteValidationCode.TaxModeBlocked,
            TariffEvaluationFailure.ConsolidatedRouteRequired => QuoteValidationCode.ConsolidatedRouteRequired,
            _ => QuoteValidationCode.InvalidRequest,
        });
    }

    private static ResolveQuoteLocationCommand ToLocationCommand(
        CreateQuoteCommand command,
        QuoteAddressInput address,
        QuoteLocationRole role) => new(
            command.ActorId,
            command.OrganizationId,
            CreateLocationSubkey(command.OrganizationId, command.IdempotencyKey, role),
            role,
            address.AddressText,
            address.ContactName,
            address.Phone,
            address.References,
            address.Lat!.Value,
            address.Lng!.Value,
            command.RequestId);

    private static QuoteLocationResult RequireResolved(ResolveQuoteLocationResult result)
    {
        if (result.Status == QuoteLocationResolutionStatus.Resolved && result.Location is not null)
        {
            return result.Location;
        }

        throw new QuoteValidationException(result.Status switch
        {
            QuoteLocationResolutionStatus.OutsideCoverage => QuoteValidationCode.OutsideCoverage,
            QuoteLocationResolutionStatus.ExcludedZone => QuoteValidationCode.ExcludedZone,
            QuoteLocationResolutionStatus.AmbiguousCoverage => QuoteValidationCode.AmbiguousLocation,
            _ => QuoteValidationCode.InvalidRequest,
        });
    }

    internal static string CreateLocationSubkey(Guid organizationId, string idempotencyKey, QuoteLocationRole role)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"PRC-001-LOCATION\0{organizationId:D}\0{idempotencyKey}\0{role.ToString().ToUpperInvariant()}"));
        return $"prcloc-{Convert.ToHexStringLower(hash)}";
    }

    internal static byte[] ComputeInputHash(CreateQuoteCommand command)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("organization_id", command.OrganizationId);
            if (command.ClientAccountId is { } accountId) writer.WriteString("client_account_id", accountId); else writer.WriteNull("client_account_id");
            WriteAddress(writer, "origin", command.Origin);
            WriteAddress(writer, "destination", command.Destination);
            writer.WriteString("service_type", command.ServiceType);
            writer.WriteBoolean("consolidated_route", command.ConsolidatedRoute);
            writer.WriteStartArray("packages");
            foreach (var package in command.Packages)
            {
                writer.WriteStartObject();
                writer.WriteString("description", package.Description);
                writer.WriteNumber("weight_grams", package.WeightGrams);
                writer.WriteNumber("declared_value_cents", package.DeclaredValueCents);
                WriteNullable(writer, "length_mm", package.LengthMm);
                WriteNullable(writer, "width_mm", package.WidthMm);
                WriteNullable(writer, "height_mm", package.HeightMm);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();

            // LOW-PRICE-MANUAL-AUTH-2026-10-02: the authorization is part of the fingerprint only when present, so
            // a request without one keeps its previous hash. The caller is included: the override records who
            // authorized, so another actor reusing the key with the same reason is a conflict, not a replay.
            if (command.LowPriceAuthorization is { } authorization)
            {
                writer.WriteStartObject("low_price_authorization");
                writer.WriteString("actor_id", command.ActorId);
                writer.WriteString("reason", authorization.Reason);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return SHA256.HashData(stream.ToArray());
    }

    private IReadOnlyList<QuotePackageInput> CreatePackageSnapshot(IReadOnlyList<QuotePackageInput> packages)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(packages, JsonOptions));
        var redactedJson = redactor.Redact(document.RootElement).Json;
        return JsonSerializer.Deserialize<QuotePackageInput[]>(redactedJson, JsonOptions)
            ?? throw new QuoteValidationException(QuoteValidationCode.InvalidRequest);
    }

    private static Dictionary<string, object?> CreateRequestSnapshot(
        CreateQuoteCommand command,
        QuoteLocationResult origin,
        QuoteLocationResult destination,
        Guid? sharedServiceAreaId) => new(StringComparer.Ordinal)
        {
            ["client_account_id"] = command.ClientAccountId,
            ["origin_location_id"] = origin.LocationId,
            ["destination_location_id"] = destination.LocationId,
            ["city_id"] = origin.CityId,
            ["service_area_id"] = sharedServiceAreaId,
            ["service_type"] = command.ServiceType,
            ["consolidated_route"] = command.ConsolidatedRoute,
            ["package_count"] = command.Packages.Count,
            ["has_dimensions"] = command.Packages.Any(package => package.LengthMm is not null || package.WidthMm is not null || package.HeightMm is not null),
        };

    private static QuoteResult ToResult(Quote quote) => new(
        quote.Id,
        new MoneyResult(Money.Currency, checked(quote.SubtotalCents - quote.DiscountCents)),
        new MoneyResult(Money.Currency, quote.TaxCents),
        new MoneyResult(Money.Currency, quote.TotalCents),
        quote.RuleIds,
        JsonSerializer.Deserialize<QuoteBreakdownLine[]>(quote.Breakdown, JsonOptions) ?? [],
        quote.ExpiresAt,
        quote.OriginLocationId,
        quote.DestinationLocationId,
        quote.ServiceType.ToContractValue(),
        quote.ConsolidatedRoute,
        JsonSerializer.Deserialize<QuotePackageInput[]>(quote.PackageSnapshot, JsonOptions) ?? [],
        quote.CityId,
        quote.ServiceAreaId,
        quote.PricingTier.ToContractValue(),
        quote.MinimumTotalCentsSnapshot,
        quote.PricingPolicyVersion,
        quote.Status.ToContractValue(),
        JsonSerializer.Deserialize<Dictionary<string, object?>>(quote.RequestSnapshotRedacted, JsonOptions)
            ?? new Dictionary<string, object?>(),
        ReadLowPriceAuthorization(quote.FinancialOverride));

    /// <summary>
    /// LOW-PRICE-MANUAL-AUTH-2026-10-02: the AI-06 financial_override of the quote (actor_id, reason, valid_until).
    /// valid_until is UTC with microseconds, the precision PostgreSQL keeps for expires_at. The order created from
    /// the quote copies the value unchanged.
    /// </summary>
    internal static string SerializeFinancialOverride(LowPriceAuthorization authorization)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("actor_id", authorization.ActorId);
            writer.WriteString("reason", authorization.Reason);
            writer.WriteString(
                "valid_until",
                authorization.ValidUntil.UtcDateTime.ToString(
                    "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Reads a stored financial_override back. A value that does not carry a valid actor, reason and validity
    /// fails closed (503): it is never shown as an authorization it is not.
    /// </summary>
    internal static QuoteLowPriceAuthorizationResult? ReadLowPriceAuthorization(string? financialOverride)
    {
        if (financialOverride is null)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(financialOverride);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("actor_id", out var actor) && actor.ValueKind == JsonValueKind.String &&
                actor.TryGetGuid(out var actorId) && actorId != Guid.Empty &&
                root.TryGetProperty("reason", out var reason) && reason.ValueKind == JsonValueKind.String &&
                LowPriceAuthorization.IsValidReason(reason.GetString()) &&
                root.TryGetProperty("valid_until", out var validUntil) && validUntil.ValueKind == JsonValueKind.String &&
                validUntil.TryGetDateTimeOffset(out var validUntilValue))
            {
                return new QuoteLowPriceAuthorizationResult(actorId, reason.GetString()!, validUntilValue.ToUniversalTime());
            }
        }
        catch (JsonException)
        {
        }

        throw new QuoteServiceUnavailableException("The stored financial override is not a valid authorization.");
    }

    internal static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % 10), TimeSpan.Zero);

    /// <summary>
    /// Append-only evidence of a manual low price authorization: who authorized, why, which quote and its total. The
    /// audit action names the decision; every value here passes the audit redactor unchanged (a dated decision id
    /// would not: its digit run reads as a phone number), so the stored evidence never holds "[REDACTED]".
    /// </summary>
    internal static JsonElement CreateLowPriceAuditPayload(Quote quote, LowPriceAuthorization authorization) =>
        JsonSerializer.SerializeToElement(
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["quote_id"] = quote.Id,
                ["actor_id"] = authorization.ActorId,
                ["reason"] = authorization.Reason,
                ["total_cents"] = quote.TotalCents,
                ["currency"] = quote.Currency,
                ["pricing_tier"] = quote.PricingTier.ToContractValue(),
                ["consolidated_route"] = quote.ConsolidatedRoute,
                ["valid_until"] = authorization.ValidUntil,
            });

    private async Task WriteLowPriceAuthorizationAuditAsync(
        PricingDbContext dbContext,
        CreateQuoteCommand command,
        Quote quote,
        LowPriceAuthorization authorization,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        var payload = CreateLowPriceAuditPayload(quote, authorization);
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
        await auditWriter.WriteAsync(
            connection,
            transaction,
            new AuditEntry(
                Guid.NewGuid(),
                command.OrganizationId,
                command.ActorId,
                LowPriceAuthorizedAuditAction,
                "QUOTE",
                quote.Id,
                command.RequestId,
                redactor.Redact(payload),
                occurredAt.ToUniversalTime()),
            cancellationToken);
    }

    private static async Task InsertQuoteAsync(PricingDbContext dbContext, Quote quote, CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO pricing.quotes(
              id,owner_org_id,client_account_id,city_id,service_area_id,origin_location_id,destination_location_id,
              service_type,pricing_tier,consolidated_route,subtotal_cents,discount_cents,tax_cents,total_cents,
              minimum_total_cents_snapshot,currency,pricing_policy_version,rule_ids,request_snapshot_redacted,
              package_snapshot,pii_snapshot_ciphertext,pii_key_version,breakdown,input_hash,financial_override,
              status,expires_at,consumed_at,created_at)
            VALUES (
              @id,@owner_org_id,@client_account_id,@city_id,@service_area_id,@origin_location_id,@destination_location_id,
              @service_type,@pricing_tier,@consolidated_route,@subtotal_cents,@discount_cents,@tax_cents,@total_cents,
              @minimum_total_cents_snapshot,@currency,@pricing_policy_version,@rule_ids,@request_snapshot_redacted,
              @package_snapshot,NULL,NULL,@breakdown,@input_hash,@financial_override,@status,@expires_at,NULL,@created_at)
            """,
            connection,
            transaction);
        AddQuoteParameters(command, quote);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddQuoteParameters(NpgsqlCommand command, Quote quote)
    {
        command.Parameters.Add(P("id", NpgsqlDbType.Uuid, quote.Id));
        command.Parameters.Add(P("owner_org_id", NpgsqlDbType.Uuid, quote.OwnerOrganizationId));
        command.Parameters.Add(P("client_account_id", NpgsqlDbType.Uuid, quote.ClientAccountId));
        command.Parameters.Add(P("city_id", NpgsqlDbType.Uuid, quote.CityId));
        command.Parameters.Add(P("service_area_id", NpgsqlDbType.Uuid, quote.ServiceAreaId));
        command.Parameters.Add(P("origin_location_id", NpgsqlDbType.Uuid, quote.OriginLocationId));
        command.Parameters.Add(P("destination_location_id", NpgsqlDbType.Uuid, quote.DestinationLocationId));
        command.Parameters.Add(P("service_type", NpgsqlDbType.Text, quote.ServiceType.ToContractValue()));
        command.Parameters.Add(P("pricing_tier", NpgsqlDbType.Text, quote.PricingTier.ToContractValue()));
        command.Parameters.Add(P("consolidated_route", NpgsqlDbType.Boolean, quote.ConsolidatedRoute));
        command.Parameters.Add(P("subtotal_cents", NpgsqlDbType.Bigint, quote.SubtotalCents));
        command.Parameters.Add(P("discount_cents", NpgsqlDbType.Bigint, quote.DiscountCents));
        command.Parameters.Add(P("tax_cents", NpgsqlDbType.Bigint, quote.TaxCents));
        command.Parameters.Add(P("total_cents", NpgsqlDbType.Bigint, quote.TotalCents));
        command.Parameters.Add(P("minimum_total_cents_snapshot", NpgsqlDbType.Bigint, quote.MinimumTotalCentsSnapshot));
        command.Parameters.Add(P("currency", NpgsqlDbType.Char, quote.Currency));
        command.Parameters.Add(P("pricing_policy_version", NpgsqlDbType.Text, quote.PricingPolicyVersion));
        command.Parameters.Add(P("rule_ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, quote.RuleIds));
        command.Parameters.Add(P("request_snapshot_redacted", NpgsqlDbType.Jsonb, quote.RequestSnapshotRedacted));
        command.Parameters.Add(P("package_snapshot", NpgsqlDbType.Jsonb, quote.PackageSnapshot));
        command.Parameters.Add(P("breakdown", NpgsqlDbType.Jsonb, quote.Breakdown));
        command.Parameters.Add(P("input_hash", NpgsqlDbType.Bytea, quote.InputHash));
        command.Parameters.Add(P("financial_override", NpgsqlDbType.Jsonb, quote.FinancialOverride));
        command.Parameters.Add(P("status", NpgsqlDbType.Text, quote.Status.ToContractValue()));
        command.Parameters.Add(P("expires_at", NpgsqlDbType.TimestampTz, quote.ExpiresAt));
        command.Parameters.Add(P("created_at", NpgsqlDbType.TimestampTz, quote.CreatedAt));
    }

    private static Task AcquireIdempotencyLockAsync(
        PricingDbContext dbContext,
        CreateQuoteCommand command,
        CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({command.OrganizationId:D} || ':' || {IdempotencyScope} || ':' || {command.IdempotencyKey}, 0));",
            cancellationToken);

    private static Task<IdempotencyRecord?> FindIdempotencyRecordAsync(
        PricingDbContext dbContext,
        CreateQuoteCommand command,
        CancellationToken cancellationToken) =>
        dbContext.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(record =>
            record.OwnerOrganizationId == command.OrganizationId &&
            record.Scope == IdempotencyScope &&
            record.IdempotencyKey == command.IdempotencyKey,
            cancellationToken);

    private static void EnsureMatchingHash(IdempotencyRecord record, byte[] inputHash)
    {
        if (!CryptographicOperations.FixedTimeEquals(record.RequestHash, inputHash))
        {
            throw new QuoteValidationException(QuoteValidationCode.IdempotencyConflict);
        }
    }

    private static QuoteResult? ReadCompletedResponse(IdempotencyRecord record)
    {
        if (record.ResponseStatus is null && record.ResponseBody is null && record.ResourceId is null)
        {
            return null;
        }

        if (record.ResponseStatus != 201 ||
            string.IsNullOrWhiteSpace(record.ResponseBody) ||
            record.ResourceId is null)
        {
            throw new QuoteServiceUnavailableException("The idempotency record is inconsistent.");
        }

        var response = JsonSerializer.Deserialize<QuoteResult>(record.ResponseBody, JsonOptions)
            ?? throw new QuoteServiceUnavailableException("The idempotency response is invalid.");
        if (response.Id != record.ResourceId)
        {
            throw new QuoteServiceUnavailableException("The idempotency resource is inconsistent.");
        }

        return response;
    }

    internal static DateTimeOffset CalculateReservationExpiration(DateTimeOffset createdAt, PricingOptions pricingOptions)
    {
        ArgumentNullException.ThrowIfNull(pricingOptions);
        var quoteLifetime = TimeSpan.FromMinutes(pricingOptions.QuoteLifetimeMinutes);
        var operationBuffer = TimeSpan.FromSeconds(checked(pricingOptions.CommandTimeoutSeconds * 3L));
        return createdAt.Add(quoteLifetime).Add(operationBuffer);
    }

    private static async Task InsertIdempotencyReservationAsync(
        PricingDbContext dbContext,
        CreateQuoteCommand create,
        byte[] inputHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO platform.idempotency_keys(
              owner_org_id,scope,idempotency_key,request_hash,response_status,response_body,resource_id,created_at,expires_at)
            VALUES (@owner_org_id,@scope,@idempotency_key,@request_hash,NULL,NULL,NULL,@created_at,@expires_at)
            """,
            connection,
            transaction);
        command.Parameters.Add(P("owner_org_id", NpgsqlDbType.Uuid, create.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("idempotency_key", NpgsqlDbType.Text, create.IdempotencyKey));
        command.Parameters.Add(P("request_hash", NpgsqlDbType.Bytea, inputHash));
        command.Parameters.Add(P("created_at", NpgsqlDbType.TimestampTz, createdAt));
        command.Parameters.Add(P("expires_at", NpgsqlDbType.TimestampTz, expiresAt));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new QuoteServiceUnavailableException("The idempotency reservation was not persisted.");
        }
    }

    private static async Task CompleteIdempotencyReservationAsync(
        PricingDbContext dbContext,
        CreateQuoteCommand create,
        byte[] inputHash,
        string responseJson,
        Quote quote,
        CancellationToken cancellationToken)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
        var transaction = (NpgsqlTransaction)dbContext.Database.CurrentTransaction!.GetDbTransaction();
        await using var command = new NpgsqlCommand(
            """
            UPDATE platform.idempotency_keys
            SET response_status=201,response_body=@response_body,resource_id=@resource_id,expires_at=@expires_at
            WHERE owner_org_id=@owner_org_id
              AND scope=@scope
              AND idempotency_key=@idempotency_key
              AND request_hash=@request_hash
              AND response_status IS NULL
              AND response_body IS NULL
              AND resource_id IS NULL
            """,
            connection,
            transaction);
        command.Parameters.Add(P("owner_org_id", NpgsqlDbType.Uuid, create.OrganizationId));
        command.Parameters.Add(P("scope", NpgsqlDbType.Text, IdempotencyScope));
        command.Parameters.Add(P("idempotency_key", NpgsqlDbType.Text, create.IdempotencyKey));
        command.Parameters.Add(P("request_hash", NpgsqlDbType.Bytea, inputHash));
        command.Parameters.Add(P("response_body", NpgsqlDbType.Jsonb, responseJson));
        command.Parameters.Add(P("resource_id", NpgsqlDbType.Uuid, quote.Id));
        command.Parameters.Add(P("expires_at", NpgsqlDbType.TimestampTz, quote.ExpiresAt));
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new QuoteServiceUnavailableException("The idempotency reservation was not completed.");
        }
    }

    private static NpgsqlParameter P(string name, NpgsqlDbType type, object? value) => new(name, type)
    {
        Value = value ?? DBNull.Value,
    };

    private static void Validate(CreateQuoteCommand command)
    {
        if (command.ActorId == Guid.Empty || command.OrganizationId == Guid.Empty ||
            !IdempotencyKeyPolicy.IsValid(command.IdempotencyKey) ||
            command.Origin is null || command.Destination is null || command.Packages is null ||
            !IsServiceType(command.ServiceType))
        {
            throw new QuoteValidationException(QuoteValidationCode.InvalidRequest);
        }

        ValidateAddress(command.Origin);
        ValidateAddress(command.Destination);
        if (!PricingPackagePolicy.IsValid(command.Packages.Select(package => new PricingPackage(
                package.Description,
                package.WeightGrams,
                package.DeclaredValueCents,
                package.LengthMm,
                package.WidthMm,
                package.HeightMm)).ToArray()))
        {
            throw new QuoteValidationException(QuoteValidationCode.InvalidRequest);
        }
    }

    /// <summary>
    /// LOW-PRICE-MANUAL-AUTH-2026-10-02: the reason arrives trimmed (1 to 200 characters) and must not carry
    /// personal data. A reason the audit redactor would alter (an email, a phone number, an address, a credential)
    /// is refused with 422 instead of being stored on the quote, the order and the audit log.
    /// </summary>
    private void ValidateLowPriceAuthorization(CreateQuoteCommand command)
    {
        if (command.LowPriceAuthorization is not { } authorization)
        {
            return;
        }

        if (!LowPriceAuthorization.IsValidReason(authorization.Reason))
        {
            throw new QuoteValidationException(QuoteValidationCode.InvalidRequest);
        }

        var probe = JsonSerializer.SerializeToElement(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["reason"] = authorization.Reason,
        });
        string redacted;
        try
        {
            redacted = redactor.Redact(probe).Json;
        }
        catch (AuditRedactionException)
        {
            throw new QuoteValidationException(QuoteValidationCode.InvalidRequest);
        }

        using var redactedDocument = JsonDocument.Parse(redacted);
        if (!redactedDocument.RootElement.TryGetProperty("reason", out var redactedReason) ||
            !string.Equals(redactedReason.GetString(), authorization.Reason, StringComparison.Ordinal))
        {
            throw new QuoteValidationException(QuoteValidationCode.LowPriceAuthorizationReasonRejected);
        }
    }

    private static void ValidateAddress(QuoteAddressInput address)
    {
        if (string.IsNullOrWhiteSpace(address.AddressText) || address.AddressText.Trim().Length < 8 ||
            string.IsNullOrWhiteSpace(address.ContactName) || string.IsNullOrWhiteSpace(address.Phone) ||
            address.References?.Length > 500)
        {
            throw new QuoteValidationException(QuoteValidationCode.InvalidRequest);
        }

        if (address.Lat is null || address.Lng is null)
        {
            throw new QuoteValidationException(QuoteValidationCode.CoordinatesRequired);
        }

        if (address.Lat is < -90 or > 90 || address.Lng is < -180 or > 180 ||
            double.IsNaN(address.Lat.Value) || double.IsNaN(address.Lng.Value) ||
            double.IsInfinity(address.Lat.Value) || double.IsInfinity(address.Lng.Value))
        {
            throw new QuoteValidationException(QuoteValidationCode.InvalidRequest);
        }
    }

    private static void WriteAddress(Utf8JsonWriter writer, string name, QuoteAddressInput address)
    {
        writer.WriteStartObject(name);
        writer.WriteString("address_text", address.AddressText);
        writer.WriteString("contact_name", address.ContactName);
        writer.WriteString("phone", address.Phone);
        writer.WriteNumber("lat", address.Lat!.Value);
        writer.WriteNumber("lng", address.Lng!.Value);
        if (address.References is null) writer.WriteNull("references"); else writer.WriteString("references", address.References);
        writer.WriteEndObject();
    }

    private static void WriteNullable(Utf8JsonWriter writer, string name, int? value)
    {
        if (value is { } actual) writer.WriteNumber(name, actual); else writer.WriteNull(name);
    }

    private static bool IsServiceType(string? value) =>
        value is "SAME_DAY" or "URGENT" or "SCHEDULED_ROUTE";

    private static ServiceType ParseServiceType(string value) => value switch
    {
        "SAME_DAY" => ServiceType.SameDay,
        "URGENT" => ServiceType.Urgent,
        "SCHEDULED_ROUTE" => ServiceType.ScheduledRoute,
        _ => throw new QuoteValidationException(QuoteValidationCode.InvalidRequest),
    };
}
