# DRV-003 driver location ingestion

## Scope and architecture

DRV-003 implements the authoritative `POST /api/v1/driver/me/location-updates`
REST lane. It accepts synthetic MVP-0 telemetry, persists canonical driver
positions, and inserts selected notifications into the dedicated location
outbox. It does not publish to SignalR and does not add a Worker consumer;
RTM-002 remains future work.

The implementation follows the existing Drivers layers:

- Domain owns pure input validation, Haversine distance, and publication policy.
- Application owns typed batches/results, the narrow authorizer and ingestion
  port, stable public exceptions, and failure-injection checkpoints.
- Infrastructure owns current database authorization, tenant transaction,
  profile resolution, advisory locking, deduplication, position/outbox writes,
  EF mapping, configuration, logging, and metrics.
- Endpoints owns HTTP binding, structural validation, status translation, body
  size enforcement, and the identity-partitioned batch rate limiter.
- Paqueteria.Api only registers and maps Drivers Endpoints.

Drivers has no dependency on Realtime Infrastructure, Realtime Endpoints,
`IHubContext`, or `IRealtimePublisher`. Paqueteria.Worker is unchanged.

## HTTP contract and authorization

The endpoint operation name is `publishDriverLocation`. A request contains a
required `positions` array with 1–20 items. Each item contains
`client_event_id`, `lat`, `lng`, `accuracy_m`, `captured_at`, and optional
`heading_degrees` and `speed_mps`. Driver, organization, city,
`publish_realtime`, topic, audience, and group are never accepted from the
client. Unknown properties and malformed or out-of-range batch shapes follow
the repository's uniform `409 INVALID_REQUEST` convention.

The complete productive response set is `202`, `401`, `403`, `404`, `409`,
`429`, and `503`. AI-05 declares this exact set. `409` is a stable Problem
Details response with `code=INVALID_REQUEST`; it does not expose parser details.
`503` fails closed when the Drivers provider is disabled or PostgreSQL
infrastructure fails, without exposing exceptions, SQL, connection details, or
stack traces.

The request requires a valid OIDC identity and `X-Organization-Id`. The existing
tenant middleware verifies the active internal identity and active membership.
Inside the productive PostgreSQL transaction, authorization is read again from
`identity.users` and `organizations.organization_memberships`. Only an active
`DRIVER` membership proceeds. This capability check happens before any
`client_event_id` query, preventing deduplication from becoming an oracle.

After capability authorization, the service resolves exactly one active `OWN`
profile by current user and organization. The server derives `driver_id`,
`org_id`, and `home_city_id`; absent, suspended, cross-tenant, EXTERNAL, or ALLY
profiles all produce the same safe `404`.

A structurally valid batch returns `202` with items in request order:

- `ACCEPTED`: a new position was committed, whether or not publication was
  throttled.
- `DUPLICATE`: the canonical existing `position_id` is returned without a new
  position or outbox.
- `REJECTED`: no persistence, `position_id=null`, and one allow-listed code:
  `INVALID_CLIENT_EVENT_ID`, `INVALID_COORDINATES`, `INVALID_ACCURACY`,
  `INVALID_HEADING`, `INVALID_SPEED`, or `INVALID_CAPTURED_AT`.

The three counters are computed from the returned items. No HTTP
`Idempotency-Key` is used.

After structural and domain validation, Infrastructure canonicalizes
`captured_at` once with `Paqueteria.Application.UtcMicrosecondPrecision`.
The UTC value is truncated, never rounded, to PostgreSQL microsecond precision
before ordering, deduplication, publication policy, persistence, location
outbox construction, and cursor calculation. Persisted `received_at` is
canonicalized by the same shared policy. A repeated `client_event_id` keeps the
existing position and does not create another outbox row.

## Validation and canonical mapping

Batch shape and item telemetry are deliberately classified at different
boundaries. Missing `positions`, a count outside 1â€“20, null items, unknown
properties, malformed JSON, an oversized body, and a missing, null, empty,
whitespace, malformed, or non-canonical `client_event_id` reject the complete
request as `409 INVALID_REQUEST`. The endpoint accepts only an exact UUID `D`
form through `Guid.TryParseExact`; it does not trim or substitute input. This
structural validation completes before the ingestion service is invoked, so it
opens no transaction, reads no profile or deduplication data, takes no advisory
lock, and produces no persistence effects, persistence metrics, or productive
ingestion logs.

