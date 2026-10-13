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
| One identifier | The 32 lowercase hex W3C trace id is `HttpContext.TraceIdentifier`, the `traceId` of every `application/problem+json`, `State.TraceId` in every JSON console line of the request and the `request_id` of every `platform.audit_logs` row written while handling the request, the Twilio voice webhooks included. The exceptions are listed in "Audit rows without the trace id". |
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
- The Twilio voice webhooks (call status, driver key press, recipient key press) pass `httpContext.TraceIdentifier`
  in `VoiceCallStatusReport.RequestId` and `VoiceKeypress.RequestId`, so their audit rows
  (`RECIPIENT_CALL_STATUS_REPORTED`, `RECIPIENT_CALL_DRIVER_CONFIRMED`, `RECIPIENT_CALL_NOT_CONFIRMED`,
  `RECIPIENT_CALL_BRIDGE_FAILED`, `RECIPIENT_CALL_BRIDGED`) carry the trace id. Before this they wrote `NULL`.
- `AzureClientTracing.WithoutDistributedTracing()` handles the Azure SDK.
  - Azure.Core adds `traceparent` (and `tracestate`) from `Activity.Current` in its own pipeline, whatever the
    process propagator says.
  - `Diagnostics.IsDistributedTracingEnabled = false` does **not** stop it. It only turns off the SDK's own
    activities.
  - The managed identity credential sends its token requests through an MSAL pipeline that ignores custom policies.
  - Both facts are proved by `Obs003AzureClientTracingTests`. So every Azure client and credential options instance
    gets a transport wrapper that removes the five trace-context headers right before sending, and the SDK
    activities are turned off.
  - Each client is built in one place: the secret reader and PII key wrap constructors, the Blob gateway
    constructor, `AzureWorkloadCredential.Create` and `DataProtectionKeyEncryptionKeyResolver.CreateKeyVault` (the
    data protection registration calls it). Each has an internal overload that takes a test transport, so the tests
    drive the production construction itself.
  - `AzureClientTracingGuard` (architecture tests) reads the source one construction at a time. Every file that uses
    an Azure SDK namespace (any `Azure.*` but `Azure.Core`, or `Microsoft.Extensions.Azure`) or an extension that
    builds Azure clients must be reviewed. In those files, every client, credential or key resolver construction,
    explicit or target-typed, must pass options through `WithoutDistributedTracing()`: directly, through a helper
    whose every return goes through it, or through a variable only ever assigned that way. A target-typed `new(`
    whose type the statement does not name is refused. Anywhere in `src`, `ProtectKeysWithAzureKeyVault` must take
    the reviewed `DataProtectionKeyEncryptionKeyResolver`, and `PersistKeysToAzureBlobStorage`, `AddAzureClients`,
    `AddAzureKeyVault` and `HttpPipelineBuilder.Build` must be routed through the wrapper; their credential overloads
    build their own unwrapped clients. No global using (in code or in a project file) may import an Azure SDK
    namespace. The project does not reference Roslyn, so the guard uses regular expressions;
    `AzureClientTracingGuardTests` shows it rejects each known bypass as a fixture source.
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
  formatters write no scopes, and `Api_and_worker_json_consoles_do_not_include_scopes` guards the configuration of
  both hosts. The OBS-003 tests capture lines with the host's own JSON console formatter and the host's scope
  provider, so their check that no line contains `RequestId` or `Scopes` would fail if the console wrote scopes
  (`Scopes_would_be_seen_if_the_console_wrote_them` is the control). A JSON line carries only one identifier.
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

## Audit rows without the trace id

Every audit row written while handling an API request carries the request's trace id in `request_id`, the voice
webhooks included. These rows do not, on purpose or for now:

- **Worker writers.** `ProofValidationProcessor` (`custody.proof_upload_session.ready` and `.rejected`) and
  `PostgreSqlAssignmentLifecycleReactor` (the assignment lifecycle audit) write `request_id = NULL`. They run in a
  Worker cycle, not in a request, and the cycle's trace id is not the trace of the request that produced the
  message. Linking a message to its producer's trace is PR 7's decision (column, payload or none), so they stay
  `NULL` until then.
