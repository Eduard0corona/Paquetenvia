using Dispatch.Application.Stops;
using Finance.Application;
using Finance.Application.Cod;
using Finance.Infrastructure;
using Finance.Infrastructure.Cod;
using Finance.Infrastructure.Persistence;
using Locations.Application.Geocoding;
using Locations.Application.Locations;
using Locations.Infrastructure.Geocoding;
using Locations.Infrastructure.Locations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Paqueteria.Application.Auditing;
using Paqueteria.ContractTests.PostgreSql.Fixtures;
using Paqueteria.Infrastructure.Auditing;
using Paqueteria.Infrastructure.Security.Pii;
using Paqueteria.Infrastructure.Tenancy;

namespace Paqueteria.ContractTests.PostgreSql;

/// <summary>
/// UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11 against real PostgreSQL with FORCE RLS: a driver reads the details of
/// their own ACCEPTED or ACTIVE assignments only, decrypted through the Locations port after the tenant transaction,
/// together with the zone, the service window, the cash the door collection must satisfy and the count of today's
/// completed stops.
/// </summary>
public sealed partial class DispatchPostgreSqlContractTests
{
    private const long DoorCodCents = 125_000;

    [PostgreSqlContractFact]
    public async Task Driver_stop_details_decrypt_the_own_stop_with_zone_window_and_cash_to_collect()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var envelope = new PiiEnvelopeProtector(new Adp001FakeKeyVault());
        var zone = await CreateZoneAsync(scenario, "Tres Ríos");
        try
        {
            await SealLocationAsync(
                scenario, envelope, scenario.OriginLocationId, 24.80, -107.40,
                LocationAddressText.Compose("Calle Sintética 10, Centro", "Portón verde"), "Farmacia Las Quintas", null);
            await SealLocationAsync(
                scenario, envelope, scenario.DestinationLocationId, 24.81, -107.39,
                LocationAddressText.Compose("Calle Sintética 20, Tres Ríos", "Casa azul\nfrente al parque"),
                "María López Pérez",
                zone.ZoneId);
            var windowFrom = new DateTimeOffset(2026, 10, 11, 20, 0, 0, TimeSpan.Zero);
            await scenario.ExecuteAdminAsync(
                """
                UPDATE orders.orders
                SET cod_expected_cents=@cod,service_window_from=@from,service_window_to=@to
                WHERE id=@order;
                """,
                P("cod", DoorCodCents),
                P("from", windowFrom),
                P("to", windowFrom.AddHours(2)),
                P("order", scenario.OrderId));
            await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(Command(scenario), default);
            var query = CreateStopsQuery(fixture.AppDataSource, new EnvelopeLocationPiiReader(envelope));

            var pickup = await SingleStopAsync(query, scenario);
            Assert.Equal("PICKUP", pickup.StopType);
            Assert.True(pickup.DetailsAvailable);
            // The owner's answer "Completo en recolección": the full name at pickup, never the short one.
            Assert.Equal("Farmacia Las Quintas", pickup.ContactFullName);
            Assert.Null(pickup.ContactShortName);
            Assert.Null(pickup.ZoneName);
            Assert.Equal(new DriverStopServiceWindowResult(windowFrom, windowFrom.AddHours(2)), pickup.ServiceWindow);
            // Cash is collected at the door: the pickup stop of a COD order never carries it.
            Assert.Null(pickup.Cod);
            Assert.Equal(
                new DriverStopNavigationResult(24.80, -107.40, "Calle Sintética 10, Centro", "Portón verde"),
                pickup.Navigation);

            await SetOrderStatusAsync(scenario, "IN_TRANSIT");
            var delivery = await SingleStopAsync(query, scenario);
            Assert.Equal("DELIVERY", delivery.StopType);
            // At the door only the short name; the recipient's full name never leaves the server.
            Assert.Equal("M. Pérez", delivery.ContactShortName);
            Assert.Null(delivery.ContactFullName);
            Assert.Equal("Tres Ríos", delivery.ZoneName);
            Assert.Equal(new DriverStopCodResult(DoorCodCents, false), delivery.Cod);
            Assert.Equal(
                new DriverStopNavigationResult(
                    24.81, -107.39, "Calle Sintética 20, Tres Ríos", "Casa azul\nfrente al parque"),
                delivery.Navigation);
            Assert.Equal("READY destination summary", delivery.AddressSummary);

            // At the door the driver records exactly the expected amount; any other amount is refused and the
            // stop keeps asking for the cash.
            await SetOrderStatusAsync(scenario, "DELIVERING");
            var cod = CreateDriverCodService(fixture.AppDataSource);
            var mismatch = await Assert.ThrowsAsync<FinanceConflictException>(() =>
                cod.RecordAsync(DoorCollection(scenario, DoorCodCents - 1), default));
            Assert.Equal(FinanceConflictCode.CodAmountMismatch, mismatch.Code);
            Assert.Equal(new DriverStopCodResult(DoorCodCents, false), (await SingleStopAsync(query, scenario)).Cod);

            var recorded = await cod.RecordAsync(DoorCollection(scenario, DoorCodCents), default);
            Assert.Equal(("RECORDED", DoorCodCents), (recorded.Status, recorded.AmountCents));
            Assert.Equal(new DriverStopCodResult(DoorCodCents, true), (await SingleStopAsync(query, scenario)).Cod);

            // The owner's answer "Completo, como recolección": the return goes back to the pickup contact, in full.
            await SetOrderStatusAsync(scenario, "RETURNING");
            var returning = await SingleStopAsync(query, scenario);
            Assert.Equal("RETURN", returning.StopType);
            Assert.Equal("Farmacia Las Quintas", returning.ContactFullName);
            Assert.Null(returning.ContactShortName);
            Assert.Null(returning.Cod);

            // Nothing the driver saw reaches the audit log or the outbox.
            Assert.Equal(0, await Adp001FakeKeyVault.CountPlaintextInAuditAndOutboxAsync(
                fixture.AdminDataSource,
                scenario.OrganizationId,
                ["Calle Sintética", "Portón verde", "Casa azul", "María López", "Farmacia Las Quintas", "M. Pérez"]));
        }
        finally
        {
            await DeleteZoneAsync(scenario, zone);
        }
    }

    [PostgreSqlContractFact]
    public async Task Driver_stop_details_are_read_only_for_the_requesting_drivers_assigned_stops()
    {
        var unassignedDriverUserId = Guid.NewGuid();
        try
        {
            await using var scenario = await DispatchScenario.CreateAsync(fixture);
            await using var foreign = await DispatchScenario.CreateAsync(fixture);
            await CreateOwnDriverAsync(scenario, unassignedDriverUserId);
            await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(Command(scenario), default);
            await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(Command(foreign), default);
            var reader = new RecordingStopLocationReader();
            var query = CreateStopsQuery(fixture.AppDataSource, reader);

            // The assigned driver reads their own stop, and only its location is handed to the reader.
            var own = await SingleStopAsync(query, scenario);
            Assert.Equal(scenario.OrderId, own.OrderId);
            Assert.Equal([scenario.OriginLocationId], reader.TakeLocationIds());

            // Another driver of the same organization, without an assignment, reads nothing and nothing is decrypted.
            Assert.Empty((await query.ListCurrentDriverStopsAsync(
                unassignedDriverUserId, scenario.OrganizationId, true, default)).Stops);
            Assert.Empty(reader.TakeLocationIds());

            // Another organization's context is refused before any stop is read.
            await Assert.ThrowsAsync<DriverStopsForbiddenException>(() =>
                query.ListCurrentDriverStopsAsync(scenario.DriverUserId, foreign.OrganizationId, true, default));
            Assert.Empty(reader.TakeLocationIds());

            // The other tenant's driver reads only their own order's stop.
            var foreignStop = await SingleStopAsync(query, foreign);
            Assert.Equal(foreign.OrderId, foreignStop.OrderId);
            Assert.Equal([foreign.OriginLocationId], reader.TakeLocationIds());

            // A stop that is no longer assigned to the driver disappears from the list, details included.
            await scenario.ExecuteAdminAsync(
                "UPDATE dispatch.assignments SET status='CANCELLED' WHERE order_id=@order;",
                P("order", scenario.OrderId));
            Assert.Empty((await query.ListCurrentDriverStopsAsync(
                scenario.DriverUserId, scenario.OrganizationId, true, default)).Stops);
            Assert.Empty(reader.TakeLocationIds());
        }
        finally
        {
            // The scenario removed the driver's membership and profile; the user row is this test's own.
            await using var cleanup = fixture.AdminDataSource.CreateCommand(
                "DELETE FROM identity.users WHERE id=@user;");
            cleanup.Parameters.AddWithValue("user", unassignedDriverUserId);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [PostgreSqlContractFact]
    public async Task A_stop_whose_details_cannot_be_read_is_still_listed_without_them()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        var vault = new Adp001FakeKeyVault();
        var envelope = new PiiEnvelopeProtector(vault);
        var zone = await CreateZoneAsync(scenario, "Centro");
        try
        {
            await scenario.ExecuteAdminAsync(
                "UPDATE locations.locations SET operating_zone_id=@zone WHERE id=@origin;",
                P("zone", zone.ZoneId),
                P("origin", scenario.OriginLocationId));
            await CreateAssignmentService(fixture.AppDataSource)
                .CreateOwnDriverAssignmentAsync(Command(scenario), default);
            var query = CreateStopsQuery(fixture.AppDataSource, new EnvelopeLocationPiiReader(envelope));

            // The synthetic scenario's placeholder bytes are not an envelope: the stop is listed, without details.
            AssertWithoutDetails(await SingleStopAsync(query, scenario), "Centro");

            // A value sealed for another row (here, the destination's) is never accepted for the pickup location.
            var sealedForDestination = await new AzureKeyVaultLocationPiiProtector(envelope).ProtectAsync(
                new LocationPiiValues(
                    scenario.OrganizationId, scenario.DestinationLocationId, "Calle Sintética 30", "Ana Ruiz", null),
                default);
            await scenario.ExecuteAdminAsync(
                """
                UPDATE locations.locations
                SET address_ciphertext=@address,contact_name_ciphertext=@contact,pii_key_version=@version
                WHERE id=@origin;
                """,
                P("address", sealedForDestination.AddressTextCiphertext),
                P("contact", sealedForDestination.ContactNameCiphertext!),
                P("version", sealedForDestination.KeyVersion),
                P("origin", scenario.OriginLocationId));
            AssertWithoutDetails(await SingleStopAsync(query, scenario), "Centro");

            // The same values sealed for the pickup row are read back.
            await SealLocationAsync(
                scenario, envelope, scenario.OriginLocationId, 24.80, -107.40, "Calle Sintética 30", "Ana Ruiz", zone.ZoneId);
            var pickup = await SingleStopAsync(query, scenario);
            Assert.True(pickup.DetailsAvailable);
            Assert.Equal("Ana Ruiz", pickup.ContactFullName);
            Assert.Equal(new DriverStopNavigationResult(24.80, -107.40, "Calle Sintética 30", null), pickup.Navigation);

            // An unavailable key service never fails the list: the stop stays, without details.
            vault.Unavailable = true;
            AssertWithoutDetails(await SingleStopAsync(query, scenario), "Centro");
        }
        finally
        {
            await DeleteZoneAsync(scenario, zone);
        }
    }

    [PostgreSqlContractFact]
    public async Task Details_false_lists_the_stops_without_reading_any_protected_value()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        await scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET cod_expected_cents=@cod WHERE id=@order;",
            P("cod", DoorCodCents),
            P("order", scenario.OrderId));
        await CreateAssignmentService(fixture.AppDataSource)
            .CreateOwnDriverAssignmentAsync(Command(scenario), default);
        await SetOrderStatusAsync(scenario, "DELIVERING");
        var reader = new RecordingStopLocationReader();
        var query = CreateStopsQuery(fixture.AppDataSource, reader);

        var confirmation = Assert.Single((await query.ListCurrentDriverStopsAsync(
            scenario.DriverUserId, scenario.OrganizationId, false, default)).Stops);

        Assert.Empty(reader.TakeLocationIds());
        Assert.False(confirmation.DetailsAvailable);
        Assert.Null(confirmation.Navigation);
        Assert.Null(confirmation.ContactShortName);
        Assert.Null(confirmation.ContactFullName);
        // What a queued action is confirmed against is still there: status, version and the cash.
        Assert.Equal("DELIVERING", confirmation.Status);
        Assert.Equal(new DriverStopCodResult(DoorCodCents, false), confirmation.Cod);
    }

    [PostgreSqlContractFact]
    public async Task Completed_today_counts_only_the_drivers_own_completions_of_the_Mazatlan_day()
    {
        await using var scenario = await DispatchScenario.CreateAsync(fixture);
        await CreateAssignmentService(fixture.AppDataSource)
            .CreateOwnDriverAssignmentAsync(Command(scenario), default);
        // 2026-10-11 in America/Mazatlan (UTC-7) runs from 07:00Z to 07:00Z the next day.
        var version = 100;
        async Task EventAsync(string status, string occurredAt, Guid? actor = null) =>
            await scenario.ExecuteAdminAsync(
                """
                INSERT INTO orders.order_events(
                  id,order_id,owner_org_id,aggregate_version,event_type,payload,actor_id,occurred_at)
                VALUES (@id,@order,@org,@version,'ORDER_STATUS_CHANGED',@payload::jsonb,@actor,@occurred);
                """,
                P("id", Guid.NewGuid()),
                P("order", scenario.OrderId),
                P("org", scenario.OrganizationId),
                P("version", version++),
                P("payload", $$"""{"previous_status":"X","new_status":"{{status}}"}"""),
                P("actor", actor ?? scenario.DriverUserId),
                P("occurred", DateTimeOffset.Parse(occurredAt, System.Globalization.CultureInfo.InvariantCulture)));
        await EventAsync("PICKED_UP", "2026-10-11T07:00:00Z");
        await EventAsync("RETURNED", "2026-10-11T12:00:00Z");
        await EventAsync("DELIVERED", "2026-10-12T06:59:59Z");
        await EventAsync("DELIVERED", "2026-10-11T06:59:59Z");
        await EventAsync("DELIVERED", "2026-10-12T07:00:00Z");
        await EventAsync("FAILED_ATTEMPT", "2026-10-11T10:00:00Z");
        await EventAsync("IN_TRANSIT", "2026-10-11T10:00:00Z");
        await EventAsync("DELIVERED", "2026-10-11T10:00:00Z", scenario.DispatcherUserId);

        async Task<int> CompletedAtAsync(string now) =>
            (await CreateStopsQuery(
                    fixture.AppDataSource,
                    now: DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture))
                .ListCurrentDriverStopsAsync(scenario.DriverUserId, scenario.OrganizationId, false, default))
            .CompletedToday;

        Assert.Equal(3, await CompletedAtAsync("2026-10-11T20:00:00Z"));
        Assert.Equal(3, await CompletedAtAsync("2026-10-11T07:00:00Z"));
        Assert.Equal(3, await CompletedAtAsync("2026-10-12T06:59:59Z"));
        // Just before midnight in Mazatlán the previous day counts; just after it, only the new day's delivery.
        Assert.Equal(1, await CompletedAtAsync("2026-10-11T06:59:59Z"));
        Assert.Equal(1, await CompletedAtAsync("2026-10-12T07:00:00Z"));
        Assert.Equal(0, await CompletedAtAsync("2026-10-13T12:00:00Z"));
    }

    private static async Task<DriverStopResult> SingleStopAsync(
        global::Dispatch.Infrastructure.Stops.PostgreSqlDriverStopsQuery query,
        DispatchScenario scenario) =>
        Assert.Single((await query.ListCurrentDriverStopsAsync(
            scenario.DriverUserId, scenario.OrganizationId, true, default)).Stops);

    private static void AssertWithoutDetails(DriverStopResult stop, string zoneName)
    {
        Assert.False(stop.DetailsAvailable);
        Assert.Null(stop.Navigation);
        Assert.Null(stop.ContactShortName);
        Assert.Null(stop.ContactFullName);
        Assert.Equal("PICKUP", stop.StopType);
        Assert.Equal(zoneName, stop.ZoneName);
        Assert.Equal("READY origin summary", stop.AddressSummary);
        Assert.DoesNotContain("Calle", stop.ToString(), StringComparison.Ordinal);
    }

    private Task SetOrderStatusAsync(DispatchScenario scenario, string status) =>
        scenario.ExecuteAdminAsync(
            "UPDATE orders.orders SET status=@status WHERE id=@order;",
            P("status", status),
            P("order", scenario.OrderId));

    /// <summary>Seals one location the way the Locations module does, bound to its owner and row.</summary>
    private static async Task SealLocationAsync(
        DispatchScenario scenario,
        IPiiEnvelopeProtector envelope,
        Guid locationId,
        double latitude,
        double longitude,
        string addressText,
        string? contactName,
        Guid? zoneId)
    {
        var protectedValues = await new AzureKeyVaultLocationPiiProtector(envelope).ProtectAsync(
            new LocationPiiValues(scenario.OrganizationId, locationId, addressText, contactName, null),
            default);
        await scenario.ExecuteAdminAsync(
            """
            UPDATE locations.locations
            SET address_ciphertext=@address,
                contact_name_ciphertext=@contact,
                pii_key_version=@version,
                point=public.ST_SetSRID(public.ST_MakePoint(@longitude,@latitude),4326),
                operating_zone_id=@zone
            WHERE id=@location;
            """,
            P("address", protectedValues.AddressTextCiphertext),
            new NpgsqlParameter("contact", NpgsqlTypes.NpgsqlDbType.Bytea)
            {
                Value = (object?)protectedValues.ContactNameCiphertext ?? DBNull.Value,
            },
            P("version", protectedValues.KeyVersion),
            P("longitude", longitude),
            P("latitude", latitude),
            new NpgsqlParameter("zone", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)zoneId ?? DBNull.Value },
            P("location", locationId));
    }

    private static async Task<(Guid AreaId, Guid ZoneId)> CreateZoneAsync(DispatchScenario scenario, string name)
    {
        var zone = (AreaId: Guid.NewGuid(), ZoneId: Guid.NewGuid());
        await scenario.ExecuteAdminAsync(
            """
            INSERT INTO locations.service_areas(id,owner_org_id,city_id,name,polygon,status)
            VALUES (
              @area,@org,@city,'Synthetic Area',
              public.ST_GeomFromText('MULTIPOLYGON(((-108 24,-106 24,-106 26,-108 26,-108 24)))',4326),'ACTIVE');
            INSERT INTO locations.operating_zones(id,owner_org_id,service_area_id,name,zone_type,polygon,status)
            VALUES (
              @zone,@org,@area,@name,'CORE',
              public.ST_GeomFromText('MULTIPOLYGON(((-108 24,-106 24,-106 26,-108 26,-108 24)))',4326),'ACTIVE');
            """,
            P("area", zone.AreaId),
            P("zone", zone.ZoneId),
            P("org", scenario.OrganizationId),
            P("city", scenario.CityId),
            P("name", name));
        return zone;
    }

    /// <summary>The scenario deletes its locations and organization; the zone must go first.</summary>
    private static Task DeleteZoneAsync(DispatchScenario scenario, (Guid AreaId, Guid ZoneId) zone) =>
        scenario.ExecuteAdminAsync(
            """
            UPDATE locations.locations SET operating_zone_id=NULL WHERE operating_zone_id=@zone;
            DELETE FROM locations.operating_zones WHERE id=@zone;
            DELETE FROM locations.service_areas WHERE id=@area;
            """,
            P("zone", zone.ZoneId),
            P("area", zone.AreaId));

    private static Task CreateOwnDriverAsync(DispatchScenario scenario, Guid userId) =>
        scenario.ExecuteAdminAsync(
            """
            INSERT INTO identity.users(id,identity_subject,status,created_at)
            VALUES (@user,@subject,'ACTIVE',@created);
            INSERT INTO organizations.organization_memberships(
              id,user_id,organization_id,role,status,is_default,granted_at)
            VALUES (@membership,@user,@org,'DRIVER','ACTIVE',true,@created);
            INSERT INTO drivers.driver_profiles(
              id,user_id,org_id,home_city_id,driver_type,vehicle_type,status,created_at)
            VALUES (@driver,@user,@org,@city,'OWN','MOTORCYCLE','ACTIVE',@created);
            """,
            P("user", userId),
            P("subject", $"ui-phase3-driver-{userId:N}"),
            P("created", OccurredAt.AddDays(-1)),
            P("membership", Guid.NewGuid()),
            P("org", scenario.OrganizationId),
            P("driver", Guid.NewGuid()),
            P("city", scenario.CityId));

    /// <summary>What the driver app sends at the door: the expected amount and the guide plus the phone's time.</summary>
    private static RecordCodCollectionCommand DoorCollection(DispatchScenario scenario, long amountCents) => new(
        scenario.DriverUserId,
        scenario.OrganizationId,
        $"drv2-{Guid.NewGuid():D}-cod",
        scenario.OrderId,
        amountCents,
        "ARC002-synthetic 2026-10-11T13:05:42-07:00",
        false,
        "ui-phase3-driver-cod");

    private static PostgreSqlCodTransactionService CreateDriverCodService(NpgsqlDataSource dataSource)
    {
        var state = new TenantDatabaseExecutionState();
        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseNpgsql(dataSource, postgres => postgres.EnableRetryOnFailure())
            .AddInterceptors(
                new TenantTransactionGuardInterceptor(state),
                new TenantSaveChangesGuardInterceptor(state))
            .Options;
        return new PostgreSqlCodTransactionService(
            new FinanceTenantGateway(
                new TenantTransactionContext<FinanceDbContext>(new FinanceDbContext(options, state), state),
                Options.Create(new FinanceOptions { Provider = FinanceProviderKind.PostgreSql })),
            new PostgreSqlAppendOnlyAuditWriter(state),
            new AuditPayloadRedactor(),
            new NoOpFinanceFailureInjector(),
            new FixedClock(OccurredAt.AddHours(1)));
    }

    /// <summary>Records which locations a stops read handed over for decryption; it never decrypts anything.</summary>
    private sealed class RecordingStopLocationReader : ILocationPiiReader
    {
        private readonly List<Guid> _locationIds = [];
        private readonly SyntheticLocationPiiReader _inner = new();

        public Task<IReadOnlyList<LocationPiiText?>> ReadAsync(
            IReadOnlyList<StoredLocationPii> locations,
            CancellationToken cancellationToken)
        {
            _locationIds.AddRange(locations.Select(location => location.LocationId));
            return _inner.ReadAsync(locations, cancellationToken);
        }

        public Guid[] TakeLocationIds()
        {
            var taken = _locationIds.ToArray();
            _locationIds.Clear();
            return taken;
        }
    }
}