A canonical empty UUID is structurally valid but semantically invalid. It
continues into item validation and returns `202` with its exact empty UUID,
`status=REJECTED`, `position_id=null`, `duplicate=false`, and
`error_code=INVALID_CLIENT_EVENT_ID`. Invalid coordinates, accuracy, heading,
speed, and captured time remain item-level `202 REJECTED` results. Application
commands and results carry non-null `Guid` values, and response mapping returns
that same value without a `Guid.Empty` fallback or any other invented
identifier.

Latitude and longitude must be finite and within `[-90,90]` and `[-180,180]`.
Accuracy and speed are finite, non-negative, and bounded by `numeric(8,2)`;
heading is finite within `[0,360]`. A non-default ISO-8601 timestamp is retained
as the supplied instant and normalized to UTC. No unapproved past/future clock
window is imposed.

Infrastructure maps the existing `drivers.driver_positions` table without
exposing NetTopologySuite from Domain or Application. The point is
`geometry(Point,4326)`, with X=longitude and Y=latitude. Numeric mappings are
`numeric(8,2)` and `numeric(6,2)`; times are `timestamp with time zone`. IDs and
`received_at` are generated explicitly by the application (`Guid.NewGuid` and
`IClock.UtcNow`) and do not rely on database defaults.

Migration `20260725000156_AdoptCanonicalDriverPositions` is non-destructive. It
verifies the canonical columns/types, PK, three FKs, deduplication unique key,
three indexes, RLS, FORCE RLS, policy, and SRID. `Down` deliberately performs no
DDL. AI-06 and AI-18 remain the schema and privilege authorities.

## Transaction, deduplication, and concurrency

Every structurally valid request runs on one connection and transaction:

1. apply tenant context and `SET LOCAL ROLE paqueteria_app`;
2. read current user/membership/role;
3. resolve the active OWN profile;
4. acquire a namespaced transaction advisory lock derived from the server-side
   driver UUID;
5. load matching `(driver_id, client_event_id)` rows in one bounded query;
6. read the deterministic latest published baseline;
7. classify and insert every valid new position;
8. insert one location-outbox row for every selected position;
9. invoke the `BeforeCommit` checkpoint and commit.

The advisory lock serializes deduplication, baseline selection, throttling, and
outbox creation across requests and DbContexts. The canonical unique constraint
is the final backstop. Repeated IDs in one batch reuse the first canonical
position ID. EF's retry execution strategy starts a new transaction and
reapplies tenant context, authorization, lock, deduplication, and all writes.

Failure checkpoints are `AuthorizationCompleted`, `DriverLocked`,
`DuplicatesRead`, `PositionInserted`, `LocationOutboxInserted`, and
`BeforeCommit`. The production injector is a no-op. Any infrastructure failure
or cancellation rolls back the entire batch; per-item validation rejection is
the only partial classification allowed in a successful batch.

## DRV-003-PERSIST-THROTTLE-LOCATION-OUTBOX

Decision: persist every valid new point and publish only deterministically
selected points. Publication is a reversible configuration policy, not an SLA
or commercial promise. `publish_realtime=true` has a one-to-one relationship
with a dedicated `platform.location_outbox_events` row in the same transaction.
The separate outbox is not the business outbox. There is no direct SignalR
publication and no public location data. Retention remains deferred by
GATE-007.

Synthetic defaults:

```json
{
  "Drivers": {
    "LocationTelemetry": {
      "MinimumPublishIntervalSeconds": 10,
      "MinimumPublishDistanceMeters": 25,
      "MaximumSilenceSeconds": 60
    }
  }
}
```

Startup validation requires interval 1–300 seconds, distance greater than zero
and at most 1000 metres, and maximum silence at least the interval and at most
3600 seconds.