- **`PENDING_MEMBERSHIP_ACCEPTED` (REG-002).** `security.apply_pending_memberships(text,bytea[],integer[])` writes it
  during the AuthCenter sign-in callback, which is an API request, with `request_id = NULL`: the SECURITY DEFINER
  function takes no request id. Passing one changes the function's signature (a migration, AI-06 and AI-18), so it
  is left for a separate change.
- **The master data loader** (`MASTER_DATA_LOADED`) is a command-line tool, not a request; it writes `mdm-001`.

AI-05 (`Problem.traceId` and the `OBS-003-W3C-CORRELATION` contract delta) says exactly this.

## Found, not changed here

- `TenantContextMiddleware` set `application/problem+json`, then called `WriteAsJsonAsync(value)`, which reset the
  type to `application/json; charset=utf-8`; the public tracking 429 writer did the same. #244 fixed both (they pass
  the media type to `WriteAsJsonAsync`); merged with this PR, the tenant context problem carries `traceId` and goes
  out as `application/problem+json`, and the public tracking 429 stays uniform without `traceId`.
- The rate limiter's 429 on GPS ingestion and hub negotiate still has an empty body. PR 2 gives it the
  `TooManyRequests` problem with `traceId`.
- **SignalR long polling.** A private hub connection over long polling spans several HTTP requests: negotiate, the
  poll that opens the connection, the sends and later polls. The PLATFORM_ADMIN `TENANT_CONTEXT_ACTIVATED` audit on
  the operations hub takes its `request_id` from the request the realtime gate saw, which the connection keeps as
  its `HttpContext`. The hub's own log lines for the same connection can be written in the trace of another
  request of that connection, so the audit id and those lines can disagree. Over WebSockets (one request) they
  agree. No code change.

## For later PRs

- **PR 6 (OpenTelemetry SDK).** Done in `obs-003-appinsights-export.md`. The listener was kept next to the SDK's
  `TracerProvider` (the logs and problems need a trace id also for the 90 % of traces the sampler drops), the
  OpenTelemetry propagator is a no-op whether the export is on (set to an empty composite) or off (the SDK is never
  loaded, so the API's no-op default stays), and only scheduled job cycles and requests
  are exported (Q-PR6-1). The original plan was:
  Replace the listener in `TraceCorrelation.ConfigureProcess()` with the SDK's
  `TracerProvider` and its sampler.
  - Keep the propagator, the `TraceIdentifier` middleware, the problem `traceId`, the log enrichment (the logs stay
    on the console) and the Azure transport wrapper.
  - The OpenTelemetry ASP.NET Core instrumentation extracts inbound `traceparent` and `baggage` through
    OpenTelemetry's own `Propagators.DefaultTextMapPropagator`, not through `DistributedContextPropagator`. PR 6 must
    set a no-op OpenTelemetry propagator and re-run the inbound tests (`Obs003RequestCorrelationTests`,
    `Obs003TraceIdPostgreSqlTests`).
  - `State.TraceId` will appear next to OpenTelemetry's own trace id (the trace id of an exported log record, or
    the operation id Application Insights shows). Both come from the same activity, so they are equal; the KQL of
    the alerts and the workbook should name the one it uses.
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
  - the secrets reader, the PII key and cryptography clients, the Blob gateway, the data protection resolver and
    the managed identity credential send none while a sampled activity with trace state and baggage is current;
  - each is built by its production constructor or factory (`AzureBlobProofGateway`,
    `DataProtectionKeyEncryptionKeyResolver.CreateKeyVault`, `AzureWorkloadCredential.Create`), with a test transport
    as the only change. Reverting the gateway's constructor to `new BlobClientOptions()` fails it: the transport is
    lost and no request is captured.
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
    - every JSON line of the request, found by its hosting request scope rather than by its `TraceId`, carries its
      id, and nothing inbound, `RequestId` or `Scopes` reaches any line. The lines are formatted by the API's own
      JSON console formatter with the host's scopes; `Scopes_would_be_seen_if_the_console_wrote_them` is the
      control with `IncludeScopes` on;
    - the private hub 401 carries `traceId`, and the tracking hub 404 is byte-for-byte uniform.
  - `Obs003TraceIdPostgreSqlTests` (PostgreSQL 18, `PublicTrackingPostgreSql` collection): a PLATFORM_ADMIN request
    has the same id in a JSON console line, in its `TENANT_CONTEXT_ACTIVATED` audit row and in its 400 problem.
    - The inbound context reaches none of them.
    - The tenant context problem carries `traceId`.
  - `Obs003CycleTraceTests`: in the real Worker composition, claim window finalization job cycles and dispatch lane
    cycles each have distinct ids. In the API composition, both realtime lanes do too. The database is unreachable,
    so every cycle logs.
  - `DriverVoiceHttpTests`: the status callback and both key-press webhooks pass a 32-hex trace id to the service,
    a different one per request.
