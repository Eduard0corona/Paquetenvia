# EXT-001 external driver offers

## Architecture and contracts

EXT-001 implements the normative v0.6 REST surface without changing the normative files:

- `POST /api/v1/external-offers` publishes an offer for an unassigned order in `READY_FOR_PICKUP` or `RESCHEDULED`.
- `GET /api/v1/driver/me/external-offers?cursor={cursor}` derives the authenticated Driver and returns a bounded page of currently eligible, open and non-expired offers.
- `POST /api/v1/external-offers/{offerId}/accept` accepts an offer with an `Idempotency-Key`.

PostgreSQL remains authoritative. Tenant filtering is applied inside the PostgreSQL queries under the existing transaction-scoped OrganizationContext and FORCE RLS policies. The implementation uses the existing `dispatch.external_offers`, `dispatch.assignments`, order event, audit, idempotency and outbox structures; it does not add a cross-module DbContext or a package dependency.

## Eligibility

The Dispatch application port used by DSP-001 now supports the EXTERNAL capability while preserving the same ACTIVE user, ACTIVE DRIVER membership, ACTIVE profile, city/service-area, document and vehicle-capacity checks. Offer constraints add vehicle type, service area and COD predicates. The server evaluates these rules when calculating the realtime audience, on every GET and again while holding the acceptance transaction locks. The browser supplies no Driver identifier or eligibility decision.

Candidate reads are tenant-scoped before materialization, ordered by `(created_at, id)`, limited to 201 candidates per bounded scan and returned in pages of at most 50 through an opaque cursor. Expired offers are excluded by the authoritative time predicate even if no scheduler has materialized their `EXPIRED` state yet. When a dispatcher publishes a replacement for the same order, expired OPEN rows are materialized as `EXPIRED` before the one-OPEN-offer constraint is evaluated. No `REJECTED` state exists.

## Atomic acceptance and concurrency

Acceptance resolves the authenticated EXTERNAL Driver, locks the offer and order, and then revalidates status, expiration, eligibility and assignment compatibility. One PostgreSQL transaction performs:

1. `OPEN -> ACCEPTED`, accepted Driver/time and offer version increment;
2. exactly one accepted `EXTERNAL` assignment whose `cost_cents` equals the offered commission;
3. `READY_FOR_PICKUP|RESCHEDULED -> ASSIGNED` and one order event;
4. audit, outbox and completed idempotency evidence.

The PostgreSQL contract suite starts 50 eligible Drivers behind one in-process gate and releases their independent transactions together. Its required invariant is one winner, 49 uniform `OfferUnavailable` conflicts, one accepted offer, one EXTERNAL assignment at the commission, three EXT-001 audit records, five related outbox records and one completed acceptance idempotency record. Replaying the winning key returns the same assignment without another effect.

## Realtime and UI

`dispatch.external-offer-changed` is statically routed to the existing Realtime owner. The internal outbox payload contains a bounded server-calculated Driver audience; evidence is rechecked against PostgreSQL before publication. Operations receives the event through `OperationsHub`, and only eligible or affected Driver groups receive it through `DriverHub`. The public SignalR envelope contains offer status, commission and expiration but not audience identifiers, PII or eligibility reasons.

The audience is resolved with one bounded statement (`IDispatchDriverEligibilityReader.ReadExternalCandidatesAsync`, at most 501 rows, more than 500 fails closed) that selects active `EXTERNAL` profiles with active user and `DRIVER` membership together with their service-area flag and latest document per type. The C# policy (`ExternalOfferConstraints.Allows` + `DriverEligibilityPolicy.EvaluateExternal`) is unchanged; the PostgreSQL contract test `External_candidate_batch_read_is_equivalent_to_per_driver_reads` proves the batch snapshots equal the per-driver reads.

SignalR is only an invalidation hint. Operations, Driver initial load, Driver reconnect and `ExternalOfferChanged` all return to REST. Operations adds the existing `publish_external_offer` action with explicit MXN commission, expiration and vehicle constraint. Driver displays commission and expiration before acceptance, protects the pending action from double clicks, reuses its idempotency key on retry and explains a lost race. A Driver without the EXTERNAL capability (for example an `OWN` Driver) receives `403` from `GET /driver/me/external-offers`; the Driver PWA treats it as "not applicable": the offers panel stays hidden, no error is shown and later realtime hints or reconnects do not poll again. “No me interesa” only removes the card from the current in-memory controller; it sends no request, persists nothing and can reappear after a reload or reconnect.

## Manual scenario

The deterministic seed includes a synthetic ACTIVE EXTERNAL motorcycle Driver with synthetic document/service-area prerequisites and the `external-driver` local credential. With the integrated environment running:

```powershell
pwsh ./tools/dev-platform.ps1 Scenario -Name ExternalOffer
```

The scenario creates and advances a fresh synthetic order, publishes a 125.00 MXN offer, reads it as the external Driver, checks commission/expiration, accepts it and verifies directly in PostgreSQL that exactly one `EXTERNAL` assignment exists at `12500` cents. It reports the Operations and Driver URLs. `Scenario -Name FreshOrder` remains unchanged.

## Rollback and limitations

Application rollback is the normal deployment rollback plus the Notifications module Down migration, which removes only the new static routing entry and does not drop shared NTF-001 functions or data. Existing accepted assignments and audits are business evidence and are not deleted by application rollback.

There is deliberately no scheduler, reject endpoint, durable dismiss, automatic commission calculation, ALLY_CAPACITY support or external provider integration. Expiration visibility is enforced from the database clock predicate; state materialization is lazy as described above. The local manual scenario and Testcontainers PostgreSQL suites require a working Docker runtime.
