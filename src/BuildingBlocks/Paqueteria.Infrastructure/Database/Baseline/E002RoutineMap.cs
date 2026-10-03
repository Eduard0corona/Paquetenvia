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

    /// <summary>
    /// D8-OUTBOX-LANE-DISPATCH: installed by the Notifications lane migration
    /// 20260927000200_AddDispatchOutboxLane after NTF-001, owned by paqueteria_outbox_executor and
    /// executable only by paqueteria_worker.
    /// </summary>
    private static readonly E002RoutineEntry[] DispatchLaneEntries =
    [
        new("security.claim_dispatch_outbox(text,integer,interval)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
        new("security.requeue_stale_dispatch_outbox(interval,integer,integer)", "paqueteria_outbox_executor", ["paqueteria_worker"]),
    ];

    /// <summary>OPS-003-CLEANUP-ROLE: installed by the Custody OPS-003 lane, independently of NTF-001 and LIF-001.</summary>
    private static readonly E002RoutineEntry[] Ops003Entries =
    [
        new("security.purge_expired_idempotency_keys(timestamp with time zone,integer,boolean)", "paqueteria_cleanup_executor", ["paqueteria_worker"]),
        new("security.expire_proof_upload_sessions(integer)", "paqueteria_cleanup_executor", ["paqueteria_worker"]),
    ];

    /// <summary>
    /// REG-001 (AUTH-OPEN-REGISTRATION): installed by the Organizations lane migration
    /// 20260927000400_AddSelfServiceRegistration, owned by paqueteria_registration_executor and executable
    /// only by paqueteria_app, independently of NTF-001, LIF-001 and OPS-003.
    /// </summary>
    private static readonly E002RoutineEntry[] Reg001Entries =
    [
        new("security.register_identity_subject(text,uuid)", "paqueteria_registration_executor", ["paqueteria_app"]),
        new("security.create_self_service_organization(uuid,uuid,uuid,uuid,text,text,text,text)", "paqueteria_registration_executor", ["paqueteria_app"]),
        new("security.list_own_organization_applications(uuid)", "paqueteria_registration_executor", ["paqueteria_app"]),
        new("security.list_pending_ally_organizations(uuid,uuid,integer)", "paqueteria_registration_executor", ["paqueteria_app"]),
        new("security.decide_ally_organization(uuid,uuid,uuid,boolean,text)", "paqueteria_registration_executor", ["paqueteria_app"]),
    ];

    /// <summary>
    /// BFF-SESSION-TABLE-SHAPE: installed by the Identity lane migration 20260927000400_AddBffSessionStore,
    /// owned by paqueteria_session_executor and executable only by paqueteria_app.
    /// </summary>
    private static readonly E002RoutineEntry[] BffSessionEntries =
    [
        new("security.create_bff_session(bytea,text,text,bytea,timestamp with time zone)", "paqueteria_session_executor", ["paqueteria_app"]),
        new("security.resolve_bff_session(bytea)", "paqueteria_session_executor", ["paqueteria_app"]),
        new("security.revoke_bff_session(bytea)", "paqueteria_session_executor", ["paqueteria_app"]),
        new("security.revoke_bff_session(text)", "paqueteria_session_executor", ["paqueteria_app"]),
        new("security.revoke_bff_session(text,timestamp with time zone)", "paqueteria_session_executor", ["paqueteria_app"]),
        new("security.register_bff_logout_jti(bytea,timestamp with time zone)", "paqueteria_session_executor", ["paqueteria_app"]),
    ];

    /// <summary>
    /// BFF-SESSION-TABLE-SHAPE purge through OPS-003-CLEANUP-ROLE: installed by the Custody lane migration
    /// 20260927000400_AddBffSessionPurge, owned by paqueteria_cleanup_executor, executable only by paqueteria_worker.
    /// </summary>
    private static readonly E002RoutineEntry[] BffPurgeEntries =
    [
        new("security.purge_bff_sessions(integer)", "paqueteria_cleanup_executor", ["paqueteria_worker"]),
    ];

    /// <summary>
    /// REG-002 (REG-JOIN-EXISTING-BY-EMAIL): installed by the Organizations lane migration
    /// 20260927000500_AddPendingMemberships after REG-001, with the same owner and grantee.
    /// </summary>
    private static readonly E002RoutineEntry[] Reg002Entries =
    [
        new("security.add_pending_membership(uuid,uuid,uuid,bytea,integer,text,text,text)", "paqueteria_registration_executor", ["paqueteria_app"]),
        new("security.renew_pending_membership(uuid,uuid,uuid,text,text)", "paqueteria_registration_executor", ["paqueteria_app"]),
        new("security.revoke_pending_membership(uuid,uuid,uuid,text,text)", "paqueteria_registration_executor", ["paqueteria_app"]),
        new("security.apply_pending_memberships(text,bytea[],integer[])", "paqueteria_registration_executor", ["paqueteria_app"]),
    ];

    /// <summary>
    /// MDM-001-OPERATOR-LOADER: installed by the Pricing lane migration 20260928000100_AddMasterDataLoader,
    /// owned by paqueteria_master_data_executor and executable only by paqueteria_master_data_loader.
    /// </summary>
    private static readonly E002RoutineEntry[] Mdm001Entries =
    [
        new("security.load_master_data(uuid,uuid,json,bytea,boolean)", "paqueteria_master_data_executor", ["paqueteria_master_data_loader"]),
    ];

    /// <summary>
    /// DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03: installed by the Dispatch lane migration
    /// 20261003000100_AddOperatorOwnerOutboxExecutor, owned by paqueteria_operator_outbox_executor and executable
    /// only by paqueteria_app.
    /// </summary>
    private static readonly E002RoutineEntry[] OperatorOutboxEntries =
    [
        new("security.append_operator_order_outbox(uuid,uuid,jsonb,text,text,uuid,integer,jsonb,smallint,timestamp with time zone,timestamp with time zone)", "paqueteria_operator_outbox_executor", ["paqueteria_app"]),
        new("security.append_operator_order_audit(uuid,uuid,uuid,text,text,uuid,text,jsonb,timestamp with time zone)", "paqueteria_operator_outbox_executor", ["paqueteria_app"]),
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
            DispatchLaneEntries.Length != 2 || DispatchLaneEntries.Sum(entry => 1 + entry.Grantees.Count) != 4 ||
            Ops003Entries.Length != 2 || Ops003Entries.Sum(entry => 1 + entry.Grantees.Count) != 4 ||
            Reg001Entries.Length != 5 || Reg001Entries.Sum(entry => 1 + entry.Grantees.Count) != 10 ||
            BffSessionEntries.Length != 6 || BffSessionEntries.Sum(entry => 1 + entry.Grantees.Count) != 12 ||
            BffPurgeEntries.Length != 1 || BffPurgeEntries.Sum(entry => 1 + entry.Grantees.Count) != 2 ||
            Reg002Entries.Length != 4 || Reg002Entries.Sum(entry => 1 + entry.Grantees.Count) != 8 ||
            Mdm001Entries.Length != 1 || Mdm001Entries.Sum(entry => 1 + entry.Grantees.Count) != 2 ||
            OperatorOutboxEntries.Length != 2 || OperatorOutboxEntries.Sum(entry => 1 + entry.Grantees.Count) != 4)
        {
            throw new InvalidOperationException("E-002 normative routine-map cardinality is invalid.");
        }
    }

    public static IReadOnlyList<E002RoutineEntry> Select(
        E002RoutineMapState state,
        bool lif001Applied,
        bool dispatchLaneApplied = false,
        bool ops003Applied = false,
        bool reg001Applied = false,
        bool bffSessionApplied = false,
        bool bffPurgeApplied = false,
        bool reg002Applied = false,
        bool mdm001Applied = false,
        bool operatorOutboxApplied = false)
    {
        IReadOnlyList<E002RoutineEntry> entries = state switch
        {
            E002RoutineMapState.Pending when !dispatchLaneApplied => Array.AsReadOnly(PendingEntries),
            E002RoutineMapState.Applied or E002RoutineMapState.Ntf001TargetApplied => AppliedEntries,
            _ => throw new InvalidOperationException("E002_ROUTINE_MAP_TRANSITION_CONTEXT_INVALID"),
        };
        var selected = entries.AsEnumerable();
        if (dispatchLaneApplied)
        {
            selected = selected.Concat(DispatchLaneEntries);
        }

        if (lif001Applied)
        {
            selected = selected.Concat(Lif001Entries);
        }

        if (ops003Applied)
        {
            selected = selected.Concat(Ops003Entries);
        }

        if (reg001Applied)
        {
            selected = selected.Concat(Reg001Entries);
        }

        if (bffSessionApplied)
        {
            selected = selected.Concat(BffSessionEntries);
        }

        if (bffPurgeApplied)
        {
            selected = selected.Concat(BffPurgeEntries);
        }

        if (reg002Applied)
        {
            selected = selected.Concat(Reg002Entries);
        }

        if (mdm001Applied)
        {
            selected = selected.Concat(Mdm001Entries);
        }

        if (operatorOutboxApplied)
        {
            selected = selected.Concat(OperatorOutboxEntries);
        }

        return Array.AsReadOnly(selected.ToArray());
    }

    public static string Name(
        E002RoutineMapState state,
        bool lif001Applied,
        bool dispatchLaneApplied = false,
        bool ops003Applied = false,
        bool reg001Applied = false,
        bool bffSessionApplied = false,
        bool bffPurgeApplied = false,
        bool reg002Applied = false,
        bool mdm001Applied = false,
        bool operatorOutboxApplied = false)
    {
        var prefix = state switch
        {
            E002RoutineMapState.Pending when !dispatchLaneApplied => "ROUTINE_MAP_AI18_PENDING",
            E002RoutineMapState.Applied or E002RoutineMapState.Ntf001TargetApplied =>
                "ROUTINE_MAP_AI18_PLUS_NTF001_APPLIED",
            _ => throw new InvalidOperationException("E002_ROUTINE_MAP_TRANSITION_CONTEXT_INVALID"),
        };
        return prefix +
            (lif001Applied ? "_PLUS_LIF001" : string.Empty) +
            (dispatchLaneApplied ? "_PLUS_D8DISPATCH" : string.Empty) +
            (ops003Applied ? "_PLUS_OPS003" : string.Empty) +
            (reg001Applied ? "_PLUS_REG001" : string.Empty) +
            (bffSessionApplied ? "_PLUS_BFFSESSION" : string.Empty) +
            (bffPurgeApplied ? "_PLUS_BFFPURGE" : string.Empty) +
            (reg002Applied ? "_PLUS_REG002" : string.Empty) +
            (mdm001Applied ? "_PLUS_MDM001" : string.Empty) +
            (operatorOutboxApplied ? "_PLUS_DSPOPOUTBOX" : string.Empty) +
            "_V1";
    }
}
