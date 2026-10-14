# OBS-003 follow-up: the Worker's audit trace id and per-message pseudonym

Phase 4 follow-up of **OBS-003** (`AI-08_BACKLOG.yaml`), after PR 6 (`obs-003-appinsights-export.md`, the HMAC
organization pseudonym) and PR 7 (`obs-003-outbox-trace-context.md`, trace context through the outbox). Decision rows:
`OBS-003-WORKER-AUDIT-TRACE-2026-10-14`, `OBS-003-WORKER-PSEUDONYM-2026-10-14` and
`OBS-003-EXPORT-AFTER-PILOT-WEEK-2026-10-14`.

The owner's answers, literal (2026-10-14). The questions, the options not chosen and the context shown are in the
decision rows.

| Question | Answer | Option text |
| --- | --- | --- |
| Q1, Worker audit rows | "Sí, en un PR aparte (Recommended)" | "Desde ese PR, esas filas guardan el trace id del mensaje. Las filas que ya existen no cambian (la auditoría es de solo agregar). Cambia el contenido de la evidencia de auditoría desde ese momento." |
| QA, proof validation audit rows | "Se quedan vacías (Recommended)" | "En este PR solo las filas del ciclo de vida de asignaciones guardan el trace id del mensaje. Las de validación de fotos siguen con request_id vacío y se ligan a la petición por el id de la sesión de carga, cuya fila de creación ya tiene el trace id. Sin cambios de base de datos." |
| Q2, Worker log lines | "Sí, en un PR aparte (Recommended)" | "Mismo seudónimo HMAC de la fase 4, nunca el id real, y nunca como dimensión de métricas o alertas. Permite filtrar en Log Analytics los mensajes de una organización." |
| QB, Worker actor role kind | "SYSTEM (Recommended)" | "Un valor fijo para todo lo que hace el Worker. Distingue en Log Analytics las líneas del Worker de las de la API, sin datos nuevos." |
| QC, proof validation lines | "Sí, si los metadatos son válidos (Recommended)" | "Igual que los mensajes del outbox. Una foto con metadatos inválidos no lleva empresa." |
| Q5, export timing | "Tras una semana de piloto (Recommended)" | "Primero se despliega sin exportar y se mide una semana cuánto ocupan los logs del tope diario. Con ese dato se enciende en un PR. Así no arriesgamos los logs de las alertas en los primeros días." |

Before QA–QC the owner was told a correction: Q2 said the scheduled jobs already carry the pseudonym. They do not; only
the API's `TelemetryOrganizationContextMiddleware` entered an organization context. The answer to Q2 still applies to
the messages the Worker processes.

## What changed

| Area | Before | After |
| --- | --- | --- |
| `ASSIGNMENT_CLOSED` audit row (`PostgreSqlAssignmentLifecycleReactor`) | `request_id` NULL | the trace id of the claimed message's stored `trace_parent`: the request or job that wrote it, in the API's format. NULL without a valid stored context |
| Proof validation audit rows (`custody.proof_upload_session.ready`, `.rejected`) | NULL | unchanged, NULL (QA) |
| Order auto-close audit row (`ORDER_STATUS_CHANGED` of the system actor) | NULL | unchanged, NULL (a scheduled job, not named by Q1) |
| Worker lines of one outbox message (notifications and dispatch lanes) | trace id only (PR 7) | trace id, `OrganizationPseudonym` of the message's owner, `ActorRoleKind` `SYSTEM` |
| Proof validation per-photo line | trace id of the poll | with valid signed metadata, also the pseudonym of the owner it names and `SYSTEM`; with invalid metadata, no organization |
| Dispatch and poll cycle lines, OBS-002 events, API realtime lanes, scheduled jobs | | unchanged |
| Export to Application Insights | off | still off; turned on by a later PR after one week of pilot (Q5) |

No schema, migration, SQL, grant, role, configuration, Bicep or package change.

## Audit rows (Q1, QA)

The Worker writes `platform.audit_logs` in three places. Only one processes an outbox message:

