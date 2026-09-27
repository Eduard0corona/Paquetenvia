namespace Paqueteria.Infrastructure.Database.Baseline;

public enum E002RoutineMapState
{
    Pending,
    Applied,
    Ntf001TargetApplied,
}

public sealed record E002RoutineEntry(string Signature, string Owner, IReadOnlyList<string> Grantees);

public static class E002RoutineMap
{
    private static readonly E002RoutineEntry[] PendingEntries =
    [
        new("security.resolve_identity_context(text)", "paqueteria_bootstrap", ["paqueteria_app"]),
        new("security.get_public_tracking_projection(text)", "paqueteria_bootstrap", ["paqueteria_app"]),
        new("security.map_public_order_status(text)", "paqueteria_migrator", ["paqueteria_app", "paqueteria_worker", "paqueteria_bootstrap"]),
        new("security.claim_outbox(text,integer,interval)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.settle_outbox(uuid,uuid,text,text,timestamp with time zone)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.requeue_stale_outbox(interval,integer,integer)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.claim_location_outbox(text,integer,interval)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.settle_location_outbox(uuid,uuid,text,text,timestamp with time zone)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.requeue_stale_location_outbox(interval,integer,integer)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.purge_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)", "paqueteria_maintenance", ["paqueteria_worker"]),
        new("security.purge_location_outbox(timestamp with time zone,timestamp with time zone,integer,boolean)", "paqueteria_maintenance", ["paqueteria_worker"]),
    ];

    private static readonly E002RoutineEntry[] Ntf001Entries =
    [
        new("security.resolve_outbox_consumer(text)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.claim_realtime_outbox(text,integer,interval)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.claim_notifications_outbox(text,integer,interval)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.claim_unowned_outbox(text,integer,interval)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.requeue_stale_realtime_outbox(interval,integer,integer)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.recover_stale_notifications_outbox(text,integer,integer,interval)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.requeue_stale_unowned_outbox(interval,integer,integer)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.read_owner_dispatcher_ids(uuid,integer)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.read_active_notification_users(uuid[])", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.emit_notification_status_changed(uuid,uuid,integer,text,text,integer,timestamp with time zone)", "paqueteria_outbox_executor", []),
        new("security.expand_order_created_notifications(uuid,uuid,uuid,text,text,timestamp with time zone,uuid[])", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.read_notification_delivery(uuid,uuid)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.apply_notification_outcome(uuid,uuid,uuid,integer,text,text,timestamp with time zone,timestamp with time zone)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.finalize_notification_max_attempts(uuid,uuid,uuid,integer,timestamp with time zone)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("notifications.provision_default_templates()", "paqueteria_outbox_executor", []),
    ];

    /// <summary>ADR-034: installed by the Orders LIF-001 lane, independently of the NTF-001 state.</summary>
    private static readonly E002RoutineEntry[] Lif001Entries =
    [
        new("security.finalize_expired_orders(integer)", "paqueteria_lifecycle_executor", ["paqueteria_worker"]),
    ];

    /// <summary>OPS-003-CLEANUP-ROLE: installed by the Custody OPS-003 lane, independently of NTF-001 and LIF-001.</summary>
    private static readonly E002RoutineEntry[] Ops003Entries =
    [
        new("security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)", "paqueteria_cleanup_executor", ["paqueteria_worker"]),
        new("security.expire_proof_upload_sessions(integer)", "paqueteria_cleanup_executor", ["paqueteria_worker"]),
    ];

    private static readonly IReadOnlyList<E002RoutineEntry> AppliedEntries =
        Array.AsReadOnly(PendingEntries.Select(entry => entry.Signature switch
        {
            "security.claim_outbox(text,integer,interval)" or
            "security.requeue_stale_outbox(interval,integer,integer)" =>
                entry with { Grantees = Array.Empty<string>() },
            _ => entry,
        }).Concat(Ntf001Entries).ToArray());

    static E002RoutineMap()
    {
        if (PendingEntries.Length != 11 || Ntf001Entries.Length != 15 || AppliedEntries.Count != 26 ||
            AppliedEntries.Sum(entry => 1 + entry.Grantees.Count) != 50 ||
            Lif001Entries.Length != 1 || Lif001Entries.Sum(entry => 1 + entry.Grantees.Count) != 2 ||
            Ops003Entries.Length != 2 || Ops003Entries.Sum(entry => 1 + entry.Grantees.Count) != 4)
        {
            throw new InvalidOperationException("E-002 normative routine-map cardinality is invalid.");
        }
    }

    public static IReadOnlyList<E002RoutineEntry> Select(
        E002RoutineMapState state,
        bool lif001Applied,
        bool ops003Applied)
    {
        IReadOnlyList<E002RoutineEntry> entries = state switch
        {
            E002RoutineMapState.Pending => Array.AsReadOnly(PendingEntries),
            E002RoutineMapState.Applied or E002RoutineMapState.Ntf001TargetApplied => AppliedEntries,
            _ => throw new InvalidOperationException("E002_ROUTINE_MAP_TRANSITION_CONTEXT_INVALID"),
        };
        IEnumerable<E002RoutineEntry> selected = entries;
        if (lif001Applied)
        {
            selected = selected.Concat(Lif001Entries);
        }

        if (ops003Applied)
        {
            selected = selected.Concat(Ops003Entries);
        }

        return lif001Applied || ops003Applied ? Array.AsReadOnly(selected.ToArray()) : entries;
    }

    public static string Name(E002RoutineMapState state, bool lif001Applied, bool ops003Applied)
    {
        var baseName = state switch
        {
            E002RoutineMapState.Pending => "ROUTINE_MAP_AI18_PENDING",
            E002RoutineMapState.Applied or E002RoutineMapState.Ntf001TargetApplied => "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED",
            _ => throw new InvalidOperationException("E002_ROUTINE_MAP_TRANSITION_CONTEXT_INVALID"),
        };
        return baseName + (lif001Applied ? "_PLUS_LIF001" : string.Empty) +
            (ops003Applied ? "_PLUS_OPS003" : string.Empty) + "_V1";
    }
}
