using System.Text.RegularExpressions;
using Notifications.Infrastructure.Persistence.Migrations;

namespace Paqueteria.UnitTests.Notifications;

/// <summary>
/// NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03: static guarantees of the Notifications lane step that makes stale
/// recovery of a WhatsApp send terminal. Behaviour is proven on PostgreSQL by the contract tests.
/// </summary>
public sealed partial class StaleWhatsAppLeaseMigrationTests
{
    private const string Signature = "security.recover_stale_notifications_outbox(";

    [Fact]
    public void Down_restores_the_ntf001_recovery_body_verbatim()
    {
        Assert.Equal(
            FunctionDefinition(AddTenantSafeOutboxNotifications.UpSql).Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal),
            FunctionDefinition(FailStaleWhatsAppNotificationLeases.DownSql));
    }

    [Fact]
    public void Up_settles_a_stale_whatsapp_send_terminally_under_its_own_lease_token()
    {
        var up = FailStaleWhatsAppNotificationLeases.UpSql;
        var definition = FunctionDefinition(up);

        // Same signature, return type, security and search_path as NTF-001: only the body changes.
        Assert.StartsWith(
            FunctionHeader(FunctionDefinition(FailStaleWhatsAppNotificationLeases.DownSql)),
            definition,
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE id=v_request.id AND status='PROCESSING' AND lease_token=v_request.lease_token;",
            definition,
            StringComparison.Ordinal);
        Assert.Contains("status='DEAD',last_error='AMBIGUOUS_TIMEOUT'", definition, StringComparison.Ordinal);
        Assert.Contains("SET status='FAILED',attempts=attempts+1,version=version+1", definition, StringComparison.Ordinal);
        Assert.Contains("last_provider_attempt_code='AMBIGUOUS_TIMEOUT'", definition, StringComparison.Ordinal);
        Assert.Contains("PERFORM security.emit_notification_status_changed(", definition, StringComparison.Ordinal);
        Assert.Contains("FOR UPDATE OF o SKIP LOCKED", definition, StringComparison.Ordinal);
        // The requeue CTE excludes every WhatsApp send request, also one skipped because it was locked.
        Assert.Contains(
            "AND NOT (o.topic='notifications.send-requested' AND EXISTS (",
            definition,
            StringComparison.Ordinal);
        Assert.DoesNotMatch(ExecuteKeyword(), definition);

        // Grants and owner are untouched; the temporary CREATE on security is revoked again.
        Assert.DoesNotContain("GRANT EXECUTE", up, StringComparison.Ordinal);
        Assert.DoesNotContain("REVOKE EXECUTE", up, StringComparison.Ordinal);
        Assert.DoesNotContain("OWNER TO", up, StringComparison.Ordinal);
        Assert.EndsWith("REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;", up.TrimEnd(), StringComparison.Ordinal);
        Assert.EndsWith(
            "REVOKE CREATE ON SCHEMA security FROM paqueteria_outbox_executor;",
            FailStaleWhatsAppNotificationLeases.DownSql.TrimEnd(),
            StringComparison.Ordinal);
    }

    private static string FunctionDefinition(string sql)
    {
        var start = sql.IndexOf(Signature, StringComparison.Ordinal);
        Assert.True(start > 0);
        start = sql.LastIndexOf("CREATE ", start, StringComparison.Ordinal);
        var end = sql.IndexOf("$fn$;", sql.IndexOf("$fn$", sql.IndexOf(Signature, StringComparison.Ordinal), StringComparison.Ordinal) + 4, StringComparison.Ordinal);
        Assert.True(end > start);
        return sql[start..(end + "$fn$;".Length)];
    }

    private static string FunctionHeader(string definition) =>
        definition[..(definition.IndexOf("AS $fn$", StringComparison.Ordinal) + "AS $fn$".Length)];

    [GeneratedRegex("(^|[^a-z_])EXECUTE([^a-z_]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ExecuteKeyword();
}
