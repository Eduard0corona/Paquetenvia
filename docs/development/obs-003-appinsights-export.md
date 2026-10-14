# OBS-003 PR 6: export of traces and metrics to Application Insights

Phase 4 PR 6 (`docs/development/phase4-platform-plan.md`, "PR 6: exportación a Application Insights (OBS-003)"),
backlog item **OBS-003** in `AI-08_BACKLOG.yaml`. Decision row: `OBS-003-APPINSIGHTS-EXPORT-2026-10-13`. It applies
`OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12` ("Application Insights (Recommended)") and
`OBS-003-TENANT-PSEUDONYM-2026-10-12` ("Seudónimo de empresa (Recommended)"), plus the two owner answers of
2026-10-13 that this PR needed:

- **Q-PR6-1**, which cycles become traces: "Solo tareas programadas (Recommended)".
- **Q-PR6-2**, how the hosts authenticate the export: "Llave en la conexión (Recommended)".

No package was added, upgraded or removed. Only the admitted `Azure.Monitor.OpenTelemetry.Exporter` 1.10.0 and
`OpenTelemetry.Extensions.Hosting` 1.19.1 (PR 5, #247) are used, and only by the two hosts. No instrumentation package
exists, so only the built-in `System.Diagnostics` sources and meters are exported.

## What changed

| Area | Behaviour |
| --- | --- |
| Export | Traces and metrics of the API and the Worker go to a workspace-based Application Insights through the official exporter. It is **off by default**: with it off nothing is registered, so no exporter exists and nothing leaves the process. Logs stay on the console for Log Analytics (OBS-002); there is no log exporter. |
| Sampling | 10 % of traces, decided once per trace from its trace id: `ParentBasedSampler(TraceIdRatioBasedSampler(0.10))`, with a filter in front of the root sampler (see "Sampler"). |
| Traces | API requests, except `/health/*` probes, with their outgoing HTTP calls. ADR-034 scheduled job cycles (claim-window finalization, auto-close, outbox retention, cleanups), with their outgoing HTTP calls. Never outbox dispatch cycles, the proof-validation poll, database spans or spans without an exported root. |
| Span attributes | An allowlist enforced when each span ends (`TelemetryExportPolicy.PrepareForExport`). Anything else is removed. A span with events, links or baggage is not exported at all. |
| Metrics | The application meters (`Paquetenvia.*`, `Paqueteria.*`) and every built-in ASP.NET Core meter (`Microsoft.AspNetCore.*`), `aspnetcore.rate_limiting.requests` included. A view keeps only the OBS-002 metric allowlist. |
| Organization pseudonym | An HMAC of the organization id under a Key Vault key, `v{key version}-<32 hex>`. It goes with the actor role kind on the log lines and the request span of a request that has an organization context. It is never a metric tag, an OBS-002 dimension, or an alert or workbook dimension. The API and the Worker refuse to start without the key outside Development and Testing. |
| Propagation | Still a no-op. With the export off the OpenTelemetry SDK is never loaded, so its default propagator stays the API's no-op one; with it on, it is set to one that extracts and injects nothing. The process propagator, the propagator hosting uses and the Azure transport wrapper of PR 1 are unchanged. |
| Bicep | `appi-pv-pilot-<suffix>` (workspace-based, pilot region, pilot workspace) in `platform.bicep`. The connection string, `TelemetryExport__Enabled='false'` and the pseudonym secret mapping go to the API and the Worker in `apps.bicep`. |
| Alerts | None added. `alertEmailAddress` is still pending from the owner, and every new alert needs its own decision. |

## Configuration and how to turn the export on

`TelemetryExport` (`TelemetryExportSettings`, read while the host composes) fails closed:

| Key | Values | Default |
| --- | --- | --- |
| `TelemetryExport:Enabled` | `true` or `false` | `false` |
| `TelemetryExport:ConnectionString` | the Application Insights connection string: `InstrumentationKey` (GUID) and an https `IngestionEndpoint` on `*.applicationinsights.azure.com` (optional `LiveEndpoint`, `ApplicationId`) | none |

The host refuses to start in four cases:

- an unknown key, for example a `SamplingRatio` (the 10 % is fixed in code);
- a value that is not a boolean;
- `Enabled` without a connection string;
- `Enabled` with a connection string of any other shape, including one with a key outside the four above
  (`EndpointSuffix`, `Authorization` or `AADAudience` would change where or how the exporter sends).

With the export off, the connection string is neither checked nor kept: nothing reads it, and a key Azure may add to
the component's string must not stop a host that exports nothing. The error message names the setting, never its
value.

`TelemetryPseudonym` (`TelemetryPseudonymOptions`, validated on start):

| Key | Values |
| --- | --- |
| `TelemetryPseudonym:CurrentKeyVersion` | 1 to 32767 (0 is the synthetic key) |
| `TelemetryPseudonym:Keys:{version}` | exactly one key, the current one: Base64 of 32 to 128 random bytes |

In the pilot, `apps.bicep` sets `TelemetryExport__Enabled='false'`, `TelemetryExport__ConnectionString` from the
component and `TelemetryPseudonym__CurrentKeyVersion='1'`. Both hosts map `paquetenvia-telemetry-pseudonym-key-1` to
`TelemetryPseudonym:Keys:1` through `KeyVaultSecrets:Mappings` (PILOT-KEYVAULT-PRIVATE-APP-READ).

The guards keep these values reviewed:

- P17 refuses any `TelemetryExport__` or `TelemetryPseudonym__` setting in `apps.settings.json`;
- P21 requires the switch to be a literal and the connection string to come from the pilot component.

**To turn the export on**, after the owner created the pseudonym secret (see "Owner's deploy actions") and after one
week of pilot, once the daily log volume against the 0.3 GB cap has been measured
(`OBS-003-EXPORT-AFTER-PILOT-WEEK-2026-10-14`), change the one literal `TelemetryExport__Enabled` from `'false'` to
`'true'` in `deploy/azure/pilot/apps.bicep`. The change goes
through the usual PR, promotion to `main` and pilot deploy; the new revisions start exporting. Locally, set
`TelemetryExport__Enabled=true` and a connection string as environment variables. The hosts read the section while
they compose, so in a `WebApplicationFactory` test pass it with `UseSetting`.

## How it works

- `src/Paqueteria.Api/Observability/AzureMonitorTelemetryExport.cs` is the only OpenTelemetry wiring. The Worker
  links the same file (`Paqueteria.Worker.csproj`), because only the two hosts reference the packages.
- `AddAzureMonitorTelemetryExport(profile)`:
  1. reads and validates `TelemetryExport`;
  2. with the export off, returns: the OpenTelemetry SDK is never loaded (no SDK self-diagnostics, no change to
     `Activity` defaults), and PR 1's no-op propagators already cover the process;
  3. with the export on, sets the OpenTelemetry default propagator to an empty composite (loading the SDK would
     otherwise make it W3C trace context and baggage) and registers `AddOpenTelemetry()` with `WithTracing` and
     `WithMetrics`.

  `TelemetryExportProfile.Api` listens to `Microsoft.AspNetCore`, `System.Net.Http` and `Paqueteria`.
  `TelemetryExportProfile.Worker` does not listen to the hosting source, because its only requests are the probes.
- The exporter classes (`AzureMonitorTraceExporter`, `AzureMonitorMetricExporter`, public since exporter 1.7) are used
  directly, behind a `BatchActivityExportProcessor` and a 60 s `PeriodicExportingMetricReader` with delta
  temporality. `AddAzureMonitorTraceExporter` is not used: in its deferred setup it replaces any sampler with its own
  and adds standard-metrics extraction.
- `CreateExporterOptions` sets every option itself, last, so neither the environment
  (`APPLICATIONINSIGHTS_CONNECTION_STRING`, `OTEL_TRACES_SAMPLER`) nor the `AzureMonitorExporter` configuration section
  changes them:
  - the connection string from `TelemetryExport` and no credential;
  - `SamplingRatio = 0.1`, so every exported item carries `sampleRate` 10 and Application Insights scales its counts;
  - `TracesPerSecond = null`;
  - Live Metrics, standard metrics, performance counters and the trace-based log sampler off;
  - `DisableOfflineStorage = true`.

  The options go through `AzureClientTracing.WithoutDistributedTracing()`, so the exporter's own ingestion requests
  carry no trace headers either.
- Three exporter features are switched off through the documented environment switches, set in code before the first
  exporter exists:
  - Statsbeat (`APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true`), the exporter's own telemetry to a Microsoft-owned
    resource;
  - customer SDK stats (`APPLICATIONINSIGHTS_SDKSTATS_DISABLED=true`), on by default since exporter 1.8.3: counts of
    the exporter's own items, whose first transmission also asks the Azure instance metadata endpoint
    (`http://169.254.169.254/metadata/instance/compute`) for the compute type;
  - the resource metric (`OTEL_DOTNET_AZURE_MONITOR_ENABLE_RESOURCE_METRICS=false`), one extra `_OTELRESOURCE_` item
    per trace batch.

  The only destination is the configured Application Insights ingestion endpoint, and it receives only the spans and
  metrics described here. The integration test records every request a real HTTP handler starts in the test process
  and fails on any that leaves loopback; without the SDK stats switch it catches the metadata request.
- `RequestSpanEnrichment.UseRequestSpanEnrichment()` is the second API middleware, right after `UseTraceCorrelation`.
  For a sampled request it writes the allowlisted request attributes, which ASP.NET Core 10 hosting does not write
  itself. A `/health/*` probe is taken out of the export together with its children.
- `TelemetryOrganizationContextMiddleware.UseTelemetryOrganizationContext()` runs right after the tenant context
  middleware.
- The PR 1 propagation-only listener stays. A trace the sampler drops still has its W3C id in the logs, the audit row
  and the problem `traceId`.

### Sampler

`AzureMonitorTelemetryExport.CreateSampler()`:

```text
ParentBasedSampler(
  root: ExportedRootSampler(TraceIdRatioBasedSampler(0.10)),
  remote parent (sampled or not): AlwaysOff,
  local parent sampled: AlwaysOn, local parent not sampled: AlwaysOff)
```

Why this one:

- The decision is a pure function of the trace id (`TraceIdRatioBasedSampler`), taken once at the root. Children
  follow their parent (`ParentBasedSampler`). A trace is therefore exported whole or not at all, and a test can tell in
  advance which trace ids are exported.
- Inbound context is ignored (PR 1), so every request is a root. A remote parent is dropped whatever its flag says, so
  a browser that sends a sampled `traceparent` cannot force an export even if propagation were turned on by mistake.
- The root filter (`TelemetryExportPolicy.IsExportedRoot`) lets only two kinds of root reach the ratio sampler:
  - a hosting request (`Microsoft.AspNetCore.Hosting.HttpRequestIn`, server kind);
  - an ADR-034 scheduled job cycle (`Paqueteria.JobCycle`).

  Outbox dispatch cycles (`Paqueteria.OutboxDispatchCycle`) are dropped by this filter (Q-PR6-1). PR 7
  (`obs-003-outbox-trace-context.md`) runs each claimed message in a child of the trace that wrote it, so a message is
  exported only when that trace was sampled; the cycles themselves stay dropped. So are polls (`Paqueteria.PollCycle`, now used by proof validation instead of `StartJobCycle`) and the
  fallback request activity. A parentless HTTP or database span is dropped too.
- The exporter's own `ApplicationInsightsSampler` would decide from a hash of the same trace id but keep the dropped
  90 % as "record only". That only helps standard metrics, which are off.

### Span attributes: the allowlist

ASP.NET Core 10 hosting writes **no** attribute on its request activity: no method, path, route or status. This was
checked with a recording listener on the hosting source.

The request span therefore carries only what `RequestSpanEnrichment` writes. The organization middleware adds two more
for a request with an organization context.

| Source | Attributes kept | Name |
| --- | --- | --- |
| `Microsoft.AspNetCore` (request, server) | `http.request.method` (known methods or `_OTHER`), `http.route` (the route template, never the path), `http.response.status_code` (100–599), `paqueteria.organization.pseudonym`, `paqueteria.actor.role_kind` | `{method} {route}` |
| `System.Net.Http` (outgoing call, client) | `http.request.method`, `http.response.status_code`, `server.address`, `server.port`, `error.type` | `{method}` |
| `Paqueteria` (`Paqueteria.JobCycle` only) | none | the fixed job name |
| anything else | not exported | |

What the sources emit by default, all checked with a recording listener:

| Source | Emits | What the allowlist does with it |
| --- | --- | --- |
| `System.Net.Http` | `url.full` (path kept, query redacted to `*`), the method, `server.address`, `server.port`, the status, `network.protocol.version` and `error.type`; a link to the connection-setup span when the experimental connection sources are listened to | `url.full` is removed; the experimental sources are not listened to |
| `Npgsql` (10.0.3) | a `CONNECT {db}` span and one `postgresql` span per command. Tags: `db.query.text` (the SQL), `db.npgsql.data_source` (host, port, database and user name), `db.namespace`, `server.*`, a connection id. One event per command (first response), and exception events on errors | not exported at all (see "Choices") |

The rules apply to any span:

- Every value is checked against the shape expected for its key. A route with a space, a method like `GET<script>`, or
  an organization id in the pseudonym attribute is removed.
- The status description is cleared.
- The span name is rebuilt from allowed values.
- A span carrying an event, a link or baggage is not exported, because an `Activity` cannot drop them. An exception
  event can carry a message with personal data.
- Never exported: the URL path or query, headers, cookies, bodies, the client address, the user agent, the user or
  subject id, the organization id, SQL text or parameters, tracking tokens.

### Metrics

`AddPaqueteriaMetrics` adds:

- the application meters, both prefixes in use;
- every built-in ASP.NET Core meter: hosting, Kestrel, routing, diagnostics, rate limiting, SignalR connections,
  authentication, authorization and the memory pool.

The `System.Net.Http`, `Npgsql` and `System.Runtime` meters are not exported. Their tags carry `network.peer.address`
and the connection string without its password.

A view on every instrument sets `TagKeys` to `TelemetryExportPolicy.MetricTagKeys`, the OBS-002 metric allowlist
(`ObservabilityArchitectureTests.MetricTags`):

- **Application tags:** `lane`, `mode`, `outcome`, `error_class`, `job`, `audience`, `event_type`, `event.type`,
  `policy`, `provider`, `proof_type`, `reason_code`, `auth.kind`, `hub`, `category`, `stage`, `publication_decision`,
  `rejection_code`.
- **ASP.NET Core tags:**
  - hosting: `http.request.method`, `http.response.status_code`, `http.route`, `url.scheme`,
    `network.protocol.version`, `error.type`, `aspnetcore.request.is_unhandled`;
  - routing: `aspnetcore.routing.match_status`, `aspnetcore.routing.is_fallback`;
  - diagnostics: `aspnetcore.diagnostics.handler.type`, `aspnetcore.diagnostics.exception.result`;
  - rate limiting: `aspnetcore.rate_limiting.policy` and `aspnetcore.rate_limiting.result`, the policy name and the
    result of `aspnetcore.rate_limiting.requests`;
  - SignalR: `signalr.connection.status`, `signalr.transport`;
  - authentication and authorization: `aspnetcore.authentication.scheme`, `aspnetcore.authentication.result`,
    `aspnetcore.authorization.policy`, `aspnetcore.authorization.result`;
  - memory pool and Kestrel: `aspnetcore.memory_pool.owner`, `tls.protocol.version`.

Every other tag is dropped. That includes Kestrel's `server.address` and `server.port`, `network.protocol.name`,
`network.type` and `user.is_authenticated`.

No metric tag carries the organization pseudonym, the actor role type, a subject, an IP address or a partition key:

- the forbidden-fragment rule of the OBS-002 test now also refuses `pseudonym`, `role`, `subject`, `client` and
  `partition`, and the words `ip`, `peer`, `user` and `sub`;
- the rate-limit metrics carry the registry policy name and the limiter result only.

## Organization pseudonym

- **What it is:**
  `v{key version}-` followed by the first 16 bytes of
  `HMAC-SHA256(key, "paquetenvia.telemetry.organization-pseudonym.v1\n" + organization id in lowercase "D" form)`,
  as lowercase hex.
  - It is never the organization id, and the id cannot be recovered without the key.
  - Fixed vectors:
    - the key of bytes 0x00–0x1f, version 1, organization `00000000-0000-0000-0000-000000000001` gives
      `v1-92d9e53b9ba938cafe99ce9cbd949926`;
    - the synthetic key gives `v0-a7d07edf91e714c657196d1683f30a96`.
- **Actor role kind:** the actor's roles in the selected organization, as their contract values, sorted and comma
  separated (for example `DISPATCHER,FINANCE`). Never the user, the subject or the e-mail. On the Worker's lines it is
  the fixed value `SYSTEM` (`OBS-003-WORKER-PSEUDONYM-2026-10-14`, "SYSTEM (Recommended)").
- **Where it goes:** only where there is an organization context, which is a request that passed
  `TenantContextMiddleware` with a selected organization (`X-Organization-Id`), or, since
  `OBS-003-WORKER-PSEUDONYM-2026-10-14`, one Worker outbox message or one photo with valid signed metadata (log lines
  only; see below). Two places:
  - the JSON console lines of the rest of the request, as `State.OrganizationPseudonym` and `State.ActorRoleKind`,
    also when the export is off;
  - the request span, when sampled, as `paqueteria.organization.pseudonym` and `paqueteria.actor.role_kind`.

  Requests without a selected organization carry neither: public tracking, sign-in and the probes. The Worker's cycle
  lines carry neither either, because its cycles span many organizations. Since `OBS-003-WORKER-PSEUDONYM-2026-10-14`
  (`obs-003-worker-correlation.md`) the lines of each claimed outbox message of the two Worker lanes carry the
  pseudonym of the message's owner organization with the role kind `SYSTEM`, and so does proof validation's per-photo
  line when the photo's signed metadata is valid. The scheduled jobs and the API realtime lanes carry neither.
- **Where it never goes:** a metric tag, an OBS-002 event or `TelemetryDimensions.Allowed`, an alert, a
  workbook.
  - The log enrichment skips every event an alert or the workbook reads or may read
    (`TelemetryEvents.IsObservabilityEvent`): the reserved block 4600–4699, where every OBS-002 event takes its id,
    and the OPS-004 retention result 4004. So an OBS-002 event written inside a request with an organization keeps
    only its dimensions (and its trace id, which is not one).
  - Each rule is checked by a test:
    - `ObservabilityArchitectureTests.The_pseudonym_and_role_kind_never_reach_metrics_obs002_events_or_alert_queries`;
    - `Obs002_events_carry_exactly_the_telemetry_dimensions` (every `TelemetryEvents` id is in the block);
    - `Pilot_alerts_query_only_obs002_events_and_dimensions` (every event a query reads is skipped);
    - the unit test `Observability_events_never_carry_the_pseudonym_or_role_kind`;
    - guard P21 checks the queries.
- **The key:**
  - Development and Testing use a synthetic key when none is configured: SHA-256 of a public constant, version 0, so
    `v0-` marks synthetic pseudonyms.
  - Every other environment refuses to start without a valid key, because the logs carry the pseudonym even with
    the export off.
  - Integration tests that run a host as Production, Staging or DevSynthetic get a key derived at run time
    (`TelemetryPseudonymTestKey`); no key-like value is in the repository.
- **Versioning and rotation:** every pseudonym carries the key version. To rotate:
  1. the owner creates `paquetenvia-telemetry-pseudonym-key-2` (README §6.6 command, with the new name);
  2. one reviewed PR changes, together:
     - `deploy/azure/pilot/apps.bicep`: the secret name in `apiSecretNames` and `workerSecretNames`, the two
       `KeyVaultSecrets__Mappings` entries (to `TelemetryPseudonym:Keys:2`) and `TelemetryPseudonym__CurrentKeyVersion='2'`;
     - `tools/azr-001/env001_pilot_guards.py`: `TELEMETRY_PSEUDONYM_KEY_VERSION = "2"`. The P06 mappings, the P21
       version check and the P23 exemption derive from it;
     - `.github/workflows/deploy-azure-pilot.yml`: the `has_secret paquetenvia-telemetry-pseudonym-key-2` check in
       window 2, which P21 requires by name;
     - the tests that name version 1: `tools/azr-001/test_env001_pilot_guards.py` and
       `PilotApiStartupConfigurationTests`.

     Version 1 is removed in the same PR. Exactly one key is configured.

  Pseudonyms written before and after the rotation do not match, which is the point of rotating: a leaked key stops
  linking new telemetry to old. Keep the old secret until the logs that carry its version are past retention (30
  days).

## Cost and the daily cap

The component is workspace-based on the pilot workspace. Its data is billed as Log Analytics ingestion and counts
against the same daily cap (`logDailyQuotaGb`, 0.3 GB/day), so the worst case of the budget does not grow
(PILOT-BUDGET-ACCEPT-98-108). These are estimates; they assume about 0.5 KB per item and 2.53 USD per GB past the free
5 GB.

| Signal | Volume | GB/month |
| --- | --- | --- |
| Request traces (API, 10 %) | a few hundred to a few thousand items a day at pilot traffic | < 0.1 |
| Scheduled job traces (Worker, 10 %) | about 3,400 cycles a day, so about 340 traces plus their HTTP children | < 0.01 |
| Metrics | about 40–70 streams per minute in both hosts at idle (up-down counters such as active requests, connections and rate-limit leases, which export every minute once used, plus the probes' request metrics); more during traffic | about 1–2 |

The total is about 1–2 GB a month, that is 0–5 USD depending on how much of the free 5 GB the logs leave. This matches
the decided "Aprox. 0–5 USD/mes".

At idle the metrics use roughly 10–20 % of the 0.3 GB daily cap. When the cap is reached, ingestion stops for the rest
of the day, also for the logs the OBS-002 alerts read. Watch `_LogOperation` and the workspace usage in the first
weeks after the export is turned on. If metrics crowd the cap, a later decision can narrow the meters or lengthen the
60 s interval.

## Owner's deploy actions

1. **Create the pseudonym secret before the next deploy.** The command is in
   `docs/operations/env-001-pilot/README.md` §6.6; it writes 32 random bytes in Base64, generated in place and never
   shown. The workflow stops in window 2 while it is missing (`has_secret paquetenvia-telemetry-pseudonym-key-1`), and
   without it the API and the Worker do not start.
2. **Deploy as usual.** Stage 2 creates the Application Insights component. Stage 4 passes its connection string with
   `TelemetryExport__Enabled='false'`, so nothing is exported yet. The logs already carry the pseudonym.
3. **Turn the export on after one week of pilot** (`OBS-003-EXPORT-AFTER-PILOT-WEEK-2026-10-14`, owner: "Tras una
   semana de piloto (Recommended)"). First deploy without exporting and measure, for one week, how much of the 0.3 GB
   daily cap the logs use (`_LogOperation` and the workspace usage). With that number, a reviewed PR changes the one
   literal in `apps.bicep` to `'true'`. Promote it and deploy. Check in Application Insights, under transaction search,
   that requests show the route template only.
4. **No alert.** Nothing reads Application Insights in an alert or the workbook. Any alert on exported data needs its
   own owner decision and `alertEmailAddress`.

## Choices not written in the task

- **One wiring file in two hosts.** Only the hosts reference the OpenTelemetry packages, so
  `AzureMonitorTelemetryExport.cs` lives in the API project and the Worker compiles it through a `<Compile Link>`.
  Everything that needs no package lives in `Paqueteria.Infrastructure/Observability`: the policy, the settings, the
  pseudonym and the request enrichment.
- **The real exporter in tests.** The integration tests do not use an in-memory exporter package (none is admitted).
  They register a keyed `HttpPipelineTransport` (`AzureMonitorTelemetryExport.TransportServiceKey`) that keeps every
  ingestion request in memory, so they see exactly what would reach Application Insights. Production registers no
  such transport. The trace pipeline test uses a small collecting exporter behind the same sampler and allowlist.
- **No database spans.** The `Npgsql` source is not exported, for two reasons:
  - every command span has an event that an `Activity` cannot drop, so the allowlist would have to drop the span
    anyway;
  - its tags carry the SQL and the data source string.

  If database spans are ever wanted, `ConfigureTracing` belongs once per role in `RuntimeDataSource` (SCL-007, one
  pool per role), behind its own decision.
- **Probes.** `/health/*` requests are not traced, as OBS-002 already excludes them from the HTTP summary. The Worker
  does not listen to the hosting source at all.
- **Proof validation is a poll.** It now starts `TraceCorrelation.StartPollCycle` (`Paqueteria.PollCycle`) instead of
  `StartJobCycle`, so the sampler can tell it from an ADR-034 job at the root (Q-PR6-1). The PR 1 guard lists it
  under the new call.
- **Exporter extras off.** Customer SDK stats would send the exporter's own counts and, on the first transmission,
  call the Azure instance metadata endpoint. Live Metrics would send unsampled documents to a second endpoint.
  Statsbeat goes to a Microsoft-owned resource. Standard metrics and performance counters are outside the decided
  meters. The resource metric adds an item per batch. Local retry storage writes telemetry to disk. All seven are off.
- **The OpenTelemetry default propagator** is set to an empty composite only when the export is on, because loading
  the SDK makes it W3C trace context and baggage. With the export off the SDK is never loaded, so off means no
  OpenTelemetry SDK at all, and its API default stays a no-op. Nothing uses it today, but an instrumentation library
  would.
- **Connection string checked only when the export is on.** Off, nothing reads it, so it is not validated: a key Azure
  adds to the component's string cannot stop the hosts. On, unknown keys are still refused rather than ignored,
  because keys like `EndpointSuffix`, `Authorization` or `AADAudience` change where or how the exporter sends, and the
  reviewed PR that turns the export on is the right moment to fail.
- **Role kind with several roles** is the sorted, comma-separated list of contract values, or `NONE` if the session
  has no role there (the tenant middleware only selects an organization the actor belongs to).
- **Resource and role names:** `appi-pv-pilot-<suffix>`; cloud roles `paqueteria-api` and `paqueteria-worker`; the
  role instance is the replica's machine name.
- **Guard P23** now lets the API and the Worker share `paquetenvia-telemetry-pseudonym-key-1`, the one secret both
  read. It is not a credential, and both must derive the same pseudonyms. Every other Worker secret stays out of the
  API.

## Residual risks

- **Ingestion key (Q-PR6-2).** As the owner's option text says: "quien la obtenga puede mandar telemetría falsa, no
  leerla". The key lives in the API and Worker environment (anyone with read access to the container apps can see
  it); the web never has it. Fake telemetry would also count against the 0.3 GB daily cap, and so could cut off the
  logs the OBS-002 alerts read for the rest of that day.
- **Daily cap shared with the logs.** See "Cost and the daily cap".
- **Process-wide listeners.** The tracer listens to the hosting, HTTP client and application sources of the whole
  process. Only the API and the Worker run in their processes, so nothing else is in scope. In tests, a host of
  another test in the same process could be exported too; the tests check their own traces.
- **Unverified until the first export in Azure:** ARM acceptance of the component, the actual ingestion volume, and
  how Application Insights renders `sampleRate` 10.

## Tests

- `tests/Paqueteria.UnitTests/Operations/Obs003TelemetryExportTests.cs` (no SDK):
  - pseudonym:
    - the fixed vectors;
    - the version is carried and the id never appears;
    - start fails without a key in Production, Staging and DevSynthetic;
    - Development and Testing use the synthetic version 0;
    - a configured key is used with its version;
    - invalid keys fail in every environment, and the message never contains the value;
  - `TelemetryExport` is off by default and fails closed on five invalid sections; seven connection strings of the
    wrong shape are accepted (and not kept) while the export is off and refused when it is on;
  - the exported roots;
  - span allowlist:
    - the request span keeps only the allowlist and gets a rebuilt name and no status description;
    - values with the wrong shape are removed;
    - an outgoing HTTP span loses `url.full`;
    - a job cycle keeps nothing;
    - spans with an event, a link or baggage, database spans, dispatch cycles and polls are taken out;
  - the metric tag allowlist refuses identifiers, addresses, the pseudonym and the role;
  - log lines inside an organization context carry the pseudonym and the role kind; lines outside do not;
  - OBS-002 events (each `TelemetryEvents` id, ids of the reserved block not assigned yet, and 4004) inside an
    organization context keep their trace id and never carry the pseudonym or the role kind; ids next to the block
    still do.
- `tests/Paqueteria.IntegrationTests/Observability/Obs003TelemetryExportTests.cs` (real hosts, real exporter, in-memory
  ingestion; a collection without parallelization):
  - off by default in the API and the Worker: no provider, no OpenTelemetry service, no request recorded, nothing sent;
  - request sampling and the wire:
    - 120 member and 120 foreign-organization requests with a sampled inbound `traceparent`, a secret query, a cookie
      and a user agent, plus 30 probes;
    - exactly the traces `TraceIdRatioBasedSampler(0.1)` accepts are exported, and no probe;
    - every item has `sampleRate` 10 and no parent;
    - member requests carry the pseudonym and `VIEWER`;
    - the wire carries no secret, organization id, inbound id, URL, client address, user agent, `_OTELRESOURCE_` or
      trace header;
  - metrics:
    - `http.server.request.duration`, `aspnetcore.rate_limiting.requests` (policy `Realtime.Connections` and a result)
      and `rate_limit.rejected` (`policy` only);
    - every tag is in the allowlist;
  - with the export on:
    - 80 requests with a sampled inbound context each start a new trace;
    - the process, hosting and OpenTelemetry propagators inject nothing;
    - none of the 80 outgoing Google Maps client requests carries a trace header;
    - the sampled ones are exported as dependencies without their path;
  - the exporter internals: no Statsbeat, no offline storage, no resource metric, SDK stats switched off;
  - the destination: a process-wide recorder of every request a real HTTP handler starts (checked with a refused
    loopback request) shows nothing but loopback, so no request reaches the instance metadata endpoint, Statsbeat or
    Live Metrics; without the SDK stats switch it fails on `http://169.254.169.254`;
  - pipeline:
    - out of 1,500 scheduled job cycles, exactly the sampled ones are exported, and their HTTP children follow them;
    - dispatch cycles, polls and parentless HTTP spans never are;
  - the Worker with the export on exports metrics and no request or dispatch cycle;
  - log lines of an organization-context request carry the pseudonym (`v0-…` in Testing) and `VIEWER`, also with the
    export off; no line carries an organization id;
  - start failures:
    - neither host starts without the key in Production or Staging;
    - an export turned on without a connection string stops the API.
- `tests/Paqueteria.IntegrationTests/Hosting/PilotApiStartupConfigurationTests.cs`: the pilot settings keep the export
  off with a valid section and map the pseudonym key version 1.
- `tests/Paqueteria.ArchitectureTests`:
  - `ObservabilityArchitectureTests`:
    - the metric tag scan now reads every file that creates an instrument, the RateLimiting folder included, through
      every way a key is written: `KeyValuePair` constructions and `KeyValuePair.Create`, target-typed pairs in a tag
      context, tuple helpers, `TagList` initializers and `.Add`. Keys are literals of any case, constants of the file
      or `Type.Constant`s of the repository; anything else is unresolved and fails;
    - 24 fixtures show each form read, and 7 show what is not a tag (a meter, a database parameter, a command, a record,
      an instrument's own `Add`, a forwarding tuple helper);
    - the export keeps exactly `MetricTags`;
    - the pseudonym and role test.
  - `TraceCorrelationArchitectureTests`:
    - the exporter file is a reviewed Azure client file;
    - proof validation runs `StartPollCycle`;
    - only `TelemetryExportPolicy.cs` writes span attributes, and only allowlisted keys;
    - only it and `TraceCorrelation.cs` set a span name.
  - `AzureClientTracingGuard` now also checks `AzureMonitor*Exporter` constructions; a fixture shows an unwrapped one
    is refused.
  - `TrackingTokenLogRedactionArchitectureTests` lists the export as a reviewed request-path sink.
- `tools/azr-001/test_env001_pilot_guards.py`:
  - P01 allows the component and still refuses other Insights types. Since
    `SCL-007-POSTGRES-CONNECTION-ALERT-2026-10-14`, P01 allows metric alerts in `observability.bicep` only, and P21
    refuses every metric alert other than the two PostgreSQL connection alerts, including one on Application Insights;
  - P06 requires the pseudonym mapping in both hosts and refuses the key in plain environment;
  - P17 refuses the telemetry settings in `apps.settings.json`;
  - P21 fails closed on each of these:
    - a classic (non-workspace) or foreign component;
    - IP masking off;
    - ingestion sampling;
    - a second component;
    - a switch that is not a literal;
    - a foreign connection string, another component's name, a literal name, or a suffix that differs from
      `platform.bicep`'s;
    - a component not named `appi-pv-pilot-<suffix>`;
    - an `OTEL_*` override;
    - a web workload with telemetry settings;
    - a query reading `AppRequests` or the pseudonym;
    - a workflow without the secret check;
  - P23 still passes with the shared key.

## Verify locally

```bash
dotnet build Paqueteria.sln -c Release
dotnet test tests/Paqueteria.UnitTests -c Release --no-build --filter "FullyQualifiedName~Obs003"
dotnet test tests/Paqueteria.ArchitectureTests -c Release --no-build --filter "FullyQualifiedName~Observability|FullyQualifiedName~TraceCorrelation|FullyQualifiedName~AzureClientTracing|FullyQualifiedName~TrackingToken"
dotnet test tests/Paqueteria.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~Obs003|FullyQualifiedName~PilotApiStartup"
BICEP_BIN=<pinned bicep> python3 -m unittest tools/azr-001/test_env001_pilot_guards.py
```

## Rollback

- **Code:** revert the commit. There is no schema, migration, role, grant, package or contract change.
  - The export is off unless the literal was flipped. With it on, reverting stops the export at the next revision;
    data already in the workspace ages out with its 30-day retention.
  - The pseudonym leaves the logs.
  - Proof validation starts `StartJobCycle` again.
- **Bicep:**
  - Reverting `apps.bicep` removes the settings and the mapping.
  - The component stays in the resource group until it is deleted with
    `az monitor app-insights component delete -g <rg> -a appi-pv-pilot-<suffix>`. Its data stays in the workspace and
    ages out.
  - The guard changes revert with the commit.
  - The Key Vault secret can stay; the vault has purge protection, so a deleted name is blocked for 90 days.
