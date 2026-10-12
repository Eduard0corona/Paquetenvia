using Dispatch.Application.Assignments;
using Dispatch.Application.Fleet;
using Dispatch.Domain;
using Finance.Application;
using Finance.Application.Settlements;
using Paqueteria.Application.Paging;

namespace Paqueteria.UnitTests.Dispatch;

/// <summary>
/// UI-PHASE3-DRIVERS-2026-10-11: the cursors, the fail-closed mapping and the stop definition of the two driver
/// lists (listOrganizationDrivers for dispatch, listSettlementPayees for Finance).
/// </summary>
public sealed class OrganizationDriverPolicyTests
{
    private static readonly Guid DriverId = Guid.Parse("d71e0000-0000-4000-8000-000000000002");

    [Fact]
    public void Id_cursor_round_trips_under_its_version_and_is_23_url_safe_characters()
    {
        foreach (var version in new byte[] { 1, 2, 3, 255 })
        {
            var encoded = IdKeysetCursorCodec.Encode(version, DriverId);
            Assert.Matches("^[A-Za-z0-9_-]{23}$", encoded);
            Assert.True(IdKeysetCursorCodec.TryDecode(encoded, version, out var decoded));
            Assert.Equal(DriverId, decoded);
        }

        Assert.Throws<ArgumentException>(() => IdKeysetCursorCodec.Encode(2, Guid.Empty));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cursor")]
    [InlineData("AtceAAAAAEAAgAAAAAAAAAI=")]
    [InlineData("AtceAAAAAEAAgAAAAAAAAAI ")]
    [InlineData("AgAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AtceAAAAAEAAgAAAAAAAAAJ")]
    public void Id_cursor_rejects_values_it_did_not_issue(string? value)
    {
        Assert.False(IdKeysetCursorCodec.TryDecode(value, 2, out var decoded));
        Assert.Equal(Guid.Empty, decoded);
    }

    [Fact]
    public void Each_list_accepts_only_the_cursor_it_issued()
    {
        var picker = AssignableDriverCursorCodec.Encode(new AssignableDriverCursor(DriverId));
        var roster = OrganizationDriverCursorCodec.Encode(DriverId);
        var payees = SettlementPayeeCursorCodec.Encode(DriverId);
        Assert.Equal(3, new[] { picker, roster, payees }.Distinct(StringComparer.Ordinal).Count());

        Assert.True(OrganizationDriverCursorCodec.TryDecode(roster, out var rosterAfter));
        Assert.Equal(DriverId, rosterAfter);
        Assert.False(OrganizationDriverCursorCodec.TryDecode(picker, out _));
        Assert.False(OrganizationDriverCursorCodec.TryDecode(payees, out _));

        Assert.True(SettlementPayeeCursorCodec.TryDecode(payees, out var payeeAfter));
        Assert.Equal(DriverId, payeeAfter);
        Assert.False(SettlementPayeeCursorCodec.TryDecode(picker, out _));
        Assert.False(SettlementPayeeCursorCodec.TryDecode(roster, out _));

        // The picker's own codec is the same layout under version 1.
        Assert.Equal(picker, IdKeysetCursorCodec.Encode(1, DriverId));
    }

    [Fact]
    public void A_listed_driver_carries_the_dashboard_reference_and_the_UTC_capture_time_only()
    {
        var capturedAt = new DateTimeOffset(2026, 10, 11, 7, 30, 0, TimeSpan.FromHours(-7));
        var driver = OrganizationDriverPolicy.ToResult(DriverId, "MOTORCYCLE", "ACTIVE", 2, capturedAt);
        Assert.Equal("DRV-3d93b4c6", driver.DriverReference);
        Assert.Equal(Paqueteria.Application.Privacy.DriverReference.From(DriverId), driver.DriverReference);
        Assert.Equal(("MOTORCYCLE", "ACTIVE", 2), (driver.VehicleType, driver.Status, driver.ActiveStopCount));
        Assert.Equal(TimeSpan.Zero, driver.LastPositionAt!.Value.Offset);
        Assert.Equal(capturedAt, driver.LastPositionAt);

        var silent = OrganizationDriverPolicy.ToResult(DriverId, "WALKER", "PENDING", 0, null);
        Assert.Null(silent.LastPositionAt);
        Assert.Equal(["PENDING", "ACTIVE", "SUSPENDED"], OrganizationDriverPolicy.Statuses.ToArray());
        Assert.Equal(100, OrganizationDriverPolicy.PageSize);
    }

    [Fact]
    public void A_listed_driver_outside_the_published_vocabulary_fails_closed()
    {
        Assert.Throws<AssignmentInfrastructureException>(() =>
            OrganizationDriverPolicy.ToResult(DriverId, "TRUCK", "ACTIVE", 0, null));
        Assert.Throws<AssignmentInfrastructureException>(() =>
            OrganizationDriverPolicy.ToResult(DriverId, "CAR", "INACTIVE", 0, null));
        Assert.Throws<AssignmentInfrastructureException>(() =>
            OrganizationDriverPolicy.ToResult(DriverId, "CAR", "active", 0, null));
        Assert.Throws<AssignmentInfrastructureException>(() =>
            OrganizationDriverPolicy.ToResult(DriverId, "CAR", "ACTIVE", -1, null));
        Assert.Throws<AssignmentInfrastructureException>(() =>
            OrganizationDriverPolicy.ToResult(Guid.Empty, "CAR", "ACTIVE", 0, null));
    }

    [Fact]
    public void A_stop_is_exactly_an_order_status_DriverStopPolicy_projects()
    {
        Assert.Equal(
            ["ASSIGNED", "AT_PICKUP", "PICKED_UP", "IN_TRANSIT", "DELIVERING", "FAILED_ATTEMPT", "RESCHEDULED", "RETURNING"],
            DriverStopPolicy.OrderStatuses);
        foreach (var status in DriverStopPolicy.OrderStatuses)
        {
            Assert.True(DriverStopPolicy.Project(status, false).Included, status);
            Assert.True(DriverStopPolicy.Project(status, true).Included, status);
        }

        foreach (var status in new[] { "READY_FOR_PICKUP", "DELIVERED", "RETURNED", "CLOSED", "CANCELLED" })
        {
            Assert.False(DriverStopPolicy.Project(status, true).Included, status);
        }
    }

    [Fact]
    public void A_settlement_payee_carries_only_id_reference_and_vehicle_and_fails_closed()
    {
        var payee = SettlementPayeePolicy.ToResult(DriverId, "VAN");
        Assert.Equal(new SettlementPayeeResult(DriverId, "DRV-3d93b4c6", "VAN"), payee);
        Assert.Equal(["MOTORCYCLE", "CAR", "VAN", "BICYCLE", "WALKER"], SettlementPayeePolicy.VehicleTypes);
        Assert.Throws<FinanceUnavailableException>(() => SettlementPayeePolicy.ToResult(DriverId, "TRUCK"));
        Assert.Throws<FinanceUnavailableException>(() => SettlementPayeePolicy.ToResult(Guid.Empty, "VAN"));

        var actor = Guid.NewGuid();
        var organization = Guid.NewGuid();
        Assert.True(SettlementPayeePolicy.IsValid(new(actor, organization, false, null)));
        Assert.True(SettlementPayeePolicy.IsValid(new(actor, organization, true, DriverId)));
        Assert.False(SettlementPayeePolicy.IsValid(new(Guid.Empty, organization, false, null)));
        Assert.False(SettlementPayeePolicy.IsValid(new(actor, Guid.Empty, false, null)));
        Assert.False(SettlementPayeePolicy.IsValid(new(actor, organization, false, Guid.Empty)));
    }
}
