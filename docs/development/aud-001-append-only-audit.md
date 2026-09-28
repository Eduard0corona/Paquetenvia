# AUD-001 append-only audit service

AUD-001 provides one reusable write path for sensitive-action evidence in the
existing `platform.audit_logs` table. It adds no table, migration, role, grant,
policy or endpoint, and does not change the frozen v0.6 baseline.

## Application contract

`Paqueteria.Application.Auditing` owns the immutable `AuditEntry`,
`RedactedAuditPayload`, `IAuditPayloadRedactor` and `IAppendOnlyAuditWriter`
contracts. An entry carries an application-generated audit UUID, organization,
optional actor, action, entity type and UUID, trusted request/correlation ID,
redacted payload and application-generated UTC timestamp.

The writer takes the already-open `DbConnection` and `DbTransaction`
explicitly. This makes transaction ownership visible to reviewers and prevents
an audit from opening a second connection, starting a second transaction,
running after commit or using fire-and-forget behavior. The PostgreSQL writer
also requires `TenantDatabaseExecutionState` to be applied, verifies that the
audited organization is in the active internal tenant context, and requires a
present actor to match the active internal user. Actor and organization are
never accepted from an HTTP body.

`PostgreSqlAppendOnlyAuditWriter` is the only productive source containing the
`INSERT INTO platform.audit_logs` statement. It binds UUID, nullable UUID,
text, `jsonb` and `timestamptz` values with typed Npgsql parameters, uses the
active transaction and cancellation token, and emits no `RETURNING`. Audit ID
and timestamp are supplied by the application even though canonical database
defaults remain available for legacy compatibility.

## Redaction policy

`AuditPayloadRedactor` accepts a `JsonElement`, never a mutable dictionary or a
manually assembled JSON string. It creates a new canonical JSON value and does
not mutate the input. Object properties are ordered ordinally for deterministic
output; objects and arrays are traversed recursively.

Sensitive names are normalized case-insensitively and across separators. The
policy covers passwords, access/refresh tokens, authorization headers,
cookies, API keys, private keys, secrets, connection strings, identity
subjects, email, phone, address and full-name fields. It also detects common
email, phone, bearer/JWT, private-key and connection-string shapes inside
arrays. Sensitive values become `[REDACTED]`; the original value is never
returned, persisted, logged or included in an exception.

Phone detection is the previous behavior minus UUIDs and whole-value ISO
dates. Field names covering phone, telephone, `tel`, `telefono*`, `*celular`,
`movil`, `mobile`, `mobileNumber`, `whatsapp`, `whatsapp_number` and `msisdn`
are always redacted. A string value that is, as a whole, exactly one ISO-8601
date or timestamp (`2026-09-28`, `2026-09-28T10:59:40Z`, with optional
seconds, fractional seconds and `Z`/`+hh:mm` offset) is kept when it parses
strictly with the invariant culture to a real calendar date with a year from
1900 to 2199; `2026-02-30`, `5512-12-31` or `2026-13-45` are not dates and are
redacted. Dates inside free text are not exempt: `llamar 5512-12-31` or
`entrega 2026-09-28 tel 6671234567` go through the full rule, so a date in a
note may be over-redacted, which is accepted. Well-formed UUIDs
(8-4-4-4-12 hex) are removed from the text first, so a digit run inside an
order, token or request id never triggers redaction. What remains is
redacted when it matches any of:

- the original generic rule: 8 or more characters of digits, spaces, dashes
  or parentheses, starting and ending with a digit, with an optional leading
  `+` (`(?<![0-9])\+?[0-9][0-9 ()-]{6,}[0-9](?![0-9])`);
- an E.164 number (`+` then 8 to 15 digits, with optional spaces, dots, dashes
  or parentheses);
- a Mexican 10-digit number with an optional `+52`/`52` and mobile `1` prefix
  in the usual groupings (`6671234567`, `667 123 4567`, `(55) 1234-5678`,
  `667.123.45.67`, `66 71 23 45 67`).