| Writer | Runs from | `request_id` |
| --- | --- | --- |
| `Dispatch.Infrastructure/Lifecycle/PostgreSqlAssignmentLifecycleReactor.cs` (`ASSIGNMENT_CLOSED`) | a claimed DISPATCH message | the message's trace id, or NULL |
| `Custody.Infrastructure/Proofs/ProofValidationProcessor.cs` (`custody.proof_upload_session.ready`, `.rejected`) | the quarantine listing, in a poll cycle | NULL |
| `Orders.Infrastructure/Orders/PostgreSqlOrderTransitionService.cs` `CloseDeliveredAsync`, from `Lifecycle/PostgreSqlOrderAutoClose.cs` (`ORDER_STATUS_CHANGED`) | the ADR-034 auto-close job | NULL |

- **The value.** `OutboxTraceContext.TraceIdOf(message.TraceParent)`: the 32 lowercase hexadecimal digits of the trace id
  of the stored W3C context. The API writes `request_id` from `HttpContext.TraceIdentifier`, which
  `HttpTraceCorrelation.UseTraceCorrelation` sets to the same 32-digit form, so a single query on the trace id finds the
  request's own audit row and the Worker's. It is also the `TraceId` of every line the Worker writes for that message.
- **No SQL.** `ClaimedDispatchOutboxMessage.TraceParent` is already read by the claim (`OutboxTraceContextColumn.Read`).
  The reactor reads only that member; it never reads `Activity.Current`.
- **NULL rather than the cycle's trace.** A message without a valid stored context (rows written before PR 7, or outside
  any trace) runs under its dispatch cycle. That cycle's trace belongs to no request. The rule was already defined by PR 1
  (`obs-003-w3c-correlation.md`, "Audit rows without the trace id") and AI-05 `Problem.traceId`: Worker rows belong to
  no request and keep `request_id` NULL. Q1 adds the message's own trace when there is one.
- **Proof validation (QA).** It processes photos, not outbox messages, and neither the photo nor its upload session
  stores a trace context. Its rows keep NULL and link to the request through the upload session id: the session's
  creation row (`custody.proof_upload_session.created`, `PostgreSqlProofUploadSessionService`) carries the creating
  request's trace id with the same entity id.
- **Append-only.** Rows written before this change keep NULL. No action, actor, payload or other column changes.
- **AI-05.** The `Problem.traceId` description said Worker rows keep NULL "until trace context crosses the outbox (phase 4
  PR 7)". It now states the current rule, and the `OBS-003-WORKER-AUDIT-TRACE` contract delta records the
  description-only change.

Before and after, the same synthetic `ASSIGNMENT_CLOSED` row:

```text
Before: id=0b6e…, org_id=<owner>, actor_id=NULL, action=ASSIGNMENT_CLOSED, entity_type=Assignment,
        entity_id=<assignment>, request_id=NULL
After:  id=0b6e…, org_id=<owner>, actor_id=NULL, action=ASSIGNMENT_CLOSED, entity_type=Assignment,
        entity_id=<assignment>, request_id=4bf92f3577b34da6a3ce929d0e0e4736
```

The request that wrote the message (for example the unassign or the delivery) has
`request_id=4bf92f3577b34da6a3ce929d0e0e4736` on its own `ORDER_STATUS_CHANGED` row.

## Log lines (Q2, QB, QC)

`TelemetryOrganizationContext.EnterSystem(pseudonymizer, ownerOrganizationId)` enters the context of one unit of Worker
work: the HMAC pseudonym of the owner organization and the fixed role kind `TelemetryOrganizationContext.SystemRoleKind`
(`SYSTEM`). An unknown owner (no organization id) enters nothing. `TraceCorrelatedLoggerFactory` then adds
`State.OrganizationPseudonym` and `State.ActorRoleKind` to every line of that unit, also with the export off. The
OBS-002 events (`TelemetryEvents.IsObservabilityEvent`) never carry them, as before.

| Site | Unit | Where the owner organization comes from |
| --- | --- | --- |
| `NotificationsOutboxDispatcher` | each claimed or recovered message, right after `TraceCorrelation.StartOutboxMessage` | `owner_org_id`, returned by `claim_notifications_outbox`, `claim_unowned_outbox` and `recover_stale_notifications_outbox` |
| `AssignmentLifecycleDispatcher` (in `AssignmentLifecycleProcessor.cs`) | each claimed message, right after `TraceCorrelation.StartOutboxMessage` | `owner_org_id`, returned by `claim_dispatch_outbox` |
| `ProofValidationProcessor` | each quarantined photo, after its signed metadata was read and found valid | the `owner-org-id` of the object's signed metadata (no query) |

