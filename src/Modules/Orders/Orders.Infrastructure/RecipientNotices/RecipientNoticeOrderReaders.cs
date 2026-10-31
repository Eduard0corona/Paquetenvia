using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.Orders;
using Orders.Application.RecipientNotices;
using Orders.Application.Tracking;
using Orders.Infrastructure.Lifecycle;
using Orders.Infrastructure.Tracking;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Orders.Infrastructure.RecipientNotices;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.expansion.reads</c>, <c>validity.check</c> and
/// <c>tracking_link.read</c>; AI-08 NTF-005): the order of a recipient notice, read by the Worker in one short tenant
/// transaction of the owner organization - BEGIN, <c>set_config('app.current_org_ids', ..., true)</c> with only that
/// organization, <c>SET LOCAL ROLE paqueteria_worker</c> (NOBYPASSRLS, FORCE RLS) - and committed before the caller does
/// anything else. It reads only columns <c>paqueteria_worker</c> already reads (AI-18 recipient notices step 4): the
/// order's public id, status and destination location, and the active derived link (generation, key version and token
/// hash) of <c>orders.public_tracking_tokens</c>. It writes nothing.
/// </summary>
internal sealed class PostgreSqlRecipientNoticeOrderReader(OrdersWorkerDataSource worker) : IRecipientNoticeOrderReader
{
    private const int CommandTimeoutSeconds = 10;

    public async Task<RecipientNoticeOrderSnapshot?> ReadAsync(
        Guid ownerOrganizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        if (ownerOrganizationId == Guid.Empty || orderId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await worker.DataSource.OpenConnectionAsync(cancellationToken);
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

        RecipientNoticeOrderSnapshot? snapshot = null;
        await using (var command = new NpgsqlCommand(
            """
            SELECT o.public_id,o.status,o.destination_location_id,t.generation,t.key_version,t.token_hash
            FROM orders.orders o
            LEFT JOIN LATERAL (
              SELECT l.generation,l.key_version,l.token_hash
              FROM orders.public_tracking_tokens l
              WHERE l.order_id=o.id AND l.owner_org_id=o.owner_org_id
                AND l.key_version IS NOT NULL AND l.revoked_at IS NULL AND l.expires_at>clock_timestamp()
              ORDER BY l.generation DESC
              LIMIT 1) t ON true
            WHERE o.id=@order_id AND o.owner_org_id=@owner_org_id
            """,
            connection,
            transaction)
        {
            CommandTimeout = CommandTimeoutSeconds,
        })
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid) { TypedValue = orderId });
            command.Parameters.Add(new NpgsqlParameter<Guid>("owner_org_id", NpgsqlDbType.Uuid)
            {
                TypedValue = ownerOrganizationId,
            });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                snapshot = new RecipientNoticeOrderSnapshot(
                    orderId,
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetGuid(2),
                    reader.IsDBNull(3)
                        ? null
                        : new RecipientNoticeLinkRecord(
                            reader.GetInt32(3),
                            reader.GetInt32(4),
                            reader.GetFieldValue<byte[]>(5)));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return snapshot;
    }
}

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-24 <c>recipient_notices.consent.effect</c> and <c>expansion.reads</c>; AI-08
/// NTF-004; NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23 N3 choice 7): whether the order recorded the WhatsApp consent, read
/// by the Worker in one short tenant transaction of the owner organization - BEGIN, <c>set_config('app.current_org_ids',
/// ..., true)</c> with only that organization, <c>SET LOCAL ROLE paqueteria_worker</c> (NOBYPASSRLS, FORCE RLS, SELECT
/// only on <c>orders.recipient_notice_consents</c>, AI-18 recipient notices step 2) - and committed before the caller
/// does anything else. Only the current consent version counts. It reads a boolean and writes nothing.
/// </summary>
internal sealed class PostgreSqlRecipientNoticeConsentReader(OrdersWorkerDataSource worker) : IRecipientNoticeConsentReader
{
    private const int CommandTimeoutSeconds = 10;