- `tests/Paqueteria.ContractTests/PostgreSql/DriverVoiceBridgePostgreSqlContractTests.cs`
  (`Each_webhook_audit_row_carries_the_trace_id_of_its_request`, PostgreSQL 18): the production webhook routes on a
  loopback Kestrel behind `UseTraceCorrelation`, over the real call service. The `RECIPIENT_CALL_DRIVER_CONFIRMED`,
  `RECIPIENT_CALL_BRIDGED` and `RECIPIENT_CALL_STATUS_REPORTED` rows carry, in `request_id`, the `TraceId` of their
  own request's hosting log line; the three ids differ.
- `tests/Paqueteria.ArchitectureTests/TraceCorrelationArchitectureTests.cs`:
  - both hosts configure correlation before building;
  - `UseTraceCorrelation` is the first middleware;
  - every file that uses an Azure SDK namespace is reviewed, and every Azure client, credential or key resolver
    construction and every guarded extension goes through the wrapper (`AzureClientTracingGuard`);
  - only the Azure transport names trace headers;
  - every `BackgroundService` is either a traced cycle loop or listed with a reason;
  - only `TraceCorrelation` starts activities (`new Activity(` included), nothing sets tags, events or baggage,
    and only it sets a display name, on any receiver.
- `tests/Paqueteria.ArchitectureTests/AzureClientTracingGuardTests.cs`: the guard rejects, as fixture sources, a
  target-typed `new(` (field, assignment and an unnamed type), `EmailClient`, `ServiceBusClient`, `QueueClient`,
  `ClientSecretCredential`, `AzureCliCredential`, a second unwrapped construction in a reviewed file, a route only a
  nested credential takes, a helper that skips the wrapper on one return, `AddAzureClients`, the credential overloads
  of `ProtectKeysWithAzureKeyVault` and `PersistKeysToAzureBlobStorage`, `HttpPipelineBuilder.Build`, an unreviewed
  namespace, a fully qualified client, a global using and a project-wide `<Using>`; the production patterns pass.
- `tests/Paqueteria.ArchitectureTests/TrackingTokenLogRedactionArchitectureTests.cs`:
  `Api_and_worker_json_consoles_do_not_include_scopes` covers both hosts' `Program.cs` and `appsettings*.json`.
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
dotnet test tests/Paqueteria.ArchitectureTests -c Release --no-build --filter "FullyQualifiedName~AzureClientTracingGuard"
dotnet test tests/Paqueteria.ContractTests -c Release --no-build --filter "FullyQualifiedName~Obs003"
# needs Docker for the PostgreSQL classes
dotnet test tests/Paqueteria.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~Obs003|FullyQualifiedName~DriverVoiceHttpTests"
dotnet test tests/Paqueteria.ContractTests -c Release --no-build --filter "FullyQualifiedName~Each_webhook_audit_row_carries"
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