All three are bounded by digits only, so a phone glued to a word
(`llamar667-123-4567`, `6671234567antes`) or to more digits
(`66712345675512345678`) is redacted, under every key, identifier keys
included. Every string the previous rule redacted is still redacted, except
UUIDs and whole-value ISO dates and timestamps. Numeric runs of 8 or more
digits that are not phones (and hex or base64 tokens that contain such a run)
are still over-redacted, as before; that is intentional.

The default limits are eight nested levels and 16 KiB of UTF-8 JSON. Invalid,
unsupported, over-depth or over-size input throws the generic
`AuditRedactionException` without an inner exception or original payload. This
failure is intentional: a mandatory sensitive action must roll back rather
than persist an unsafe payload or complete without audit evidence.

## Existing sensitive actions

The two existing audit flows now delegate to the general writer:

- `TENANT_CONTEXT_ACTIVATED` retains the authorized platform-admin actor,
  selected organization, organization entity, request ID and tenant
  transaction created by `TenantTransactionContext`.
- `INITIAL_ORGANIZATION_PROVISIONED` retains the generated user/actor,
  generated organization and audit IDs, request ID, UTC timestamp and the
  transaction containing the user, organization and membership inserts.

Both currently use the safe empty payload `{}`. Their authorization, public
responses, concurrency and rollback behavior are unchanged. Provisioning was
not expanded and TEN-003 remains separate.

## Atomicity, retry and cancellation

`TenantTransactionContext` begins a transaction, applies both tenant GUCs and
`SET LOCAL ROLE paqueteria_app`, and then invokes the action. The action and
general audit writer receive the same Npgsql connection and transaction. An
action failure, redaction failure, PostgreSQL audit failure, post-audit failure
or cancellation reaches the existing rollback path. No audit row or sensitive
action row remains.

The EF/Npgsql execution strategy retries the complete delegate with a new
PostgreSQL transaction and reapplied tenant context. Contract tests force a
transient first attempt after both inserts, observe two distinct transaction
IDs and one committed action/audit pair. Reusing application-generated IDs is
safe because the failed attempt is rolled back; no duplicate audit is committed.

## PostgreSQL append-only and tenant isolation

AI-18 intentionally grants `SELECT, INSERT` on `platform.audit_logs` to both
`paqueteria_app` and `paqueteria_worker`, and revokes `UPDATE, DELETE` from both.
Both roles are `NOBYPASSRLS`, cannot assume privileged roles and remain subject
to the forced `audit_logs_tenant` policy. Real PostgreSQL 18/PostGIS 3.6 tests
prove permitted inserts, denied updates/deletes, empty-context fail-closed
behavior, cross-organization invisibility and concurrent isolation.

The database capability of the `paqueteria_worker` role is distinct from the
current application process: `Paqueteria.Worker` remains disconnected from
PostgreSQL, has no audit registration or connection string, and does not use
the role's future audit capability. AUD-001 does not connect it.

## Operation and observability

Callers first construct structured synthetic-safe data, pass it through
`IAuditPayloadRedactor`, build an `AuditEntry` from trusted internal actor,
organization and request context, and invoke the writer inside their mandatory
tenant transaction. Log only the audit UUID, action name and operational result;
never log the input payload, redacted payload contents, credentials or PII.

Run the focused evidence with:

```powershell
dotnet test .\tests\Paqueteria.UnitTests\Paqueteria.UnitTests.csproj `
  --filter "FullyQualifiedName~AuditPayloadRedactorTests"

dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj `
  --filter "FullyQualifiedName~AppendOnlyAuditContractTests"

dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj `
  --filter "Category=PostgreSqlContract"
```

PostgreSQL evidence runs only in the pinned disposable Testcontainers image and
never uses the persistent FND-002 database.

## Residual risks and rollback

GATE-007 still governs real PII privacy/retention and GATE-014 still governs a
future PgBouncer topology. AUD-001 stores only pre-redacted structured payloads
and implements no retention, purge, search, export, SIEM or audit endpoint.

Rollback is a revert of the AUD-001 code, test and documentation commits. There
is no database rollback because no migration or normative object changed.
Disposable Testcontainers databases are destroyed normally; do not remove
canonical triggers, policies, roles or grants. Existing audit rows remain
append-only and require an approved future privacy operation rather than
runtime mutation.
