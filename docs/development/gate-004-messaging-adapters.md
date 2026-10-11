# GATE-004: WhatsApp and email messaging adapters

Owner decision **GATE-004-CHANNELS** (decision-log, 2026-09-27): customer notifications and tracking
links use WhatsApp through the Meta Cloud API and email through Azure Communication Services (ACS).
GATE-004 stays **open**: templates, Meta business verification and the sender domain are pending.
This change adds the adapters behind the AI-03 §15 `IMessagingProvider` port. It does not send any
message on its own: no producer, recipient source or pilot configuration uses it yet (see
"Not in this change").

## Port

`Paqueteria.Application.Messaging` (building block, no provider SDK):

| Type | Meaning |
| --- | --- |
| `IMessagingProvider.SendAsync(MessagingRequest, CancellationToken)` | one bounded attempt; never throws for a provider failure, only for caller cancellation |
| `MessagingRequest(MessageId, Channel, Recipient, TemplateKey, Parameters)` | `MessageId` is stable across retries (the Notification id); `TemplateKey` is a logical key mapped per channel in configuration; `Parameters` are positional (`{{1}}`, `{{2}}`…) |
| `MessagingRecipient` | E.164 phone or email; `ToString()` is `[redacted]`, and `MessagingRequest.ToString()` omits recipient and parameters |
| `MessagingResult(Outcome, Code, ProviderReference, RetryAfter)` | `Code` is one of the closed `MessagingResultCodes` set (safe for logs and persistence) |

Outcome mapping onto the NTF-001 outbox vocabulary (`NotificationDeliveryOutcome.From(outcome, channel)`,
used by the same `apply_notification_outcome` settle under `lease_token`):

| `MessagingOutcome` | Channel | Outbox outcome | Notification / outbox |
| --- | --- | --- | --- |
| `Accepted` | any | `SUCCESS` | SENT / PROCESSED |
| `TransientFailure` | any | `TRANSIENT` | PENDING / RETRY with backoff |
| `AmbiguousTimeout` | WhatsApp | `AMBIGUOUS_FAILED` | FAILED / DEAD, code `AMBIGUOUS_TIMEOUT`, never retried |
| `AmbiguousTimeout` | Email | `AMBIGUOUS` | PENDING / RETRY with backoff |
| `PermanentFailure` | any | `PERMANENT` | FAILED / DEAD |

**Ambiguous WhatsApp sends (NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02, owner: "Marcar fallido y avisar").** A WhatsApp
template send has no idempotency key, so a retry after a timeout could deliver the message twice. The outcome is
terminal: `NotificationDeliveryOutcome.CodeFor` records `AMBIGUOUS_TIMEOUT` (never the provider code), the
Notification ends FAILED and the send request DEAD, which no claim, requeue or stale-recovery function picks up
again. The Notifications lane `20261002000100_FailAmbiguousWhatsAppNotifications` enforces it in
`security.apply_notification_outcome` as well: `AMBIGUOUS_FAILED` must carry `AMBIGUOUS_TIMEOUT` and is refused
(`22023`) for any channel but WHATSAPP, and an `AMBIGUOUS` report for a WHATSAPP Notification is settled as
`AMBIGUOUS_FAILED`. The dispatcher alert is the `notifications.status-changed` row the settle already writes in the
same transaction (REALTIME lane → `NotificationStatusChanged.v1` on OperationsHub, operations audience, payload
`notification_id`, `channel`, `status`, `attempts`, `occurred_at`; no recipient or template parameter). Email keeps
the AMBIGUOUS retry: the owner answer names WhatsApp only, and ACS reuses one `Operation-Id` per Notification.

**Stale WhatsApp leases (NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03, owner: "Sí, marcar fallido y avisar").** If
the process sending a WhatsApp message dies mid-send, its `notifications.send-requested` row stays PROCESSING
until the lease expires and the message may already have reached Meta. The Notifications lane
`20261003000100_FailStaleWhatsAppNotificationLeases` makes `security.recover_stale_notifications_outbox` settle it
like an ambiguous send instead of requeueing it (or re-leasing it for max-attempts finalization): the request ends
DEAD with `AMBIGUOUS_TIMEOUT` under its own expired `lease_token`, the PENDING Notification ends FAILED with
`AMBIGUOUS_TIMEOUT`, and the same `notifications.status-changed` row reaches the dispatchers, all in the recovery
transaction. A WhatsApp row locked by a concurrent settle is skipped and excluded from the requeue. IN_APP and
email keep the NTF-001 recovery (requeue, then max-attempts finalization). The rows settled this way are not
returned to the worker, so the OBS-002 settled counter does not count them; the FAILED history row and the
status-changed event are their record.

