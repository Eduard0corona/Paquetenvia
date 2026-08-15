using Npgsql;
using Paqueteria.ContractTests.PostgreSql.Fixtures;

namespace Paqueteria.ContractTests.PostgreSql;

[Collection(PostgreSqlContractCollection.Name)]
[Trait("Category", "PostgreSqlContract")]
public sealed class NotificationsPostgreSqlContractTests(PostgreSqlContractFixture fixture)
{
    [PostgreSqlContractFact]
    public async Task Notification_schema_routing_rls_and_least_privilege_are_installed()
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              to_regclass('notifications.notification_templates') IS NOT NULL
              AND to_regclass('notifications.notification_status_events') IS NOT NULL
              AND (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='notifications.notifications'::regclass)
              AND (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='notifications.notification_templates'::regclass)
              AND (SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid='notifications.notification_status_events'::regclass)
              AND security.resolve_outbox_consumer('orders.status-changed')='REALTIME'
              AND security.resolve_outbox_consumer('notifications.status-changed')='REALTIME'
              AND security.resolve_outbox_consumer('orders.created')='NOTIFICATIONS'
              AND security.resolve_outbox_consumer('notifications.send-requested')='NOTIFICATIONS'
              AND security.resolve_outbox_consumer('synthetic.unknown')='UNROUTED'
              AND NOT has_function_privilege('paqueteria_worker','security.claim_outbox(text,integer,interval)','EXECUTE')
              AND has_function_privilege('paqueteria_worker','security.claim_realtime_outbox(text,integer,interval)','EXECUTE')
              AND has_function_privilege('paqueteria_worker','security.claim_notifications_outbox(text,integer,interval)','EXECUTE')
              AND has_function_privilege('paqueteria_worker','security.claim_unowned_outbox(text,integer,interval)','EXECUTE')
              AND NOT has_table_privilege('paqueteria_worker','notifications.notifications','SELECT')
              AND NOT has_table_privilege('paqueteria_worker','notifications.notifications','INSERT')
              AND NOT has_table_privilege('paqueteria_worker','notifications.notifications','UPDATE');
            """);

        Assert.True(await command.ExecuteScalarAsync() is true);
    }

    [PostgreSqlContractFact]
    public async Task Claims_are_mutually_exclusive_and_unknown_topic_settles_dead()
    {
        var owner = Guid.NewGuid();
        var realtime = Guid.NewGuid();
        var notifications = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@owner,'NTF Contract','NTF Contract','BUSINESS')",
            new NpgsqlParameter("owner", owner));
        foreach (var (id, topic) in new[]
        {
            (realtime, "orders.status-changed"),
            (notifications, "orders.created"),
            (unknown, "contract.synthetic-unknown"),
        })
        {
            await ExecuteAdminAsync(
                """
                INSERT INTO platform.outbox_events(
                  id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
                  payload,priority,status,attempts,available_at,created_at)
                VALUES(@id,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
                  @topic,'Order',@aggregate,1,'{}',100,'PENDING',0,clock_timestamp(),clock_timestamp())
                """,
                new NpgsqlParameter("id", id),
                new NpgsqlParameter("owner", owner),
                new NpgsqlParameter("topic", topic),
                new NpgsqlParameter("aggregate", Guid.NewGuid()));
        }

        var realtimeClaims = await ClaimAsync("claim_realtime_outbox");
        var notificationClaims = await ClaimAsync("claim_notifications_outbox");
        var unknownClaims = await ClaimAsync("claim_unowned_outbox");

        Assert.Equal(realtime, Assert.Single(realtimeClaims).Id);
        Assert.Equal(notifications, Assert.Single(notificationClaims).Id);
        var poison = Assert.Single(unknownClaims);
        Assert.Equal(unknown, poison.Id);
        Assert.True(await WorkerScalarAsync<bool>(
            "SELECT security.settle_outbox(@id,@lease,'DEAD','UNKNOWN_TOPIC',NULL)",
            new NpgsqlParameter("id", poison.Id),
            new NpgsqlParameter("lease", poison.Lease)));
        await using var status = fixture.AdminDataSource.CreateCommand(
            "SELECT status || ':' || last_error FROM platform.outbox_events WHERE id=@id");
        status.Parameters.AddWithValue("id", poison.Id);
        Assert.Equal("DEAD:UNKNOWN_TOPIC", await status.ExecuteScalarAsync());
    }

    [PostgreSqlContractFact]
    public async Task Source_expansion_is_atomic_and_idempotent_for_one_recipient()
    {
        var owner = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var order = Guid.NewGuid();
        var source = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@owner,'NTF Expansion','NTF Expansion','BUSINESS')",
            new NpgsqlParameter("owner", owner));
        await ExecuteAdminAsync(
            """
            INSERT INTO platform.outbox_events(
              id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
              payload,priority,status,attempts,available_at,created_at)
            VALUES(@id,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
              'orders.created','Order',@order,1,'{}',100,'PENDING',0,
              timestamptz '2000-01-01 00:00:00Z',timestamptz '2000-01-01 00:00:00Z')
            """,
            new NpgsqlParameter("id", source), new NpgsqlParameter("owner", owner), new NpgsqlParameter("order", order));
        var claim = (await ClaimAsync("claim_notifications_outbox")).Single(value => value.Id == source);

        var result = await WorkerScalarAsync<string>(
            """
            SELECT security.expand_order_created_notifications(
              @source,@lease,@order,'ORD_abcdefghijklmnopqrstuv','DRAFT',clock_timestamp(),@recipients)
            """,
            new NpgsqlParameter("source", source),
            new NpgsqlParameter("lease", claim.Lease),
            new NpgsqlParameter("order", order),
            new NpgsqlParameter<Guid[]>("recipients", [recipient]));

        Assert.Equal("SOURCE_EXPANDED", result);
        await using var counts = fixture.AdminDataSource.CreateCommand(
            """
            SELECT
              (SELECT count(*)::integer FROM notifications.notifications WHERE source_event_id=@source),
              (SELECT count(*)::integer FROM notifications.notification_status_events h
               JOIN notifications.notifications n ON n.id=h.notification_id WHERE n.source_event_id=@source),
              (SELECT count(*)::integer FROM platform.outbox_events
               WHERE aggregate_type='Notification' AND aggregate_id IN
                 (SELECT id FROM notifications.notifications WHERE source_event_id=@source));
            """);
        counts.Parameters.AddWithValue("source", source);
        await using var reader = await counts.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.Equal(1, reader.GetInt32(1));
        Assert.Equal(2, reader.GetInt32(2));
        await reader.DisposeAsync();

        await using (var ownTenant = await TenantTransaction.BeginAsync(
                         fixture.AppDataSource,
                         "paqueteria_app",
                         Guid.NewGuid(),
                         [owner]))
        {
            await using var own = new NpgsqlCommand(
                "SELECT count(*)::integer FROM notifications.notifications WHERE source_event_id=@source",
                ownTenant.Connection,
                ownTenant.Transaction);
            own.Parameters.AddWithValue("source", source);
            Assert.Equal(1, await own.ExecuteScalarAsync());
        }

        var foreignOwner = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@owner,'NTF Foreign','NTF Foreign','BUSINESS')",
            new NpgsqlParameter("owner", foreignOwner));
        await using (var foreignTenant = await TenantTransaction.BeginAsync(
                         fixture.AppDataSource,
                         "paqueteria_app",
                         Guid.NewGuid(),
                         [foreignOwner]))
        {
            await using var foreign = new NpgsqlCommand(
                "SELECT count(*)::integer FROM notifications.notifications WHERE source_event_id=@source",
                foreignTenant.Connection,
                foreignTenant.Transaction);
            foreign.Parameters.AddWithValue("source", source);
            Assert.Equal(0, await foreign.ExecuteScalarAsync());
        }

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAdminAsync(
            """
            INSERT INTO notifications.notifications(
              id,owner_org_id,order_id,recipient_user_id,channel,template_key,recipient_ciphertext,
              pii_key_version,status,attempts,provider_reference,created_at,sent_at,version,template_version,
              variables_snapshot,source_event_id,last_provider_attempt_code,last_attempt_at,updated_at)
            SELECT gen_random_uuid(),owner_org_id,order_id,recipient_user_id,channel,template_key,recipient_ciphertext,
              pii_key_version,status,attempts,provider_reference,created_at,sent_at,version,template_version,
              variables_snapshot,source_event_id,last_provider_attempt_code,last_attempt_at,updated_at
            FROM notifications.notifications WHERE source_event_id=@source
            """,
            new NpgsqlParameter("source", source)));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
    }

    [PostgreSqlContractFact]
    public async Task Notification_claims_skip_locked_enforce_leases_and_recover_only_their_lane()
    {
        var owner = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@owner,'NTF Lease','NTF Lease','BUSINESS')",
            new NpgsqlParameter("owner", owner));
        foreach (var (id, priority) in new[] { (firstId, 100), (secondId, 99) })
        {
            await ExecuteAdminAsync(
                """
                INSERT INTO platform.outbox_events(
                  id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
                  payload,priority,status,attempts,available_at,created_at)
                VALUES(@id,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
                  'notifications.send-requested','Notification',@aggregate,1,'{}',@priority,'PENDING',0,
                  timestamptz '2000-01-01 00:00:00Z',timestamptz '2000-01-01 00:00:00Z')
                """,
                new NpgsqlParameter("id", id),
                new NpgsqlParameter("owner", owner),
                new NpgsqlParameter("aggregate", Guid.NewGuid()),
                new NpgsqlParameter("priority", priority));
        }

        await using var firstConnection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var firstTransaction = await firstConnection.BeginTransactionAsync();
        await SetWorkerRoleAsync(firstConnection, firstTransaction);
        var firstClaim = await ReadSingleClaimAsync(firstConnection, firstTransaction, "claim_notifications_outbox", "lease-a");
        Assert.Equal(firstId, firstClaim.Id);

        await using var concurrentConnection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var concurrentTransaction = await concurrentConnection.BeginTransactionAsync();
        await SetWorkerRoleAsync(concurrentConnection, concurrentTransaction);
        var concurrentClaim = await ReadSingleClaimAsync(concurrentConnection, concurrentTransaction, "claim_notifications_outbox", "lease-b");
        Assert.Equal(secondId, concurrentClaim.Id);
        await concurrentTransaction.CommitAsync();
        await firstTransaction.CommitAsync();

        Assert.False(await WorkerScalarAsync<bool>(
            "SELECT security.settle_outbox(@id,@lease,'PROCESSED',NULL,NULL)",
            new NpgsqlParameter("id", firstId),
            new NpgsqlParameter("lease", Guid.NewGuid())));
        Assert.True(await WorkerScalarAsync<bool>(
            "SELECT security.settle_outbox(@id,@lease,'PROCESSED',NULL,NULL)",
            new NpgsqlParameter("id", firstId),
            new NpgsqlParameter("lease", firstClaim.Lease)));

        await ExecuteAdminAsync(
            "UPDATE platform.outbox_events SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id",
            new NpgsqlParameter("id", secondId));
        Assert.Equal(0, await WorkerScalarAsync<int>(
            "SELECT count(*)::integer FROM security.recover_stale_notifications_outbox('lease-recovery',10,3,interval '30 seconds')"));
        await using var status = fixture.AdminDataSource.CreateCommand(
            "SELECT status || ':' || attempts::text FROM platform.outbox_events WHERE id=@id");
        status.Parameters.AddWithValue("id", secondId);
        Assert.Equal("RETRY:1", await status.ExecuteScalarAsync());
    }

    [PostgreSqlContractFact]
    public async Task Provider_retry_cas_and_max_finalization_preserve_atomic_history()
    {
        var owner = Guid.NewGuid();
        var recipient = Guid.NewGuid();
        var order = Guid.NewGuid();
        var source = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@owner,'NTF Retry','NTF Retry','BUSINESS')",
            new NpgsqlParameter("owner", owner));
        await InsertSourceAsync(owner, order, source);
        var sourceClaim = await ClaimByIdAsync("claim_notifications_outbox", source);
        Assert.Equal("SOURCE_EXPANDED", await WorkerScalarAsync<string>(
            "SELECT security.expand_order_created_notifications(@source,@lease,@order,'ORD_abcdefghijklmnopqrstuv','DRAFT',clock_timestamp(),@recipients)",
            new NpgsqlParameter("source", source),
            new NpgsqlParameter("lease", sourceClaim.Lease),
            new NpgsqlParameter("order", order),
            new NpgsqlParameter<Guid[]>("recipients", [recipient])));

        var notificationId = await AdminScalarAsync<Guid>(
            "SELECT id FROM notifications.notifications WHERE source_event_id=@source",
            new NpgsqlParameter("source", source));
        var sendId = await AdminScalarAsync<Guid>(
            "SELECT id FROM platform.outbox_events WHERE topic='notifications.send-requested' AND aggregate_id=@id",
            new NpgsqlParameter("id", notificationId));
        var first = await ClaimByIdAsync("claim_notifications_outbox", sendId);
        Assert.True(await WorkerScalarAsync<bool>(
            "SELECT security.apply_notification_outcome(@outbox,@lease,@notification,1,'TRANSIENT','SYNTHETIC_TRANSIENT',clock_timestamp(),clock_timestamp())",
            new NpgsqlParameter("outbox", sendId), new NpgsqlParameter("lease", first.Lease),
            new NpgsqlParameter("notification", notificationId)));

        await ExecuteAdminAsync(
            "UPDATE platform.outbox_events SET available_at=timestamptz '2000-01-01 00:00:00Z' WHERE id=@id",
            new NpgsqlParameter("id", sendId));
        var second = await ClaimByIdAsync("claim_notifications_outbox", sendId);
        Assert.False(await WorkerScalarAsync<bool>(
            "SELECT security.apply_notification_outcome(@outbox,@lease,@notification,99,'AMBIGUOUS','SYNTHETIC_AMBIGUOUS_TIMEOUT',clock_timestamp(),clock_timestamp())",
            new NpgsqlParameter("outbox", sendId), new NpgsqlParameter("lease", second.Lease),
            new NpgsqlParameter("notification", notificationId)));
        Assert.True(await WorkerScalarAsync<bool>(
            "SELECT security.apply_notification_outcome(@outbox,@lease,@notification,2,'AMBIGUOUS','SYNTHETIC_AMBIGUOUS_TIMEOUT',clock_timestamp(),clock_timestamp())",
            new NpgsqlParameter("outbox", sendId), new NpgsqlParameter("lease", second.Lease),
            new NpgsqlParameter("notification", notificationId)));

        await ExecuteAdminAsync(
            "UPDATE platform.outbox_events SET available_at=timestamptz '2000-01-01 00:00:00Z' WHERE id=@id",
            new NpgsqlParameter("id", sendId));
        var third = await ClaimByIdAsync("claim_notifications_outbox", sendId);
        await ExecuteAdminAsync(
            "UPDATE platform.outbox_events SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE id=@id",
            new NpgsqlParameter("id", sendId));
        var finalization = await RecoverSingleAsync();
        Assert.Equal(sendId, finalization.Id);
        Assert.NotEqual(third.Lease, finalization.Lease);
        Assert.True(await WorkerScalarAsync<bool>(
            "SELECT security.finalize_notification_max_attempts(@outbox,@lease,@notification,3,clock_timestamp())",
            new NpgsqlParameter("outbox", sendId), new NpgsqlParameter("lease", finalization.Lease),
            new NpgsqlParameter("notification", notificationId)));

        await using var state = fixture.AdminDataSource.CreateCommand(
            """
            SELECT n.status,n.attempts,n.version,o.status,o.last_error,
              (SELECT count(*)::integer FROM notifications.notification_status_events h WHERE h.notification_id=n.id),
              (SELECT count(*)::integer FROM platform.outbox_events e WHERE e.topic='notifications.status-changed' AND e.aggregate_id=n.id)
            FROM notifications.notifications n
            JOIN platform.outbox_events o ON o.id=@send
            WHERE n.id=@notification
            """);
        state.Parameters.AddWithValue("send", sendId);
        state.Parameters.AddWithValue("notification", notificationId);
        await using var reader = await state.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("FAILED", reader.GetString(0));
        Assert.Equal(2, reader.GetInt32(1));
        Assert.Equal(4, reader.GetInt32(2));
        Assert.Equal("DEAD", reader.GetString(3));
        Assert.Equal("MAX_ATTEMPTS_EXHAUSTED", reader.GetString(4));
        Assert.Equal(4, reader.GetInt32(5));
        Assert.Equal(4, reader.GetInt32(6));
    }

    [Theory]
    [InlineData(false, "TEMPLATE_NOT_FOUND")]
    [InlineData(true, "TEMPLATE_VERSION_UNKNOWN")]
    public async Task Template_lookup_is_exact_tenant_version_channel_and_immutable(
        bool retainDifferentVersion,
        string expectedCode)
    {
        var owner = Guid.NewGuid();
        var source = Guid.NewGuid();
        var order = Guid.NewGuid();
        await ExecuteAdminAsync(
            "INSERT INTO organizations.organizations(id,legal_name,display_name,organization_type) VALUES (@owner,'NTF Template','NTF Template','BUSINESS')",
            new NpgsqlParameter("owner", owner));
        var immutable = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAdminAsync(
            "UPDATE notifications.notification_templates SET body='forbidden' WHERE owner_org_id=@owner",
            new NpgsqlParameter("owner", owner)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, immutable.SqlState);

        await ExecuteAdminAsync("ALTER TABLE notifications.notification_templates DISABLE TRIGGER notification_templates_immutable");
        if (retainDifferentVersion)
        {
            await ExecuteAdminAsync(
                """
                INSERT INTO notifications.notification_templates(
                  owner_org_id,template_key,version,channel,body,allowed_variables)
                VALUES(@owner,'orders.created.operations',2,'IN_APP','v2',
                  '["occurred_at","order_public_id","order_status"]'::jsonb)
                """,
                new NpgsqlParameter("owner", owner));
        }

        await ExecuteAdminAsync(
            "DELETE FROM notifications.notification_templates WHERE owner_org_id=@owner AND version=1",
            new NpgsqlParameter("owner", owner));
        await ExecuteAdminAsync("ALTER TABLE notifications.notification_templates ENABLE TRIGGER notification_templates_immutable");
        await InsertSourceAsync(owner, order, source);
        var claim = await ClaimByIdAsync("claim_notifications_outbox", source);

        Assert.Equal(expectedCode, await WorkerScalarAsync<string>(
            "SELECT security.expand_order_created_notifications(@source,@lease,@order,'ORD_abcdefghijklmnopqrstuv','DRAFT',clock_timestamp(),@recipients)",
            new NpgsqlParameter("source", source), new NpgsqlParameter("lease", claim.Lease),
            new NpgsqlParameter("order", order), new NpgsqlParameter<Guid[]>("recipients", [Guid.NewGuid()])));
        Assert.Equal(0, await AdminScalarAsync<int>(
            "SELECT count(*)::integer FROM notifications.notifications WHERE source_event_id=@source",
            new NpgsqlParameter("source", source)));
    }

    private async Task<IReadOnlyList<(Guid Id, Guid Lease)>> ClaimAsync(string function)
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(
            $"SELECT id,lease_token FROM security.{function}('ntf-contract',1,interval '30 seconds')",
            connection,
            transaction);
        var result = new List<(Guid, Guid)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetGuid(0), reader.GetGuid(1)));
        }

        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return result;
    }

    private async Task<(Guid Id, Guid Lease)> ClaimByIdAsync(string function, Guid expectedId)
    {
        var claims = await ClaimAsync(function);
        return Assert.Single(claims, value => value.Id == expectedId);
    }

    private async Task<(Guid Id, Guid Lease)> RecoverSingleAsync()
    {
        await using var connection = await fixture.WorkerDataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SetWorkerRoleAsync(connection, transaction);
        await using var command = new NpgsqlCommand(
            "SELECT id,lease_token FROM security.recover_stale_notifications_outbox('ntf-finalize',10,3,interval '30 seconds')",
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = (reader.GetGuid(0), reader.GetGuid(1));
        Assert.False(await reader.ReadAsync());
        await reader.DisposeAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static async Task<(Guid Id, Guid Lease)> ReadSingleClaimAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string function,
        string worker)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT id,lease_token FROM security.{function}(@worker,1,interval '30 seconds')",
            connection,
            transaction);
        command.Parameters.AddWithValue("worker", worker);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = (reader.GetGuid(0), reader.GetGuid(1));
        Assert.False(await reader.ReadAsync());
        return result;
    }

    private static async Task SetWorkerRoleAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await using var role = new NpgsqlCommand("SET LOCAL ROLE paqueteria_worker", connection, transaction);
        await role.ExecuteNonQueryAsync();
    }

    private async Task InsertSourceAsync(Guid owner, Guid order, Guid source) => await ExecuteAdminAsync(
        """
        INSERT INTO platform.outbox_events(
          id,owner_org_id,tenant_context,topic,aggregate_type,aggregate_id,aggregate_version,
          payload,priority,status,attempts,available_at,created_at)
        VALUES(@id,@owner,jsonb_build_object('organization_ids',jsonb_build_array(@owner::text)),
          'orders.created','Order',@order,1,'{}',100,'PENDING',0,
          timestamptz '2000-01-01 00:00:00Z',timestamptz '2000-01-01 00:00:00Z')
        """,
        new NpgsqlParameter("id", source), new NpgsqlParameter("owner", owner), new NpgsqlParameter("order", order));

    private async Task<T> AdminScalarAsync<T>(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        return (T)(await command.ExecuteScalarAsync())!;
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

    private async Task ExecuteAdminAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = fixture.AdminDataSource.CreateCommand(sql);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }
}
