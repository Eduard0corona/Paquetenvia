# VOICE-001: llamadas enmascaradas repartidor–destinatario (Twilio)

Decisiones: `VOICE-001-MASKED-CALLS-2026-10-11` ("Sí, con número enmascarado") y
`VOICE-001-PROVIDER-TWILIO-2026-10-11` ("Twilio (Recommended)"). ADR:
[`docs/adr/ADR-VOICE-001-MASKED-CALL-BRIDGE.md`](../adr/ADR-VOICE-001-MASKED-CALL-BRIDGE.md). Ítem de backlog:
VOICE-001 (AI-08), puerta GATE-007. **Apagado por defecto y apagado en el piloto** hasta que el owner complete los
pasos de abajo y GATE-007 quede resuelta.

## Qué hace

En el detalle de una parada de entrega en `DELIVERING`, el repartidor toca "Llamar al destinatario". El servidor llama
al celular del repartidor desde el número de la empresa y, cuando contesta, lo enlaza con el destinatario. Ninguno ve el
número del otro; la PWA nunca recibe un teléfono. Es una acción solo en línea: sin conexión el botón queda deshabilitado
con "Necesitas conexión a internet para llamar" y nunca se encola.

## Superficie

| Operación (AI-05) | Ruta | Quién |
| --- | --- | --- |
| `getMyDriverPhone` | `GET /api/v1/driver/me/phone` | DRIVER |
| `registerMyDriverPhone` | `PUT /api/v1/driver/me/phone` | DRIVER, con consentimiento `VOICE-001-CONSENT-V1` |
| `removeMyDriverPhone` | `DELETE /api/v1/driver/me/phone` | DRIVER |
| `getRecipientCallAvailability` | `GET /api/v1/driver/me/stops/{orderId}/recipient-call` | DRIVER |
| `requestRecipientCall` | `POST /api/v1/driver/me/stops/{orderId}/recipient-call` | DRIVER, `Idempotency-Key` |
| `receiveTwilioCallStatus` | `POST /api/v1/voice/twilio/call-status?o=&r=` | Twilio, firmado |
| `answerTwilioInboundCall` | `POST /api/v1/voice/twilio/inbound` | Twilio, firmado |

Respuestas de `requestRecipientCall`: 202 `{call_request_id, status}` (`PLACED`, `REQUESTED` o `UNCONFIRMED`); 403
para cualquier rol que no sea DRIVER o sin perfil activo; 404 uniforme; 409 con `code` (`INVALID_REQUEST`,
`IDEMPOTENCY_CONFLICT`, `ORDER_STATE_NOT_ALLOWED`, `RECIPIENT_PHONE_UNAVAILABLE`, `DRIVER_PHONE_REQUIRED`,
`DRIVER_PHONE_REJECTED`); 429 con `Retry-After`; 503 si el puente está apagado o el proveedor falló.

## Datos

- `drivers.driver_profiles.phone_ciphertext` (+ `phone_pii_key_version`, `phone_consent_version`,
  `phone_consented_at`): el celular del repartidor con el sobre ADP-001, ligado a (organización, perfil,
  `drivers.phone`).
- `drivers.recipient_call_requests`: una fila por solicitud (quién, orden, asignación, proveedor, estado, código,
  CallSid, estado final, duración). Ningún número. FORCE RLS por `org_id`; la API lee, inserta y actualiza; nadie borra
  en runtime; el Worker no tiene privilegios.
- Auditoría: `DRIVER_PHONE_REGISTERED`, `DRIVER_PHONE_REMOVED`, `RECIPIENT_CALL_REQUESTED`, `RECIPIENT_CALL_PLACED`,
  `RECIPIENT_CALL_UNCONFIRMED`, `RECIPIENT_CALL_FAILED` y `RECIPIENT_CALL_STATUS_REPORTED` (este sin actor). Ninguna
  lleva números; el redactor de auditoría además enmascara cualquier valor que parezca teléfono (un CallSid con una
  racha larga de dígitos queda `[REDACTED]`; la fila de la solicitud conserva el CallSid y `call_request_id` las une).
- El teléfono del destinatario es `locations.locations.phone_ciphertext` (ADP-001, propósito `locations.phone`); solo
  se descifra en memoria dentro de la solicitud, después de confirmar la fila `REQUESTED` y sin transacción abierta.

## Configuración

