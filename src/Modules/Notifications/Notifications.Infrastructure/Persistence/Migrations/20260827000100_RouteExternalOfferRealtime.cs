using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Notifications.Infrastructure.Persistence.Migrations;

[DbContext(typeof(NotificationsDbContext))]
[Migration(MigrationId)]
public sealed class RouteExternalOfferRealtime : Migration
{
    public const string MigrationId = "20260827000100_RouteExternalOfferRealtime";

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;

            RESET ROLE;
            SET ROLE paqueteria_outbox_executor;

            CREATE OR REPLACE FUNCTION security.resolve_outbox_consumer(p_topic text) RETURNS text
            LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
            SET search_path=pg_catalog,security,pg_temp AS $fn$
              SELECT CASE
                WHEN p_topic IN (
                  'orders.status-changed','orders.timeline-event-added',
                  'dispatch.assignment-changed','dispatch.external-offer-changed',
                  'notifications.status-changed') THEN 'REALTIME'
                WHEN p_topic IN ('orders.created','notifications.send-requested') THEN 'NOTIFICATIONS'
                ELSE 'UNROUTED'
              END;
            $fn$;

            RESET ROLE;
            SET ROLE paqueteria_migrator;

            REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;
            """);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            """
            -- NTF-001 rollback blocked from destructive table/function removal; only the EXT-001 topic is withdrawn.
            GRANT CREATE ON SCHEMA security TO paqueteria_outbox_executor;

            RESET ROLE;
            SET ROLE paqueteria_outbox_executor;

            CREATE OR REPLACE FUNCTION security.resolve_outbox_consumer(p_topic text) RETURNS text
            LANGUAGE sql IMMUTABLE STRICT PARALLEL SAFE
            SET search_path=pg_catalog,security,pg_temp AS $fn$
              SELECT CASE
                WHEN p_topic IN (
                  'orders.status-changed','orders.timeline-event-added',
                  'dispatch.assignment-changed','notifications.status-changed') THEN 'REALTIME'
                WHEN p_topic IN ('orders.created','notifications.send-requested') THEN 'NOTIFICATIONS'
                ELSE 'UNROUTED'
              END;
            $fn$;

            RESET ROLE;
            SET ROLE paqueteria_migrator;

            REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;
            """);
}
