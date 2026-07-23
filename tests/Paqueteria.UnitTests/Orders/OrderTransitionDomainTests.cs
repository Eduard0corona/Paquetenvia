using Orders.Application.Orders;
using Orders.Domain;
using Paqueteria.Application.Auditing;

namespace Paqueteria.UnitTests.Orders;

public sealed class OrderTransitionDomainTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Status_enum_contains_exactly_the_17_normative_values()
    {
        var values = Enum.GetValues<OrderStatus>();
        Assert.Equal(17, values.Length);
        Assert.Equal(
            [
                "DRAFT", "CONFIRMED", "READY_FOR_PICKUP", "ASSIGNED", "AT_PICKUP",
                "PICKED_UP", "IN_TRANSIT", "DELIVERING", "FAILED_ATTEMPT", "RESCHEDULED",
                "RETURNING", "RETURNED", "DELIVERED", "CLOSED", "CLAIM_OPEN",
                "CLAIM_RESOLVED", "CANCELLED",
            ],
            values.Select(value => value.ToContractValue()));
        Assert.All(values, value =>
        {
            Assert.True(OrderContractValues.TryParseOrderStatus(value.ToContractValue(), out var parsed));
            Assert.Equal(value, parsed);
        });
    }

    [Fact]
    public void Matrix_is_exact_and_table_driven_over_every_possible_pair()
    {
        var expected = ExpectedEdges().ToHashSet();
        Assert.Equal(30, expected.Count);

        foreach (var source in Enum.GetValues<OrderStatus>())
        {
            foreach (var target in Enum.GetValues<OrderStatus>())
            {
                var actual = OrderTransitionMatrix.Evaluate(
                    source,
                    target,
                    Now,
                    Now.AddHours(1),
                    null);
                Assert.Equal(expected.Contains((source, target)), actual.Allowed);
            }
        }

        var exposed = OrderTransitionMatrix.AllowedTransitions
            .SelectMany(pair => pair.Value.Select(target => (pair.Key, target)))
            .ToHashSet();
        Assert.True(expected.SetEquals(exposed));
    }

    [Fact]
    public void Immediate_terminals_reject_every_target()
    {
        Assert.Equal(
            [OrderStatus.Returned, OrderStatus.ClaimResolved, OrderStatus.Cancelled],
            OrderTransitionMatrix.ImmediateTerminalStates.Order());
        foreach (var source in OrderTransitionMatrix.ImmediateTerminalStates)
        {
            foreach (var target in Enum.GetValues<OrderStatus>())
            {
                var result = OrderTransitionMatrix.Evaluate(source, target, Now, null, null);
                Assert.False(result.Allowed);
                Assert.Equal(OrderTransitionRuleCode.TerminalState, result.Code);
            }
        }
    }

    [Fact]
    public void Closed_claim_window_is_inclusive_and_finalization_is_authoritative()
    {
        Assert.True(OrderTransitionMatrix.Evaluate(
            OrderStatus.Closed, OrderStatus.ClaimOpen, Now, Now, null).Allowed);
        Assert.Equal(
            OrderTransitionRuleCode.ClaimWindowExpired,
            OrderTransitionMatrix.Evaluate(
                OrderStatus.Closed, OrderStatus.ClaimOpen, Now.AddTicks(1), Now, null).Code);
        Assert.Equal(
            OrderTransitionRuleCode.Finalized,
            OrderTransitionMatrix.Evaluate(
                OrderStatus.Closed, OrderStatus.ClaimOpen, Now, Now.AddHours(1), Now).Code);
    }

    [Fact]
    public void Version_policy_rejects_mismatch_and_overflow()
    {
        Assert.True(OrderTransitionMatrix.EvaluateVersion(7, 7).Allowed);
        Assert.Equal(
            OrderTransitionRuleCode.VersionMismatch,
            OrderTransitionMatrix.EvaluateVersion(7, 6).Code);
        Assert.Equal(
            OrderTransitionRuleCode.VersionOverflow,
            OrderTransitionMatrix.EvaluateVersion(int.MaxValue, int.MaxValue).Code);
    }

    [Fact]
    public void Public_event_code_mapping_is_exact_and_private_by_default()
    {
        var expected = new Dictionary<OrderStatus, string>
        {
            [OrderStatus.ReadyForPickup] = "PICKUP_SCHEDULED",
            [OrderStatus.PickedUp] = "PICKED_UP",
            [OrderStatus.InTransit] = "IN_TRANSIT",
            [OrderStatus.Delivering] = "OUT_FOR_DELIVERY",
            [OrderStatus.FailedAttempt] = "DELIVERY_ATTEMPTED",
            [OrderStatus.Rescheduled] = "RESCHEDULED",
            [OrderStatus.Delivered] = "DELIVERED",
            [OrderStatus.Returning] = "RETURNING",
            [OrderStatus.Returned] = "RETURNED",
            [OrderStatus.Cancelled] = "CANCELLED",
        };
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            Assert.Equal(expected.GetValueOrDefault(status), OrderPublicEventCodePolicy.Map(status));
        }
    }

    [Fact]
    public void Metadata_policy_allows_only_the_transition_specific_flat_fields()
    {
        Assert.True(Normalize(
            """{"restricted_goods_acknowledged":true}""",
            OrderStatus.Draft,
            OrderStatus.Confirmed,
            out var confirmed));
        Assert.True(confirmed.RestrictedGoodsAcknowledged);
        Assert.Equal("""{"restricted_goods_acknowledged":true}""", confirmed.Json);

        var incidentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        Assert.True(Normalize(
            $$"""{"incident_id":"{{incidentId:D}}"}""",
            OrderStatus.Delivering,
            OrderStatus.FailedAttempt,
            out var failed));
        Assert.Equal(incidentId, failed.IncidentId);

        Assert.True(Normalize("{}", OrderStatus.Draft, OrderStatus.Cancelled, out _));
        Assert.True(Normalize(null, OrderStatus.Draft, OrderStatus.Cancelled, out _));
        Assert.False(Normalize("""{"unknown":true}""", OrderStatus.Draft, OrderStatus.Cancelled, out _));
        Assert.False(Normalize("[]", OrderStatus.Draft, OrderStatus.Cancelled, out _));
        Assert.False(Normalize("""{"incident_id":{"nested":"value"}}""",
            OrderStatus.Delivering, OrderStatus.FailedAttempt, out _));
        Assert.False(Normalize("""{"incident_id":"not-a-uuid"}""",
            OrderStatus.Delivering, OrderStatus.FailedAttempt, out _));
        Assert.False(Normalize(
            """{"restricted_goods_acknowledged":"true"}""",
            OrderStatus.Draft,
            OrderStatus.Confirmed,
            out _));
        Assert.False(Normalize(new string('x', 4_097), OrderStatus.Draft, OrderStatus.Cancelled, out _));
    }

    [Fact]
    public void Canonical_hash_is_stable_excludes_actor_request_and_changes_for_exact_inputs()
    {
        var metadata = NormalizedTransitionMetadata.Empty;
        var command = Command(reason: "safe reason");
        var sameContract = command with { ActorId = Guid.NewGuid(), RequestId = "another-request" };
        Assert.Equal(
            OrderTransitionCanonicalizer.ComputeSha256(command, OrderStatus.Cancelled, metadata),
            OrderTransitionCanonicalizer.ComputeSha256(sameContract, OrderStatus.Cancelled, metadata));
        Assert.NotEqual(
            OrderTransitionCanonicalizer.ComputeSha256(command, OrderStatus.Cancelled, metadata),
            OrderTransitionCanonicalizer.ComputeSha256(
                command with { Reason = "safe reason " },
                OrderStatus.Cancelled,
                metadata));
        Assert.Equal(
            Convert.ToHexString(OrderTransitionCanonicalizer.ComputeSha256(command, OrderStatus.Cancelled, metadata)),
            Convert.ToHexString(OrderTransitionCanonicalizer.ComputeSha256(command, OrderStatus.Cancelled, metadata)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Command_shape_rejects_absent_or_whitespace_reason(string? reason) =>
        Assert.False(OrderTransitionInputPolicy.IsValidCommandShape(
            Command(reason ?? "synthetic") with { Reason = reason },
            OrderTransitionInputPolicy.DefaultMaximumMetadataUtf8Bytes));

    [Theory]
    [InlineData("PLATFORM_ADMIN", true, true)]
    [InlineData("PLATFORM_ADMIN", false, false)]
    [InlineData("DISPATCHER", false, true)]
    [InlineData("FINANCE", true, false)]
    [InlineData("ALLY_ADMIN", true, false)]
    [InlineData("BUSINESS_OPERATOR", true, false)]
    [InlineData("VIEWER", true, false)]
    public void Role_authorization_is_safe_and_reversible(
        string role,
        bool mfa,
        bool expected)
    {
        var authorizer = new OrderTransitionAuthorizer();
        Assert.Equal(expected, authorizer.IsAuthorized(new(
            role,
            OrderStatus.Draft,
            OrderStatus.Cancelled,
            mfa,
            false)));
    }

    [Fact]
    public void Driver_requires_matching_assignment_and_an_allowlisted_edge()
    {
        var authorizer = new OrderTransitionAuthorizer();
        Assert.True(authorizer.IsAuthorized(new(
            "DRIVER",
            OrderStatus.Assigned,
            OrderStatus.AtPickup,
            false,
            true)));
        Assert.False(authorizer.IsAuthorized(new(
            "DRIVER",
            OrderStatus.Assigned,
            OrderStatus.AtPickup,
            false,
            false)));
        Assert.False(authorizer.IsAuthorized(new(
            "DRIVER",
            OrderStatus.Draft,
            OrderStatus.Confirmed,
            false,
            true)));
    }

    [Fact]
    public void Guard_registry_has_unique_codes_and_deterministic_order()
    {
        var registry = new OrderTransitionGuardRegistry();
        Assert.Equal(23, registry.Guards.Count);
        Assert.Equal(
            registry.Guards.Select(guard => guard.Code).Distinct(StringComparer.Ordinal).Count(),
            registry.Guards.Count);
        Assert.Equal(registry.Guards.OrderBy(guard => guard.Order).Select(guard => guard.Code),
            registry.Guards.Select(guard => guard.Code));
        Assert.Throws<InvalidOperationException>(() => new OrderTransitionGuardRegistry(
            [new TestGuard("duplicate", 1), new TestGuard("duplicate", 2)]));
    }

    [Theory]
    [InlineData(OrderStatus.FailedAttempt, OrderStatus.Delivered)]
    [InlineData(OrderStatus.Draft, OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled, OrderStatus.Confirmed)]
    public void Explicit_prohibited_edges_are_rejected(OrderStatus source, OrderStatus target) =>
        Assert.False(OrderTransitionMatrix.Evaluate(source, target, Now, Now.AddHours(1), null).Allowed);

    [Fact]
    public void Rescheduled_delivery_guard_rejects_missing_custody_or_assignment()
    {
        var registry = new OrderTransitionGuardRegistry();
        var withoutCustody = BaseGuardContext(OrderStatus.Rescheduled, OrderStatus.Delivering);
        Assert.Equal("retry_custody_acquired_true", registry.Evaluate(withoutCustody).Code);

        var withCustody = WithProofs();
        Assert.Equal("retry_valid_assignment", registry.Evaluate(withCustody).Code);

        OrderTransitionGuardContext WithProofs() => new()
        {
            Source = OrderStatus.Rescheduled,
            Target = OrderStatus.Delivering,
            Reason = "retry",
            OccurredAt = Now,
            ClaimWindowEndsAt = null,
            FinalizedAt = null,
            CodExpectedCents = 0,
            MonetaryIntegrityValid = true,
            Metadata = NormalizedTransitionMetadata.Empty,
            Proofs = new ProofGuardSnapshot(true, false),
        };
    }

    [Fact]
    public void Central_redactor_removes_sensitive_reason_values()
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            """{"reason":"hidden@example.test +52 667 123 4567 Avenida Universidad 1234 token=super-secret ciphertext=deadbeef"}""");
        var redacted = new AuditPayloadRedactor().Redact(document.RootElement).Json;
        Assert.DoesNotContain("hidden@example.test", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("667 123 4567", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Universidad 1234", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("deadbeef", redacted, StringComparison.Ordinal);
        Assert.Contains(AuditPayloadRedactor.Replacement, redacted, StringComparison.Ordinal);
    }

    private static bool Normalize(
        string? json,
        OrderStatus source,
        OrderStatus target,
        out NormalizedTransitionMetadata metadata) =>
        OrderTransitionInputPolicy.TryNormalizeMetadata(json, source, target, 4_096, out metadata);

    private static TransitionOrderCommand Command(string reason) => new(
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        Guid.Parse("22222222-2222-2222-2222-222222222222"),
        "transition-key-00000001",
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "CANCELLED",
        reason,
        1,
        null,
        false,
        "request-one");

    private static OrderTransitionGuardContext BaseGuardContext(OrderStatus source, OrderStatus target) => new()
    {
        Source = source,
        Target = target,
        Reason = "synthetic",
        OccurredAt = Now,
        ClaimWindowEndsAt = null,
        FinalizedAt = null,
        CodExpectedCents = 0,
        MonetaryIntegrityValid = true,
        Metadata = NormalizedTransitionMetadata.Empty,
    };

    private static IEnumerable<(OrderStatus, OrderStatus)> ExpectedEdges()
    {
        yield return (OrderStatus.Draft, OrderStatus.Confirmed);
        yield return (OrderStatus.Draft, OrderStatus.Cancelled);
        yield return (OrderStatus.Confirmed, OrderStatus.ReadyForPickup);
        yield return (OrderStatus.Confirmed, OrderStatus.Cancelled);
        yield return (OrderStatus.ReadyForPickup, OrderStatus.Assigned);
        yield return (OrderStatus.ReadyForPickup, OrderStatus.Cancelled);
        yield return (OrderStatus.Assigned, OrderStatus.AtPickup);
        yield return (OrderStatus.Assigned, OrderStatus.ReadyForPickup);
        yield return (OrderStatus.Assigned, OrderStatus.Cancelled);
        yield return (OrderStatus.AtPickup, OrderStatus.PickedUp);
        yield return (OrderStatus.AtPickup, OrderStatus.FailedAttempt);
        yield return (OrderStatus.AtPickup, OrderStatus.Cancelled);
        yield return (OrderStatus.PickedUp, OrderStatus.InTransit);
        yield return (OrderStatus.PickedUp, OrderStatus.Returning);
        yield return (OrderStatus.InTransit, OrderStatus.Delivering);
        yield return (OrderStatus.InTransit, OrderStatus.FailedAttempt);
        yield return (OrderStatus.InTransit, OrderStatus.Returning);
        yield return (OrderStatus.Delivering, OrderStatus.Delivered);
        yield return (OrderStatus.Delivering, OrderStatus.FailedAttempt);
        yield return (OrderStatus.FailedAttempt, OrderStatus.Rescheduled);
        yield return (OrderStatus.FailedAttempt, OrderStatus.Returning);
        yield return (OrderStatus.FailedAttempt, OrderStatus.Delivering);
        yield return (OrderStatus.Rescheduled, OrderStatus.ReadyForPickup);
        yield return (OrderStatus.Rescheduled, OrderStatus.Assigned);
        yield return (OrderStatus.Rescheduled, OrderStatus.Delivering);
        yield return (OrderStatus.Returning, OrderStatus.Returned);
        yield return (OrderStatus.Delivered, OrderStatus.Closed);
        yield return (OrderStatus.Delivered, OrderStatus.ClaimOpen);
        yield return (OrderStatus.Closed, OrderStatus.ClaimOpen);
        yield return (OrderStatus.ClaimOpen, OrderStatus.ClaimResolved);
    }

    private sealed class TestGuard(string code, int order) : IOrderTransitionGuard
    {
        public string Code { get; } = code;
        public int Order { get; } = order;
        public bool AppliesTo(OrderTransitionGuardContext context) => true;
        public OrderTransitionGuardResult Evaluate(OrderTransitionGuardContext context) =>
            OrderTransitionGuardResult.Success(Code);
    }
}
