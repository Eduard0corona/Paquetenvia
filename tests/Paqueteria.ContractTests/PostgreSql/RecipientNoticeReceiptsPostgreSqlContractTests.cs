using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure.Dispatching;
using Notifications.Infrastructure.Persistence.Migrations;
using Notifications.Infrastructure.RecipientNotices.Webhooks;
using Npgsql;
using Paqueteria.Application.Messaging;
using Paqueteria.Application.Security;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Database.Baseline;
using Paqueteria.Infrastructure.Database.Pooling;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25 (AI-08 NTF-006 required PostgreSQL tests; AI-18 recipient notices step 3; AI-24
/// <c>recipient_notices.receipts</c>, <c>opt_out</c> and <c>auto_reply</c>) on a real PostgreSQL 18 / PostGIS 3.6, through
/// the API's store on the <c>paqueteria_app</c> pool and the Worker's store on the <c>paqueteria_worker</c> pool: receipts
/// only move a SENT recipient notice forward, record a failure code (O5), write one status event and one status-changed
/// row, and ignore repeats and unknown references; opt-outs are recorded once per contact whatever the key version;
/// automatic replies are limited to one per contact in 24 hours, the opt-out confirmation only for the message that
/// records the opt-out, a request older than 23 hours gets nothing; the Worker leases, settles and erases the number, a
/// stale lease ends FAILED and an old pending request DROPPED; the runtime roles reach the store only through the
/// functions. Every hash is random and every number is a synthetic placeholder.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class RecipientNoticeReceiptsPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task The_receipt_step_is_the_latest_notifications_migration_and_the_baseline_assertions_hold()
    {
        Assert.Equal(
            AddRecipientNoticeReceipts.MigrationId,
            await AdminScalarAsync<string>("SELECT max(\"MigrationId\") FROM platform.__ef_migrations_history_notifications"));
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync();
        await new DatabaseBaselineAssertions().AssertAsync(connection);

        // The executor is NOLOGIN BYPASSRLS, inherits nothing and is granted to no runtime role.
        Assert.Equal("false|true|false|false|false", await AdminScalarAsync<string>(
            $"""
            SELECT rolcanlogin::text || '|' || rolbypassrls::text || '|' ||
              pg_has_role('paqueteria_app','{AddRecipientNoticeReceipts.Role}','MEMBER')::text || '|' ||
              pg_has_role('paqueteria_worker','{AddRecipientNoticeReceipts.Role}','MEMBER')::text || '|' ||
              pg_has_role('paqueteria_realtime','{AddRecipientNoticeReceipts.Role}','MEMBER')::text
            FROM pg_roles WHERE rolname='{AddRecipientNoticeReceipts.Role}'
            """));
    }

    [PostgreSqlContractFact]
    public async Task The_runtime_roles_reach_the_stores_only_through_their_own_functions()
    {
        await using var app = await fixture.AppDataSource.OpenConnectionAsync();
        foreach (var sql in new[]
                 {
                     "SELECT count(*) FROM notifications.recipient_auto_replies",
                     "SELECT count(*) FROM notifications.recipient_opt_outs",
                     "SELECT security.claim_recipient_auto_replies(1,interval '1 minute')",
                     $"SET LOCAL ROLE {AddRecipientNoticeReceipts.Role}",
                 })
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => RunAsync(app, "paqueteria_app", sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        await using var worker = await fixture.WorkerDataSource.OpenConnectionAsync();
        foreach (var sql in new[]
                 {
                     "SELECT count(*) FROM notifications.recipient_auto_replies",
                     "SELECT security.record_messaging_receipt('EMAIL','x','DELIVERED',NULL,clock_timestamp())",
                     "SELECT security.record_recipient_opt_out('EMAIL','EMAIL_LINK',decode(repeat('00',32),'hex'),1,ARRAY[decode(repeat('00',32),'hex')])",
                     $"SET LOCAL ROLE {AddRecipientNoticeReceipts.Role}",
                 })
        {
            var denied = await Assert.ThrowsAsync<PostgresException>(() => RunAsync(worker, "paqueteria_worker", sql));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
    }

    [PostgreSqlContractFact]
    public async Task Receipts_move_a_sent_notice_forward_once_record_the_failure_code_and_ignore_unknown_references()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-006 Receipts");
        try
        {
            var (order, orderEvent, _) = await RecipientNoticeSeed.InsertOrderAsync(admin, owner);
            var store = ApiStore();
            var delivered = await InsertSentNoticeAsync(owner, order, orderEvent, "WHATSAPP", "recipient.tracking-link");
            var failed = await InsertSentNoticeAsync(owner, order, orderEvent, "EMAIL", "recipient.tracking-link");
            var outboxBefore = await StatusChangedRowsAsync(owner);
            var reportedLater = DateTimeOffset.UtcNow.AddHours(2);

            Assert.Equal(MessagingReceiptResult.Recorded, await store.RecordReceiptAsync(
                new MessagingReceipt("WHATSAPP", delivered.Reference, MessagingReceiptStatus.Delivered, null, reportedLater),
                CancellationToken.None));
            // DELIVERED keeps the send's code and never takes a delivery time after the server's.
            Assert.Equal("DELIVERED:4:MESSAGING_ACCEPTED:delivered_at<=now", await NoticeStateAsync(delivered.Id));
            Assert.Equal(MessagingReceiptResult.Recorded, await store.RecordReceiptAsync(
                new MessagingReceipt("EMAIL", failed.Reference, MessagingReceiptStatus.Failed, MessagingResultCodes.PolicyBlocked, null),
                CancellationToken.None));
            // O5: the failure code is recorded on the notice, so the dispatcher sees why it failed.
            Assert.Equal("FAILED:4:MESSAGING_POLICY_BLOCKED:-", await NoticeStateAsync(failed.Id));
            Assert.Equal(
                ["4:DELIVERED:1:-", "4:FAILED:1:MESSAGING_POLICY_BLOCKED"],
                await AdminListAsync(
                    $"""
                    SELECT version || ':' || status || ':' || attempts || ':' || COALESCE(provider_attempt_code,'-')
                    FROM notifications.notification_status_events
                    WHERE notification_id IN ('{delivered.Id}','{failed.Id}') AND version=4 ORDER BY status
                    """));
            Assert.Equal(outboxBefore + 2, await StatusChangedRowsAsync(owner));
            Assert.Equal(
                ["DELIVERED:notification-status-changed-v2", "FAILED:notification-status-changed-v2"],
                await AdminListAsync(
                    $"""
                    SELECT payload->>'status' || ':' || (payload->>'schema_version')
                    FROM platform.outbox_events
                    WHERE topic='notifications.status-changed' AND aggregate_id IN ('{delivered.Id}','{failed.Id}')
                      AND aggregate_version=4
                    ORDER BY 1
                    """));

            // Repeated, contradicting and unknown receipts change nothing: forward only, never backwards or sideways.
            foreach (var receipt in new[]
                     {
                         new MessagingReceipt("WHATSAPP", delivered.Reference, MessagingReceiptStatus.Delivered, null, null),
                         new MessagingReceipt("WHATSAPP", delivered.Reference, MessagingReceiptStatus.Failed, MessagingResultCodes.Rejected, null),
                         new MessagingReceipt("EMAIL", failed.Reference, MessagingReceiptStatus.Delivered, null, null),
                     })
            {
                Assert.Equal(MessagingReceiptResult.Unchanged, await store.RecordReceiptAsync(receipt, CancellationToken.None));
            }

            Assert.Equal(MessagingReceiptResult.UnknownReference, await store.RecordReceiptAsync(
                new MessagingReceipt("EMAIL", delivered.Reference, MessagingReceiptStatus.Delivered, null, null), CancellationToken.None));
            Assert.Equal(MessagingReceiptResult.UnknownReference, await store.RecordReceiptAsync(
                new MessagingReceipt("WHATSAPP", $"wamid.{Guid.NewGuid():N}", MessagingReceiptStatus.Delivered, null, null), CancellationToken.None));
            Assert.Equal("DELIVERED:4:MESSAGING_ACCEPTED:delivered_at<=now", await NoticeStateAsync(delivered.Id));
            Assert.Equal("FAILED:4:MESSAGING_POLICY_BLOCKED:-", await NoticeStateAsync(failed.Id));
            Assert.Equal(outboxBefore + 2, await StatusChangedRowsAsync(owner));

            // A PENDING notice (its send not settled yet) is not moved by an early receipt either (O11 choice 6).
            var pending = await InsertSentNoticeAsync(owner, order, orderEvent, "WHATSAPP", "recipient.in-transit", status: "PENDING");
            Assert.Equal(MessagingReceiptResult.Unchanged, await store.RecordReceiptAsync(
                new MessagingReceipt("WHATSAPP", pending.Reference, MessagingReceiptStatus.Delivered, null, null), CancellationToken.None));

            // The function refuses a code outside the closed set and a DELIVERED receipt that carries one.
            await using var app = await fixture.AppDataSource.OpenConnectionAsync();
            foreach (var call in new[]
                     {
                         $"SELECT security.record_messaging_receipt('EMAIL','{failed.Reference}','FAILED','SOMETHING_ELSE',clock_timestamp())",
                         $"SELECT security.record_messaging_receipt('EMAIL','{failed.Reference}','DELIVERED','MESSAGING_PROVIDER_REJECTED',clock_timestamp())",
                         "SELECT security.record_messaging_receipt('SMS','ref','DELIVERED',NULL,clock_timestamp())",
                         "SELECT security.record_messaging_receipt('EMAIL','bad reference','DELIVERED',NULL,clock_timestamp())",
                     })
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(() => RunAsync(app, "paqueteria_app", call));
                Assert.Equal("22023", refused.SqlState);
            }
        }
        finally
        {
            await RetireAsync(owner);
        }
    }

    [PostgreSqlContractFact]
    public async Task An_opt_out_is_recorded_once_per_contact_under_any_key_version_and_is_honoured_by_the_next_send()
    {
        var store = ApiStore();
        var current = Hash(2);
        var previous = Hash(1);

        Assert.True(await store.RecordEmailOptOutAsync([current, previous], CancellationToken.None));
        Assert.False(await store.RecordEmailOptOutAsync([current, previous], CancellationToken.None));
        // After a rotation the contact hashes differently under the new current version; the earlier row still counts.
        Assert.False(await store.RecordEmailOptOutAsync([Hash(3), current], CancellationToken.None));
        Assert.Equal(
            ["EMAIL:EMAIL_LINK:2"],
            await AdminListAsync(
                $"""
                SELECT channel || ':' || source || ':' || key_version FROM notifications.recipient_opt_outs
                WHERE contact_hash IN ('\x{Convert.ToHexStringLower(current.Hash)}','\x{Convert.ToHexStringLower(previous.Hash)}')
                """));
        // O10: a recipient opt-out writes no audit row and keeps no contact data.
        Assert.Equal(0L, await AdminScalarAsync<long>(
            "SELECT count(*) FROM platform.audit_logs WHERE occurred_at > clock_timestamp() - interval '1 minute' AND action LIKE '%OPT%'"));

        // The Worker's check before every send (NTF-005) hashes the contact under every configured version and now
        // finds it, on that channel only.
        Assert.True(await WorkerScalarAsync<bool>(
            "SELECT security.is_recipient_opted_out('EMAIL',@hashes)",
            new NpgsqlParameter<byte[][]>("hashes", [Hash(9).Hash, current.Hash])));
        Assert.False(await WorkerScalarAsync<bool>(
            "SELECT security.is_recipient_opted_out('EMAIL',@hashes)",
            new NpgsqlParameter<byte[][]>("hashes", [Hash(9).Hash, Hash(8).Hash])));
        Assert.False(await WorkerScalarAsync<bool>(
            "SELECT security.is_recipient_opted_out('WHATSAPP',@hashes)",
            new NpgsqlParameter<byte[][]>("hashes", [current.Hash, previous.Hash])));
    }

    [PostgreSqlContractFact]
    public async Task Automatic_replies_are_one_per_contact_in_24_hours_and_the_confirmation_comes_only_with_a_new_opt_out()
    {
        var store = ApiStore();
        var now = DateTimeOffset.UtcNow;
        var contact = new[] { Hash(2), Hash(1) };

        Assert.Equal(RecipientInboundResult.AutoReplyRequested, await store.RecordWhatsAppInboundAsync(Inbound(contact, false, now.AddMinutes(-1)), CancellationToken.None));
        Assert.Equal(RecipientInboundResult.AutoReplyLimited, await store.RecordWhatsAppInboundAsync(Inbound(contact, false, now), CancellationToken.None));
        // Under a rotated key the same contact is still limited (its earlier hash is one of the known ones).
        Assert.Equal(RecipientInboundResult.AutoReplyLimited, await store.RecordWhatsAppInboundAsync(Inbound([Hash(3), contact[0]], false, now), CancellationToken.None));
        // Meta's 23-hour bound: an old message gets nothing.
        var other = new[] { Hash(1) };
        Assert.Equal(RecipientInboundResult.Stale, await store.RecordWhatsAppInboundAsync(Inbound(other, false, now.AddHours(-23).AddMinutes(-1)), CancellationToken.None));

        // The opt-out of the same contact records its one confirmation outside the automatic-reply limit (E4-P15).
        Assert.Equal(RecipientInboundResult.OptOutRecorded, await store.RecordWhatsAppInboundAsync(Inbound(contact, true, now), CancellationToken.None));
        Assert.Equal(RecipientInboundResult.OptOutAlreadyRecorded, await store.RecordWhatsAppInboundAsync(Inbound(contact, true, now), CancellationToken.None));
        Assert.Equal(
            ["AUTO_REPLY:PENDING:0", "OPT_OUT_CONFIRMATION:PENDING:0"],
            await AdminListAsync(
                $"""
                SELECT kind || ':' || status || ':' || attempts FROM notifications.recipient_auto_replies
                WHERE contact_hash='\x{Convert.ToHexStringLower(contact[0].Hash)}' ORDER BY kind
                """));
        Assert.Equal(
            ["WHATSAPP:RECIPIENT_REPLY"],
            await AdminListAsync(
                $"""
                SELECT channel || ':' || source FROM notifications.recipient_opt_outs
                WHERE contact_hash='\x{Convert.ToHexStringLower(contact[0].Hash)}'
                """));

        // An opt-out message older than 23 hours still records the opt-out, without a confirmation.
        var late = new[] { Hash(2) };
        Assert.Equal(RecipientInboundResult.OptOutRecorded, await store.RecordWhatsAppInboundAsync(Inbound(late, true, now.AddHours(-23).AddMinutes(-5)), CancellationToken.None));
        Assert.Equal(0L, await AdminScalarAsync<long>(
            $"SELECT count(*) FROM notifications.recipient_auto_replies WHERE contact_hash='\\x{Convert.ToHexStringLower(late[0].Hash)}'"));

        // A confirmation is never requested on its own, without the opt-out of the same transaction.
        await using var app = await fixture.AppDataSource.OpenConnectionAsync();
        var unrelated = Hash(1);
        Assert.Equal("NOT_ELIGIBLE", await AppScalarAsync<string>(app,
            "SELECT security.request_recipient_auto_reply(gen_random_uuid(),'OPT_OUT_CONFIRMATION',@hash,1,ARRAY[@hash],decode('01','hex'),'ntf006-auto-reply-mock-v1',clock_timestamp())",
            new NpgsqlParameter<byte[]>("hash", unrelated.Hash)));

        await DrainAutoRepliesAsync();
    }

    [PostgreSqlContractFact]
    public async Task The_worker_leases_settles_and_erases_the_number_and_a_stale_lease_or_an_old_request_never_sends()
    {
        await DrainAutoRepliesAsync();
        var store = ApiStore();
        var worker = WorkerStore();
        var now = DateTimeOffset.UtcNow;
        var first = new[] { Hash(1) };
        var second = new[] { Hash(1) };
        var third = new[] { Hash(1) };
        Assert.Equal(RecipientInboundResult.AutoReplyRequested, await store.RecordWhatsAppInboundAsync(Inbound(first, false, now.AddMinutes(-2)), CancellationToken.None));
        Assert.Equal(RecipientInboundResult.AutoReplyRequested, await store.RecordWhatsAppInboundAsync(Inbound(second, false, now.AddMinutes(-1)), CancellationToken.None));
        Assert.Equal(RecipientInboundResult.AutoReplyRequested, await store.RecordWhatsAppInboundAsync(Inbound(third, false, now), CancellationToken.None));

        var claimed = await worker.ClaimAutoRepliesAsync(2, TimeSpan.FromMinutes(1), CancellationToken.None);
        Assert.Equal(2, claimed.Count);
        Assert.Single(claimed.Select(request => request.LeaseToken).Distinct());
        Assert.All(claimed, request =>
        {
            Assert.Equal(RecipientAutoReplyKind.AutoReply, request.Kind);
            Assert.Equal(RecipientAutoReplyNumbers.SyntheticKeyVersion, request.NumberKeyVersion);
            Assert.NotEmpty(request.SealedNumber);
        });
        // The leased rows are not claimed twice.
        var rest = await worker.ClaimAutoRepliesAsync(10, TimeSpan.FromSeconds(10), CancellationToken.None);
        var last = Assert.Single(rest);

        // SENT erases the number; a wrong lease changes nothing; a second settle is lost.
        Assert.False(await worker.SettleAutoReplyAsync(claimed[0] with { LeaseToken = Guid.NewGuid() }, RecipientAutoReplyOutcome.Sent, MessagingResultCodes.SyntheticAccepted, null, CancellationToken.None));
        Assert.True(await worker.SettleAutoReplyAsync(claimed[0], RecipientAutoReplyOutcome.Sent, MessagingResultCodes.SyntheticAccepted, null, CancellationToken.None));
        Assert.False(await worker.SettleAutoReplyAsync(claimed[0], RecipientAutoReplyOutcome.Failed, MessagingResultCodes.Rejected, null, CancellationToken.None));
        Assert.Equal("SENT:1:MESSAGING_SYNTHETIC_ACCEPTED:number=false:settled=true", await AutoReplyStateAsync(claimed[0].RequestId));

        // RETRY (Key Vault unavailable before the call) puts it back with its number until the given time.
        Assert.True(await worker.SettleAutoReplyAsync(claimed[1], RecipientAutoReplyOutcome.Retry, null, now.AddSeconds(-1), CancellationToken.None));
        Assert.Equal("PENDING:0:-:number=true:settled=false", await AutoReplyStateAsync(claimed[1].RequestId));
        var again = Assert.Single(await worker.ClaimAutoRepliesAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None));
        Assert.Equal(claimed[1].RequestId, again.RequestId);
        Assert.True(await worker.SettleAutoReplyAsync(again, RecipientAutoReplyOutcome.Dropped, MessagingResultCodes.ChannelDisabled, null, CancellationToken.None));
        Assert.Equal("DROPPED:0:MESSAGING_CHANNEL_DISABLED:number=false:settled=true", await AutoReplyStateAsync(again.RequestId));

        // A lease that expired may have reached Meta: the next claim ends it FAILED with AMBIGUOUS_TIMEOUT, never resent.
        await ExecuteAdminAsync(
            "UPDATE notifications.recipient_auto_replies SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id",
            new NpgsqlParameter("id", last.RequestId));
        Assert.Empty(await worker.ClaimAutoRepliesAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None));
        Assert.Equal("FAILED:1:AMBIGUOUS_TIMEOUT:number=false:settled=true", await AutoReplyStateAsync(last.RequestId));
        Assert.False(await worker.SettleAutoReplyAsync(last, RecipientAutoReplyOutcome.Sent, MessagingResultCodes.SyntheticAccepted, null, CancellationToken.None));

        // A pending request older than 23 hours is dropped at the next claim, its number erased, without a send.
        var old = new[] { Hash(1) };
        Assert.Equal(RecipientInboundResult.AutoReplyRequested, await store.RecordWhatsAppInboundAsync(Inbound(old, false, now.AddMinutes(-3)), CancellationToken.None));
        var oldId = await AdminScalarAsync<Guid>(
            $"SELECT id FROM notifications.recipient_auto_replies WHERE contact_hash='\\x{Convert.ToHexStringLower(old[0].Hash)}'");
        await ExecuteAdminAsync(
            "UPDATE notifications.recipient_auto_replies SET received_at=clock_timestamp()-interval '23 hours 1 minute' WHERE id=@id",
            new NpgsqlParameter("id", oldId));
        Assert.Empty(await worker.ClaimAutoRepliesAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None));
        Assert.Equal("DROPPED:0:AUTO_REPLY_STALE:number=false:settled=true", await AutoReplyStateAsync(oldId));

        // The settle refuses an outcome code outside its closed set.
        Assert.Equal(RecipientInboundResult.AutoReplyRequested, await store.RecordWhatsAppInboundAsync(Inbound([Hash(1)], false, now), CancellationToken.None));
        var leased = Assert.Single(await worker.ClaimAutoRepliesAsync(10, TimeSpan.FromMinutes(1), CancellationToken.None));
        await Assert.ThrowsAsync<PostgresException>(() =>
            worker.SettleAutoReplyAsync(leased, RecipientAutoReplyOutcome.Sent, "SOMETHING_ELSE", null, CancellationToken.None));
        Assert.True(await worker.SettleAutoReplyAsync(leased, RecipientAutoReplyOutcome.Failed, MessagingResultCodes.ReengagementRequired, null, CancellationToken.None));
        await DrainAutoRepliesAsync();
    }

    [PostgreSqlContractFact]
    public async Task Every_valid_batch_size_claims_after_the_clean_up_and_the_batch_is_clamped_to_200()
    {
        // Notifications:BatchSize admits 1 to 200 and the dispatcher passes it unchanged: the claim clamps the batch like
        // security.claim_notifications_outbox instead of refusing it, so the expired-lease and stale clean-up always runs.
        await DrainAutoRepliesAsync();
        var store = ApiStore();
        var worker = WorkerStore();
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(RecipientInboundResult.AutoReplyRequested, await store.RecordWhatsAppInboundAsync(Inbound([Hash(1)], false, now.AddMinutes(-3)), CancellationToken.None));
        var expired = Assert.Single(await worker.ClaimAutoRepliesAsync(1, TimeSpan.FromMinutes(1), CancellationToken.None));
        await ExecuteAdminAsync(
            "UPDATE notifications.recipient_auto_replies SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id",
            new NpgsqlParameter("id", expired.RequestId));
        var stale = new[] { Hash(1) };
        Assert.Equal(RecipientInboundResult.AutoReplyRequested, await store.RecordWhatsAppInboundAsync(Inbound(stale, false, now.AddMinutes(-2)), CancellationToken.None));
        var staleId = await AdminScalarAsync<Guid>(
            $"SELECT id FROM notifications.recipient_auto_replies WHERE contact_hash='\\x{Convert.ToHexStringLower(stale[0].Hash)}'");
        await ExecuteAdminAsync(
            "UPDATE notifications.recipient_auto_replies SET received_at=clock_timestamp()-interval '23 hours 1 minute' WHERE id=@id",
            new NpgsqlParameter("id", staleId));
        var fresh = new[] { Hash(1) };
        Assert.Equal(RecipientInboundResult.AutoReplyRequested, await store.RecordWhatsAppInboundAsync(Inbound(fresh, false, now), CancellationToken.None));

        var claimed = Assert.Single(await worker.ClaimAutoRepliesAsync(150, TimeSpan.FromMinutes(1), CancellationToken.None));
        Assert.NotEqual(expired.RequestId, claimed.RequestId);
        Assert.NotEqual(staleId, claimed.RequestId);
        Assert.Equal("FAILED:1:AMBIGUOUS_TIMEOUT:number=false:settled=true", await AutoReplyStateAsync(expired.RequestId));
        Assert.Equal("DROPPED:0:AUTO_REPLY_STALE:number=false:settled=true", await AutoReplyStateAsync(staleId));
        Assert.True(await worker.SettleAutoReplyAsync(claimed, RecipientAutoReplyOutcome.Sent, MessagingResultCodes.SyntheticAccepted, null, CancellationToken.None));

        // 201 pending requests: the largest valid batch (200) and a larger argument both lease at most 200.
        await ExecuteAdminAsync(
            """
            INSERT INTO notifications.recipient_auto_replies(
              id,kind,contact_hash,contact_key_version,sealed_number,number_key_version,received_at,status,attempts,available_at)
            SELECT gen_random_uuid(),'AUTO_REPLY',extensions.gen_random_bytes(32),1,decode('01','hex'),'ntf006-auto-reply-mock-v1',
              clock_timestamp(),'PENDING',0,clock_timestamp()
            FROM generate_series(1,201)
            """);
        Assert.Equal(200, (await worker.ClaimAutoRepliesAsync(500, TimeSpan.FromMinutes(1), CancellationToken.None)).Count);
        Assert.Single(await worker.ClaimAutoRepliesAsync(200, TimeSpan.FromMinutes(1), CancellationToken.None));
        await DrainAutoRepliesAsync();
    }

    [PostgreSqlContractFact]
    public async Task The_unsubscribe_reads_only_the_email_copy_of_that_organizations_email_notice()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-006 Unsubscribe");
        var other = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-006 Unsubscribe other");
        try
        {
            var (order, orderEvent, _) = await RecipientNoticeSeed.InsertOrderAsync(admin, owner);
            var destination = await AdminScalarAsync<Guid>($"SELECT destination_location_id FROM orders.orders WHERE id='{order}'");
            var store = ApiStore();
            var email = await InsertSentNoticeAsync(owner, order, orderEvent, "EMAIL", "recipient.tracking-link");
            var whatsApp = await InsertSentNoticeAsync(owner, order, orderEvent, "WHATSAPP", "recipient.tracking-link");

            // No e-mail copy yet: nothing to read (the uniform 404).
            Assert.Null(await store.ReadUnsubscribeContactAsync(owner, email.Id, CancellationToken.None));
            await ExecuteAdminAsync(
                "UPDATE locations.locations SET email_ciphertext=decode('0a0b','hex') WHERE id=@id",
                new NpgsqlParameter("id", destination));

            var contact = await store.ReadUnsubscribeContactAsync(owner, email.Id, CancellationToken.None);
            Assert.NotNull(contact);
            Assert.Equal(owner, contact.OwnerOrganizationId);
            Assert.Equal(destination, contact.LocationId);
            Assert.Equal("test-v1", contact.KeyVersion);
            Assert.Equal([0x0a, 0x0b], contact.EmailCiphertext);
            Assert.DoesNotContain("0a0b", contact.ToString(), StringComparison.OrdinalIgnoreCase);

            // Another organization (RLS), a WhatsApp notice and an unknown notification read nothing.
            Assert.Null(await store.ReadUnsubscribeContactAsync(other, email.Id, CancellationToken.None));
            Assert.Null(await store.ReadUnsubscribeContactAsync(owner, whatsApp.Id, CancellationToken.None));
            Assert.Null(await store.ReadUnsubscribeContactAsync(owner, Guid.NewGuid(), CancellationToken.None));
        }
        finally
        {
            await RetireAsync(owner);
            await RetireAsync(other);
        }
    }

    // ------------------------------------------------------------------ helpers

    private PostgreSqlRecipientNoticeReceiptStore ApiStore() =>
        new(fixture.AppDataSource, NullLogger<PostgreSqlRecipientNoticeReceiptStore>.Instance);

    private PostgreSqlNotificationsStore WorkerStore() =>
        new(new NotificationsWorkerConnectionFactory(
            RuntimeDataSource.Create(RuntimeDatabaseRole.Worker, fixture.WorkerConnectionString, 2, [])));

    private static ContactLookupHash Hash(int keyVersion) => new(keyVersion, RandomNumberGenerator.GetBytes(32));

    private static RecipientInboundRecord Inbound(IReadOnlyList<ContactLookupHash> hashes, bool optOut, DateTimeOffset receivedAt) =>
        new(Guid.NewGuid(), optOut, hashes, RandomNumberGenerator.GetBytes(32), RecipientAutoReplyNumbers.SyntheticKeyVersion, receivedAt);

    private async Task<(Guid Id, string Reference)> InsertSentNoticeAsync(
        Guid owner,
        Guid order,
        Guid orderEvent,
        string channel,
        string templateKey,
        string status = "SENT")
    {
        var id = Guid.NewGuid();
        var reference = channel == "WHATSAPP" ? $"wamid.{Guid.NewGuid():N}==" : $"op-{Guid.NewGuid():N}";
        await ExecuteAdminAsync(
            """
            INSERT INTO notifications.notifications(
              id,owner_org_id,order_id,order_event_id,recipient_kind,channel,status,attempts,version,
              template_key,template_version,variables_snapshot,source_event_id,provider_reference,
              last_provider_attempt_code,last_attempt_at,sent_at,created_at,updated_at)
            VALUES(@id,@owner,@order,@event,'ORDER_DESTINATION',@channel,@status,1,3,
              @template,1,'{"order_public_id":"ORD_ntf006"}'::jsonb,gen_random_uuid(),@reference,
              'MESSAGING_ACCEPTED',clock_timestamp(),CASE WHEN @status='SENT' THEN clock_timestamp() END,
              clock_timestamp(),clock_timestamp())
            """,
            new NpgsqlParameter("id", id), new NpgsqlParameter("owner", owner), new NpgsqlParameter("order", order),
            new NpgsqlParameter("event", orderEvent), new NpgsqlParameter("channel", channel),
            new NpgsqlParameter("status", status), new NpgsqlParameter("template", templateKey),
            new NpgsqlParameter("reference", reference));
        return (id, reference);
    }

    private Task<string> NoticeStateAsync(Guid id) =>
        AdminScalarAsync<string>(
            """
            SELECT status || ':' || version || ':' || COALESCE(last_provider_attempt_code,'-') || ':' ||
              CASE WHEN delivered_at IS NULL THEN '-' WHEN delivered_at <= clock_timestamp() THEN 'delivered_at<=now' ELSE 'future' END
            FROM notifications.notifications WHERE id=@id
            """,
            new NpgsqlParameter("id", id));

    private Task<string> AutoReplyStateAsync(Guid id) =>
        AdminScalarAsync<string>(
            """
            SELECT status || ':' || attempts || ':' || COALESCE(last_code,'-') || ':number=' || (sealed_number IS NOT NULL)::text ||
              ':settled=' || (settled_at IS NOT NULL)::text
            FROM notifications.recipient_auto_replies WHERE id=@id
            """,
            new NpgsqlParameter("id", id));

    private Task<long> StatusChangedRowsAsync(Guid owner) =>
        AdminScalarAsync<long>(
            "SELECT count(*) FROM platform.outbox_events WHERE owner_org_id=@owner AND topic='notifications.status-changed'",
            new NpgsqlParameter("owner", owner));

    /// <summary>Every request the shared database holds is settled, so no other run claims it.</summary>
    private Task DrainAutoRepliesAsync() =>
        ExecuteAdminAsync(
            """
            UPDATE notifications.recipient_auto_replies SET status='DROPPED',last_code='MESSAGING_CHANNEL_DISABLED',
              sealed_number=NULL,number_key_version=NULL,lease_token=NULL,lease_expires_at=NULL,settled_at=clock_timestamp()
            WHERE status IN ('PENDING','PROCESSING')
            """);

    /// <summary>Every outbox row this test left claimable for the organization ends DEAD, so no other test claims it.</summary>
    private Task RetireAsync(Guid owner) =>
        ExecuteAdminAsync(
            """
            UPDATE platform.outbox_events SET status='DEAD',processed_at=clock_timestamp(),
              locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
            WHERE owner_org_id=@owner AND status IN ('PENDING','RETRY','PROCESSING')
            """,
            new NpgsqlParameter("owner", owner));

    private static async Task RunAsync(NpgsqlConnection connection, string role, string sql)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            $"""
            SELECT set_config('app.current_user_id', gen_random_uuid()::text, true);
            SELECT set_config('app.current_org_ids', '{"{}"}', true);
            SET LOCAL ROLE {role};
            """,
            connection,
            transaction))
        {
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
        await transaction.RollbackAsync();
    }

    private static async Task<T> AppScalarAsync<T>(NpgsqlConnection connection, string sql, params NpgsqlParameter[] parameters)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            """
            SELECT set_config('app.current_user_id', gen_random_uuid()::text, true);
            SELECT set_config('app.current_org_ids', '{}', true);
            SET LOCAL ROLE paqueteria_app;
            """,
            connection,
            transaction))
        {
            await context.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        var result = (T)(await command.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();
        return result;
    }

    private async Task<T> WorkerScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
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
        return value is DBNull ? default! : (T)value!;
    }

    private async Task<string[]> AdminListAsync(string sql)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }
}