| Setting | Default | Notas |
| --- | --- | --- |
| `Voice:Provider` | `Disabled` | `Synthetic` solo en Development/Testing/DEV_SYNTHETIC; `Twilio` nunca ahí |
| `Voice:SyntheticOutcome` | `Placed` | solo el fake |
| `Voice:Twilio:AccountSid` | — | Key Vault, `AC` + 32 hex |
| `Voice:Twilio:AuthToken` | — | Key Vault; también firma los webhooks |
| `Voice:Twilio:ApiKeySid` / `ApiKeySecret` | — | Key Vault, opcionales (ambos o ninguno) |
| `Voice:Twilio:CompanyNumber` | — | Key Vault, `+52` y 10 dígitos |
| `Voice:Twilio:InboundForwardNumber` | — | opcional; sin él, quien llama escucha un mensaje fijo |
| `Voice:Twilio:WebhookBaseUri` | — | origen público `https://…` sin ruta (nunca se toma de `Host`) |
| `Voice:Twilio:Gate007DecisionId` | — | id de la fila del decision log que resuelve GATE-007 (`GATE-007-…`) |
| `Voice:Twilio:SayVoice` | `Polly.Mia` | voz es-MX |
| `Voice:Twilio:TimeoutSeconds` | `10` | 1–30 |
| `Voice:Twilio:DriverRingSeconds` / `RecipientRingSeconds` | `25` / `30` | 5–60 |
| `Voice:Twilio:MaximumCallSeconds` | `300` | 60–1800, tope de costo por llamada |
| `Voice:Twilio:CircuitBreakerFailureThreshold` / `CircuitBreakerBreakSeconds` / `MaxConcurrentRequests` | `5` / `30` / `8` | AI-03 §16 |
| `Drivers:RecipientCalls:MaximumPerOrder` / `OrderWindowMinutes` | `3` / `15` | por repartidor y orden |
| `Drivers:RecipientCalls:MaximumPerDriverPerHour` | `20` | por repartidor |
| `Drivers:RecipientCalls:IdempotencyLifetimeMinutes` | `1440` | 60–10080 |

Con `Twilio`, `PiiProtection:AzureKeyVault:KeyId` es obligatorio (sobre ADP-001). En el piloto `Voice__` es
administrado por `apps.bicep`: encenderlo es un cambio de infraestructura revisado, no un setting libre.

Mapeos de Key Vault sugeridos (`KeyVaultSecrets:Mappings`):

| Secreto | Clave de configuración |
| --- | --- |
| `twilio-account-sid` | `Voice:Twilio:AccountSid` |
| `twilio-auth-token` | `Voice:Twilio:AuthToken` |
| `twilio-api-key-sid` (opcional) | `Voice:Twilio:ApiKeySid` |
| `twilio-api-key-secret` (opcional) | `Voice:Twilio:ApiKeySecret` |
| `twilio-company-number` | `Voice:Twilio:CompanyNumber` |
| `twilio-inbound-forward-number` (opcional) | `Voice:Twilio:InboundForwardNumber` |

## Pasos del owner antes de encenderlo

1. Crear la cuenta de Twilio de la empresa y completar el regulatory bundle de México (CSF, comprobante de domicilio y
   lo que Twilio pida) para comprar un número mexicano con voz (local US$6.25/mes o móvil US$15/mes).
2. En la consola de Twilio: permisos geográficos de voz solo para México; alertas de gasto (por ejemplo al 50 % y 80 %
   del presupuesto mensual) y, si se desea, una API key dedicada.
3. Configurar en el número: "A call comes in" → `POST https://{origen público}/api/v1/voice/twilio/inbound`. El
   callback de estado lo envía la API en cada llamada.
4. Guardar los secretos de la tabla en el Key Vault del piloto.
5. Resolver GATE-007: aviso de privacidad que nombre a Twilio como encargado, la transferencia internacional de los
   números y la retención de los registros de llamadas de Twilio; registrar la fila `GATE-007-…` en el decision log.
6. Avisar a los repartidores: registran su celular en "Cuenta" aceptando el texto de consentimiento.
7. Encender con un PR que agregue a `apps.bicep` los settings `Voice__*` y los mapeos de Key Vault.

## Pruebas

- Unitarias: `Voice001TwilioBridgeTests` (petición REST, TwiML, `+52`, firma con el ejemplo documentado por Twilio,
  clasificación de códigos, timeouts, breaker, redacción, registro de servicios) y `RecipientCallPolicyTests`.
- PostgreSQL: `DriverVoiceBridgePostgreSqlContractTests` (catálogo del carril, idempotencia, deriva, rollback
  protegido, cifrado del celular, matriz de autorización, estados, 404 uniforme, idempotencia, límites, callback).
- HTTP: `DriverVoiceHttpTests`; arquitectura: `VoiceArchitectureTests`; web: `voice.test.ts`, `voice-api.test.ts`,
  `recipient-call-controller.test.ts`, `driver-phone-controller.test.ts`, `driver-voice-components.test.ts`.

## Reversión

`Voice:Provider=Disabled`. Los teléfonos guardados se pueden borrar desde "Cuenta" aun con el puente apagado. El down
de la migración se niega mientras haya datos (`VOICE001_DOWNGRADE_BLOCKED_DATA_PRESENT`).
