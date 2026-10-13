# SEC-004 PR 2: shared rate-limit plumbing and the 429 body

Phase 4 PR 2 (`docs/development/phase4-platform-plan.md`, "PR 2: plomería compartida de límites y cuerpo del 429"),
backlog item **SEC-004** in `AI-08_BACKLOG.yaml`. Decision rows: `PHASE4-PLATFORM-APPROVAL-2026-10-12` and
`SEC-004-RATE-LIMITS-2026-10-12`, and the two owner answers of 2026-10-13 quoted in "Owner decisions" below. This PR
changes no limit and limits no new endpoint: the decided list ("CSV 10/min, …, inicio de sesión 20/min por red") is
PR 3 and waits on the three owner decisions recorded in AI-08.

No normative document, migration, role, grant or package changes. AI-05 already declares 429 (`TooManyRequests`) on
every limited operation (`publicTracking`, `publishDriverLocation`, `requestRecipientCall`); the SignalR hubs are AI-12
endpoints, not AI-05 operations.

## What changed

| Area | Before | After |
| --- | --- | --- |
| Registration | Orders, Drivers and Realtime each called `AddRateLimiter` with their own partition code. The one `OnRejected` was the Orders one, global, and wrote a body only for the public tracking path. | One registry: `AddPaqueteriaRateLimiting` (`src/BuildingBlocks/Paqueteria.Infrastructure/Security/RateLimiting/`) is the only `AddRateLimiter`. The host calls it once with the full policy table (`src/Paqueteria.Api/Http/ApiRateLimitPolicies.cs`). Modules only name a policy with `RequireRateLimiting` and expose its window from their own options (`PublicTrackingEndpointDefaults.RateLimit`, `RealtimeEndpointDefaults.ConnectionRateLimit`, `DriverLocationEndpointOptions.RateLimit`). |
| Partition keys | Each module hashed `identity:{sub}` or the client network itself. | `RateLimitPartitionKeys`: `SubjectPartition.Key` (SHA-256 of `identity:{sub}`, as GPS did) for authenticated callers, `ClientNetworkPartition.Key` (IPv4 address or IPv6 /64) for anonymous ones. |
| Rejection writer | The Orders `OnRejected` (public tracking only). No limiter 429 sent `Retry-After`; GPS ingestion and hub negotiate answered 429 with an empty body. | `RateLimitRejectionWriter`, one for every policy: counts the rejection by policy name, runs the policy's own counter, sets `Retry-After` and `Cache-Control: no-store`, and writes the 429. |
| 429 body | GPS and hubs: empty. Recipient calls: `{"title":"Too many requests.","status":429,"traceId":…}`, without the `type` AI-05 `Problem` requires. | AI-05 `TooManyRequests` everywhere: `{"type":"about:blank","title":"Too Many Requests","status":429,"traceId":"<32 hex>"}` as `application/problem+json`. The public tracking surfaces keep the uniform body without `traceId`. |
| Metric | Only `public_tracking.rate_limit.rejected`. | Also `rate_limit.rejected` (meter `Paquetenvia.RateLimiting`), one tag `policy`. The public tracking counter is unchanged. |

## Policies

The numbers are the ones each module used before this PR; `Existing_limits_keep_their_numbers` pins them from the API's
own `appsettings.json`. The deploy templates set none of these keys.

| Policy | Endpoints | Limit (configuration) | Partition | 429 |
| --- | --- | --- | --- | --- |
| `PublicTrackingLookup` | `GET /api/v1/tracking/{token}` (`publicTracking`) | 60 per 60 s (`PublicTracking:LookupPermitLimit`, `LookupWindowSeconds`) | client network, always | uniform body, no `traceId`, `Retry-After` (owner decision 1), its privacy headers unchanged (`Cache-Control: no-store, private`, …), `public_tracking.rate_limit.rejected` |
| `Realtime.Connections` | negotiate and connection requests of `/hubs/operations`, `/hubs/driver` and `/hubs/tracking`, one budget for the three | 20 per 60 s (`Realtime:ConnectionPermitLimit`, `ConnectionWindowSeconds`) | subject hash; client network when anonymous | private hubs: problem with `traceId` and `Retry-After`; tracking hub: uniform body, no `traceId`, with `Retry-After` |
| `Drivers.LocationIngestion` | `POST /api/v1/driver/me/location-updates` (`publishDriverLocation`) | 30 per 60 s (`Drivers:LocationIngestion:BatchPermitLimit`, `WindowSeconds`) | subject hash; client network when anonymous | problem with `traceId` and `Retry-After` |
| recipient calls (not a limiter policy) | `POST …/recipient-call` (`requestRecipientCall`) | 3 per driver and order in 15 min, 20 per driver per hour (`Drivers:RecipientCalls`), counted in PostgreSQL | driver and order (database) | problem with `traceId` and the database's `Retry-After` |