Every claim and recovery function already returns the full row (`RETURNS SETOF platform.outbox_events`), so no new SQL
access to the outbox and no grant is needed. `owner_org_id` is `NOT NULL`, so in practice every outbox message has an
owner.

Not changed:

- the lines of the cycles themselves (claim, loop failure, the poll's own lines): they span many organizations;
- the API realtime lanes (`RealtimeOutboxProcessor`): API code, outside the question, which named the Worker;
- the scheduled jobs (auto-close, claim window, retention, cleanups): they do not carry the pseudonym, as the correction
  told the owner;
- the API requests: their role kind is still the person's roles in the selected organization.

Before and after, a synthetic notifications line (JSON console, `State` shortened):

```json
{"Category":"Notifications.Infrastructure.Dispatching.NotificationsOutboxProcessor",
 "Message":"Notifications dispatcher completed with owner NOTIFICATIONS, channel IN_APP, outcome SUCCESS, code SYNTHETIC_ACCEPTED.",
 "State":{"Owner":"NOTIFICATIONS","Channel":"IN_APP","Outcome":"SUCCESS","Code":"SYNTHETIC_ACCEPTED",
          "TraceId":"4bf92f3577b34da6a3ce929d0e0e4736"}}
```

```json
{"Category":"Notifications.Infrastructure.Dispatching.NotificationsOutboxProcessor",
 "Message":"Notifications dispatcher completed with owner NOTIFICATIONS, channel IN_APP, outcome SUCCESS, code SYNTHETIC_ACCEPTED.",
 "State":{"Owner":"NOTIFICATIONS","Channel":"IN_APP","Outcome":"SUCCESS","Code":"SYNTHETIC_ACCEPTED",
          "TraceId":"4bf92f3577b34da6a3ce929d0e0e4736",
          "OrganizationPseudonym":"v1-0c4f8e2a9b6d1e3f5a7c9e0b2d4f6a81","ActorRoleKind":"SYSTEM"}}
```

The pseudonym is never the organization id, a metric tag, an alert or workbook dimension, or an entry of
`TelemetryDimensions.Allowed`. AI-03 §18 already asks for the tenant pseudonym and the technical actor as a role kind
"cuando hay contexto de organización", so it needs no wording change.

## Log Analytics

All lines of one organization's Worker messages, without its id:

```kql
ContainerAppConsoleLogs_CL
| where TimeGenerated > ago(1d)
| extend e = parse_json(Log_s)
| where tostring(e.State.OrganizationPseudonym) == "<pseudonym>" and tostring(e.State.ActorRoleKind) == "SYSTEM"
| project TimeGenerated, ContainerAppName_s, Category = tostring(e.Category), TraceId = tostring(e.State.TraceId), Message = tostring(e.Message)
| order by TimeGenerated asc
```

The audit rows of one trace, the API's and the Worker's:

```sql
SELECT occurred_at, action, entity_type, entity_id FROM platform.audit_logs
WHERE request_id = '<trace id>' ORDER BY occurred_at;
```

## Export timing (Q5)

No code change. The pilot keeps deploying with `TelemetryExport__Enabled='false'` in `deploy/azure/pilot/apps.bicep`.
After one week of pilot, once the daily log volume of the pilot workspace has been measured against the 0.3 GB daily cap
(`logDailyQuotaGb`), a later reviewed PR changes that literal to `'true'`. It goes through promotion to `main` and the
pilot deploy, as before (guard P21). Recorded in `obs-003-appinsights-export.md` ("Owner's deploy actions") and in the
pilot README §10.1.

## Tests

- `tests/Paqueteria.UnitTests/Operations/Obs003WorkerCorrelationTests.cs`:
  - the audit value is the API's format (the AI-05 pattern, the `TraceIdentifier` of a request of the same trace and the
    `TraceId` of the message's lines), and NULL without a valid stored context, also inside a dispatch cycle;
  - `EnterSystem` enters the owner's pseudonym and `SYSTEM`, nothing for an unknown owner, and restores the context;
  - proof validation: the failure line of a photo with valid metadata carries the pseudonym and `SYSTEM`, the line of a
    photo with invalid metadata carries none, no line names the organization id, and the proof metrics recorded inside
    the context carry neither value;
  - neither value is an OBS-002 dimension or an exported metric tag.
- `tests/Paqueteria.UnitTests/Operations/Obs003OutboxTraceContextTests.cs`: the notifications and dispatch lanes write
  each message's line with its owner's pseudonym and `SYSTEM`, and none for an unknown owner.
- `tests/Paqueteria.ArchitectureTests/ObservabilityArchitectureTests.cs`: the exact Worker sites that enter an
  organization context, each with its one call, the lanes right after the message activity and proof validation after
  valid metadata; the role kind is always `SYSTEM`; the API realtime lanes enter none.
- `tests/Paqueteria.ArchitectureTests/OutboxTraceContextArchitectureTests.cs`: the reactor is the one audit writer that
  reads the stored context, once, through `OutboxTraceContext.TraceIdOf`, never `Activity.Current`; proof validation
  passes NULL.
- `tests/Paqueteria.ContractTests/PostgreSql/DispatchWorkerAuditTracePostgreSqlContractTests.cs` (PostgreSQL): a request
  with its own trace unassigns an order through the real ORD-002 transition; the Worker claims and processes the reaction
  inside its own dispatch cycle; the `ASSIGNMENT_CLOSED` row carries the request's trace id, never the cycle's, and one
  query on it finds both audit rows. `DispatchAssignmentLifecyclePostgreSqlContractTests` checks NULL for reaction rows
  without a stored context.
- `tests/Paqueteria.IntegrationTests/Dispatch/AssignmentLifecycleWorkerPostgreSqlTests.cs` (real Worker host): a traced
  reaction closes the assignment with that trace id as `request_id`.
- `tests/Paqueteria.IntegrationTests/Custody/SecureProofUploadPipelineTests.cs`: the proof validation `ready` row keeps
  `request_id` NULL.
- `tests/Paqueteria.ContractTests/Obs003ProblemTraceIdContractTests.cs`: the AI-05 text and its contract delta.

The lanes and proof validation now take `IOrganizationPseudonymizer` as a required dependency, so the test hosts that
compose them register it (`AddTelemetryOrganizationPseudonym`), as the Worker does.

## Verify locally

```bash
dotnet build Paqueteria.sln -c Release
dotnet test tests/Paqueteria.UnitTests -c Release --no-build --filter "FullyQualifiedName~Obs003|FullyQualifiedName~Scl007"
dotnet test tests/Paqueteria.ArchitectureTests -c Release --no-build
dotnet test tests/Paqueteria.ContractTests -c Release --no-build --filter "FullyQualifiedName~Worker_assignment_closure_audit|FullyQualifiedName~DispatchPostgreSqlContractTests.D8_|FullyQualifiedName~Obs003ProblemTraceId"
```

## Choices not written in the task

- **Where the context is entered.** In the two dispatchers, next to the message activity, so every line of the message
  (processor, provider, failure) carries it. Proof validation enters it in the per-photo wrapper, so the failure line
  written by its `catch` carries it too.
- **Unknown owner.** `Guid.Empty` stands for an unknown owner; the column is `NOT NULL`, so it only guards malformed
  input.
- **Required dependency.** The lanes and proof validation take the pseudonymizer as a constructor dependency. A host that
  composes them without `AddTelemetryOrganizationPseudonym` fails at start instead of writing lines without the
  pseudonym.
- **AI-05 delta.** The description change is recorded as its own `x-pilot-contract-deltas` entry; the PR 1 entry keeps
  its historical text.

## Rollback

Revert the PR. There is no schema, migration, SQL, grant, configuration, Bicep or package change, so the application
can be rolled back alone. Afterwards the Worker lines lose the pseudonym and `SYSTEM`, and new `ASSIGNMENT_CLOSED` rows
write `request_id` NULL again. Rows written in the meantime keep their trace id (audit rows are append-only). The
revert also restores the AI-05 text, the AI-08 criteria, the v0.6 integrity files and the pinned hashes of the same
commit.
