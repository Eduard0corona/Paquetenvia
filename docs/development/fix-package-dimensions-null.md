# Fix: an order without package measurements can be assigned and routed

Pilot defect, no backlog item and no normative change. Branch `fix/package-dimensions-null`.

## Defect

An order created in "Nueva orden" without package measurements (the wizard marks them optional) could not be
assigned to any own driver:

- the driver picker said "No disponible: Los paquetes de la orden no tienen peso o medidas válidos"
  (`PACKAGE_REQUIREMENT_INVALID`);
- `assignDriver` answered `409 CAPACITY_INSUFFICIENT`;
- `addRouteStop` answered `DRIVER_INELIGIBLE`;
- the same order could not get an external offer (`createExternalOffer` answered `INVALID_ORDER_STATE`, an open
  offer was skipped by `listEligibleExternalOffers` and `acceptExternalOffer` answered `DRIVER_INELIGIBLE`);
- the ORD-002 guards `eligible_driver` and `capacity_available` (towards `ASSIGNED` and on re-delivery) failed.

## Root cause

Pricing snapshots empty measurements as JSON nulls, and `QuoteSnapshotToOrderCoordinator.ParsePackages`
(`src/Modules/Orders/Orders.Infrastructure/Orders/QuoteSnapshotToOrderCoordinator.cs:742-750`) serializes them into
`orders.package_items.dimensions_mm` as `{"length_mm":null,"width_mm":null,"height_mm":null}`.

Every reader of `dimensions_mm` accepted a missing key as "no measurement" but rejected a present `null`
(`ValueKind != Number`), so the whole package requirement became invalid. The capacity model already treats
measurements as optional (`PackageCapacityItem(weight, int? length, int? width, int? height)`, AI-06
`dimensions_mm jsonb NOT NULL DEFAULT '{}'`), and `DriverEligibilityPolicy` only demands them when the vehicle's
`RequireDimensions` is true.

## Fix: the readers

A JSON `null` now means exactly what a missing key means: no measurement, valid. Everything else is as strict as
before: a string, boolean, object, array, zero, negative, fraction or a value outside `int` is still invalid, and a
document that is not a JSON object (or is not JSON) is still invalid.

Every reader of `dimensions_mm` in the repository:

| Reader | Used by | Changed |
| --- | --- | --- |
| `src/Modules/Dispatch/Dispatch.Infrastructure/Assignments/PostgreSqlAssignmentToOrderCoordinator.cs:556` `TryReadDimensions` / `:590` `TryReadDimension` | `assignDriver` (`ParsePackages`, `:541`); `PostgreSqlAssignableDriversQuery.cs:128` (picker); `ExternalOffers/PostgreSqlExternalOfferService.cs:996` `ParseCapacity` (create `:119`, list `:876`, accept `:943`) | yes |
| `src/Modules/Routing/Routing.Infrastructure/Routes/PostgreSqlRouteService.Persistence.cs:282` `TryReadDimensions` / `:311` `TryReadDimension` | `addRouteStop` (`ReadCapacityAsync`, `:269`) | yes |
| `src/Modules/Orders/Orders.Infrastructure/Orders/PostgreSqlOrderTransitionEligibility.cs:169` `TryReadDimensions` / `:194` `TryReadDimension` | ORD-002 `PostgreSqlOrderAssignmentGuardReader` (`PostgreSqlOrderTransitionReaders.cs:250`) | yes (also `private` to `internal` for the unit tests) |

The SQL that touches `dimensions_mm` only passes it through to those readers; none casts or filters it, and no
database function or view reads it:

- `PostgreSqlDispatchReaders.cs:128` and `PostgreSqlAssignableDriversQuery.cs:160` (`dimensions_mm::text`);
- `PostgreSqlExternalOfferService.cs:851`, `:931` and `:1021` (`jsonb_build_object('dimensions_mm', ...)`);
- `PostgreSqlRouteService.Persistence.cs:259` and `PostgreSqlOrderTransitionReaders.cs:360` (`dimensions_mm::text`);
- `20260722_AdoptCanonicalOrdersBaseline.cs:56` only checks that the column exists as `jsonb`.

