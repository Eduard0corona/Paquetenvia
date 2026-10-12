# OBS-003 PR 1: W3C correlation without packages

Phase 4 PR 1 (`docs/development/phase4-platform-plan.md`, "PR 1: correlación W3C, sin paquetes"), backlog item
**OBS-003** in `AI-08_BACKLOG.yaml`. Decision row: `OBS-003-W3C-CORRELATION-2026-10-12`, which applies
`PHASE4-PLATFORM-APPROVAL-2026-10-12` (owner's answer: "Sí, avanza con la fase 4 (Recommended)").

No NuGet or npm package was added: `Directory.Packages.props` and every lock file are unchanged. Everything is
built on `System.Diagnostics.Activity`.

## What changed

| Area | Behaviour |
| --- | --- |
| Inbound | `traceparent`, `tracestate`, `baggage`, `Request-Id` and `Correlation-Context` are ignored. Every API request starts a new W3C trace, even when a browser sends a sampled context. |
| Outbound | No request carries those headers: Google Maps, WhatsApp, ACS e-mail, Twilio, the AuthCenter OIDC back-channel (discovery, JWKS, token, revocation), Azure Key Vault (secrets, keys, cryptography), Blob Storage, the data protection key resolver and the managed identity token requests. |
| One identifier | The 32 lowercase hex W3C trace id is `HttpContext.TraceIdentifier`, `platform.audit_logs.request_id`, the `traceId` of every `application/problem+json` and `State.TraceId` in every JSON console line of the request. |
| Worker | Every job cycle (ADR-034 `PeriodicJobScheduler`, proof validation) and every outbox dispatch cycle (notifications and dispatch lanes in the Worker, the two realtime lanes in the API) runs in its own new root trace. Its id is in the cycle's lines. |
| AI-05 | `Problem` documents the optional `traceId` (`^[0-9a-f]{32}$`). The change is additive. The contract delta `OBS-003-W3C-CORRELATION` records it. This change adds no path, schema or internal reference (69, 125 and 651 on the merged base). |

Public tracking is unchanged. The `publicTracking` 404, 429 and 503 bodies and the tracking hub's bodies stay
uniform and never carry `traceId` (Invariant 5). AI-05 types those answers as `Problem`, where `traceId` is
optional, so they still conform.

## How it works

- `TraceCorrelation.ConfigureProcess()` (`src/BuildingBlocks/Paqueteria.Infrastructure/Observability/TraceCorrelation.cs`)
  is the first statement of both `Program.cs`.
  - It installs `NoTraceContextPropagator` as `DistributedContextPropagator.Current`. This propagator extracts and
    injects nothing. It must run first because `SocketsHttpHandler` captures the current propagator when it is
    built, and the Key Vault configuration source builds an Azure client while the host is still being created.
  - It adds one `ActivityListener` for the hosting source (`Microsoft.AspNetCore`) and the application source
    (`Paqueteria`) only. The listener samples `PropagationData`, so activities exist with W3C ids but record no
    tags, events or baggage. Without a listener, hosting creates an activity only while some logger is enabled at
    `Critical`, and `ActivitySource.StartActivity` returns null.
- `AddTraceCorrelation()` puts the same propagator in DI, where ASP.NET Core hosting resolves it to read inbound
  headers. It also decorates `ILoggerFactory` with `TraceCorrelatedLoggerFactory`, which appends `TraceId` to the
  structured state of every line written inside a W3C activity.
- `UseTraceCorrelation()` is the first API middleware. It sets `HttpContext.TraceIdentifier` to the trace id of the
  hosting activity, or of its own root activity if hosting started none. About 30 audit and problem call sites
  already read `TraceIdentifier` (endpoints, `TenantContextMiddleware`, the Identity problem writers), so they all
  use the trace id with no other change.
- `AddProblemDetailsTraceId()` sets `traceId` on every problem written through `IProblemDetailsService`, in two
  places.
  - `CustomizeProblemDetails` runs after the framework default, which would otherwise write the full `Activity.Id`
    (`00-…-…-00`) or the Kestrel id.
  - A decorator sets it before the inner service runs. That covers the fallback that `Results.Problem` uses when the
    request's `Accept` excludes JSON and no writer runs.
- Two middlewares write problems by hand. Both now add `traceId`: the tenant context middleware, and the realtime
  gate for the private hubs (operations, driver). The tracking hub keeps its uniform body.
- `AzureClientTracing.WithoutDistributedTracing()` handles the Azure SDK.
  - Azure.Core adds `traceparent` (and `tracestate`) from `Activity.Current` in its own pipeline, whatever the
    process propagator says.
  - `Diagnostics.IsDistributedTracingEnabled = false` does **not** stop it. It only turns off the SDK's own
    activities.
  - The managed identity credential sends its token requests through an MSAL pipeline that ignores custom policies.
  - Both facts are proved by `Obs003AzureClientTracingTests`. So every Azure client and credential options instance
    gets a transport wrapper that removes the five trace-context headers right before sending, and the SDK
    activities are turned off.
- Worker loops call `TraceCorrelation.StartJobCycle(job)` or `StartOutboxDispatchCycle(lane)` once per cycle. This
  clears any ambient activity and starts a new root.
  - Activity names are fixed operation names (`Paqueteria.JobCycle`, `Paqueteria.OutboxDispatchCycle`). The display
    name is the fixed job or lane name.
  - No tag, event, baggage or request data is ever set on an activity.
  - The OBS-002 lane heartbeat (4601) and the HTTP status summary (4603) summarize many cycles or requests, so they
    run outside any trace and carry no `TraceId`.

## Choices

- **`TraceIdentifier` instead of a new accessor.** Setting it once in the first middleware covers every existing
  reader: about 30 call sites, the Identity problem writers and the tenant activation audit. It also covers any
  future reader, with no risk of missing one. The hosting log scope still captures Kestrel's id as `RequestId`,
  because the scope is created before any middleware runs. It is never written: the API and Worker JSON console
  formatters write no scopes, and `Api_json_console_does_not_include_scopes` guards that for the API. The tests
  check that no line contains `RequestId` or `Scopes`, so a JSON line carries only one identifier.
- **`State.TraceId`, not a new formatter.** .NET 10's `JsonConsoleFormatter` has no option for trace ids, and
  writing activity ids as a scope would also turn on every other scope. A custom formatter would duplicate the
  framework's JSON writer. The decorator uses public APIs only, leaves `Message`, the template and every property
  unchanged, and adds one property. In KQL it is `parse_json(Log_s).State.TraceId`.
  - The trace id is a random correlation id, not an identifier of a person or an organization.
  - It is not an OBS-002 telemetry dimension, a template placeholder, or an alert or workbook property, so
    `TelemetryDimensions.Allowed` and its forbidden fragments are unchanged.
  - It adds about 50 bytes per line inside a trace.
- **A propagator that does nothing, not the runtime's `CreateNoOutputPropagator()`.** That one still extracts inbound
  context, and `CreatePassThroughPropagator()` injects.
- **Listener scope.** The listener does not observe `System.Net.Http`, Npgsql, SignalR or Azure sources. PR 6
  decides which dependency activities to create.
- **Idle cycles are cycles.** A dispatcher iteration that claims nothing still runs in its own trace, which costs
  nothing while nothing is recorded. PR 6 must not export idle cycles at 10 % sampling without a decision, because
  each lane polls every 250-500 ms.
- **Problem schemas.** Only `Problem` documents `traceId`, as the plan says. The other 15 problem schemas
  (`ForbiddenProblem` and the `*ConflictProblem` family) are standalone open objects with no `additionalProperties`
  or `allOf`. They carry the member at runtime, but their AI-05 text does not list it yet.

## Found, not changed here

- `TenantContextMiddleware` sets `application/problem+json`, then calls `WriteAsJsonAsync(value)`, which resets the
  type to `application/json; charset=utf-8`. Its 400, 401, 403 and 503 problems have always gone out as JSON. The
  body now carries `traceId`; the media type is left for a separate fix.
- The rate limiter's 429 on GPS ingestion and hub negotiate still has an empty body. PR 2 gives it the
  `TooManyRequests` problem with `traceId`.

## For later PRs

- **PR 6 (OpenTelemetry SDK).** Replace the listener in `TraceCorrelation.ConfigureProcess()` with the SDK's
  `TracerProvider` and its sampler.
  - Keep the propagator, the `TraceIdentifier` middleware, the problem `traceId`, the log enrichment (the logs stay
    on the console) and the Azure transport wrapper.
  - Keep `Microsoft.AspNetCore.SignalR.Server` unobserved unless hub-message trace context is ignored too.
  - Any request-path sink must be listed in `ReviewedRequestPathSinks`.
- **PR 7.** Decide on trace context through the outbox. Until then every cycle and message has its own trace, and
  correlation goes through the outbox id (already the SignalR `event_id`) and the aggregate.

## Tests

- `tests/Paqueteria.UnitTests/Operations/Obs003TraceCorrelationTests.cs`:
  - the propagator ignores and injects nothing;
  - the process configuration;
  - roots never continue an ambient trace;
  - the id format;
  - JSON console lines carry `State.TraceId` inside a trace and nothing outside;
  - two scheduler cycles and three dispatch-lane cycles each have their own id.
- `tests/Paqueteria.UnitTests/Security/Obs003AzureClientTracingTests.cs`:
  - the SDK default sends `traceparent` (the control);
  - the secrets reader, the PII key and cryptography clients, the Blob options, the data protection resolver and
    the managed identity credential send none while a sampled activity with trace state and baggage is current.
- `tests/Paqueteria.IntegrationTests/Observability/`:
  - `Obs003OutboundTraceHeadersTests`: real clients against a loopback Kestrel that records headers on the wire.
    - The control: the runtime's default propagator would inject.
    - Handlers built after startup send nothing.
    - From inside an API request that brought a sampled context, the Google Maps and Twilio named clients and the
      production AuthCenter back-channel send nothing.
    - Inside a Worker cycle, the WhatsApp and ACS named clients send nothing.
    - An Azure Blob request sends nothing on the wire.
  - `Obs003RequestCorrelationTests` (no database):
    - an inbound context is ignored;
    - the problem `traceId` is set with `Accept` JSON, HTML or absent;
    - every JSON line of the request carries its id, and nothing inbound, `RequestId` or `Scopes` reaches any line;
    - the private hub 401 carries `traceId`, and the tracking hub 404 is byte-for-byte uniform.
  - `Obs003TraceIdPostgreSqlTests` (PostgreSQL 18, `PublicTrackingPostgreSql` collection): a PLATFORM_ADMIN request
    has the same id in a JSON console line, in its `TENANT_CONTEXT_ACTIVATED` audit row and in its 400 problem.
    - The inbound context reaches none of them.
    - The tenant context problem carries `traceId`.
  - `Obs003CycleTraceTests`: in the real Worker composition, claim window finalization job cycles and dispatch lane
    cycles each have distinct ids. In the API composition, both realtime lanes do too. The database is unreachable,
    so every cycle logs.
- `tests/Paqueteria.ArchitectureTests/TraceCorrelationArchitectureTests.cs`:
  - both hosts configure correlation before building;
  - `UseTraceCorrelation` is the first middleware;
  - every Azure SDK client file is reviewed and strips trace context;
  - only the Azure transport names trace headers;
  - every `BackgroundService` is either a traced cycle loop or listed with a reason;
  - only `TraceCorrelation` starts activities, and nothing sets tags, events or baggage.
- `tests/Paqueteria.ContractTests/Obs003ProblemTraceIdContractTests.cs`:
  - the AI-05 `Problem.traceId` shape;
  - no problem schema is closed or requires it;
  - the implementation's id format matches the pattern;
  - the contract delta entry.

## Verify locally

```bash
dotnet build Paqueteria.sln -c Release
dotnet test tests/Paqueteria.UnitTests -c Release --no-build --filter "FullyQualifiedName~Obs003"
dotnet test tests/Paqueteria.ArchitectureTests -c Release --no-build --filter "FullyQualifiedName~TraceCorrelation"
dotnet test tests/Paqueteria.ContractTests -c Release --no-build --filter "FullyQualifiedName~Obs003"
# needs Docker for the PostgreSQL class
dotnet test tests/Paqueteria.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~Obs003"
```

To see it by hand, run the API with `ASPNETCORE_ENVIRONMENT=Development` and call any endpoint that answers a
problem, for example `GET /__tests/security/authenticated` with no credential, while sending a `traceparent`:

- the problem's `traceId` is a new 32-hex id, not the one you sent;
- the console lines of that request show the same value in `State.TraceId`.

## Rollback

Revert the commit. There is no schema, migration, role, grant, package or data change.

- Audit rows written in the meantime keep the W3C trace id in `request_id` (`text`). Older rows keep the Kestrel id.
  Both are opaque correlation values.
- The AI-05 member is optional, so reverting it breaks no client: the web already accepts `traceId` as an optional
  string.