    public async Task<bool> HasWhatsAppConsentAsync(
        Guid ownerOrganizationId,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        if (ownerOrganizationId == Guid.Empty || orderId == Guid.Empty)
        {
            return false;
        }

        await using var connection = await worker.DataSource.OpenConnectionAsync(cancellationToken);
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

        bool consented;
        await using (var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
              SELECT 1 FROM orders.recipient_notice_consents c
              WHERE c.order_id=@order_id AND c.owner_org_id=@owner_org_id
                AND c.channel='WHATSAPP' AND c.consent_version=@consent_version)
            """,
            connection,
            transaction)
        {
            CommandTimeout = CommandTimeoutSeconds,
        })
        {
            command.Parameters.Add(new NpgsqlParameter<Guid>("order_id", NpgsqlDbType.Uuid) { TypedValue = orderId });
            command.Parameters.Add(new NpgsqlParameter<Guid>("owner_org_id", NpgsqlDbType.Uuid)
            {
                TypedValue = ownerOrganizationId,
            });
            command.Parameters.Add(new NpgsqlParameter<string>("consent_version", NpgsqlDbType.Text)
            {
                TypedValue = RecipientNoticeConsentPolicy.Version,
            });
            consented = await command.ExecuteScalarAsync(cancellationToken) is true;
        }

        await transaction.CommitAsync(cancellationToken);
        return consented;
    }
}

/// <summary>
/// AI-24 <c>recipient_notices.tracking_link.derive</c> with the Worker's own key ring (<c>PublicTracking:LinkKeys</c>, the
/// Key Vault secret <c>public-tracking-link-key</c> mapped by AI-08 NTF-008) and <c>PublicTracking:PublicBaseUrl</c>. The
/// token is derived in memory with <c>TrackingLinkTokenDerivation</c>, accepted only when the SHA-256 of its exact UTF-8
/// bytes equals the stored hash (compared in constant time), and never stored or logged.
/// </summary>
internal sealed class RecipientNoticeTrackingLinks(
    PublicTrackingLinkKeyRing keyRing,
    IOptions<PublicTrackingOptions> options,
    PublicTrackingBaseUrlPolicy baseUrlPolicy) : IRecipientNoticeTrackingLinks
{
    public RecipientNoticeLinkReadiness Readiness =>
        !keyRing.IsAvailable || !baseUrlPolicy.IsValid(options.Value.PublicBaseUrl)
            ? RecipientNoticeLinkReadiness.Unavailable
            : keyRing.IsSynthetic
                ? RecipientNoticeLinkReadiness.Synthetic
                : RecipientNoticeLinkReadiness.Configured;

    public RecipientNoticeTrackingLink? TryBuild(Guid orderId, RecipientNoticeLinkRecord link)
    {
        ArgumentNullException.ThrowIfNull(link);
        var baseUrl = options.Value.PublicBaseUrl;
        if (orderId == Guid.Empty || link.Generation < 1 || link.TokenHash is not { Length: 32 } ||
            !keyRing.IsAvailable || !baseUrlPolicy.IsValid(baseUrl) ||
            !keyRing.TryDerive(link.KeyVersion, orderId, link.Generation, out var token))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(token);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), link.TokenHash))
            {
                return null;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }

        return new RecipientNoticeTrackingLink(token, baseUrlPolicy.BuildUrl(baseUrl!, token));
    }
}

public static class RecipientNoticeOrderDependencyInjection
{
    /// <summary>
    /// NTF-RECIPIENT-NOTICES-2026-10-20 (AI-08 NTF-005 and NTF-004) Worker composition: the order reader, the consent
    /// reader of <c>orders.recipient_notice_consents</c> and the tracking link deriver with the Worker's key ring. Only
    /// Development and Testing may derive with the synthetic runtime key or build links on an http loopback origin, as in
    /// the API; any configured key must be valid everywhere.
    /// </summary>
    public static IServiceCollection AddOrdersRecipientNoticeReaders(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        var allowSyntheticLinkKey = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var baseUrlPolicy = new PublicTrackingBaseUrlPolicy(allowLoopbackHttp: allowSyntheticLinkKey);
        services.TryAddSingleton(baseUrlPolicy);
        services.AddOptions<PublicTrackingOptions>()
            .Bind(configuration.GetSection(PublicTrackingOptions.SectionName))
            .Validate(options => string.IsNullOrWhiteSpace(options.PublicBaseUrl) ||
                    baseUrlPolicy.IsValid(options.PublicBaseUrl),
                "PublicTracking:PublicBaseUrl must be an absolute https origin without path, query or fragment.")
            .Validate(PublicTrackingLinkKeyRing.IsValid,
                "PublicTracking must hold 1 to 8 LinkKeys with versions 1 to 32767, each Base64 of 32 to 128 bytes, and CurrentLinkKeyVersion must be one of them.")
            .ValidateOnStart();
        services.TryAddSingleton(serviceProvider => new PublicTrackingLinkKeyRing(
            serviceProvider.GetRequiredService<IOptions<PublicTrackingOptions>>().Value,
            allowSyntheticLinkKey));
        services.AddRuntimeDataSource(configuration, RuntimeDatabaseRole.Worker);
        services.TryAddSingleton<OrdersWorkerDataSource>();
        // These replace the unavailable fallbacks the Notifications lane registers for hosts without them.
        services.RemoveAll<IRecipientNoticeOrderReader>();
        services.AddSingleton<IRecipientNoticeOrderReader, PostgreSqlRecipientNoticeOrderReader>();
        services.RemoveAll<IRecipientNoticeTrackingLinks>();
        services.AddSingleton<IRecipientNoticeTrackingLinks, RecipientNoticeTrackingLinks>();
        services.RemoveAll<IRecipientNoticeConsentReader>();
        services.AddSingleton<IRecipientNoticeConsentReader, PostgreSqlRecipientNoticeConsentReader>();
        return services;
    }
}