No `->>`/`->` cast of `dimensions_mm` exists in any module, migration or AI-06 function.

### One implementation or three

The three copies stay three. Orders, Dispatch and Routing cannot reference each other's infrastructure
(`Modules_do_not_reference_foreign_module_internals`; Routing may not depend on Orders), and the only projects all
three reach are building blocks and `Drivers.Application`, which would then own the format of an Orders column.
Each module already keeps its own capacity aggregation on purpose (`PackageCapacityAggregator`,
`RoutingCapacityAggregator`, `OrderTransitionEligibility.Aggregate`). The same one-line change is applied to each
copy, and one shared test contract (`tests/Paqueteria.UnitTests/PackageDimensionsReaderContract.cs`) runs the same
cases against each of them, so they cannot drift apart silently. Dispatch.Infrastructure and Routing.Infrastructure
gain `InternalsVisibleTo("Paqueteria.UnitTests")`, as Orders.Infrastructure and others already have.

## The writer is unchanged

`QuoteSnapshotToOrderCoordinator` still writes the JSON nulls. Nothing depends on that exact JSON:

- the order idempotency hash (`ComputeRequestHash`) covers the command only, never the packages;
- the `ORDER_CREATED` order event (`order_id`, `quote_id`, `status`, `restricted_goods_acknowledged`), its outbox
  payload (`order_id`, `public_id`, `status`) and the audit payload carry no package; the existing PostgreSQL
  contract test already asserts that none of them contains "package";
- `OrderAcceptanceCanonicalForm v1` covers the acceptance evidence only (order, quote, owner, actor, versions,
  accepted at, channel), so invariant 12 is untouched;
- `orders.orders.package_snapshot` is the quote's snapshot copied verbatim, not the writer's output;
- no test, snapshot or contract pins the JSON.

It is left alone anyway: pilot rows already hold the nulls, so the readers had to accept them regardless, and
changing the writer would only make two shapes coexist in the table.

## Tests

- Unit (`Paqueteria.UnitTests`): `DispatchPackageDimensionsReaderTests`, `RoutingPackageDimensionsReaderTests` and
  `OrderPackageDimensionsReaderTests`, each running the shared contract: all three null, some null and some
  numbers, a missing key, `{}`, all present; a string, zero, negative, fraction, overflow, boolean, object, array
  and an invalid value next to a null still rejected; `null`, an array, a string, a number, a boolean and malformed
  JSON rejected as documents. The Orders class also checks `Aggregate` and the two ORD-002 guards. Against the
  unfixed readers, exactly the null cases fail (15 tests); every rejection case already passed.
- PostgreSQL (`DispatchPackageDimensionsPostgreSqlContractTests.cs`, on the Dispatch harness, runtime role
  NOBYPASSRLS): a quote without measurements, `createOrder` through the real coordinator (asserting the stored
  nulls), `DRAFT -> CONFIRMED -> READY_FOR_PICKUP` through ORD-002, then
  - the picker lists the own driver as eligible, `assignDriver` accepts it, the ORD-002 assignment guards attest
    capacity, and `addRouteStop` puts the order on the driver's route;
  - an external offer can be created, is listed for an external driver and is accepted.

  With `RequireDimensions=true` the same order is still refused, with `PACKAGE_LENGTH_EXCEEDED`,
  `PACKAGE_WIDTH_EXCEEDED` and `PACKAGE_HEIGHT_EXCEEDED` (not `PACKAGE_REQUIREMENT_INVALID`), `CAPACITY_INSUFFICIENT`
  and `DRIVER_INELIGIBLE`: the capacity rules are unchanged. Against the unfixed readers the first test fails at the
  picker with `PACKAGE_REQUIREMENT_INVALID` and the second at `createExternalOffer`.

## Rollback

Revert the commit. No migration, schema, role, contract, configuration or data change is involved; the stored rows
are the same before and after, so a rollback only brings the defect back.
