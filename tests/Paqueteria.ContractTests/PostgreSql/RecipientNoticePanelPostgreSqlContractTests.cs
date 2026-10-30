using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure.Persistence;
using Notifications.Infrastructure.RecipientNotices.Panel;
using Npgsql;
using Paqueteria.Application.Auditing;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Application.Security;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Security;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// NTF-RECIPIENT-NOTICE-PANEL-2026-10-30 (AI-08 NTF-007 required tests "no recipient in the answer; idempotent stop";
/// AI-24 <c>recipient_notices.dispatcher_view</c> and <c>opt_out.dispatcher</c>) on a real PostgreSQL 18 / PostGIS 3.6
/// through the API's store on the <c>paqueteria_app</c> pool (NOBYPASSRLS, FORCE RLS): the list of the owner organization
/// only (an operator, another organization or a missing order reads nothing), IN_APP rows left out, the order and the
/// cap, the two booleans and the fail-closed code set; and the stop - DISPATCHER opt-outs on both channels through the
/// receipt executor's writer, a contact already opted out not recorded again, the RECIPIENT_NOTICES_STOPPED audit row with
/// the key and the channels only, a replay that writes nothing, a key reused elsewhere, no usable contact (nothing
/// written) and a failure that rolls everything back. Every contact is a synthetic placeholder hashed under a random key.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class RecipientNoticePanelPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string Phone = "+520000000002";
    private const string Email = "destinatario@example.invalid";

    [PostgreSqlContractFact]
    public async Task The_list_reads_only_the_owner_organizations_recipient_notices_in_order_with_its_two_booleans()
    {
        var seed = await SeedAsync("NTF-007 List");
        await using var panel = Create();
        var list = await panel.Service.ListAsync(Query(seed.Owner, seed.Order), CancellationToken.None);

        // No consent box and no e-mail yet; nothing decrypted, nothing listed.
        Assert.Empty(list.Items);
        Assert.False(list.WhatsAppConsentAttested);
        Assert.False(list.EmailOnFile);

        var laterEvent = await InsertOrderEventAsync(seed, DateTimeOffset.UtcNow.AddMinutes(5));
        var email = await InsertNoticeAsync(seed, seed.OrderEvent, "EMAIL", "recipient.tracking-link", "FAILED", 3, "MESSAGING_RECIPIENT_UNDELIVERABLE", seed.CreatedAt);
        var whatsApp = await InsertNoticeAsync(seed, seed.OrderEvent, "WHATSAPP", "recipient.tracking-link", "CANCELLED", 0, "RECIPIENT_OPTED_OUT", seed.CreatedAt);
        var inTransit = await InsertNoticeAsync(seed, laterEvent, "WHATSAPP", "recipient.in-transit", "PENDING", 0, null, seed.CreatedAt.AddMinutes(5));
        await InsertInAppAsync(seed);
        await ExecuteAdminAsync(
            """
            INSERT INTO orders.recipient_notice_consents(order_id,owner_org_id,channel,consent_version,attested_by,attested_at)
            VALUES(@order,@owner,'WHATSAPP','NTF-RECIPIENT-WHATSAPP-CONSENT-V1',@actor,clock_timestamp());
            UPDATE locations.locations SET email_ciphertext=decode('0b','hex') WHERE id=@destination;
            """,
            new NpgsqlParameter("order", seed.Order), new NpgsqlParameter("owner", seed.Owner),
            new NpgsqlParameter("actor", seed.Actor), new NpgsqlParameter("destination", seed.Destination));

        list = await panel.Service.ListAsync(Query(seed.Owner, seed.Order), CancellationToken.None);

        Assert.True(list.WhatsAppConsentAttested);
        Assert.True(list.EmailOnFile);
        // By creation, WhatsApp before e-mail; the IN_APP notification of the same order is never listed.
        Assert.Equal([whatsApp, email, inTransit], list.Items.Select(item => item.NotificationId));
        Assert.Equal(
            [
                "TRACKING_LINK:WHATSAPP:CANCELLED:0:RECIPIENT_OPTED_OUT",
                "TRACKING_LINK:EMAIL:FAILED:3:MESSAGING_RECIPIENT_UNDELIVERABLE",
                "IN_TRANSIT:WHATSAPP:PENDING:0:-",
            ],
            list.Items.Select(item => $"{item.Notice}:{item.Channel}:{item.Status}:{item.Attempts}:{item.LastCode ?? "-"}"));

        // The operator of the order, another organization and a missing order read nothing (the uniform 404).
        await ExecuteAdminAsync("UPDATE orders.orders SET operator_org_id=@operator WHERE id=@order",
            new NpgsqlParameter("operator", seed.Other), new NpgsqlParameter("order", seed.Order));
        foreach (var query in new[] { Query(seed.Other, seed.Order), Query(seed.Owner, Guid.NewGuid()) })
        {
            await Assert.ThrowsAsync<RecipientNoticePanelNotFoundException>(() =>
                panel.Service.ListAsync(query, CancellationToken.None));
        }

        // RLS really admits the operator to the order: only the explicit owner filter makes it a 404.
        Assert.Equal(1L, await AppScalarAsync<long>(
            seed.Other,
            "SELECT count(*) FROM orders.orders WHERE id=@order",
            new NpgsqlParameter("order", seed.Order)));

        // A stored code outside the closed set fails the read closed and is never passed on.
        await ExecuteAdminAsync("UPDATE notifications.notifications SET last_provider_attempt_code='SYNTHETIC_ACCEPTED' WHERE id=@id",
            new NpgsqlParameter("id", inTransit));
        await Assert.ThrowsAsync<RecipientNoticePanelUnavailableException>(() =>
            panel.Service.ListAsync(Query(seed.Owner, seed.Order), CancellationToken.None));
    }

    [PostgreSqlContractFact]
    public async Task The_list_answers_at_most_100_notices()
    {
        var seed = await SeedAsync("NTF-007 Cap");
        await ExecuteAdminAsync(
            """
            WITH events AS (
              INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,public_event_code,payload,occurred_at)
              SELECT gen_random_uuid(),@order,@owner,2+g,'ORDER_STATUS_CHANGED','ORDER_CREATED','{}',clock_timestamp()
              FROM generate_series(1,51) g
              RETURNING id)
            INSERT INTO notifications.notifications(
              id,owner_org_id,order_id,order_event_id,recipient_kind,channel,status,attempts,version,
              template_key,template_version,variables_snapshot,source_event_id,created_at,updated_at)
            SELECT gen_random_uuid(),@owner,@order,e.id,'ORDER_DESTINATION',c.channel,'PENDING',0,1,
              'recipient.in-transit',1,'{"order_public_id":"ORD_ntf007"}'::jsonb,gen_random_uuid(),clock_timestamp(),clock_timestamp()
            FROM events e CROSS JOIN (VALUES ('WHATSAPP'),('EMAIL')) AS c(channel)
            """,
            new NpgsqlParameter("order", seed.Order), new NpgsqlParameter("owner", seed.Owner));
        await using var panel = Create();

        var list = await panel.Service.ListAsync(Query(seed.Owner, seed.Order), CancellationToken.None);

        Assert.Equal(102L, await AdminScalarAsync<long>(
            "SELECT count(*) FROM notifications.notifications WHERE order_id=@order", new NpgsqlParameter("order", seed.Order)));
        Assert.Equal(RecipientNoticePanelContract.MaximumItems, list.Items.Count);
    }

    [PostgreSqlContractFact]
    public async Task The_stop_records_both_dispatcher_opt_outs_audits_the_channels_only_and_a_replay_writes_nothing()
    {
        var seed = await SeedAsync("NTF-007 Stop", phone: true, email: true);
        await using var panel = Create();
        var command = Command(seed);

        var result = await panel.Service.StopAsync(command, CancellationToken.None);

        Assert.Equal(seed.Order, result.OrderId);
        Assert.Equal(["WHATSAPP", "EMAIL"], result.Channels);
        var whatsAppHash = panel.Lookup.HashForLookup(RecipientContactChannel.WhatsApp, Phone).Single();
        var emailHash = panel.Lookup.HashForLookup(RecipientContactChannel.Email, Email).Single();
        Assert.Equal("WHATSAPP:DISPATCHER:1", await OptOutAsync(whatsAppHash.Hash, "WHATSAPP"));
        Assert.Equal("EMAIL:DISPATCHER:1", await OptOutAsync(emailHash.Hash, "EMAIL"));

        // T6 choice 7: one audit row on the order, the key as request_id and the channels only.
        var audit = Assert.Single(await AuditRowsAsync(seed));
        Assert.Equal($"{seed.Actor}|Order|{seed.Order}|{command.IdempotencyKey}|{{\"channels\": [\"WHATSAPP\", \"EMAIL\"]}}", audit);
        foreach (var value in new[] { Phone, "0000000002", Email, Convert.ToHexStringLower(whatsAppHash.Hash), Convert.ToHexStringLower(emailHash.Hash) })
        {
            Assert.DoesNotContain(value, audit, StringComparison.OrdinalIgnoreCase);
        }

        // The stored answer is the answer; it holds no value either.
        var stored = await AdminScalarAsync<string>(
            "SELECT response_status || '|' || response_body::text FROM platform.idempotency_keys WHERE owner_org_id=@owner AND scope=@scope AND idempotency_key=@key",
            new NpgsqlParameter("owner", seed.Owner), new NpgsqlParameter("scope", RecipientNoticePanelContract.IdempotencyScope),
            new NpgsqlParameter("key", command.IdempotencyKey));
        Assert.StartsWith("200|{\"channels\": [\"WHATSAPP\", \"EMAIL\"], \"order_id\": \"" + seed.Order, stored, StringComparison.Ordinal);
        Assert.DoesNotContain("+52", stored, StringComparison.Ordinal);

        // A replay answers the same, from the stored row, and writes nothing.
        var replay = await panel.Service.StopAsync(command, CancellationToken.None);
        Assert.Equal(result.Channels, replay.Channels);
        Assert.Equal(result.RecordedAt, replay.RecordedAt);
        Assert.Single(await AuditRowsAsync(seed));

        // Another press: audited again, the same channels, no second opt-out (already on record under that version).
        var again = await panel.Service.StopAsync(Command(seed), CancellationToken.None);
        Assert.Equal(["WHATSAPP", "EMAIL"], again.Channels);
        Assert.Equal(2, (await AuditRowsAsync(seed)).Length);
        Assert.Equal("WHATSAPP:DISPATCHER:1", await OptOutAsync(whatsAppHash.Hash, "WHATSAPP"));

        // The key used for another order is a conflict, and nothing is written for it.
        var otherOrder = await SeedOrderAsync(seed);
        var conflict = await Assert.ThrowsAsync<RecipientNoticeStopConflictException>(() =>
            panel.Service.StopAsync(command with { OrderId = otherOrder }, CancellationToken.None));
        Assert.Equal("IDEMPOTENCY_CONFLICT", conflict.Code);
        Assert.Equal(2, (await AuditRowsAsync(seed)).Length);
    }

    [PostgreSqlContractFact]
    public async Task An_opt_out_already_on_record_from_another_path_is_named_and_not_recorded_again()
    {
        var seed = await SeedAsync("NTF-007 Already", phone: true, email: false);
        await using var panel = Create();
        var hash = panel.Lookup.HashForLookup(RecipientContactChannel.WhatsApp, Phone).Single();
        Assert.True(await AppScalarAsync<bool>(
            Guid.Empty,
            "SELECT security.record_recipient_opt_out('WHATSAPP','RECIPIENT_REPLY',@hash,1,ARRAY[@hash])",
            new NpgsqlParameter("hash", hash.Hash)));

        var result = await panel.Service.StopAsync(Command(seed), CancellationToken.None);

        // T6 choice 6: named all the same, so an opt-out made elsewhere never shows; the first record stays.
        Assert.Equal(["WHATSAPP"], result.Channels);
        Assert.Equal("WHATSAPP:RECIPIENT_REPLY:1", await OptOutAsync(hash.Hash, "WHATSAPP"));
        Assert.Single(await AuditRowsAsync(seed));
    }

    [PostgreSqlContractFact]
    public async Task No_usable_contact_a_foreign_order_and_a_failure_write_nothing()
    {
        // The seeded destination keeps neither a phone nor an e-mail: 409, nothing recorded or audited.
        var seed = await SeedAsync("NTF-007 Nothing");
        await using var panel = Create();
        var conflict = await Assert.ThrowsAsync<RecipientNoticeStopConflictException>(() =>
            panel.Service.StopAsync(Command(seed), CancellationToken.None));
        Assert.Equal("RECIPIENT_CONTACT_UNAVAILABLE", conflict.Code);
        Assert.Empty(await AuditRowsAsync(seed));
        Assert.Equal(0L, await IdempotencyRowsAsync(seed.Owner));

        // Another organization, the operator and a missing order: the uniform 404 before anything is opened.
        await ExecuteAdminAsync("UPDATE orders.orders SET operator_org_id=@operator WHERE id=@order",
            new NpgsqlParameter("operator", seed.Other), new NpgsqlParameter("order", seed.Order));
        foreach (var command in new[]
                 {
                     Command(seed) with { OrganizationId = seed.Other, ActorId = seed.OtherActor },
                     Command(seed) with { OrderId = Guid.NewGuid() },
                 })
        {
            await Assert.ThrowsAsync<RecipientNoticePanelNotFoundException>(() => panel.Service.StopAsync(command, CancellationToken.None));
        }

        Assert.Equal(0, panel.Phones.Calls);

        // A failure after the opt-outs (here the audit row) rolls the whole transaction back: no opt-out, no answer.
        await ExecuteAdminAsync("UPDATE locations.locations SET phone_ciphertext=decode('0c','hex') WHERE id=@destination",
            new NpgsqlParameter("destination", seed.Destination));
        await using var failing = Create(failAudit: true);
        var hash = failing.Lookup.HashForLookup(RecipientContactChannel.WhatsApp, Phone).Single();
        await Assert.ThrowsAsync<RecipientNoticePanelUnavailableException>(() =>
            failing.Service.StopAsync(Command(seed), CancellationToken.None));
        Assert.Equal("-", await OptOutAsync(hash.Hash, "WHATSAPP"));
        Assert.Empty(await AuditRowsAsync(seed));
        Assert.Equal(0L, await IdempotencyRowsAsync(seed.Owner));
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Seed(
        Guid Owner,
        Guid Other,
        Guid Actor,
        Guid OtherActor,
        Guid Order,
        Guid OrderEvent,
        Guid Destination,
        DateTimeOffset CreatedAt);

    private sealed class Panel(
        NpgsqlDataSource dataSource,
        NotificationsDbContext context,
        RecipientNoticePanelService service,
        HmacRecipientContactLookup lookup,
        CountingPhoneOpener phones) : IAsyncDisposable
    {
        public RecipientNoticePanelService Service { get; } = service;

        public HmacRecipientContactLookup Lookup { get; } = lookup;

        public CountingPhoneOpener Phones { get; } = phones;

        public async ValueTask DisposeAsync()
        {
            await context.DisposeAsync();
            await dataSource.DisposeAsync();
        }
    }

    private Panel Create(bool failAudit = false)
    {
        var dataSource = fixture.CreateAppDataSource(maxPoolSize: 2, applicationName: "Paqueteria.NTF007.Contract");
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(new TenantTransactionGuardInterceptor(state), new TenantSaveChangesGuardInterceptor(state))
            .Options;
        var context = new NotificationsDbContext(options);
        var writer = new PostgreSqlAppendOnlyAuditWriter(state);
        var store = new PostgreSqlRecipientNoticePanelStore(
            new TenantTransactionContext<NotificationsDbContext>(context, state),
            failAudit ? new FailingAuditWriter(writer) : writer,
            new AuditPayloadRedactor(),
            NullLogger<PostgreSqlRecipientNoticePanelStore>.Instance);
        // A key of this run only, so the synthetic placeholders hash to values no other test shares.
        var lookup = new HmacRecipientContactLookup(new ContactLookupKeyRing(Options.Create(new ContactLookupOptions
        {
            CurrentKeyVersion = 1,
            Keys = new() { [1] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) },
        })));
        var phones = new CountingPhoneOpener();
        var service = new RecipientNoticePanelService(
            store,
            lookup,
            new Openers(phones, new SyntheticEmailOpener()),
            TimeProvider.System,
            NullLogger<RecipientNoticePanelService>.Instance);
        return new Panel(dataSource, context, service, lookup, phones);
    }

    private async Task<Seed> SeedAsync(string name, bool phone = false, bool email = false)
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, name);
        var other = await RecipientNoticeSeed.InsertOrganizationAsync(admin, name + " other");
        var actor = await InsertUserAsync();
        var otherActor = await InsertUserAsync();
        var (order, orderEvent, _) = await RecipientNoticeSeed.InsertOrderAsync(admin, owner);
        var destination = await AdminScalarAsync<Guid>(
            "SELECT destination_location_id FROM orders.orders WHERE id=@order", new NpgsqlParameter("order", order));
        await ExecuteAdminAsync(
            """
            UPDATE locations.locations SET
              phone_ciphertext=CASE WHEN @phone THEN decode('0c','hex') END,
              email_ciphertext=CASE WHEN @email THEN decode('0d','hex') END
            WHERE id=@destination
            """,
            new NpgsqlParameter("phone", phone), new NpgsqlParameter("email", email), new NpgsqlParameter("destination", destination));
        var createdAt = await AdminScalarAsync<DateTime>("SELECT date_trunc('second',clock_timestamp())");
        return new Seed(owner, other, actor, otherActor, order, orderEvent, destination, new DateTimeOffset(createdAt, TimeSpan.Zero));
    }

    private async Task<Guid> SeedOrderAsync(Seed seed)
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        return (await RecipientNoticeSeed.InsertOrderAsync(admin, seed.Owner)).OrderId;
    }

    private async Task<Guid> InsertUserAsync()
    {
        var id = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO identity.users(id,identity_subject,status) VALUES (@id,@subject,'ACTIVE')",
            new NpgsqlParameter("id", id), new NpgsqlParameter("subject", $"ntf007-{id:N}"));
        return id;
    }

    private async Task<Guid> InsertOrderEventAsync(Seed seed, DateTimeOffset occurredAt)
    {
        var id = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,public_event_code,payload,occurred_at)
            VALUES (@id,@order,@owner,3,'ORDER_STATUS_CHANGED','ORDER_CREATED','{}',@occurred_at)
            """,
            new NpgsqlParameter("id", id), new NpgsqlParameter("order", seed.Order), new NpgsqlParameter("owner", seed.Owner),
            new NpgsqlParameter("occurred_at", occurredAt));
        return id;
    }

    private async Task<Guid> InsertNoticeAsync(
        Seed seed,
        Guid orderEvent,
        string channel,
        string templateKey,
        string status,
        int attempts,
        string? code,
        DateTimeOffset createdAt)
    {
        var id = Guid.NewGuid();
        await ExecuteAdminAsync(
            """
            INSERT INTO notifications.notifications(
              id,owner_org_id,order_id,order_event_id,recipient_kind,channel,status,attempts,version,
              template_key,template_version,variables_snapshot,source_event_id,last_provider_attempt_code,created_at,updated_at)
            VALUES(@id,@owner,@order,@event,'ORDER_DESTINATION',@channel,@status,@attempts,1,
              @template,1,'{"order_public_id":"ORD_ntf007"}'::jsonb,gen_random_uuid(),@code,@created_at,@created_at)
            """,
            new NpgsqlParameter("id", id), new NpgsqlParameter("owner", seed.Owner), new NpgsqlParameter("order", seed.Order),
            new NpgsqlParameter("event", orderEvent), new NpgsqlParameter("channel", channel), new NpgsqlParameter("status", status),
            new NpgsqlParameter("attempts", attempts), new NpgsqlParameter("template", templateKey),
            new NpgsqlParameter("code", (object?)code ?? DBNull.Value), new NpgsqlParameter("created_at", createdAt));
        return id;
    }

    private Task InsertInAppAsync(Seed seed) =>
        ExecuteAdminAsync(
            """
            INSERT INTO notifications.notifications(
              id,owner_org_id,order_id,recipient_user_id,channel,status,attempts,version,
              template_key,template_version,variables_snapshot,source_event_id,created_at,updated_at)
            VALUES(gen_random_uuid(),@owner,@order,@user,'IN_APP','PENDING',0,1,
              'orders.created.operations',1,'{}'::jsonb,gen_random_uuid(),clock_timestamp(),clock_timestamp())
            """,
            new NpgsqlParameter("owner", seed.Owner), new NpgsqlParameter("order", seed.Order), new NpgsqlParameter("user", seed.Actor));

    private static ListOrderRecipientNoticesQuery Query(Guid organization, Guid order) =>
        new(Guid.NewGuid(), organization, order);

    private static StopOrderRecipientNoticesCommand Command(Seed seed) =>
        new(seed.Actor, seed.Owner, seed.Order, $"ntf007-pg-{Guid.NewGuid():N}");

    private async Task<string> OptOutAsync(byte[] hash, string channel) =>
        await AdminScalarAsync<string>(
            "SELECT channel || ':' || source || ':' || key_version FROM notifications.recipient_opt_outs WHERE contact_hash=@hash AND channel=@channel",
            new NpgsqlParameter("hash", hash), new NpgsqlParameter("channel", channel)) ?? "-";

    private async Task<string[]> AuditRowsAsync(Seed seed)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT actor_id || '|' || entity_type || '|' || entity_id || '|' || request_id || '|' || payload_redacted::text
            FROM platform.audit_logs WHERE org_id=@owner AND action='RECIPIENT_NOTICES_STOPPED' ORDER BY occurred_at
            """);
        command.Parameters.Add(new NpgsqlParameter("owner", seed.Owner));
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private Task<long> IdempotencyRowsAsync(Guid owner) =>
        AdminScalarAsync<long>(
            "SELECT count(*) FROM platform.idempotency_keys WHERE owner_org_id=@owner AND scope=@scope",
            new NpgsqlParameter("owner", owner), new NpgsqlParameter("scope", RecipientNoticePanelContract.IdempotencyScope));

    /// <summary>One statement as <c>paqueteria_app</c> in a transaction of <paramref name="organization"/> (none for Empty).</summary>
    private async Task<T> AppScalarAsync<T>(Guid organization, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = await fixture.AppDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            """
            SELECT set_config('app.current_user_id', gen_random_uuid()::text, true);
            SELECT set_config('app.current_org_ids', @organizations::uuid[]::text, true);
            SET LOCAL ROLE paqueteria_app;
            """,
            connection,
            transaction))
        {
            context.Parameters.Add(new NpgsqlParameter("organizations", organization == Guid.Empty ? Array.Empty<Guid>() : new[] { organization }));
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        var result = (T)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return result;
    }

    private async Task<T> AdminScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default! : (T)value;
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CountingPhoneOpener : IRecipientPhoneOpener
    {
        public int Calls { get; private set; }

        public Task<OpenedRecipientPhone> OpenAsync(StoredRecipientPhone phone, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(OpenedRecipientPhone.Opened(Phone));
        }
    }

    private sealed class SyntheticEmailOpener : IRecipientEmailOpener
    {
        public Task<OpenedRecipientEmail> OpenAsync(StoredRecipientEmail email, CancellationToken cancellationToken) =>
            Task.FromResult(OpenedRecipientEmail.Opened(Email));
    }

    private sealed class Openers(IRecipientPhoneOpener phones, IRecipientEmailOpener emails) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IRecipientPhoneOpener) ? phones :
            serviceType == typeof(IRecipientEmailOpener) ? emails : null;
    }

    private sealed class FailingAuditWriter(IAppendOnlyAuditWriter inner) : IAppendOnlyAuditWriter
    {
        public async Task WriteAsync(DbConnection connection, DbTransaction transaction, AuditEntry entry, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(connection, transaction, entry, cancellationToken);
            throw new InvalidOperationException("Synthetic failure after the audit row.");
        }
    }
}