The latest baseline is ordered by `captured_at DESC, received_at DESC, id DESC`.
Without a baseline, the earliest accepted point publishes. New points are
evaluated by `captured_at ASC`, original batch position, then client event ID.
A candidate publishes only when it is newer and either maximum silence elapsed,
or both minimum interval and minimum distance were met. A selected candidate
becomes the next baseline. Older/equal points and throttled points remain
persisted with `publish_realtime=false`.

Distance uses explicit Haversine calculation with mean Earth radius
6,371,008.8 metres. It never treats planar SRID-4326 distance as metres.

## Dedicated location outbox

The internal topic is exactly `drivers.location-updated`. The minimal
snake_case payload is schema version `driver-location-updated-v1` and contains
only `driver_position_id`, `driver_id`, `lat`, `lng`, `accuracy_m`, and
`captured_at`. Audience tenancy is the server-derived `owner_org_id`.

Every insert explicitly supplies all lifecycle columns: application-generated
ID, owner and position IDs, topic, payload, `PENDING`, zero attempts,
`available_at`/`created_at` equal to `received_at`, and null lock, lease, error,
and processed fields. The SQL has no `RETURNING`. Drivers does not map or read
the outbox. `paqueteria_app` retains INSERT-only privileges; privileged test
fixtures inspect rows.

## Rate limiting, observability, and privacy

`Drivers:LocationIngestion` configures a fixed-window batch limit and maximum
body size (synthetic defaults: 30 batches/60 seconds and 32 KiB). The queue is
disabled. Partitions use a one-way hash of authenticated subject, with hashed IP
only as the anonymous fallback; tokens are never partition keys and the
partition key is never logged.

Rate limiting is an anti-abuse boundary. `UseRateLimiter()` runs after identity
authentication but before `TenantContextMiddleware` and endpoint authorization.
Once an identity or network-fallback partition exhausts its quota, `429` may
preempt the normal functional `401`, `403`, or `404` response. This protection
does not query PostgreSQL, profile data, driver data, event existence, or
deduplication and therefore is not a deduplication oracle. It does not alter the
service's internal precedence: capability authorization, then OWN profile
resolution, then deduplication.

Metrics cover batches, accepted/duplicate/rejected positions, rejection code,
selected/suppressed publication, persistence failures, and duration. Labels are
limited to provider, outcome, rejection code, and publication decision. Logs
are only `driver_location_batch_completed` and
`driver_location_batch_failed`; they contain no coordinates, telemetry values,
IDs, claims, tokens, SQL values, or payload.

Driver location is labor-related personal data. Only synthetic coordinates are
used in configuration and tests. While GATE-007 remains open, production use
with real telemetry is blocked pending legal basis, privacy notice, retention,
restricted access, ARCO handling, deletion/restriction policy, labor monitoring
rules, and location security. This block adds no retention, deletion, export,
device data, background tracking, geofencing, map, or public coordinate flow.

## Verification and rollback

Unit tests cover field validation, UTC normalization, exact boundaries,
Haversine vectors, publication decisions, counters, options, and stable codes.
HTTP tests cover authentication/tenant/capability, uniform 404, 1/20/21 batch
shapes, order, mixed outcomes, malformed and non-canonical event IDs, exact
empty-UUID rejection, full-batch rejection before service invocation,
rate-limit preemption without a service call, and Disabled provider.
PostgreSQL 18/PostGIS tests cover geometry/SRID, coordinates, numeric/UTC
storage, RLS/FORCE RLS, insert-only outbox privileges, payload/lifecycle,
empty-UUID no-write behavior, throttling, replay deduplication, cross-DbContext
concurrency, all failure checkpoints, cancellation, adoption history, and zero
pending migrations.
Architecture and contract tests keep Drivers isolated from Realtime and Worker
and compare the exact operation, request, response schema, and seven response
statuses to AI-05 while retaining AI-06/AI-12 protections.

Operational rollback first sets `Drivers:Provider=Disabled`, which fails closed
without simulating success or producing outbox. Code commits may then be
reverted in reverse order. Do not run inverse DDL, delete confirmed positions,
remove EF migration history, or change canonical location-outbox functions.