All limiter policies are fixed windows with no queue (`QueueLimit = 0`) and automatic replenishment, as before. The
limiter runs after authentication and before the tenant middleware and authorization, as before, so an anonymous call
to a private endpoint spends its network's budget. Limiter state lives in the API process: exact while the pilot runs
one API replica (GATE-013).

`Retry-After` is whole seconds, rounded up, at least 1. The .NET fixed window limiter always reports its full configured
window as the retry hint, however much of the window has passed (checked: 60 s after 1.5 s of a 60 s window). So each
policy's value is a constant, 60 with the default windows: an upper bound, never too early, and it says nothing about a
token or about other callers. Every 429 also carries `Cache-Control: no-store`, like the realtime gate's 401, 404 and
503; on `publicTracking` its own privacy headers are set when the response starts and stay exactly as before
(`no-store, private`, `Pragma: no-cache`, …).

## How it works

- `RateLimitPolicy(Name, Partition, Limit)` describes one policy. `Limit` calls the owning module's accessor, which reads
  that module's validated options on every request, exactly as each module did; a partition keeps the numbers it was
  created with. `CountRejection` (an extra module counter, only public tracking's today) gets the request services and
  nothing else, so a counter cannot read the user, the address or the path. The accessors return a plain
  `(PermitLimit, Window)` pair because the host may reference module endpoint projects but not their application
  projects (`DependencyRulesTests`).
- `RateLimitPolicyRegistry` refuses a duplicate, blank or unknown-kind policy, and `AddPaqueteriaRateLimiting` refuses a
  second registration.
- The writer finds the policy from the endpoint's `EnableRateLimitingAttribute`. A rejection from a policy outside the
  registry throws: every limited endpoint must name a registered policy, and the integration tests check that every
  `RequireRateLimiting` does.
- `PublicTrackingRateLimitRejection` is endpoint metadata on `publicTracking` and on the tracking hub. Those endpoints
  answer the uniform body (written as fixed bytes, so it stays byte-identical); every other 429 goes through
  `Results.Problem`, so OBS-003 adds `traceId` even when the request's `Accept` excludes JSON.
- If the response already started, the writer counts the rejection and writes nothing.
- `RecipientCallEndpoints` uses the same `TooManyRequestsProblem` type, title and `Retry-After` rounding.

## Choices not written in the task

- **`type` and `title`.** `about:blank` and `Too Many Requests`, the RFC 9457 pairing and the public tracking body, so
  the only difference between the two 429 bodies is `traceId`. ASP.NET Core has no default `type` for 429, which is why
  the recipient-call 429 had none.
- **Recipient calls.** Their limit stays where it is (PostgreSQL, per driver and order, across replicas); it is not a
  limiter policy, so it is not in `rate_limit.rejected`, and, as before, no metric counts its 429. Only its body
  changes: it gains `type` and its title becomes `Too Many Requests` (was `Too many requests.`). The PWA reads only the
  status and `Retry-After`.
- **The tracking hub.** Its 429 is the uniform public body without `traceId`, because AI-05 `Problem.traceId` names the
  tracking hub among the public tracking answers that never carry it. It gets `Retry-After` and `Cache-Control:
  no-store` like every other 429.
- **The metric.** `rate_limit.rejected` with one tag, `policy`, whose values are the registry's fixed names. It is not an
  OBS-002 log event: nothing is logged, `TelemetryDimensions.Allowed` is unchanged, and no alert or workbook reads it.
  `policy` passes the OBS-002 forbidden-fragment rule (`policy_name` would not: it contains `name`). Metrics are not
  exported until PR 6, so the counter is only visible in the process until then. For PR 6: `policy` is not yet in
  `ObservabilityArchitectureTests.MetricTags`, and that test does not scan the `Security/RateLimiting` folder; when the
  export starts, both should be added (here `RateLimitingArchitectureTests` pins the one counter and its one tag).
- **Anonymous fallback key.** GPS and the hubs hashed `ClientNetworkPartition.Describe(address)`; they now use
  `ClientNetworkPartition.Key(address)` (SHA-256 of `network:` plus the same text), like public tracking. Both are
  opaque and in memory only, so nothing observable changes; a deploy resets limiter state anyway.
- **Blank subject.** A `sub` claim that is only white space counts as anonymous for both partitioned policies (GPS
  already did this; the hubs only checked for empty).
- **Coverage test.** `HttpSurfaceOpenApiCoverageTests` now treats every operation under a rate-limit policy as emitting
  429, whatever its own `Produces` metadata says, so PR 3 cannot limit an AI-05 operation without declaring 429 there.
  The exception is the dashboard (`GET /operations/dashboard`): the test skips it through `KnownUndeclaredOperations`
  (ADR-OBS-001), so its 429 is recorded through the ADR-OBS-001 amendment the owner chose for PR 3, not by this test.
- **The architecture guard.** `RateLimitingGuard` refuses, outside the registry folder, the two rate limiting namespaces
  themselves (any using, alias, global using or fully qualified name), `OnRejected` assignments, any
  `RateLimiterOptions`, `RateLimitPartition`, limiter subclasses and instances, limiter extensions and attributes, and
  `UseRateLimiter` outside the API host; it also refuses a project-wide `<Using>` of those namespaces in any project,
  props or targets file. It is still text matching (no Roslyn), so the integration tests also check the composed
  `RateLimiterOptions` at run time.

## Owner decisions (2026-10-13)

Literal questions and answers.

1. Question: "Límites de tasa, seguimiento público: cuando alguien consulta demasiado rápido una guía, el 429 hoy no
   dice cuántos segundos esperar (encabezado Retry-After). El backlog (SEC-004) pide que todo 429 lo diga. ¿Lo
   agregamos?" Answer: "Sí, agregarlo (Recommended)", whose option read "El 429 del seguimiento público dirá cuántos
   segundos esperar, como todos los demás. El cuerpo de la respuesta no cambia y sigue sin identificador de traza. Es
   una línea en este mismo PR." Not chosen: "No, dejarlo así".
   - Applied here: `publicTracking` sends `Retry-After` like every other 429. Its body is byte-identical and still has
     no `traceId`, and its privacy headers are unchanged. The per-policy switch is gone: every policy sends it.
2. Question: "Límites de tasa, visibilidad: el backlog pide ver cuántos rechazos hay por cada límite desde el primer
   despliegue de los límites nuevos (PR 3). Hoy nada exporta métricas hasta el PR 6 (Application Insights). ¿Cómo lo
   resolvemos?" Answer: "Resumen en los logs (Recommended)", whose option read "Un evento periódico en los logs, como el
   resumen de estados HTTP que ya existe, con cuántos rechazos hubo por límite (solo el nombre del límite, nunca usuario
   ni IP). Se ve en Log Analytics desde el primer despliegue, sin esperar el PR 6. Agrega el nombre del límite a la lista
   de dimensiones permitidas."
   - Not in this PR: it goes in PR 3 (see "Next").

## Next

- **PR 3, the rejection summary (owner decision 2).** A periodic OBS-002 log event, like `HttpStatusSummary` (4603),
  with the rejection count per policy over the window: only the policy name, never a user or an address.
  - It adds the policy name to `TelemetryDimensions.Allowed`. `Policy` passes the forbidden-fragment rule;
    `PolicyName` would not.
  - It can read the counts where `RateLimitRejectionWriter` already counts them.
  - It must not reach an alert query unless a later decision asks for one.
- **PR 6.** Add `policy` to `ObservabilityArchitectureTests.MetricTags` and scan `Security/RateLimiting` there when
  metrics start being exported.

## Tests

- `tests/Paqueteria.IntegrationTests/Security/Sec004RateLimitPlumbingTests.cs` (assembled API, no database):
  - GPS ingestion and the operations and driver hub negotiates: 429, exactly `application/problem+json`, the four
    members with a 32-hex `traceId`, `Retry-After` an integer from 1 to the window;
  - every non-public 429 carries `Cache-Control: no-store`;
  - the tracking hub negotiate: the uniform body byte for byte, with `Retry-After` and `no-store`;
  - `publicTracking`:
    - the uniform body byte for byte, without `traceId`;
    - `Retry-After` equal to the configured window;
    - its privacy headers unchanged (`no-store, private`, `no-cache`, `no-referrer`);
    - its own counter called once;
  - two subjects on one network have separate GPS and hub budgets, and one subject keeps its budget across networks;
  - two IPv4 addresses and two IPv6 /64s have separate tracking hub and public tracking budgets; one /64 shares one;
  - each policy's rejection is counted once, with `policy` as its only tag (the host's own meter);
  - at Trace level, no log line, log scope, metric of any meter or activity carries the addresses, their network text,
    a partition key or a subject (raw or hashed). The activities are captured by recording listeners on the hosting and
    `Paqueteria` sources, which record more than the API's own propagation-only listener;
  - the composed `RateLimiterOptions` are exactly the registry's:
    - its writer alone in `OnRejected`;
    - no global limiter;
    - status 429;
    - the same policy names (read from the options' internal policy maps).
  - every `RequireRateLimiting` names a registered policy and every policy is used; only the public tracking surfaces
    are marked uniform;
  - the deployed numbers above, recipient calls included.
- `tests/Paqueteria.IntegrationTests/Drivers/DriverVoiceHttpTests.cs`: the recipient-call 429 has the same shape.
- `tests/Paqueteria.IntegrationTests/HttpSurfaceOpenApiCoverageTests.cs`: rate-limited operations emit 429.
- `tests/Paqueteria.UnitTests/Security/Sec004RateLimitTests.cs`:
  - `Retry-After` rounding;
  - the shape constants;
  - the subject hash;
  - the partition kinds;
  - the registry's refusals;
  - the writer: window fallback, `no-store`, the uniform body with `Retry-After`, nothing written once the response
    started, an unknown policy.
- `tests/Paqueteria.ArchitectureTests/RateLimitingArchitectureTests.cs`:
  - the guard finds nothing in the source or in any project, props or targets file;
  - only the registry calls `AddRateLimiter`;
  - modules pass only a `*.RateLimitPolicy` constant;
  - the host registers the table once, after routing and authentication;
  - the writer counts by policy only and reads no address, user or partition key.
- `tests/Paqueteria.ArchitectureTests/RateLimitingGuardTests.cs`: the guard refuses, as fixture sources at a module path,
  the bypass the review reproduced (alias using, `Configure<RL.RateLimiterOptions>`, `OnRejected=`, a fully qualified
  `RateLimitPartition<string>`). It also refuses each of:
  - global, static and spaced usings, and a `global::` name;
  - `AddRateLimiter` and `OnRejected +=`;
  - limiter options, partitions, the rejection status and a global limiter;
  - partitioned limiters, policy types, `RateLimiter` subclasses and limiter instances;
  - limiter extensions and attributes;
  - `UseRateLimiter` outside the host;
  - a project-wide `<Using>`.

  The registry folder and the production patterns pass. The same bypasses were also checked as real files in a module
  (the reviewed one, a `RateLimiter` subclass and a csproj `<Using>`): the architecture tests failed, and the files
  were removed.
- Unchanged and green: `DriverLocationHttpTests`, `RealtimeHubIntegrationTests`, `HttpHardeningTests`, the public
  tracking PostgreSQL 429 test and the OBS-002 allowlist tests.

## Verify locally

```bash
dotnet build Paqueteria.sln -c Release
dotnet test tests/Paqueteria.UnitTests -c Release --no-build --filter "FullyQualifiedName~Sec004"
dotnet test tests/Paqueteria.ArchitectureTests -c Release --no-build --filter "FullyQualifiedName~RateLimiting|FullyQualifiedName~Observability"
dotnet test tests/Paqueteria.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~Sec004|FullyQualifiedName~HttpSurfaceOpenApiCoverage|FullyQualifiedName~DriverVoiceHttp|FullyQualifiedName~DriverLocationHttp|FullyQualifiedName~RealtimeHubIntegration|FullyQualifiedName~HttpHardening"
```

## Rollback

Revert the commit. There is no schema, migration, role, grant, package, configuration or contract change. The limits
and their configuration keys are the same before and after, and limiter state is in memory, so nothing is left behind.
After a revert, GPS ingestion and hub negotiate answer 429 with an empty body again, no limiter 429 says
`Retry-After` (public tracking included), and the recipient-call 429 loses `type`.
