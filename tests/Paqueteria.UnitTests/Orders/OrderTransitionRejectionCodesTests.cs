using Orders.Application.Orders;
using Orders.Domain;

namespace Paqueteria.UnitTests.Orders;

/// <summary>ORD-002-GUARD-CODES-2026-10-05: the one map from ORD-002 rejections to AI-05 rule codes.</summary>
public sealed class OrderTransitionRejectionCodesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_closed_enum_is_distinct_upper_snake_and_excludes_the_offline_code()
    {
        Assert.Equal(23, OrderTransitionRejectionCodes.All.Count);
        Assert.Equal(
            OrderTransitionRejectionCodes.All.Count,
            OrderTransitionRejectionCodes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.All(OrderTransitionRejectionCodes.All, code =>
        {
            Assert.Matches("^[A-Z][A-Z0-9_]{0,63}$", code);
            Assert.True(OrderTransitionRejectionCodes.IsDefined(code));
        });
        Assert.False(OrderTransitionRejectionCodes.IsDefined(null));
        Assert.False(OrderTransitionRejectionCodes.IsDefined(""));
        Assert.False(OrderTransitionRejectionCodes.IsDefined("version_conflict"));
        Assert.False(OrderTransitionRejectionCodes.IsDefined("pickup_proof_complete"));
        Assert.False(OrderTransitionRejectionCodes.IsDefined("OFFLINE_OPERATION_EXPIRED"));
    }

    [Fact]
    public void Every_registry_guard_is_mapped_exactly_once_to_a_defined_code()
    {
        var registry = new OrderTransitionGuardRegistry();
        Assert.Equal(
            registry.Guards.Select(guard => guard.Code).Order(StringComparer.Ordinal),
            OrderTransitionRejectionCodes.MappedGuardCodes.Order(StringComparer.Ordinal));
        Assert.All(registry.Guards, guard =>
            Assert.True(OrderTransitionRejectionCodes.IsDefined(OrderTransitionRejectionCodes.ForGuard(guard.Code))));
        Assert.Null(OrderTransitionRejectionCodes.ForGuard("unknown_guard"));
        Assert.Null(OrderTransitionRejectionCodes.ForGuard("SATISFIED"));

        // Every code of the enum is reachable from a rule or a guard; none is decorative.
        var reachable = registry.Guards.Select(guard => OrderTransitionRejectionCodes.ForGuard(guard.Code)!)
            .Concat(Enum.GetValues<OrderTransitionRuleCode>()
                .Select(OrderTransitionRejectionCodes.ForRule)
                .OfType<string>())
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            OrderTransitionRejectionCodes.All.Order(StringComparer.Ordinal),
            reachable.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(OrderTransitionRuleCode.Allowed, null)]
    [InlineData(OrderTransitionRuleCode.VersionMismatch, "VERSION_CONFLICT")]
    [InlineData(OrderTransitionRuleCode.VersionOverflow, "TRANSITION_NOT_ALLOWED")]
    [InlineData(OrderTransitionRuleCode.NotAllowed, "TRANSITION_NOT_ALLOWED")]
    [InlineData(OrderTransitionRuleCode.TerminalState, "ORDER_TERMINAL")]
    [InlineData(OrderTransitionRuleCode.Finalized, "ORDER_FINALIZED")]
    [InlineData(OrderTransitionRuleCode.ClaimWindowExpired, "CLAIM_WINDOW_CLOSED")]
    public void Matrix_and_version_rules_map_to_their_code(OrderTransitionRuleCode rule, string? expected) =>
        Assert.Equal(expected, OrderTransitionRejectionCodes.ForRule(rule));

    [Fact]
    public void Matrix_and_version_codes_come_from_the_real_evaluations()
    {
        Assert.Equal("VERSION_CONFLICT", RuleCode(OrderTransitionMatrix.EvaluateVersion(3, 2)));
        Assert.Equal("TRANSITION_NOT_ALLOWED", RuleCode(OrderTransitionMatrix.EvaluateVersion(int.MaxValue, int.MaxValue)));
        Assert.Equal("TRANSITION_NOT_ALLOWED", RuleCode(OrderTransitionMatrix.Evaluate(
            OrderStatus.Draft, OrderStatus.Delivered, Now, null, null)));
        Assert.Equal("ORDER_TERMINAL", RuleCode(OrderTransitionMatrix.Evaluate(
            OrderStatus.Cancelled, OrderStatus.Confirmed, Now, null, null)));
        Assert.Equal("ORDER_FINALIZED", RuleCode(OrderTransitionMatrix.Evaluate(
            OrderStatus.Closed, OrderStatus.ClaimOpen, Now, Now.AddHours(1), Now.AddMinutes(-1))));
        Assert.Equal("CLAIM_WINDOW_CLOSED", RuleCode(OrderTransitionMatrix.Evaluate(
            OrderStatus.Closed, OrderStatus.ClaimOpen, Now, Now.AddSeconds(-1), null)));

        static string? RuleCode(OrderTransitionEvaluation evaluation)
        {
            Assert.False(evaluation.Allowed);
            return OrderTransitionRejectionCodes.ForRule(evaluation.Code);
        }
    }

    /// <summary>Each guard is driven to be the first failing one, then mapped as the service maps it.</summary>
    [Theory]
    [InlineData("valid_active_quote", "QUOTE_NOT_VALID")]
    [InlineData("payer_acceptance", "PAYER_ACCEPTANCE_REQUIRED")]
    [InlineData("restricted_goods_check", "RESTRICTED_GOODS_ACK_REQUIRED")]
    [InlineData("eligible_driver", "VALID_ASSIGNMENT_REQUIRED")]
    [InlineData("capacity_available", "DRIVER_CAPACITY_EXCEEDED")]
    [InlineData("assignment_cost_present", "ASSIGNMENT_COST_REQUIRED")]
    [InlineData("pickup_proof_complete", "PICKUP_PROOF_REQUIRED")]
    [InlineData("delivery_proof_complete", "DELIVERY_PROOF_REQUIRED")]
    [InlineData("if_cod_expected_then_cod_status_recorded_or_reconciled", "COD_NOT_RECORDED")]
    [InlineData("no_unresolved_incident", "UNRESOLVED_INCIDENT")]
    [InlineData("if_cod_expected_then_cod_status_reconciled", "COD_NOT_RECONCILED")]
    [InlineData("financial_reconciliation_complete", "FINANCIAL_RECONCILIATION_INCOMPLETE")]
    [InlineData("claim_window_ends_at_set", "CLAIM_WINDOW_NOT_SET")]
    [InlineData("now_before_or_equal_claim_window_ends_at", "CLAIM_WINDOW_CLOSED")]
    [InlineData("claim_reason_present", "REASON_REQUIRED")]
    [InlineData("claim_resolution_reason_present", "REASON_REQUIRED")]
    [InlineData("cancellation_reason_present", "REASON_REQUIRED")]
    [InlineData("if_from_at_pickup_then_custody_not_acquired", "CUSTODY_ALREADY_ACQUIRED")]
    [InlineData("attempt_stage_recorded", "INCIDENT_REQUIRED")]
    [InlineData("custody_acquired_true", "CUSTODY_NOT_ACQUIRED")]
    [InlineData("retry_custody_acquired_true", "CUSTODY_NOT_ACQUIRED")]
    [InlineData("retry_valid_assignment", "VALID_ASSIGNMENT_REQUIRED")]
    [InlineData("failed_attempt_next_action_respected", "NEXT_ACTION_MISMATCH")]
    public void Each_failing_guard_maps_to_its_code(string guardCode, string expected)
    {
        var result = new OrderTransitionGuardRegistry().Evaluate(FirstFailing(guardCode));
        Assert.False(result.Satisfied);
        Assert.Equal(guardCode, result.Code);
        Assert.Equal(expected, OrderTransitionRejectionCodes.ForGuard(result.Code));
    }

    [Fact]
    public void Custody_acquired_recorded_shares_the_incident_code_and_never_fails_before_attempt_stage()
    {
        // Both guards read the same requested-incident evidence; attempt_stage_recorded (order 180) always fails
        // first, so custody_acquired_recorded (order 190) is mapped for completeness only.
        Assert.Equal("INCIDENT_REQUIRED", OrderTransitionRejectionCodes.ForGuard("custody_acquired_recorded"));
        var registry = new OrderTransitionGuardRegistry();
        foreach (var incidentId in new Guid?[] { null, Guid.NewGuid() })
        {
            foreach (var valid in new[] { false, true })
            {
                var context = With(Base(OrderStatus.InTransit, OrderStatus.FailedAttempt), c => c with
                {
                    Metadata = new NormalizedTransitionMetadata("{}", null, incidentId),
                    Incidents = new IncidentGuardSnapshot(valid, false, false, false),
                });
                Assert.NotEqual("custody_acquired_recorded", registry.Evaluate(context.Build()).Code);
            }
        }
    }

    [Fact]
    public void Capability_for_codes_is_the_role_table_without_the_driver_edge_list()
    {
        var authorizer = new OrderTransitionAuthorizer();
        Assert.True(authorizer.HoldsTransitionCapability("DISPATCHER", false, false));
        Assert.True(authorizer.HoldsTransitionCapability("PLATFORM_ADMIN", true, false));
        Assert.False(authorizer.HoldsTransitionCapability("PLATFORM_ADMIN", false, true));
        Assert.True(authorizer.HoldsTransitionCapability("DRIVER", false, true));
        Assert.False(authorizer.HoldsTransitionCapability("DRIVER", true, false));
        foreach (var role in new string?[] { null, "", "VIEWER", "FINANCE", "CUSTOMER_SUPPORT", "dispatcher" })
        {
            Assert.False(authorizer.HoldsTransitionCapability(role, true, true));
        }

        // Every authorized edge implies the capability, so a guard failure (reached only after IsAuthorized) may
        // always carry its code.
        foreach (var role in new string?[] { null, "VIEWER", "DISPATCHER", "PLATFORM_ADMIN", "DRIVER" })
        {
            foreach (var mfa in new[] { false, true })
            {
                foreach (var assignment in new[] { false, true })
                {
                    foreach (var source in Enum.GetValues<OrderStatus>())
                    {
                        foreach (var target in Enum.GetValues<OrderStatus>())
                        {
                            if (authorizer.IsAuthorized(new(role, source, target, mfa, assignment)))
                            {
                                Assert.True(authorizer.HoldsTransitionCapability(role, mfa, assignment));
                            }
                        }
                    }
                }
            }
        }
    }

    private static OrderTransitionGuardContext FirstFailing(string guardCode) => (guardCode switch
    {
        "valid_active_quote" => Base(OrderStatus.Draft, OrderStatus.Confirmed),
        "payer_acceptance" => With(Base(OrderStatus.Draft, OrderStatus.Confirmed), c => c with
        {
            QuoteAcceptance = new QuoteAcceptanceGuardSnapshot(true, false),
        }),
        "restricted_goods_check" => With(Base(OrderStatus.Draft, OrderStatus.Confirmed), c => c with
        {
            QuoteAcceptance = new QuoteAcceptanceGuardSnapshot(true, true),
        }),
        "eligible_driver" => Base(OrderStatus.ReadyForPickup, OrderStatus.Assigned),
        "capacity_available" => With(Base(OrderStatus.ReadyForPickup, OrderStatus.Assigned), c => c with
        {
            Assignment = new AssignmentGuardSnapshot(true, true, false, true),
        }),
        "assignment_cost_present" => With(Base(OrderStatus.ReadyForPickup, OrderStatus.Assigned), c => c with
        {
            Assignment = new AssignmentGuardSnapshot(true, true, true, false),
        }),
        "pickup_proof_complete" => Base(OrderStatus.AtPickup, OrderStatus.PickedUp),
        "delivery_proof_complete" => Base(OrderStatus.Delivering, OrderStatus.Delivered),
        "if_cod_expected_then_cod_status_recorded_or_reconciled" =>
            With(Base(OrderStatus.Delivering, OrderStatus.Delivered), c => c with
            {
                Proofs = new ProofGuardSnapshot(false, true),
                CodExpectedCents = 15_000,
            }),
        "no_unresolved_incident" => With(Base(OrderStatus.Delivered, OrderStatus.Closed), c => c with
        {
            Incidents = new IncidentGuardSnapshot(false, false, false, true),
        }),
        "if_cod_expected_then_cod_status_reconciled" => With(Base(OrderStatus.Delivered, OrderStatus.Closed), c => c with
        {
            CodExpectedCents = 15_000,
            Cod = new CodGuardSnapshot(true, "RECORDED", 15_000),
        }),
        "financial_reconciliation_complete" => With(Base(OrderStatus.Delivered, OrderStatus.Closed), c => c with
        {
            MonetaryIntegrityValid = false,
        }),
        "claim_window_ends_at_set" => Base(OrderStatus.Delivered, OrderStatus.Closed),
        "now_before_or_equal_claim_window_ends_at" => With(Base(OrderStatus.Delivered, OrderStatus.ClaimOpen), c => c with
        {
            ClaimWindowEndsAt = Now.AddSeconds(-1),
        }),
        "claim_reason_present" => With(Base(OrderStatus.Delivered, OrderStatus.ClaimOpen), c => c with
        {
            ClaimWindowEndsAt = Now.AddHours(1),
            Reason = " ",
        }),
        "claim_resolution_reason_present" => With(Base(OrderStatus.ClaimOpen, OrderStatus.ClaimResolved), c => c with
        {
            Reason = " ",
        }),
        "cancellation_reason_present" => With(Base(OrderStatus.Draft, OrderStatus.Cancelled), c => c with
        {
            Reason = " ",
        }),
        "if_from_at_pickup_then_custody_not_acquired" => With(Base(OrderStatus.AtPickup, OrderStatus.Cancelled), c => c with
        {
            Custody = new CustodyGuardSnapshot(true),
        }),
        "attempt_stage_recorded" => Base(OrderStatus.AtPickup, OrderStatus.FailedAttempt),
        "custody_acquired_true" => Base(OrderStatus.PickedUp, OrderStatus.Returning),
        "retry_custody_acquired_true" => Base(OrderStatus.FailedAttempt, OrderStatus.Delivering),
        "retry_valid_assignment" => With(Base(OrderStatus.FailedAttempt, OrderStatus.Delivering), c => c with
        {
            Custody = new CustodyGuardSnapshot(true),
        }),
        "failed_attempt_next_action_respected" => With(Base(OrderStatus.FailedAttempt, OrderStatus.Rescheduled), c => c with
        {
            Incidents = new IncidentGuardSnapshot(false, false, false, false, "RETURNING"),
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(guardCode)),
    }).Build();

    private static GuardContextBuilder Base(OrderStatus source, OrderStatus target) => new(source, target);

    private static GuardContextBuilder With(GuardContextBuilder builder, Func<GuardContextBuilder, GuardContextBuilder> change) =>
        change(builder);

    /// <summary>A mutable-by-copy stand-in for the init-only guard context.</summary>
    private sealed record GuardContextBuilder(OrderStatus Source, OrderStatus Target)
    {
        public string Reason { get; init; } = "synthetic";
        public DateTimeOffset? ClaimWindowEndsAt { get; init; }
        public long CodExpectedCents { get; init; }
        public bool MonetaryIntegrityValid { get; init; } = true;
        public NormalizedTransitionMetadata Metadata { get; init; } = NormalizedTransitionMetadata.Empty;
        public QuoteAcceptanceGuardSnapshot QuoteAcceptance { get; init; } = new(false, false);
        public AssignmentGuardSnapshot Assignment { get; init; } = new(false, false, false, false);
        public ProofGuardSnapshot Proofs { get; init; } = new(false, false);
        public IncidentGuardSnapshot Incidents { get; init; } = new(false, false, false, false);
        public CodGuardSnapshot Cod { get; init; } = new(false, null, null);
        public CustodyGuardSnapshot Custody { get; init; } = new(false);

        public OrderTransitionGuardContext Build() => new()
        {
            Source = Source,
            Target = Target,
            Reason = Reason,
            OccurredAt = Now,
            ClaimWindowEndsAt = ClaimWindowEndsAt,
            FinalizedAt = null,
            CodExpectedCents = CodExpectedCents,
            MonetaryIntegrityValid = MonetaryIntegrityValid,
            Metadata = Metadata,
            QuoteAcceptance = QuoteAcceptance,
            Assignment = Assignment,
            Proofs = Proofs,
            Incidents = Incidents,
            Cod = Cod,
            Custody = Custody,
        };
    }
}