`NotificationRetryPolicy.CalculateDelay(attempts, base, max, retryAfter)` raises the exponential delay
to a provider `Retry-After` hint and caps it at `RetryMaximumSeconds`. Max attempts still end in the
existing stale-recovery finalization lease. Adapters never retry internally: a WhatsApp send has no
idempotency key, so the outbox RETRY is the only retry loop (AI-03 §16 "retries solo en operaciones
idempotentes").

## Failure classification

Shared by both adapters (`MessagingHttp`):

| Situation | Outcome | Code |
| --- | --- | --- |
| connect / DNS / TLS / proxy failure (nothing left the process) | transient | `MESSAGING_PROVIDER_UNREACHABLE` |
| per-attempt timeout, broken response, 408, 504 (the provider may have accepted) | ambiguous | `MESSAGING_PROVIDER_TIMEOUT` |
| 429 | transient, `Retry-After` / `retry-after-ms` honoured (≤ 1 h) | `MESSAGING_PROVIDER_RATE_LIMITED` |
| other 5xx | transient | `MESSAGING_PROVIDER_UNAVAILABLE` |
| 401 / 403 | permanent | `MESSAGING_PROVIDER_AUTH_FAILED` |
| other 4xx | permanent | `MESSAGING_PROVIDER_REJECTED` |
| 2xx without a provider id (WhatsApp) | ambiguous | `MESSAGING_PROVIDER_RESPONSE_INVALID` |
| template not configured, wrong parameter count, a parameter with control characters or 5+ spaces, invalid recipient | permanent, **no HTTP call** | `MESSAGING_TEMPLATE_*` / `MESSAGING_RECIPIENT_INVALID` |

WhatsApp error codes in the body take precedence over the HTTP status: throughput, spam and pair
rate limits (4, 80007, 130429, 131048, 131056) are transient `RATE_LIMITED`; 1, 2, 131000, 131016,
131057, 133004 are transient `UNAVAILABLE`; 131026/131030/133010 → `RECIPIENT_UNDELIVERABLE`; 131047
→ `REENGAGEMENT_REQUIRED`; 131008, 131009 and 132xxx → `PROVIDER_TEMPLATE_REJECTED`; 368, 131031,
131049, 131050 → `POLICY_BLOCKED`; 0, 3, 10, 190, 200–299 → `AUTH_FAILED`; all permanent. Provider
error messages are never read into logs or results (they can echo the phone number).

## Adapters

**WhatsApp — `MetaWhatsAppCloudApiProvider`.** `POST {BaseUri}/{ApiVersion}/{PhoneNumberId}/messages`
with `Authorization: Bearer {AccessToken}` and a `template` message (name, language code, one body
component with text parameters). Only templates: business-initiated WhatsApp messages outside the
24-hour customer service window must use an approved template, so no free-form text is offered.
The `to` value is E.164 digits without `+`. The `wamid` is the provider reference.

**Email — `AzureCommunicationEmailProvider`.** `POST {Endpoint}/emails:send?api-version=2023-03-31`
with a Microsoft Entra token for `https://communication.azure.com/.default` from the existing
`AzureWorkloadCredential` (managed identity, `AZURE_CLIENT_ID`); no connection string or access key.
The token is cached until 5 minutes before expiry and dropped after a 401. `Operation-Id` is the
message id, so retries of one Notification reuse one ACS operation. The body is `plainText` only,
rendered from the configured subject and text; engagement tracking is disabled. `202 Accepted` means
ACS queued the email, not that it was delivered. A success status whose body has no operation `id`
is classified ambiguous (`MESSAGING_PROVIDER_RESPONSE_INVALID`), as the WhatsApp adapter does for a
missing `wamid`.

Both adapters take `IHttpClientFactory` and call `CreateClient` on every send, so the factory's pooled
handlers keep rotating (DNS refresh) for the life of the Worker singleton.

**Resilience (AI-03 §16).** Each adapter wraps the provider call in a `MessagingProviderGuard`:
- **Circuit breaker:** after `CircuitBreakerFailureThreshold` consecutive transient or ambiguous
  outcomes (default 5) the circuit opens for `CircuitBreakerBreakSeconds` (default 30). While it is
  open, sends return `TransientFailure` / `MESSAGING_PROVIDER_CIRCUIT_OPEN` without calling the
  provider, and the break time is the `Retry-After` hint. After the break, a single half-open probe
  runs and closes or reopens the circuit.
- **Bulkhead:** at most `MaxConcurrentRequests` calls run at once (default 8). A call beyond that
  returns `TransientFailure` / `MESSAGING_PROVIDER_CONCURRENCY_LIMITED`.
- **What counts:** accepted and permanent answers mean the provider responded, so they close the
  circuit. Caller cancellation records nothing.
- **Configuration:** the settings live under `Messaging:WhatsApp:MetaCloudApi` and
  `Messaging:Email:AzureCommunicationServices`, validated as 1-50, 1-600 and 1-64.
- The outbox RETRY/backoff remains the only retry loop.

**Synthetic — `SyntheticWhatsAppProvider` / `SyntheticEmailProvider`.** Deterministic fake for tests,
CI and DEV_SYNTHETIC: enforces the same template, parameter and recipient rules, performs no I/O, and
returns the configured `SyntheticOutcome` with a receipt derived from message id, channel and
template key. Refused at start outside Development, Testing and DEV_SYNTHETIC.

HTTP clients are named `IHttpClientFactory` clients with **all default loggers removed**, so the
request URI (which contains the phone number id) never reaches the logs. Adapter logs carry only
channel, provider, outcome, code and HTTP status.

## Configuration (Worker)

All channels are `Disabled` by default; a `Disabled` channel returns `MESSAGING_CHANNEL_DISABLED`
(permanent) and never builds its HTTP client or credential. Invalid configuration stops the host at
start with a message that names keys, never values.

| Key | Value | Source |
| --- | --- | --- |
| `Messaging__WhatsApp__Provider` | `Disabled` (default) · `MetaCloudApi` · `Synthetic` | app setting |
| `Messaging:WhatsApp:MetaCloudApi:AccessToken` | Meta system-user token | **Key Vault** mapping |
| `Messaging:WhatsApp:MetaCloudApi:PhoneNumberId` | numeric phone number id | **Key Vault** mapping |
| `Messaging__WhatsApp__MetaCloudApi__ApiVersion` | `v23.0` (default) | app setting |
| `Messaging__WhatsApp__MetaCloudApi__BaseUri` | `https://graph.facebook.com` (default) | app setting |
| `Messaging__WhatsApp__MetaCloudApi__TimeoutSeconds` | `10` (1–60) | app setting |
| `Messaging__WhatsApp__Templates__<key>__Name` / `__LanguageCode` / `__ParameterCount` | owner-approved Meta template | app setting |
| `Messaging__Email__Provider` | `Disabled` (default) · `AzureCommunicationServices` · `Synthetic` | app setting |
| `Messaging__Email__AzureCommunicationServices__Endpoint` | `https://<resource>.<geo>.communication.azure.com` | app setting (not a secret) |
| `Messaging__Email__AzureCommunicationServices__SenderAddress` | verified MailFrom address | app setting |
| `Messaging__Email__AzureCommunicationServices__ApiVersion` | `2023-03-31` (default) | app setting |
| `Messaging__Email__AzureCommunicationServices__TimeoutSeconds` | `15` (1–60) | app setting |
| `Messaging__Email__Templates__<key>__Subject` / `__PlainText` / `__ParameterCount` | owner-provided text with `{{1}}`…`{{n}}` exactly | app setting |

Neutral recipient text while GATE-001 is open (GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10, AI-07
`public_tracking.branding`): the commercial name is not validated before IMPI, so the WhatsApp templates
approved in WhatsApp Manager and the email `Subject`/`PlainText` sent to recipients use the neutral
"Seguimiento de envío" and never name "Paquetenvia". The repository holds no recipient text: these
adapters have no default template, and the only stored template (`orders.created.operations`, `IN_APP`)
goes to dispatchers.

Key Vault mappings follow PILOT-KEYVAULT-PRIVATE-APP-READ, for example
`KeyVaultSecrets__Mappings__2__SecretName=whatsapp-cloud-api-token`,
`KeyVaultSecrets__Mappings__2__ConfigurationKey=Messaging:WhatsApp:MetaCloudApi:AccessToken`,
`KeyVaultSecrets__Mappings__3__SecretName=whatsapp-phone-number-id`,
`KeyVaultSecrets__Mappings__3__ConfigurationKey=Messaging:WhatsApp:MetaCloudApi:PhoneNumberId`.
The pilot guard pattern `.*Token` already refuses `Messaging__WhatsApp__MetaCloudApi__AccessToken` as a
plain environment variable. ACS with Microsoft Entra authentication needs an Azure role for the
Worker identity on the ACS resource; the exact least-privilege role must be confirmed against the
current ACS documentation when the resource is added, and it is not yet in the pilot RBAC allowlist
(`ALLOWED_ROLE_IDS`).

**Pilot wiring is deliberately not in this change.** `deploy/azure/pilot/apps.bicep`, the
`REQUIRED_SECRET_MAPPINGS` / `ALLOWED_ROLE_IDS` allowlists in `tools/azr-001/env001_pilot_guards.py`
and the Key Vault secrets must change together, after the owner creates the secrets: a mapped secret
that does not exist stops the Worker at start and fails the `existing` secret reference in Bicep.

## Not in this change (blocked on owner decisions or GATE-007)

1. **Producer and audience**: which events notify which customers (NTF-001-OWNER-001..003 approve only
   `orders.created` → dispatchers → `IN_APP`).
2. **Recipient source**: customer phone and email are real PII (GATE-007). No Notification row yet
   carries a protected recipient (`recipient_ciphertext`, `pii_key_version` exist in AI-06 but are unused).
3. **Database lane**: `notification_templates.channel` is `CHECK (channel='IN_APP')` and
   `apply_notification_outcome` accepts only `SYNTHETIC_*` codes. External channels need a Notifications
   migration (template channels, `MESSAGING_*` code allowlist, provider reference) with up/down tested
   on PostgreSQL before the processor can route `WHATSAPP`/`EMAIL` rows to `IMessagingProvider`.
4. **Delivery receipts**: the Meta webhook (verify token + `X-Hub-Signature-256` with the app secret) and
   ACS delivery reports (Event Grid) are not implemented; `SENT` means provider acceptance.
5. **Opt-in / consent and opt-out** handling for WhatsApp and email.

## Tests

- `tests/Paqueteria.UnitTests/Messaging/Gate004MessagingProviderTests.cs`: request shape, error-code
  and status classification, timeout vs. caller cancellation, connect vs. maybe-sent transport
  failures, `Retry-After`, invalid requests without HTTP, log redaction, ACS token caching and 401
  invalidation, credential failure, disabled channels, deterministic synthetic outcomes, start-up
  validation without values, Key Vault mapping into the options, outbox outcome mapping and retry cap.
  All HTTP goes through a fake `HttpMessageHandler`; nothing calls Meta or Azure.
- `tests/Paqueteria.ContractTests/PostgreSql/NotificationsPostgreSqlContractTests.cs` (PostgreSQL): an ambiguous
  WhatsApp settle ends FAILED/DEAD with `AMBIGUOUS_TIMEOUT`, writes one FAILED status-changed row without PII and
  refuses a stale lease; the terminal outcome is refused for IN_APP and EMAIL or without its code; IN_APP and EMAIL
  keep the retry. `NotificationsMigrationGuardPostgreSqlTests` runs the lane up, down and up again.
- `tests/Paqueteria.ArchitectureTests/MessagingArchitectureTests.cs`: outbound `HttpClient` only in the
  messaging adapters, request loggers removed, allowlisted log dimensions, ACS without keys, Worker
  wiring after the Key Vault source.

## Rollback

NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03: migrate the Notifications lane down to
`20261002000100_FailAmbiguousWhatsAppNotifications` (restores the NTF-001 recovery body), then revert the commit;
WhatsApp Notifications already FAILED by stale recovery stay terminal.

NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02: migrate the Notifications lane down to
`20260927000200_AddDispatchOutboxLane` (restores the NTF-001 settle body), then revert the commit; Notifications
already FAILED with `AMBIGUOUS_TIMEOUT` stay terminal. For the adapters alone: revert the commit. No migration, no normative contract and no deployment template changed; every
channel is `Disabled` unless configured, so the running pilot is unaffected either way.
