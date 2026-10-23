using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Identity.Application.Notifications;
using Locations.Infrastructure.RecipientNotices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Notifications.Application.Dispatching;
using Notifications.Application.RecipientNotices;
using Notifications.Infrastructure;
using Notifications.Infrastructure.Dispatching;
using Notifications.Infrastructure.Persistence.Migrations;
using Notifications.Infrastructure.RecipientNotices;
using Npgsql;
using NpgsqlTypes;
using Orders.Application.RecipientNotices;
using Orders.Infrastructure.RecipientNotices;
using Organizations.Application.Notifications;
using Paqueteria.Application.Messaging;
using Paqueteria.Application.RecipientNotices;
using Paqueteria.Application.Security;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Contracts.Tracking;
using Paqueteria.Infrastructure.Messaging;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// NTF-RECIPIENT-NOTICES-2026-10-20 and NTF-RECIPIENT-NOTICE-CAPS-2026-10-23 (AI-08 NTF-005 required PostgreSQL contract
/// tests and end-to-end synthetic run; AI-24 <c>recipient_notices</c>): the count function of the caps
/// (<c>security.read_recipient_notice_counts</c>) and its two indexes, the Worker readers under FORCE RLS, and the
/// recipient notice lane composed exactly as the Worker composes it - the PostgreSQL store, the Orders and Locations
/// readers, the link key ring and the synthetic providers behind the real router - driven through
/// <see cref="NotificationsOutboxProcessor"/>. No producer writes <c>orders.recipient-notice-requested</c> before AI-08
/// NTF-004, so the test administrator writes each request row as AI-24 <c>recipient_notices.producer</c> declares it.
/// Every contact is a one-byte synthetic copy and the synthetic providers use the AI-24 placeholders; nothing is
/// decrypted and no key or token is a literal of this file.
/// </summary>
[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class RecipientNoticeDeliveryPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    private const string BaseUrl = "https://paquetenvia.test";
    private const string SyntheticPhone = "+520000000002";
    private const string SyntheticEmail = "destinatario@example.invalid";

    // ------------------------------------------------------------------ the count function (L1, L2)

    [PostgreSqlContractFact]
    public async Task Count_function_and_its_indexes_match_the_declared_contract()
    {
        Assert.Equal(
            "paqueteria_outbox_executor|true|paqueteria_outbox_executor:EXECUTE,paqueteria_worker:EXECUTE|" +
            "search_path=pg_catalog, platform, notifications, pg_temp|p_outbox_id uuid, p_lease_token uuid|" +
            "TABLE(order_channel_count integer, day_channel_count integer)|plpgsql",
            await AdminScalarAsync<string>(
                """
                SELECT pg_get_userbyid(p.proowner) || '|' || p.prosecdef::text || '|' ||
                  (SELECT string_agg(pg_get_userbyid(acl.grantee) || ':' || acl.privilege_type, ','
                     ORDER BY pg_get_userbyid(acl.grantee) COLLATE "C") FROM aclexplode(p.proacl) acl) || '|' ||
                  array_to_string(p.proconfig, ';') || '|' || pg_get_function_arguments(p.oid) || '|' ||
                  pg_get_function_result(p.oid) || '|' || l.lanname
                FROM pg_proc p JOIN pg_language l ON l.oid=p.prolang
                WHERE p.oid=to_regprocedure(@signature)
                """,
                new NpgsqlParameter("signature", AddRecipientNoticeCounts.CountsSignature)));
        Assert.False(await AdminScalarAsync<bool>(
            """
            SELECT has_function_privilege('paqueteria_app',@signature,'EXECUTE')
              OR has_function_privilege('public',@signature,'EXECUTE')
              OR has_function_privilege('paqueteria_realtime',@signature,'EXECUTE')
              OR has_function_privilege('paqueteria_bootstrap',@signature,'EXECUTE')
            """,
            new NpgsqlParameter("signature", AddRecipientNoticeCounts.CountsSignature)));
        Assert.Equal(
            "notifications_recipient_attempt_day_idx:CREATE INDEX notifications_recipient_attempt_day_idx ON notifications.notifications USING btree (channel, last_attempt_at) WHERE ((recipient_kind = 'ORDER_DESTINATION'::text) AND (attempts > 0))|" +
            "notifications_recipient_order_channel_idx:CREATE INDEX notifications_recipient_order_channel_idx ON notifications.notifications USING btree (owner_org_id, order_id, channel) WHERE (recipient_kind = 'ORDER_DESTINATION'::text)",
            await AdminScalarAsync<string>(
                """
                SELECT string_agg(c.relname || ':' || pg_get_indexdef(c.oid), '|' ORDER BY c.relname COLLATE "C")
                FROM pg_class c WHERE c.relname IN ('notifications_recipient_attempt_day_idx','notifications_recipient_order_channel_idx')
                  AND c.relnamespace='notifications'::regnamespace
                """));

        // The step is recorded as the tail of the Notifications lane.
        Assert.Equal(
            AddRecipientNoticeCounts.MigrationId,
            await AdminScalarAsync<string>(
                "SELECT max(\"MigrationId\") FROM platform.__ef_migrations_history_notifications"));

        // The Worker still reads nothing of the Notifications table, and the API cannot ask for the counts.
        await using (var worker = await fixture.WorkerDataSource.OpenConnectionAsync())
        await using (var transaction = await worker.BeginTransactionAsync())
        {
            await ExecuteAsync(worker, transaction, "SET LOCAL ROLE paqueteria_worker");
            var denied = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(worker, transaction, "SELECT count(*) FROM notifications.notifications"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }

        await using (var app = await fixture.AppDataSource.OpenConnectionAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ExecuteAsync(app, transaction, "SET LOCAL ROLE paqueteria_app");
            var denied = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(app, transaction,
                $"SELECT * FROM security.read_recipient_notice_counts('{Guid.NewGuid():D}','{Guid.NewGuid():D}')"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        }
    }

    [PostgreSqlContractFact]
    public async Task Counts_answer_only_under_a_live_send_lease_and_count_attempted_notices_per_order_and_per_platform_day()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-005 Counts");
        var other = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-005 Counts other");
        try
        {
            var (order, firstEvent, publicId) = await RecipientNoticeSeed.InsertOrderAsync(admin, owner);
            var (otherOrder, otherEvent, otherPublicId) = await RecipientNoticeSeed.InsertOrderAsync(admin, other);

            // The notice whose counts are asked for: e-mail, PENDING, its send request leased.
            var subject = await ExpandAsync(admin, owner, order, firstEvent, publicId, "EMAIL");
            var lease = await LeaseAsync(subject.Send);
            var initial = await CountsAsync(subject.Send, lease);
            Assert.NotNull(initial);
            var (orderCount, dayCount) = initial.Value;
            Assert.Equal(0, orderCount);

            // Only the holder of the live lease of that send request learns anything.
            Assert.Null(await CountsAsync(subject.Send, Guid.NewGuid()));
            Assert.Null(await CountsAsync(Guid.NewGuid(), lease));

            // Another notice of the same order and channel counts once it has an attempt (attempts > 0) and not
            // before; a cancelled one never does.
            var sent = await ExpandAsync(admin, owner, order, await InsertOrderEventAsync(admin, owner, order, 3), publicId, "EMAIL");
            Assert.Equal((0, dayCount), await CountsAsync(subject.Send, lease));
            Assert.True(await ApplyAsync(sent, "SUCCESS", "MESSAGING_SYNTHETIC_ACCEPTED", $"synthetic-{Guid.NewGuid():N}"));
            Assert.Equal((1, dayCount + 1), await CountsAsync(subject.Send, lease));
            var failed = await ExpandAsync(admin, owner, order, await InsertOrderEventAsync(admin, owner, order, 4), publicId, "EMAIL");
            Assert.True(await ApplyAsync(failed, "PERMANENT", "MESSAGING_RECIPIENT_INVALID", null));
            Assert.Equal((2, dayCount + 2), await CountsAsync(subject.Send, lease));
            var cancelled = await ExpandAsync(admin, owner, order, await InsertOrderEventAsync(admin, owner, order, 5), publicId, "EMAIL");
            Assert.True(await CancelAsync(cancelled, "NOTICE_SUPERSEDED"));
            Assert.Equal((2, dayCount + 2), await CountsAsync(subject.Send, lease));

            // The other channel of the same order counts for neither number.
            var whatsApp = await ExpandAsync(admin, owner, order, await InsertOrderEventAsync(admin, owner, order, 6), publicId, "WHATSAPP");
            Assert.True(await ApplyAsync(whatsApp, "SUCCESS", "MESSAGING_SYNTHETIC_ACCEPTED", $"synthetic-{Guid.NewGuid():N}"));
            Assert.Equal((2, dayCount + 2), await CountsAsync(subject.Send, lease));

            // L2 "Toda la plataforma": another organization's e-mail notice counts for the day, not for the order.
            var foreign = await ExpandAsync(admin, other, otherOrder, otherEvent, otherPublicId, "EMAIL");
            Assert.True(await ApplyAsync(foreign, "SUCCESS", "MESSAGING_SYNTHETIC_ACCEPTED", $"synthetic-{Guid.NewGuid():N}"));
            Assert.Equal((2, dayCount + 3), await CountsAsync(subject.Send, lease));

            // The Mazatlan day of the last attempt: its first instant counts, the instant before it does not.
            await ExecuteAdminAsync(
                """
                UPDATE notifications.notifications
                SET last_attempt_at=(date_trunc('day', clock_timestamp() AT TIME ZONE 'America/Mazatlan')) AT TIME ZONE 'America/Mazatlan'
                WHERE id=@id
                """,
                new NpgsqlParameter("id", foreign.Notification));
            Assert.Equal((2, dayCount + 3), await CountsAsync(subject.Send, lease));
            await ExecuteAdminAsync(
                """
                UPDATE notifications.notifications
                SET last_attempt_at=((date_trunc('day', clock_timestamp() AT TIME ZONE 'America/Mazatlan')) AT TIME ZONE 'America/Mazatlan')
                  - interval '1 microsecond'
                WHERE id=@id
                """,
                new NpgsqlParameter("id", foreign.Notification));
            Assert.Equal((2, dayCount + 2), await CountsAsync(subject.Send, lease));
            // An earlier day of the same order still counts for the order cap, never for the day.
            await ExecuteAdminAsync(
                "UPDATE notifications.notifications SET last_attempt_at=last_attempt_at - interval '2 days' WHERE id=@id",
                new NpgsqlParameter("id", sent.Notification));
            Assert.Equal((2, dayCount + 1), await CountsAsync(subject.Send, lease));

            // An expired lease, a send request of a notice no longer PENDING and a row of another topic get no row.
            await ExecuteAdminAsync(
                "UPDATE platform.outbox_events SET lease_expires_at=clock_timestamp() - interval '1 second' WHERE id=@id",
                new NpgsqlParameter("id", subject.Send));
            Assert.Null(await CountsAsync(subject.Send, lease));
            lease = await LeaseAsync(subject.Send);
            Assert.NotNull(await CountsAsync(subject.Send, lease));
            var requestRow = await RecipientNoticeSeed.InsertRequestAsync(
                admin, owner, order, await InsertOrderEventAsync(admin, owner, order, 7), "DELIVERED", DateTimeOffset.UtcNow);
            Assert.Null(await CountsAsync(requestRow, await LeaseAsync(requestRow)));
            Assert.True(await CancelAsync(subject with { Lease = lease }, "NOTICE_STALE"));
            Assert.Null(await CountsAsync(subject.Send, lease));
        }
        finally
        {
            await RetireAsync(owner);
            await RetireAsync(other);
        }
    }

    // ------------------------------------------------------------------ the Worker readers under FORCE RLS

    [PostgreSqlContractFact]
    public async Task Readers_return_only_the_owner_organizations_order_its_active_link_and_its_notice_copies()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-005 Readers");
        var stranger = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-005 Readers stranger");
        var scenario = new Scenario(fixture);
        await using var worker = scenario.CreateWorker();
        var seeded = await scenario.SeedOrderAsync(admin, owner, "IN_TRANSIT", phone: true, email: true, link: true);
        // Older revoked generations never win over the live link.
        await ExecuteAdminAsync(
            """
            UPDATE orders.public_tracking_tokens SET generation=3 WHERE order_id=@order;
            INSERT INTO orders.public_tracking_tokens(order_id,owner_org_id,token_hash,expires_at,revoked_at,generation,key_version)
              VALUES (@order,@org,extensions.digest('ntf005-revoked','sha256'),'infinity',clock_timestamp(),1,1),
                     (@order,@org,extensions.digest('ntf005-revoked-expired','sha256'),clock_timestamp()-interval '1 day',clock_timestamp(),2,1);
            """,
            new NpgsqlParameter("order", seeded.Order), new NpgsqlParameter("org", owner));
        var token = scenario.Token(seeded.Order, 3);
        await ExecuteAdminAsync(
            "UPDATE orders.public_tracking_tokens SET token_hash=@hash WHERE order_id=@order AND generation=3",
            new NpgsqlParameter("hash", SHA256.HashData(Encoding.UTF8.GetBytes(token))), new NpgsqlParameter("order", seeded.Order));

        var orders = worker.GetRequiredService<IRecipientNoticeOrderReader>();
        var snapshot = Assert.IsType<RecipientNoticeOrderSnapshot>(await orders.ReadAsync(owner, seeded.Order, default));
        Assert.Equal((seeded.Order, seeded.PublicId, "IN_TRANSIT", seeded.Destination), (snapshot.OrderId, snapshot.PublicId, snapshot.Status, snapshot.DestinationLocationId));
        var link = Assert.IsType<RecipientNoticeLinkRecord>(snapshot.ActiveLink);
        Assert.Equal((3, 1), (link.Generation, link.KeyVersion));
        // The derived token matches the stored hash, so the link is built in memory with the public origin.
        var built = Assert.IsType<RecipientNoticeTrackingLink>(worker.GetRequiredService<IRecipientNoticeTrackingLinks>().TryBuild(seeded.Order, link));
        Assert.Equal(token, built.Token);
        Assert.Equal($"{BaseUrl}/track/{token}", built.Url);
        Assert.Equal("[redacted]", built.ToString());
        // A hash that the Worker's key ring does not derive is no link.
        Assert.Null(worker.GetRequiredService<IRecipientNoticeTrackingLinks>().TryBuild(
            seeded.Order, link with { TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes("ntf005-other")) }));

        // Another organization reads neither the order nor its destination.
        Assert.Null(await orders.ReadAsync(stranger, seeded.Order, default));
        var contacts = worker.GetRequiredService<IRecipientNoticeContactReader>();
        Assert.Null(await contacts.ReadAsync(stranger, seeded.Destination, default));
        var copies = Assert.IsType<RecipientNoticeContactCopies>(await contacts.ReadAsync(owner, seeded.Destination, default));
        Assert.True(copies.HasPhone);
        Assert.True(copies.HasEmail);
        Assert.Equal("ntf005-synthetic-v1", copies.KeyVersion);
        Assert.DoesNotContain("ntf005-synthetic-v1", copies.ToString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the end-to-end synthetic run

    [PostgreSqlContractFact]
    public async Task Four_notices_leave_on_both_channels_with_the_link_the_buttons_and_the_unsubscribe_link()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-005 Four notices");
        var scenario = new Scenario(fixture);
        try
        {
            var seeded = await scenario.SeedOrderAsync(admin, owner, "CONFIRMED", phone: true, email: true, link: true);
            scenario.Consents.Add(seeded.Order);
            await using var worker = scenario.CreateWorker();
            var token = scenario.Token(seeded.Order, 1);
            var version = 3;
            foreach (var (notice, status) in new[]
                     {
                         ("TRACKING_LINK", "CONFIRMED"),
                         ("IN_TRANSIT", "IN_TRANSIT"),
                         ("DELIVERY_FAILED", "FAILED_ATTEMPT"),
                         ("DELIVERED", "DELIVERED"),
                     })
            {
                await scenario.SetStatusAsync(admin, seeded.Order, status);
                var source = await scenario.RequestAsync(admin, owner, seeded.Order, await InsertOrderEventAsync(admin, owner, seeded.Order, version++), notice);
                await scenario.ProcessAsync(worker, source);
                Assert.Equal("PROCESSED:SOURCE_EXPANDED", await OutboxStateAsync(source));
                var sends = await scenario.SendRowsAsync(source);
                Assert.Equal(["WHATSAPP", "EMAIL"], sends.Select(static send => send.Channel));
                foreach (var send in sends)
                {
                    await scenario.ProcessAsync(worker, send.Send);
                    Assert.Equal("PROCESSED:-", await OutboxStateAsync(send.Send));
                    Assert.Equal("SENT:1:MESSAGING_SYNTHETIC_ACCEPTED", await NotificationStateAsync(send.Notification));
                }
            }

            // Eight provider calls, WhatsApp first for each notice, each with the AI-24 placeholders.
            var requests = scenario.Provider.Requests.ToArray();
            Assert.Equal(8, requests.Length);
            var keys = new[] { "recipient.tracking-link", "recipient.in-transit", "recipient.delivery-failed", "recipient.delivered" };
            for (var index = 0; index < 4; index++)
            {
                var whatsApp = requests[index * 2];
                Assert.Equal(MessagingChannel.WhatsApp, whatsApp.Channel);
                Assert.Equal(keys[index], whatsApp.TemplateKey);
                Assert.Equal(SyntheticPhone, whatsApp.Recipient.Address);
                Assert.Equal([seeded.PublicId], whatsApp.Parameters);
                Assert.Equal(token, whatsApp.UrlButtonSuffix);
                Assert.Equal("RECIPIENT_NOTICE_OPT_OUT", whatsApp.QuickReplyPayload);
                Assert.Null(whatsApp.OneClickUnsubscribeUrl);

                var email = requests[index * 2 + 1];
                Assert.Equal(MessagingChannel.Email, email.Channel);
                Assert.Equal(keys[index], email.TemplateKey);
                Assert.Equal(SyntheticEmail, email.Recipient.Address);
                Assert.Equal(seeded.PublicId, email.Parameters[0]);
                Assert.Equal($"{BaseUrl}/track/{token}", email.Parameters[1]);
                Assert.StartsWith($"{BaseUrl}/avisos/baja/", email.Parameters[2], StringComparison.Ordinal);
                var unsubscribe = email.Parameters[2][$"{BaseUrl}/avisos/baja/".Length..];
                Assert.Equal($"{BaseUrl}/api/v1/recipient-notices/unsubscribe/{unsubscribe}", email.OneClickUnsubscribeUrl);
                Assert.True(worker.GetRequiredService<IRecipientNoticeUnsubscribeTokens>().TryVerify(unsubscribe, out var tokenOwner, out var notification));
                Assert.Equal(owner, tokenOwner);
                Assert.Equal(email.MessageId, notification);
                Assert.Null(email.UrlButtonSuffix);
            }

            // Nothing personal and no link or token reached a row, an event or a log line.
            await scenario.AssertNothingLeakedAsync(owner, token);
            Assert.Contains(scenario.Logs.Lines, line => line.Contains("outcome SUCCESS, code MESSAGING_SYNTHETIC_ACCEPTED", StringComparison.Ordinal));
        }
        finally
        {
            await RetireAsync(owner);
        }
    }

    [PostgreSqlContractFact]
    public async Task Opted_out_stale_disabled_superseded_capped_linkless_and_unusable_notices_never_reach_the_provider()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-005 Never sent");
        var scenario = new Scenario(fixture);
        try
        {
            await using var worker = scenario.CreateWorker();

            // Opted out: the keyed hash of the e-mail placeholder under a run's own lookup key is in the store. The opt-out
            // is global (E4-P8) and every synthetic notice uses the same placeholder, so this run has a key of its own.
            var optOutScenario = new Scenario(fixture);
            await using (var optOutWorker = optOutScenario.CreateWorker())
            {
                var optedOut = await optOutScenario.SeedOrderAsync(admin, owner, "DELIVERED", phone: false, email: true, link: true);
                await ExecuteAdminAsync(
                    "INSERT INTO notifications.recipient_opt_outs(contact_hash,channel,key_version,source,recorded_at) VALUES (@hash,'EMAIL',1,'EMAIL_LINK',clock_timestamp())",
                    new NpgsqlParameter("hash", HMACSHA256.HashData(optOutScenario.LookupKey, Encoding.UTF8.GetBytes($"paquetenvia-contact-v1|EMAIL|{SyntheticEmail}"))));
                Assert.Equal("CANCELLED:0:RECIPIENT_OPTED_OUT", await optOutScenario.ExpandAndSendAsync(admin, optOutWorker, owner, optedOut, "DELIVERED"));
                Assert.Empty(optOutScenario.Provider.Requests);
                await optOutScenario.AssertNothingLeakedAsync(owner, null);
            }

            // Stale at expansion: the request is past the 12-hour bound; nothing is created.
            var stale = await scenario.SeedOrderAsync(admin, owner, "DELIVERED", phone: false, email: true, link: true);
            var staleSource = await scenario.RequestAsync(admin, owner, stale.Order, stale.OrderEvent, "DELIVERED", DateTimeOffset.UtcNow.AddHours(-13));
            await scenario.ProcessAsync(worker, staleSource);
            Assert.Equal("PROCESSED:NOTICE_STALE", await OutboxStateAsync(staleSource));
            Assert.Empty(await scenario.SendRowsAsync(staleSource));

            // Stale at send: created in time, sent after the bound (here 1 hour, as after an outage).
            var late = await scenario.SeedOrderAsync(admin, owner, "DELIVERED", phone: false, email: true, link: true);
            var lateSource = await scenario.RequestAsync(admin, owner, late.Order, late.OrderEvent, "DELIVERED", DateTimeOffset.UtcNow.AddMinutes(-90));
            await scenario.ProcessAsync(worker, lateSource);
            await using (var shortBound = scenario.CreateWorker(settings => settings["Notifications:RecipientNotices:MaximumNoticeDelayHours"] = "1"))
            {
                var send = Assert.Single(await scenario.SendRowsAsync(lateSource));
                await scenario.ProcessAsync(shortBound, send.Send);
                Assert.Equal("CANCELLED:0:NOTICE_STALE", await NotificationStateAsync(send.Notification));
                Assert.Equal("PROCESSED:NOTICE_STALE", await OutboxStateAsync(send.Send));
            }

            // Disabled: a request with notices off creates nothing; a notice created before the switch went off fails.
            var disabled = await scenario.SeedOrderAsync(admin, owner, "DELIVERED", phone: false, email: true, link: true);
            await using (var off = scenario.CreateWorker(settings => settings["Notifications:RecipientNotices:Enabled"] = "false"))
            {
                var offSource = await scenario.RequestAsync(admin, owner, disabled.Order, disabled.OrderEvent, "DELIVERED");
                await scenario.ProcessAsync(off, offSource);
                Assert.Equal("PROCESSED:RECIPIENT_NOTICES_DISABLED", await OutboxStateAsync(offSource));
                Assert.Empty(await scenario.SendRowsAsync(offSource));

                var onSource = await scenario.RequestAsync(admin, owner, disabled.Order, await InsertOrderEventAsync(admin, owner, disabled.Order, 3), "DELIVERED");
                await scenario.ProcessAsync(worker, onSource);
                var send = Assert.Single(await scenario.SendRowsAsync(onSource));
                await scenario.ProcessAsync(off, send.Send);
                Assert.Equal("FAILED:1:MESSAGING_CHANNEL_DISABLED", await NotificationStateAsync(send.Notification));
                Assert.Equal("DEAD:MESSAGING_CHANNEL_DISABLED", await OutboxStateAsync(send.Send));
            }

            // Superseded by an offline replay: the IN_TRANSIT notice waits, then the delivery is replayed first.
            var replayed = await scenario.SeedOrderAsync(admin, owner, "IN_TRANSIT", phone: false, email: true, link: true);
            var replayedSource = await scenario.RequestAsync(admin, owner, replayed.Order, replayed.OrderEvent, "IN_TRANSIT");
            await scenario.ProcessAsync(worker, replayedSource);
            await scenario.SetStatusAsync(admin, replayed.Order, "DELIVERED");
            var replayedSend = Assert.Single(await scenario.SendRowsAsync(replayedSource));
            await scenario.ProcessAsync(worker, replayedSend.Send);
            Assert.Equal("CANCELLED:0:NOTICE_SUPERSEDED", await NotificationStateAsync(replayedSend.Notification));

            // Caps: the second notice of an order and channel whose first one was sent, and the day of the channel.
            var capped = await scenario.SeedOrderAsync(admin, owner, "DELIVERED", phone: false, email: true, link: true);
            Assert.Equal("SENT:1:MESSAGING_SYNTHETIC_ACCEPTED", await scenario.ExpandAndSendAsync(admin, worker, owner, capped, "DELIVERED"));
            var sentCount = scenario.Provider.Requests.Count;
            await using (var perOrder = scenario.CreateWorker(settings => settings["Notifications:RecipientNotices:MaximumPerOrderAndChannel"] = "1"))
            {
                Assert.Equal(
                    "CANCELLED:0:NOTICE_LIMIT_REACHED",
                    await scenario.ExpandAndSendAsync(admin, perOrder, owner, capped with { OrderEvent = await InsertOrderEventAsync(admin, owner, capped.Order, 3) }, "DELIVERED"));
            }

            await using (var perDay = scenario.CreateWorker(settings => settings["Notifications:RecipientNotices:DailyLimitPerChannel"] = "1"))
            {
                var fresh = await scenario.SeedOrderAsync(admin, owner, "DELIVERED", phone: false, email: true, link: true);
                Assert.Equal("CANCELLED:0:NOTICE_LIMIT_REACHED", await scenario.ExpandAndSendAsync(admin, perDay, owner, fresh, "DELIVERED"));
            }

            Assert.Equal(sentCount, scenario.Provider.Requests.Count);
            Assert.Contains(scenario.Logs.Lines, line => line ==
                "Recipient notice cap reached with lane NOTIFICATIONS, channel EMAIL, notice DELIVERED, code NOTICE_LIMIT_REACHED.");

            // No live link: none was ever issued, or the stored hash is not one the Worker's key ring derives.
            var linkless = await scenario.SeedOrderAsync(admin, owner, "DELIVERED", phone: false, email: true, link: false);
            Assert.Equal("FAILED:1:TRACKING_LINK_UNAVAILABLE", await scenario.ExpandAndSendAsync(admin, worker, owner, linkless, "DELIVERED"));
            var foreignKey = await scenario.SeedOrderAsync(admin, owner, "DELIVERED", phone: false, email: true, link: true);
            await ExecuteAdminAsync(
                "UPDATE orders.public_tracking_tokens SET token_hash=extensions.digest('ntf005-not-derived','sha256') WHERE order_id=@order",
                new NpgsqlParameter("order", foreignKey.Order));
            Assert.Equal("FAILED:1:TRACKING_LINK_UNAVAILABLE", await scenario.ExpandAndSendAsync(admin, worker, owner, foreignKey, "DELIVERED"));

            // L3: the copy of the channel is gone when the notice is sent.
            var unusable = await scenario.SeedOrderAsync(admin, owner, "DELIVERED", phone: false, email: true, link: true);
            var unusableSource = await scenario.RequestAsync(admin, owner, unusable.Order, unusable.OrderEvent, "DELIVERED");
            await scenario.ProcessAsync(worker, unusableSource);
            await ExecuteAdminAsync(
                "UPDATE locations.locations SET notice_email_ciphertext=NULL,notice_key_version=NULL WHERE id=@id",
                new NpgsqlParameter("id", unusable.Destination));
            var unusableSend = Assert.Single(await scenario.SendRowsAsync(unusableSource));
            await scenario.ProcessAsync(worker, unusableSend.Send);
            Assert.Equal("FAILED:1:MESSAGING_RECIPIENT_INVALID", await NotificationStateAsync(unusableSend.Notification));
            Assert.Equal("DEAD:MESSAGING_RECIPIENT_INVALID", await OutboxStateAsync(unusableSend.Send));

            // Only the one notice under the cap ever reached the provider.
            Assert.Single(scenario.Provider.Requests);
            await scenario.AssertNothingLeakedAsync(owner, null);
        }
        finally
        {
            await RetireAsync(owner);
        }
    }

    [PostgreSqlContractFact]
    public async Task An_ambiguous_whatsapp_fails_once_without_a_retry_and_no_whatsapp_notice_exists_without_consent()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-005 Ambiguous");
        var scenario = new Scenario(fixture);
        try
        {
            await using var worker = scenario.CreateWorker();
            var seeded = await scenario.SeedOrderAsync(admin, owner, "DELIVERED", phone: true, email: true, link: true);
            scenario.Consents.Add(seeded.Order);
            var source = await scenario.RequestAsync(admin, owner, seeded.Order, seeded.OrderEvent, "DELIVERED");
            await scenario.ProcessAsync(worker, source);
            var sends = await scenario.SendRowsAsync(source);
            var whatsApp = Assert.Single(sends, send => send.Channel == "WHATSAPP");
            scenario.Provider.AmbiguousFor.Add(whatsApp.Notification);

            await scenario.ProcessAsync(worker, whatsApp.Send);

            // NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02: FAILED at once, never retried, so never sent twice.
            Assert.Equal($"FAILED:1:{NotificationErrorCodes.AmbiguousTimeout}", await NotificationStateAsync(whatsApp.Notification));
            Assert.Equal($"DEAD:{NotificationErrorCodes.AmbiguousTimeout}", await OutboxStateAsync(whatsApp.Send));
            Assert.Single(scenario.Provider.Requests, request => request.MessageId == whatsApp.Notification);

            // Without the recorded consent (AI-08 NTF-004) the expansion creates the e-mail notice only.
            scenario.Consents.Clear();
            var noConsent = await scenario.RequestAsync(admin, owner, seeded.Order, await InsertOrderEventAsync(admin, owner, seeded.Order, 3), "DELIVERED");
            await scenario.ProcessAsync(worker, noConsent);
            Assert.Equal(["EMAIL"], (await scenario.SendRowsAsync(noConsent)).Select(static send => send.Channel));
        }
        finally
        {
            await RetireAsync(owner);
        }
    }

    [PostgreSqlContractFact]
    public async Task A_send_outside_the_window_waits_for_the_next_opening_without_an_attempt()
    {
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync();
        var owner = await RecipientNoticeSeed.InsertOrganizationAsync(admin, "NTF-005 Quiet hours");
        var scenario = new Scenario(fixture);
        try
        {
            // A one-hour window that is certainly closed now in America/Mazatlan.
            var localHour = await AdminScalarAsync<int>("SELECT extract(hour FROM clock_timestamp() AT TIME ZONE 'America/Mazatlan')::integer");
            var (start, end, dayOffset) = localHour < 12 ? ("13:00", "14:00", 0) : ("01:00", "02:00", 1);
            await using var worker = scenario.CreateWorker(settings =>
            {
                settings["Notifications:RecipientNotices:QuietHours:Start"] = start;
                settings["Notifications:RecipientNotices:QuietHours:End"] = end;
            });
            var seeded = await scenario.SeedOrderAsync(admin, owner, "DELIVERED", phone: false, email: true, link: true);
            var source = await scenario.RequestAsync(admin, owner, seeded.Order, seeded.OrderEvent, "DELIVERED");
            await scenario.ProcessAsync(worker, source);
            var send = Assert.Single(await scenario.SendRowsAsync(source));

            await scenario.ProcessAsync(worker, send.Send);

            Assert.Equal("PENDING:0:-", await NotificationStateAsync(send.Notification));
            Assert.Equal("RETRY:-", await OutboxStateAsync(send.Send));
            Assert.True(await AdminScalarAsync<bool>(
                """
                SELECT available_at=((date_trunc('day', clock_timestamp() AT TIME ZONE 'America/Mazatlan') + @offset * interval '1 day'
                  + @start::time::interval) AT TIME ZONE 'America/Mazatlan')
                FROM platform.outbox_events WHERE id=@id
                """,
                new NpgsqlParameter("offset", dayOffset), new NpgsqlParameter("start", start), new NpgsqlParameter("id", send.Send)));
            Assert.Empty(scenario.Provider.Requests);
            Assert.Contains(scenario.Logs.Lines, line => line.Contains("outcome DEFERRED, code -", StringComparison.Ordinal));
        }
        finally
        {
            await RetireAsync(owner);
        }
    }

    // ------------------------------------------------------------------ helpers

    private sealed record Expanded(Guid Notification, Guid Send, Guid Lease, int Version);

    /// <summary>One PENDING recipient notice on <paramref name="channel"/>, created through the NTF-003 expansion.</summary>
    private async Task<Expanded> ExpandAsync(NpgsqlConnection admin, Guid owner, Guid order, Guid orderEvent, string publicId, string channel)
    {
        var source = await RecipientNoticeSeed.InsertRequestAsync(admin, owner, order, orderEvent, "DELIVERED", DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal("SOURCE_EXPANDED", await WorkerScalarAsync<string>(
            "SELECT security.expand_recipient_notice_request(@source,@lease,true,interval '12 hours',time '00:00',time '23:59:59.999999',@public_id,@channels)",
            new NpgsqlParameter("source", source), new NpgsqlParameter("lease", await LeaseAsync(source)),
            new NpgsqlParameter("public_id", publicId), new NpgsqlParameter<string[]>("channels", [channel])));
        var notification = await AdminScalarAsync<Guid>(
            "SELECT id FROM notifications.notifications WHERE source_event_id=@id", new NpgsqlParameter("id", source));
        var send = await AdminScalarAsync<Guid>(
            "SELECT id FROM platform.outbox_events WHERE topic='notifications.send-requested' AND aggregate_id=@id",
            new NpgsqlParameter("id", notification));
        return new Expanded(notification, send, Guid.Empty, 1);
    }

    private async Task<bool> ApplyAsync(Expanded notice, string outcome, string code, string? reference)
    {
        var lease = await LeaseAsync(notice.Send);
        return await WorkerScalarAsync<bool>(
            "SELECT security.apply_notification_outcome(@outbox,@lease,@notification,@version,@outcome,@code,@reference,clock_timestamp(),NULL)",
            new NpgsqlParameter("outbox", notice.Send), new NpgsqlParameter("lease", lease),
            new NpgsqlParameter("notification", notice.Notification), new NpgsqlParameter("version", notice.Version),
            new NpgsqlParameter("outcome", outcome), new NpgsqlParameter("code", code),
            new NpgsqlParameter("reference", NpgsqlDbType.Text) { Value = (object?)reference ?? DBNull.Value });
    }

    private async Task<bool> CancelAsync(Expanded notice, string code)
    {
        var lease = notice.Lease == Guid.Empty ? await LeaseAsync(notice.Send) : notice.Lease;
        return await WorkerScalarAsync<bool>(
            "SELECT security.cancel_notification(@outbox,@lease,@notification,@version,@code,clock_timestamp())",
            new NpgsqlParameter("outbox", notice.Send), new NpgsqlParameter("lease", lease),
            new NpgsqlParameter("notification", notice.Notification), new NpgsqlParameter("version", notice.Version),
            new NpgsqlParameter("code", code));
    }

    /// <summary>The two counts as <c>paqueteria_worker</c>, or <see langword="null"/> when the function returns no row.</summary>
    private async Task<(int, int)?> CountsAsync(Guid outbox, Guid lease)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_worker");
        await using var command = new NpgsqlCommand(
            "SELECT order_channel_count,day_channel_count FROM security.read_recipient_notice_counts(@outbox,@lease)",
            connection,
            transaction);
        command.Parameters.AddWithValue("outbox", outbox);
        command.Parameters.AddWithValue("lease", lease);
        (int, int)? result = null;
        await using (var reader = await command.ExecuteReaderAsync())
        {
            if (await reader.ReadAsync())
            {
                result = (reader.GetInt32(0), reader.GetInt32(1));
                Assert.Equal(2, reader.FieldCount);
                Assert.False(await reader.ReadAsync());
            }
        }

        await transaction.CommitAsync();
        return result;
    }

    private static async Task<Guid> InsertOrderEventAsync(NpgsqlConnection admin, Guid owner, Guid order, int version)
    {
        var id = Guid.NewGuid();
        await RecipientNoticeSeed.ExecuteAsync(admin,
            """
            INSERT INTO orders.order_events(id,order_id,owner_org_id,aggregate_version,event_type,public_event_code,payload,occurred_at)
              VALUES (@id,@order,@org,@version,'ORDER_STATUS_CHANGED','ORDER_CREATED','{}',clock_timestamp())
            """,
            new NpgsqlParameter("id", id), new NpgsqlParameter("order", order), new NpgsqlParameter("org", owner),
            new NpgsqlParameter("version", version));
        return id;
    }

    /// <summary>The lease a claim would give, set on one row only so no other row of the shared database is claimed.</summary>
    private Task<Guid> LeaseAsync(Guid id) =>
        AdminScalarAsync<Guid>(
            """
            UPDATE platform.outbox_events SET status='PROCESSING',attempts=attempts+1,locked_at=clock_timestamp(),
              locked_by='ntf005-contract',lease_token=gen_random_uuid(),lease_expires_at=clock_timestamp()+interval '1 minute',
              available_at=LEAST(available_at,clock_timestamp())
            WHERE id=@id AND status IN ('PENDING','RETRY','PROCESSING')
            RETURNING lease_token
            """,
            new NpgsqlParameter("id", id));

    /// <summary>Every row this test left claimable for the organization ends DEAD, so no other test claims it.</summary>
    private Task RetireAsync(Guid owner) =>
        ExecuteAdminAsync(
            """
            UPDATE platform.outbox_events SET status='DEAD',processed_at=clock_timestamp(),
              locked_at=NULL,locked_by=NULL,lease_token=NULL,lease_expires_at=NULL
            WHERE owner_org_id=@owner AND status IN ('PENDING','RETRY','PROCESSING')
            """,
            new NpgsqlParameter("owner", owner));

    private Task<string> NotificationStateAsync(Guid id) =>
        AdminScalarAsync<string>(
            "SELECT status || ':' || attempts || ':' || COALESCE(last_provider_attempt_code,'-') FROM notifications.notifications WHERE id=@id",
            new NpgsqlParameter("id", id));

    private Task<string> OutboxStateAsync(Guid id) =>
        AdminScalarAsync<string>(
            "SELECT status || ':' || COALESCE(last_error,'-') FROM platform.outbox_events WHERE id=@id",
            new NpgsqlParameter("id", id));

    private async Task<T> AdminScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? default! : (T)value!;
    }

    private async Task<T> WorkerScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, transaction, "SET LOCAL ROLE paqueteria_worker");
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.AddRange(parameters);
        var result = (T)(await command.ExecuteScalarAsync())!;
        await transaction.CommitAsync();
        return result;
    }

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>One order of the run, with its first order event, número de guía and destination location.</summary>
    private sealed record SeededOrder(Guid Order, Guid OrderEvent, string PublicId, Guid Destination);

    /// <summary>
    /// One run of the lane as the Worker composes it, with this run's own link key and contact lookup key (generated at
    /// runtime), the synthetic providers behind the real router (recorded, and ambiguous where a test says so), the
    /// consent the test records, and every log line captured.
    /// </summary>
    private sealed class Scenario(PostgreSqlContractFixture fixture)
    {
        public byte[] LinkKey { get; } = RandomNumberGenerator.GetBytes(32);

        public byte[] LookupKey { get; } = RandomNumberGenerator.GetBytes(32);

        public ConcurrentBag<Guid> Consents { get; } = [];

        public RecordingProvider Provider { get; } = new();

        public CapturedLogs Logs { get; } = new();

        public string Token(Guid order, int generation) => TrackingLinkTokenDerivation.DeriveToken(LinkKey, 1, order, generation);

        public ServiceProvider CreateWorker(Action<Dictionary<string, string?>>? configure = null)
        {
            var settings = new Dictionary<string, string?>
            {
                ["ConnectionStrings:PaqueteriaWorker"] = fixture.WorkerConnectionString,
                ["Notifications:RecipientNotices:Enabled"] = "true",
                // Open all day but its last second, so the run does not depend on the hour it starts at.
                ["Notifications:RecipientNotices:QuietHours:Start"] = "00:00",
                ["Notifications:RecipientNotices:QuietHours:End"] = "23:59:59",
                ["Messaging:WhatsApp:Provider"] = "Synthetic",
                ["Messaging:Email:Provider"] = "Synthetic",
                ["ContactLookup:CurrentKeyVersion"] = "1",
                ["ContactLookup:Keys:1"] = Convert.ToBase64String(LookupKey),
                ["PublicTracking:PublicBaseUrl"] = BaseUrl,
                ["PublicTracking:CurrentLinkKeyVersion"] = "1",
                ["PublicTracking:LinkKeys:1"] = Convert.ToBase64String(LinkKey),
            };
            foreach (var kind in RecipientNoticeCatalog.All)
            {
                var key = RecipientNoticeCatalog.TemplateKey(kind);
                settings[$"Messaging:WhatsApp:Templates:{key}:Name"] =
                    "ntf005_" + key.Replace("recipient.", string.Empty, StringComparison.Ordinal).Replace('-', '_');
                settings[$"Messaging:WhatsApp:Templates:{key}:LanguageCode"] = RecipientNoticeTexts.WhatsAppLanguage;
                settings[$"Messaging:WhatsApp:Templates:{key}:ParameterCount"] = "1";
                settings[$"Messaging:Email:Templates:{key}:Subject"] = RecipientNoticeTexts.EmailSubject(kind);
                settings[$"Messaging:Email:Templates:{key}:PlainText"] = RecipientNoticeTexts.EmailPlainText(kind);
                settings[$"Messaging:Email:Templates:{key}:ParameterCount"] = "3";
            }

            configure?.Invoke(settings);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var environment = new TestingEnvironment();
            var services = new ServiceCollection()
                .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs))
                .AddSingleton<IConfiguration>(configuration)
                .AddSingleton<IHostEnvironment>(environment)
                .AddSingleton<IOwnerOrganizationDispatcherReader, NoAudience>()
                .AddSingleton<IActiveNotificationRecipientReader, NoAudience>();
            // The Worker's own order (Program.cs): Notifications, messaging, then the Orders and Locations readers.
            services.AddNotificationsInfrastructure(configuration);
            services.AddPaqueteriaMessaging(configuration, environment);
            services.AddOrdersRecipientNoticeReaders(configuration, environment);
            services.AddLocationsRecipientNoticeContactReader(configuration);
            // The consent store arrives with AI-08 NTF-004; the run records it per order.
            services.RemoveAll<IRecipientNoticeConsentReader>();
            services.AddSingleton<IRecipientNoticeConsentReader>(new RecordedConsents(Consents));
            // The real router (and its synthetic providers) behind a recorder.
            var router = services.Last(descriptor => descriptor.ServiceType == typeof(IMessagingProvider)).ImplementationType!;
            Assert.Equal("MessagingProviderRouter", router.Name);
            services.RemoveAll<IMessagingProvider>();
            services.AddSingleton<IMessagingProvider>(provider =>
                Provider.Wrap((IMessagingProvider)ActivatorUtilities.CreateInstance(provider, router)));
            var built = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            // The start validation the Worker runs (ValidateOnStart) passes.
            _ = built.GetRequiredService<IOptions<RecipientNoticesOptions>>().Value;
            return built;
        }

        public async Task<SeededOrder> SeedOrderAsync(NpgsqlConnection admin, Guid owner, string status, bool phone, bool email, bool link)
        {
            var (order, orderEvent, publicId) = await RecipientNoticeSeed.InsertOrderAsync(admin, owner);
            await SetStatusAsync(admin, order, status);
            var destination = (Guid)(await new NpgsqlCommand(
                $"SELECT destination_location_id FROM orders.orders WHERE id='{order:D}'", admin).ExecuteScalarAsync())!;
            // One-byte synthetic copies: the synthetic providers never open them.
            await RecipientNoticeSeed.ExecuteAsync(admin,
                """
                UPDATE locations.locations SET
                  email_ciphertext=CASE WHEN @email THEN decode('02','hex') END,
                  notice_phone_ciphertext=CASE WHEN @phone THEN decode('03','hex') END,
                  notice_email_ciphertext=CASE WHEN @email THEN decode('04','hex') END,
                  notice_key_version=CASE WHEN @phone OR @email THEN 'ntf005-synthetic-v1' END
                WHERE id=@id
                """,
                new NpgsqlParameter("phone", phone), new NpgsqlParameter("email", email), new NpgsqlParameter("id", destination));
            if (link)
            {
                await RecipientNoticeSeed.ExecuteAsync(admin,
                    """
                    INSERT INTO orders.public_tracking_tokens(order_id,owner_org_id,token_hash,expires_at,generation,key_version)
                    VALUES (@order,@org,@hash,'infinity',1,1)
                    """,
                    new NpgsqlParameter("order", order), new NpgsqlParameter("org", owner),
                    new NpgsqlParameter("hash", SHA256.HashData(Encoding.UTF8.GetBytes(Token(order, 1)))));
            }

            return new SeededOrder(order, orderEvent, publicId, destination);
        }

        public Task SetStatusAsync(NpgsqlConnection admin, Guid order, string status) =>
            RecipientNoticeSeed.ExecuteAsync(admin,
                "UPDATE orders.orders SET status=@status WHERE id=@id",
                new NpgsqlParameter("status", status), new NpgsqlParameter("id", order));

        public Task<Guid> RequestAsync(NpgsqlConnection admin, Guid owner, Guid order, Guid orderEvent, string notice, DateTimeOffset? occurredAt = null) =>
            RecipientNoticeSeed.InsertRequestAsync(admin, owner, order, orderEvent, notice, occurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-1));

        /// <summary>Leases one row and hands it to the composed <see cref="NotificationsOutboxProcessor"/>, as a claim would.</summary>
        public async Task ProcessAsync(ServiceProvider worker, Guid id)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                UPDATE platform.outbox_events SET status='PROCESSING',attempts=attempts+1,locked_at=clock_timestamp(),
                  locked_by='ntf005-contract',lease_token=gen_random_uuid(),lease_expires_at=clock_timestamp()+interval '1 minute',
                  available_at=LEAST(available_at,clock_timestamp())
                WHERE id=@id AND status IN ('PENDING','RETRY')
                RETURNING id,owner_org_id,tenant_context::text,topic,aggregate_type,aggregate_id,aggregate_version,payload::text,
                  attempts,lease_token,lease_expires_at,created_at,available_at,trace_parent
                """);
            command.Parameters.AddWithValue("id", id);
            ClaimedNotificationOutboxMessage message;
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert.True(await reader.ReadAsync());
                message = new ClaimedNotificationOutboxMessage(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetGuid(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.GetString(7),
                    reader.GetInt32(8),
                    reader.GetGuid(9),
                    reader.GetFieldValue<DateTimeOffset>(10),
                    reader.GetFieldValue<DateTimeOffset>(11),
                    reader.GetFieldValue<DateTimeOffset>(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13));
            }

            await worker.GetRequiredService<NotificationsOutboxProcessor>().ProcessOwnedAsync(message, CancellationToken.None);
        }

        /// <summary>The send requests a source created, WhatsApp first.</summary>
        public async Task<IReadOnlyList<(string Channel, Guid Notification, Guid Send)>> SendRowsAsync(Guid source)
        {
            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT n.channel,n.id,e.id FROM notifications.notifications n
                JOIN platform.outbox_events e ON e.aggregate_id=n.id AND e.topic='notifications.send-requested'
                WHERE n.source_event_id=@source
                ORDER BY CASE n.channel WHEN 'WHATSAPP' THEN 0 ELSE 1 END
                """);
            command.Parameters.AddWithValue("source", source);
            var rows = new List<(string, Guid, Guid)>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                rows.Add((reader.GetString(0), reader.GetGuid(1), reader.GetGuid(2)));
            }

            return rows;
        }

        /// <summary>Requests one notice for an e-mail-only order, sends it and returns the notification state.</summary>
        public async Task<string> ExpandAndSendAsync(NpgsqlConnection admin, ServiceProvider worker, Guid owner, SeededOrder order, string notice)
        {
            var source = await RequestAsync(admin, owner, order.Order, order.OrderEvent, notice);
            await ProcessAsync(worker, source);
            var send = Assert.Single(await SendRowsAsync(source));
            await ProcessAsync(worker, send.Send);
            await using var command = fixture.AdminDataSource.CreateCommand(
                "SELECT status || ':' || attempts || ':' || COALESCE(last_provider_attempt_code,'-') FROM notifications.notifications WHERE id=@id");
            command.Parameters.AddWithValue("id", send.Notification);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        /// <summary>No placeholder, link, token or unsubscribe link in a row, an event or a captured log line.</summary>
        public async Task AssertNothingLeakedAsync(Guid owner, string? token)
        {
            var secrets = new List<string> { SyntheticPhone, "520000000002", SyntheticEmail, "example.invalid", "/track/", "/avisos/baja/", "/unsubscribe/" };
            if (token is not null)
            {
                secrets.Add(token);
            }

            await using var command = fixture.AdminDataSource.CreateCommand(
                """
                SELECT coalesce(string_agg(e.payload::text || '|' || coalesce(e.last_error,'') || '|' || coalesce(e.trace_parent,''), '#'),'') ||
                  (SELECT coalesce(string_agg(n.variables_snapshot::text || '|' || coalesce(n.provider_reference,'') || '|' ||
                     coalesce(n.last_provider_attempt_code,''), '#'),'') FROM notifications.notifications n WHERE n.owner_org_id=@owner) ||
                  (SELECT coalesce(string_agg(h.status, '#'),'') FROM notifications.notification_status_events h
                   WHERE h.owner_org_id=@owner)
                FROM platform.outbox_events e WHERE e.owner_org_id=@owner
                """);
            command.Parameters.AddWithValue("owner", owner);
            var rows = (string)(await command.ExecuteScalarAsync())!;
            foreach (var secret in secrets)
            {
                Assert.DoesNotContain(secret, rows, StringComparison.Ordinal);
                Assert.DoesNotContain(Logs.Lines, line => line.Contains(secret, StringComparison.Ordinal));
            }

            Assert.NotEmpty(Logs.Lines);
        }
    }

    /// <summary>The real router behind a recorder; a scripted notification gets an ambiguous timeout instead.</summary>
    private sealed class RecordingProvider
    {
        public ConcurrentQueue<MessagingRequest> RequestsQueue { get; } = new();

        public ConcurrentBag<Guid> AmbiguousFor { get; } = [];

        public IReadOnlyCollection<MessagingRequest> Requests => RequestsQueue;

        public IMessagingProvider Wrap(IMessagingProvider inner) => new Recorder(this, inner);

        private sealed class Recorder(RecordingProvider owner, IMessagingProvider inner) : IMessagingProvider
        {
            public ValueTask<MessagingResult> SendAsync(MessagingRequest request, CancellationToken cancellationToken)
            {
                owner.RequestsQueue.Enqueue(request);
                return owner.AmbiguousFor.Contains(request.MessageId)
                    ? ValueTask.FromResult(new MessagingResult(MessagingOutcome.AmbiguousTimeout, MessagingResultCodes.SyntheticAmbiguous))
                    : inner.SendAsync(request, cancellationToken);
            }
        }
    }

    private sealed class RecordedConsents(ConcurrentBag<Guid> orders) : IRecipientNoticeConsentReader
    {
        public Task<bool> HasWhatsAppConsentAsync(Guid ownerOrganizationId, Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(orders.Contains(orderId));
    }

    private sealed class NoAudience : IOwnerOrganizationDispatcherReader, IActiveNotificationRecipientReader
    {
        public Task<IReadOnlyList<Guid>> ReadActiveDispatcherUserIdsAsync(Guid ownerOrganizationId, int limit, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<IReadOnlyList<Guid>> ReadActiveUserIdsAsync(IReadOnlyCollection<Guid> requestedUserIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);
    }

    private sealed class TestingEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";

        public string ApplicationName { get; set; } = "Paqueteria.Worker";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    /// <summary>Every log line of the run, formatted, with its scope values.</summary>
    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _lines = new();

        public IReadOnlyCollection<string> Lines => _lines;

        public ILogger CreateLogger(string categoryName) => new Logger(_lines);

        public void Dispose()
        {
        }

        private sealed class Logger(ConcurrentQueue<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull
            {
                lines.Enqueue($"scope {state}");
                return null;
            }

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lines.Enqueue(formatter(state, exception));
                if (exception is not null)
                {
                    lines.Enqueue(exception.ToString());
                }
            }
        }
    }
}
