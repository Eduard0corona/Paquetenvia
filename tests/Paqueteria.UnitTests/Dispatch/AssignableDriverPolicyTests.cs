using Dispatch.Application.Assignments;
using Drivers.Application.Eligibility;
using Paqueteria.Application.Privacy;
using Reporting.Application.Operations;

namespace Paqueteria.UnitTests.Dispatch;

/// <summary>UI-PHASE2-DRIVER-PICKER-2026-10-05: cursor, driver label and fail-closed mapping of the picker read.</summary>
public sealed class AssignableDriverPolicyTests
{
    private static readonly Guid DriverId = Guid.Parse("d71e0000-0000-4000-8000-000000000002");

    [Fact]
    public void Cursor_round_trips_and_is_23_url_safe_characters()
    {
        var cursor = new AssignableDriverCursor(DriverId);
        var encoded = AssignableDriverCursorCodec.Encode(cursor);

        Assert.Equal(23, encoded.Length);
        Assert.Matches("^[A-Za-z0-9_-]{23}$", encoded);
        Assert.True(AssignableDriverCursorCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(cursor, decoded);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("AdceAAAAAEAAgAAAAAAAAAI=")]
    [InlineData("AdceAAAAAEAAgAAAAAAAAAI ")]
    [InlineData("AtceAAAAAEAAgAAAAAAAAAI")]
    [InlineData("AQAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AdceAAAAAEAAgAAAAAAAAAJ")]
    public void Cursor_rejects_values_this_operation_did_not_issue(string? value)
    {
        Assert.False(AssignableDriverCursorCodec.TryDecode(value, out var cursor));
        Assert.Null(cursor);
    }

    [Fact]
    public void Driver_reference_is_the_dashboard_label_and_carries_no_id()
    {
        Assert.Equal("DRV-3d93b4c6", DriverReference.From(DriverId));
        Assert.Equal(
            OperationsDashboardProjectionPolicy.DriverReference(DriverId),
            DriverReference.From(DriverId));
        Assert.DoesNotContain("d71e0000", DriverReference.From(DriverId), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => DriverReference.From(Guid.Empty));
    }

    [Fact]
    public void Eligible_and_ineligible_drivers_map_to_the_published_shape()
    {
        var eligible = AssignableDriverPolicy.ToResult(
            Snapshot("MOTORCYCLE"),
            new DriverEligibilityResult(true, DriverId, "MOTORCYCLE", "piloto-2026-10-v1", []),
            2);
        Assert.True(eligible.Eligible);
        Assert.Empty(eligible.IneligibilityReasons);
        Assert.Equal("DRV-3d93b4c6", eligible.DriverReference);
        Assert.Equal(2, eligible.ActiveAssignmentCount);

        var ineligible = AssignableDriverPolicy.ToResult(
            Snapshot("CAR"),
            new DriverEligibilityResult(
                false,
                DriverId,
                "CAR",
                "piloto-2026-10-v1",
                [
                    new(DriverEligibilityRejectionCodes.HomeCityMismatch),
                    new(DriverEligibilityRejectionCodes.DocumentExpired),
                ]),
            0);
        Assert.False(ineligible.Eligible);
        Assert.Equal(["HOME_CITY_MISMATCH", "DOCUMENT_EXPIRED"], ineligible.IneligibilityReasons);
    }

    [Fact]
    public void Values_outside_the_published_vocabulary_fail_closed()
    {
        Assert.Throws<AssignmentInfrastructureException>(() => AssignableDriverPolicy.ToResult(
            Snapshot("TRUCK"),
            new DriverEligibilityResult(true, DriverId, "TRUCK", "v1", []),
            0));
        Assert.Throws<AssignmentInfrastructureException>(() => AssignableDriverPolicy.ToResult(
            Snapshot("CAR"),
            new DriverEligibilityResult(false, DriverId, "CAR", "v1", [new(DriverEligibilityRejectionCodes.DriverTypeNotOwn)]),
            0));
        Assert.Throws<AssignmentInfrastructureException>(() => AssignableDriverPolicy.ToResult(
            Snapshot("CAR"),
            new DriverEligibilityResult(true, Guid.NewGuid(), "CAR", "v1", []),
            0));
        Assert.Throws<AssignmentInfrastructureException>(() => AssignableDriverPolicy.ToResult(
            Snapshot("CAR"),
            new DriverEligibilityResult(true, DriverId, "CAR", "v1", []),
            -1));
    }

    [Fact]
    public void Only_ready_for_pickup_and_rescheduled_orders_are_listed()
    {
        Assert.Equal(["READY_FOR_PICKUP", "RESCHEDULED"], AssignableDriverPolicy.AssignableOrderStatuses.ToArray());
        Assert.Equal(100, AssignableDriverPolicy.PageSize);
    }

    private static DriverEligibilitySnapshot Snapshot(string vehicleType) => new(
        DriverId,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "OWN",
        vehicleType,
        "ACTIVE",
        "ACTIVE",
        true,
        null,
        new Dictionary<string, DriverDocumentSnapshot>(),
        "piloto-2026-10-v1");
}
