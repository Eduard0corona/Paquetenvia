using Orders.Application.Orders;
using Orders.Domain;

namespace Paqueteria.UnitTests.Orders;

/// <summary>
/// UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05: getOrder's advisory allowed_transitions reuse the ORD-002 matrix, the
/// transitionOrder authorizer and its owner-only rule; they never evaluate or expose guards.
/// </summary>
public sealed class OrderAllowedTransitionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Owner = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    private static readonly Guid Operator = Guid.Parse("a1000000-0000-0000-0000-000000000002");
    private static readonly OrderTransitionAuthorizer Authorizer = new();

    [Fact]
    public void A_dispatcher_of_the_owner_gets_every_matrix_edge_of_the_current_status_in_AI04_order()
    {
        foreach (var source in Enum.GetValues<OrderStatus>())
        {
            // A failed attempt whose decision dispatch still has to take, with custody: every exit is open.
            var allowed = Compute(Order(source), Snapshot("DISPATCHER"), failedAttempt: PendingDecision(custody: true));
            var expected = OrderTransitionMatrix.AllowedTransitions.TryGetValue(source, out var targets)
                ? targets.Order().ToArray()
                : [];
            Assert.Equal(expected, allowed.Select(item => item.Target));
        }
    }

    [Fact]
    public void Owner_dispatcher_examples_match_the_state_machine()
    {
        Assert.Equal(
            [OrderStatus.Confirmed, OrderStatus.Cancelled],
            Targets(Order(OrderStatus.Draft), Snapshot("DISPATCHER")));
        Assert.Equal(
            [OrderStatus.ReadyForPickup, OrderStatus.Cancelled],
            Targets(Order(OrderStatus.Confirmed), Snapshot("DISPATCHER")));
        Assert.Equal(
            [OrderStatus.Closed, OrderStatus.ClaimOpen],
            Targets(Order(OrderStatus.Delivered), Snapshot("DISPATCHER")));
        Assert.Empty(Targets(Order(OrderStatus.Cancelled), Snapshot("DISPATCHER")));
        Assert.Empty(Targets(Order(OrderStatus.Returned), Snapshot("DISPATCHER")));
        Assert.Empty(Targets(Order(OrderStatus.ClaimResolved), Snapshot("DISPATCHER")));
    }

    [Fact]
    public void Platform_admin_needs_MFA_like_transitionOrder()
    {
        Assert.Empty(Targets(Order(OrderStatus.Confirmed), Snapshot("PLATFORM_ADMIN"), mfa: false));
        Assert.Equal(
            [OrderStatus.ReadyForPickup, OrderStatus.Cancelled],
            Targets(Order(OrderStatus.Confirmed), Snapshot("PLATFORM_ADMIN"), mfa: true));
    }

    [Theory]
    [InlineData("VIEWER")]
    [InlineData("FINANCE")]
    [InlineData("SUPPORT")]
    [InlineData(null)]
    public void Roles_that_cannot_transition_get_nothing(string? role)
    {
        foreach (var source in Enum.GetValues<OrderStatus>())
        {
            Assert.Empty(Targets(Order(source), Snapshot(role), mfa: true));
        }
    }

    [Fact]
    public void A_driver_gets_only_its_own_driver_steps_and_only_with_a_matching_assignment()
    {
        Assert.Equal(
            [OrderStatus.AtPickup],
            Targets(Order(OrderStatus.Assigned), Snapshot("DRIVER", matchingAssignment: true)));
        Assert.Empty(Targets(Order(OrderStatus.Assigned), Snapshot("DRIVER", matchingAssignment: false)));
        Assert.Empty(Targets(Order(OrderStatus.ReadyForPickup), Snapshot("DRIVER", matchingAssignment: true)));
        Assert.Empty(Targets(Order(OrderStatus.Delivered), Snapshot("DRIVER", matchingAssignment: true)));
    }

    [Fact]
    public void ORD002_is_owner_only_so_an_operator_or_other_organization_gets_nothing()
    {
        var operated = Order(OrderStatus.Confirmed) with { OperatorOrganizationId = Operator };
        Assert.NotEmpty(Compute(operated, Snapshot("DISPATCHER"), organizationId: Owner));
        Assert.Empty(Compute(operated, Snapshot("DISPATCHER"), organizationId: Operator));
        Assert.Empty(Compute(operated, Snapshot("PLATFORM_ADMIN"), organizationId: Operator, mfa: true));
        Assert.Empty(Compute(operated, Snapshot("DISPATCHER"), organizationId: Guid.Empty));
    }

    [Fact]
    public void The_claim_window_and_finalization_rules_of_CLOSED_apply_at_the_server_clock()
    {
        var open = Order(OrderStatus.Closed) with { ClaimWindowEndsAt = Now.AddHours(1) };
        Assert.Equal([OrderStatus.ClaimOpen], Targets(open, Snapshot("DISPATCHER")));
        Assert.Empty(Targets(open with { ClaimWindowEndsAt = Now.AddTicks(-10) }, Snapshot("DISPATCHER")));
        Assert.Empty(Targets(open with { ClaimWindowEndsAt = null }, Snapshot("DISPATCHER")));
        Assert.Empty(Targets(open with { FinalizedAt = Now.AddMinutes(-1) }, Snapshot("DISPATCHER")));
    }

    [Fact]
    public void An_exhausted_version_or_an_unknown_status_admits_nothing()
    {
        Assert.Empty(Targets(Order(OrderStatus.Confirmed) with { Version = int.MaxValue }, Snapshot("DISPATCHER")));
        Assert.Empty(Targets(Order(OrderStatus.Confirmed) with { Status = "SHIPPED" }, Snapshot("DISPATCHER")));
    }

    [Fact]
    public void Required_metadata_is_the_transitionOrder_input_contract()
    {
        var confirm = Assert.Single(
            Compute(Order(OrderStatus.Draft), Snapshot("DISPATCHER")),
            item => item.Target == OrderStatus.Confirmed);
        Assert.Equal(["restricted_goods_acknowledged"], confirm.RequiredMetadata);
        var failed = Assert.Single(
            Compute(Order(OrderStatus.InTransit), Snapshot("DISPATCHER")),
            item => item.Target == OrderStatus.FailedAttempt);
        Assert.Equal(["incident_id"], failed.RequiredMetadata);
        Assert.All(
            Compute(Order(OrderStatus.Confirmed), Snapshot("DISPATCHER")),
            item => Assert.Empty(item.RequiredMetadata));
    }

    /// <summary>
    /// The required keys are exactly the ones transitionOrder needs: an edge without required metadata accepts an
    /// empty object, and an edge with required metadata is refused without it (input policy or guard).
    /// </summary>
    [Fact]
    public void Required_metadata_agrees_with_the_input_policy_and_guards_on_every_edge()
    {
        var registry = new OrderTransitionGuardRegistry();
        foreach (var (source, targets) in OrderTransitionMatrix.AllowedTransitions)
        {
            foreach (var target in targets)
            {
                var required = OrderTransitionInputPolicy.RequiredMetadataKeys(source, target);
                Assert.True(OrderTransitionInputPolicy.TryNormalizeMetadata(
                    BuildMetadata(required),
                    source,
                    target,
                    OrderTransitionInputPolicy.DefaultMaximumMetadataUtf8Bytes,
                    out var withRequired));
                if (required.Count == 0)
                {
                    Assert.True(OrderTransitionInputPolicy.TryNormalizeMetadata(
                        "{}", source, target, OrderTransitionInputPolicy.DefaultMaximumMetadataUtf8Bytes, out _));
                    continue;
                }

                var acceptedWithout = OrderTransitionInputPolicy.TryNormalizeMetadata(
                    "{}", source, target, OrderTransitionInputPolicy.DefaultMaximumMetadataUtf8Bytes, out var without);
                var guardWithout = registry.Evaluate(GuardContext(source, target, without));
                var guardWith = registry.Evaluate(GuardContext(source, target, withRequired));
                Assert.True(!acceptedWithout || (!guardWithout.Satisfied && guardWith.Code != guardWithout.Code),
                    $"{source}->{target} accepts the request without {string.Join(',', required)}.");
            }
        }
    }

    [Fact]
    public void Only_the_documented_metadata_keys_can_ever_be_required()
    {
        var keys = OrderTransitionMatrix.AllowedTransitions
            .SelectMany(pair => pair.Value.SelectMany(target =>
                OrderTransitionInputPolicy.RequiredMetadataKeys(pair.Key, target)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(["incident_id", "restricted_goods_acknowledged"], keys);
    }

    private static string BuildMetadata(IReadOnlyList<string> keys) =>
        keys.Count switch
        {
            0 => "{}",
            1 when keys[0] == OrderTransitionInputPolicy.RestrictedGoodsAcknowledgedKey =>
                "{\"restricted_goods_acknowledged\":true}",
            1 when keys[0] == OrderTransitionInputPolicy.IncidentIdKey =>
                "{\"incident_id\":\"" + Guid.Parse("b1000000-0000-0000-0000-000000000001").ToString("D") + "\"}",
            _ => throw new InvalidOperationException("Unexpected metadata keys."),
        };

    /// <summary>
    /// INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11: a driver report leaves the decision to dispatch, so a DISPATCHER
    /// or an MFA-satisfied PLATFORM_ADMIN gets "Reprogramar", "Reintentar hoy" and "Regresar" and a DRIVER gets none.
    /// </summary>
    [Fact]
    public void A_pending_failed_attempt_offers_every_exit_to_dispatch_and_none_to_the_driver()
    {
        var failed = Order(OrderStatus.FailedAttempt);
        OrderStatus[] all = [OrderStatus.Delivering, OrderStatus.Rescheduled, OrderStatus.Returning];

        Assert.Equal(all, Targets(failed, Snapshot("DISPATCHER"), failedAttempt: PendingDecision(custody: true)));
        Assert.Equal(
            all,
            Targets(failed, Snapshot("PLATFORM_ADMIN"), mfa: true, failedAttempt: PendingDecision(custody: true)));
        Assert.Empty(Targets(failed, Snapshot("PLATFORM_ADMIN"), mfa: false, failedAttempt: PendingDecision(custody: true)));
        Assert.Empty(Targets(
            failed,
            Snapshot("DRIVER", matchingAssignment: true),
            failedAttempt: PendingDecision(custody: true)));
        Assert.Empty(Targets(failed, Snapshot("VIEWER"), mfa: true, failedAttempt: PendingDecision(custody: true)));
    }

    [Fact]
    public void Returning_and_the_retry_are_listed_only_once_custody_was_acquired()
    {
        // A failed pickup: nothing was collected, so only the reschedule can be taken.
        Assert.Equal(
            [OrderStatus.Rescheduled],
            Targets(Order(OrderStatus.FailedAttempt), Snapshot("DISPATCHER"), failedAttempt: PendingDecision(custody: false)));
    }

    [Fact]
    public void A_recorded_next_action_keeps_binding_the_exits_for_everyone()
    {
        var failed = Order(OrderStatus.FailedAttempt);
        var driver = Snapshot("DRIVER", matchingAssignment: true);

        Assert.Equal(
            [OrderStatus.Delivering, OrderStatus.Rescheduled],
            Targets(failed, Snapshot("DISPATCHER"), failedAttempt: Decided("RESCHEDULED")));
        Assert.Equal(
            [OrderStatus.Delivering, OrderStatus.Rescheduled],
            Targets(failed, driver, failedAttempt: Decided("RESCHEDULED")));
        Assert.Equal([OrderStatus.Returning], Targets(failed, Snapshot("DISPATCHER"), failedAttempt: Decided("RETURNING")));
        Assert.Equal([OrderStatus.Returning], Targets(failed, driver, failedAttempt: Decided("RETURNING")));
        // An adopted incident binds nothing; custody still applies.
        Assert.Equal(
            [OrderStatus.Delivering, OrderStatus.Rescheduled, OrderStatus.Returning],
            Targets(failed, driver, failedAttempt: Decided("RETURNING", adopted: true)));
    }

    [Fact]
    public void Without_the_failed_attempt_snapshot_or_its_incident_no_exit_is_listed()
    {
        var failed = Order(OrderStatus.FailedAttempt);
        Assert.Empty(Targets(failed, Snapshot("DISPATCHER")));
        Assert.Empty(Targets(
            failed,
            Snapshot("DISPATCHER"),
            failedAttempt: new FailedAttemptExitSnapshot(
                new IncidentGuardSnapshot(false, false, true, true),
                new CustodyGuardSnapshot(true))));
        // The snapshot only ever narrows the exits of FAILED_ATTEMPT.
        Assert.Equal(
            [OrderStatus.ReadyForPickup, OrderStatus.Cancelled],
            Targets(Order(OrderStatus.Confirmed), Snapshot("DISPATCHER"), failedAttempt: PendingDecision(custody: false)));
    }

    /// <summary>The list and the guards are the same policy: whatever is listed passes the decision guards.</summary>
    [Fact]
    public void Every_listed_exit_passes_the_failed_attempt_decision_guards()
    {
        var registry = new OrderTransitionGuardRegistry();
        foreach (var role in new[] { "DISPATCHER", "PLATFORM_ADMIN", "DRIVER" })
        {
            foreach (var snapshot in new[]
                     {
                         PendingDecision(custody: true), PendingDecision(custody: false), Decided("RESCHEDULED"),
                         Decided("RETURNING"), Decided("RETURNING", adopted: true),
                     })
            {
                foreach (var target in Targets(
                             Order(OrderStatus.FailedAttempt),
                             Snapshot(role, matchingAssignment: true),
                             mfa: true,
                             failedAttempt: snapshot))
                {
                    var result = registry.Evaluate(new OrderTransitionGuardContext
                    {
                        Source = OrderStatus.FailedAttempt,
                        Target = target,
                        Reason = "motivo",
                        OccurredAt = Now,
                        ClaimWindowEndsAt = null,
                        FinalizedAt = null,
                        CodExpectedCents = 0,
                        MonetaryIntegrityValid = true,
                        Metadata = NormalizedTransitionMetadata.Empty,
                        Assignment = new AssignmentGuardSnapshot(true, true, true, true),
                        Incidents = snapshot.Incidents,
                        Custody = snapshot.Custody,
                        ActorRole = role,
                    });
                    Assert.True(result.Satisfied, $"{role} -> {target}: {result.Code}");
                }
            }
        }
    }

    /// <summary>A context whose only failing guards can be the metadata-driven ones.</summary>
    private static OrderTransitionGuardContext GuardContext(
        OrderStatus source,
        OrderStatus target,
        NormalizedTransitionMetadata metadata) => new()
        {
            Source = source,
            Target = target,
            Reason = "motivo",
            OccurredAt = Now,
            ClaimWindowEndsAt = Now.AddHours(1),
            FinalizedAt = null,
            CodExpectedCents = 0,
            MonetaryIntegrityValid = true,
            Metadata = metadata,
            QuoteAcceptance = new QuoteAcceptanceGuardSnapshot(true, true),
            Assignment = new AssignmentGuardSnapshot(true, true, true, true),
            Proofs = new ProofGuardSnapshot(true, true),
            Incidents = new IncidentGuardSnapshot(true, true, true, false, "RESCHEDULED"),
            Cod = new CodGuardSnapshot(false, null, null),
            Custody = new CustodyGuardSnapshot(true),
        };

    private static OrderStatus[] Targets(
        OrderResult order,
        OrderTransitionAuthorizationSnapshot snapshot,
        bool mfa = false,
        FailedAttemptExitSnapshot? failedAttempt = null) =>
        Compute(order, snapshot, mfa: mfa, failedAttempt: failedAttempt).Select(item => item.Target).ToArray();

    private static IReadOnlyList<OrderAllowedTransition> Compute(
        OrderResult order,
        OrderTransitionAuthorizationSnapshot snapshot,
        Guid? organizationId = null,
        bool mfa = false,
        FailedAttemptExitSnapshot? failedAttempt = null) =>
        OrderAllowedTransitionsPolicy.Compute(
            Authorizer,
            organizationId ?? Owner,
            order,
            snapshot,
            mfa,
            Now,
            failedAttempt);

    /// <summary>A failed attempt reported by the driver: its incident was read and records no next action.</summary>
    private static FailedAttemptExitSnapshot PendingDecision(bool custody) => new(
        new IncidentGuardSnapshot(false, false, custody, true, null, LatestFailedAttemptIncidentFound: true),
        new CustodyGuardSnapshot(custody));

    /// <summary>A failed attempt reported by dispatch with the next action it chose.</summary>
    private static FailedAttemptExitSnapshot Decided(string nextAction, bool custody = true, bool adopted = false) => new(
        new IncidentGuardSnapshot(
            false, false, custody, true, nextAction, LatestFailedAttemptIncidentAdopted: adopted,
            LatestFailedAttemptIncidentFound: true),
        new CustodyGuardSnapshot(custody));

    private static OrderTransitionAuthorizationSnapshot Snapshot(string? role, bool matchingAssignment = false) =>
        new(role, matchingAssignment);

    private static OrderResult Order(OrderStatus status) => new(
        Guid.Parse("c1000000-0000-0000-0000-000000000001"),
        "ORD_AAAAAAAAAAAAAAAAAAAAAA",
        Owner,
        null,
        status.ToContractValue(),
        new MoneyResult("MXN", 10_000),
        3,
        Guid.Parse("c1000000-0000-0000-0000-000000000002"),
        Guid.Parse("c1000000-0000-0000-0000-000000000003"),
        "SAME_DAY",
        Guid.Parse("c1000000-0000-0000-0000-000000000004"),
        Guid.Parse("c1000000-0000-0000-0000-000000000005"),
        null,
        "OCCASIONAL",
        new MoneyResult("MXN", 10_000),
        status == OrderStatus.Closed ? Now.AddHours(1) : null,
        null,
        Now.AddDays(-1));
}
