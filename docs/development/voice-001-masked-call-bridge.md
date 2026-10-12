# VOICE-001: llamadas enmascaradas repartidor–destinatario (Twilio)

Decisiones: `VOICE-001-MASKED-CALLS-2026-10-11` ("Sí, con número enmascarado"),
`VOICE-001-PROVIDER-TWILIO-2026-10-11` ("Twilio (Recommended)") y `VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11`. ADR:
[`docs/adr/ADR-VOICE-001-MASKED-CALL-BRIDGE.md`](../adr/ADR-VOICE-001-MASKED-CALL-BRIDGE.md). Ítem de backlog:
VOICE-001 (AI-08), puerta GATE-007. **Apagado por defecto y apagado en el piloto** hasta que el owner complete los
pasos de abajo y GATE-007 quede resuelta.

## Qué hace

En el detalle de una parada de entrega en `DELIVERING`, el repartidor toca "Llamar al destinatario". El servidor llama
al celular del repartidor desde el número de la empresa y lo enlaza con el destinatario solo con teclas. Ninguno ve el
número del otro; la PWA nunca recibe un teléfono. Es una acción solo en línea: sin conexión el botón queda deshabilitado
con "Necesitas conexión a internet para llamar" (solo si el servidor ya confirmó que la llamada está disponible; antes
no se muestra nada) y nunca se encola. Tras un 429 o una disponibilidad `RATE_LIMITED`, la PWA vuelve a consultar una
vez cuando termina la espera (`Retry-After` / `retry_after_seconds`) y descarta respuestas que llegan después de que la
parada cambió.

### Confirmar con una tecla (buzones de voz)

Pregunta y respuesta literales del project owner (2026-10-11): "Llamadas enmascaradas: si contesta un buzón de voz, el
saludo del buzón puede decir el número del repartidor o del destinatario, y eso rompe el enmascarado. ¿Cómo lo
evitamos?" → "Confirmar con una tecla (Recommended)" ("Al repartidor le dice "Presiona 1 para llamar al destinatario".
Al destinatario: "Tiene una llamada sobre su envío; presione 1 para contestar" (texto neutro, sin marca). Si nadie
presiona, se cuelga sin enlazar. Gratis y no falla con buzones.").

1. Tramo del repartidor: la llamada de la plataforma solo dice "Presiona 1 para llamar al destinatario." y espera un
   dígito (`Voice:Twilio:DriverConfirmSeconds`). Twilio envía el resultado, también sin tecla, a
   `POST /api/v1/voice/twilio/driver-confirm?o=&r=` (firmado). Sin 1: se cuelga, nadie es marcado y la solicitud
   guarda `bridge_stage=DRIVER_NOT_CONFIRMED`.
2. Con el primer 1 a tiempo, el webhook descifra en memoria el teléfono del destinatario y responde el `<Dial>` con el
   número de la empresa como identificador y `<Number url=".../recipient-screen">` (`DRIVER_CONFIRMED`; si el número
   no sirve, `BRIDGE_FAILED` y se cuelga).
3. Tramo del destinatario: al contestar escucha solo "Tiene una llamada sobre su envío; presione 1 para contestar."
   (sin marca) y tiene `Voice:Twilio:RecipientConfirmSeconds` para presionar 1. Con 1 se conectan los dos tramos
   (`RECIPIENT_CONFIRMED`, auditoría `RECIPIENT_CALL_BRIDGED`); sin 1 se cuelga sin enlazar (`RECIPIENT_NOT_CONFIRMED`).
4. Nada se reintenta. Una tecla repetida, tardía (más de 15 minutos), de otra llamada o de otro tenant cuelga y no
   cambia nada; un fallo del almacén también cuelga.

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
| `confirmTwilioDriverKeypress` | `POST /api/v1/voice/twilio/driver-confirm?o=&r=` | Twilio, firmado |
| `screenTwilioRecipient` | `POST /api/v1/voice/twilio/recipient-screen?o=&r=` | Twilio, firmado |
| `confirmTwilioRecipientKeypress` | `POST /api/v1/voice/twilio/recipient-confirm?o=&r=` | Twilio, firmado |

Respuestas de `requestRecipientCall`: 202 `{call_request_id, status}` (`PLACED`, `REQUESTED` o `UNCONFIRMED`); 403
para cualquier rol que no sea DRIVER o sin perfil activo; 404 uniforme; 409 con `code` (`INVALID_REQUEST`,
`IDEMPOTENCY_CONFLICT`, `ORDER_STATE_NOT_ALLOWED`, `RECIPIENT_PHONE_UNAVAILABLE`, `DRIVER_PHONE_REQUIRED`,
`DRIVER_PHONE_REJECTED`); 429 con `Retry-After`; 503 si el puente está apagado o el proveedor falló sin crear la
llamada. Un timeout, una respuesta rota o cualquier 5xx de Twilio dejan `UNCONFIRMED` (la llamada puede existir): la
llave se conserva, cuenta para los límites y nunca se reintenta. La disponibilidad (`getRecipientCallAvailability`)
responde `{available, reason, retry_after_seconds}`; `retry_after_seconds` solo con `RATE_LIMITED`.

Reintentos y solicitudes abandonadas: el id de la solicitud se elige antes de la transacción 1, así que si su commit se
confirma pero la respuesta se pierde, el reintento de EF reconoce su propia reserva y sigue con esa llamada (sin otra
fila ni otra auditoría). Si el proceso se detiene entre las dos transacciones, la fila queda `REQUESTED`: la repetición
de la llave responde `REQUESTED` mientras pueda seguir en curso y, pasados `Drivers:RecipientCalls:StaleRequestSeconds`,
la cierra `UNCONFIRMED` con `VOICE_OUTCOME_UNKNOWN` (nunca se vuelve a llamar). Un callback de estado nunca promueve
una solicitud `FAILED`.

Celular del repartidor (ARCO): consultar (`GET`) y borrar (`DELETE`) funcionan para el perfil propio del repartidor con
cualquier estado (`PENDING`, `ACTIVE`, `SUSPENDED`, `INACTIVE`); registrar (`PUT`) y pedir llamadas exigen un perfil
`ACTIVE` (403 si no). La sesión sigue exigiendo una membresía DRIVER activa en la organización.

## Datos

- `drivers.driver_profiles.phone_ciphertext` (+ `phone_pii_key_version`, `phone_consent_version`,
  `phone_consented_at`): el celular del repartidor con el sobre ADP-001, ligado a (organización, perfil,
  `drivers.phone`).
- `drivers.recipient_call_requests`: una fila por solicitud (quién, orden, asignación, proveedor, estado, código,
  CallSid, estado final, duración y `bridge_stage`). Ningún número. FORCE RLS por `org_id`; la API lee, inserta y
  actualiza; nadie borra en runtime; el Worker no tiene privilegios.
- Auditoría: `DRIVER_PHONE_REGISTERED`, `DRIVER_PHONE_REMOVED`, `RECIPIENT_CALL_REQUESTED`, `RECIPIENT_CALL_PLACED`,
  `RECIPIENT_CALL_UNCONFIRMED`, `RECIPIENT_CALL_FAILED`, y sin actor `RECIPIENT_CALL_STATUS_REPORTED`,
  `RECIPIENT_CALL_DRIVER_CONFIRMED`, `RECIPIENT_CALL_NOT_CONFIRMED`, `RECIPIENT_CALL_BRIDGED` y
  `RECIPIENT_CALL_BRIDGE_FAILED`. Ninguna lleva números; el redactor de auditoría además enmascara cualquier valor que
  parezca teléfono (un CallSid con una racha larga de dígitos queda `[REDACTED]`; la fila de la solicitud conserva el
  CallSid y `call_request_id` las une).
- El teléfono del destinatario es `locations.locations.phone_ciphertext` (ADP-001, propósito `locations.phone`); solo
  se descifra en memoria, sin transacción abierta: dentro de la solicitud (después de confirmar la fila `REQUESTED`,
  junto con el del repartidor y en paralelo) y en el webhook del 1 del repartidor (solo el del destinatario, para la
  respuesta TwiML).

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
| `Voice:Twilio:InboundForwardRingSeconds` / `InboundForwardMaximumSeconds` | `30` / `3600` | 5–60 / 60–14400; solo el desvío entrante, nunca los del puente |
| `Voice:Twilio:WebhookBaseUri` | — | origen público `https://…` sin ruta (nunca se toma de `Host`) |
| `Voice:Twilio:Gate007DecisionId` | — | id de la fila del decision log que resuelve GATE-007 (`GATE-007-…`) |
| `Voice:Twilio:SayVoice` | `Polly.Mia` | voz es-MX |
| `Voice:Twilio:TimeoutSeconds` | `10` | 1–30 |
| `Voice:Twilio:DriverRingSeconds` / `RecipientRingSeconds` | `25` / `30` | 5–60 |
| `Voice:Twilio:DriverConfirmSeconds` / `RecipientConfirmSeconds` | `10` / `10` | 3–30, tiempo para presionar 1 |
| `Voice:Twilio:MaximumCallSeconds` | `300` | 60–1800, tope de costo por llamada (el `TimeLimit` REST suma las teclas, el timbrado del destinatario y un minuto) |
| `Voice:Twilio:CircuitBreakerFailureThreshold` / `CircuitBreakerBreakSeconds` / `MaxConcurrentRequests` | `5` / `30` / `8` | AI-03 §16 |
| `Drivers:RecipientCalls:MaximumPerOrder` / `OrderWindowMinutes` | `3` / `15` | por repartidor y orden |
| `Drivers:RecipientCalls:MaximumPerDriverPerHour` | `20` | por repartidor |
| `Drivers:RecipientCalls:IdempotencyLifetimeMinutes` | `1440` | 60–10080 |
| `Drivers:RecipientCalls:StaleRequestSeconds` | `300` | 60–3600; después, una repetición de la llave cierra `UNCONFIRMED` una solicitud abandonada |

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
   callback de estado y las URL de las teclas (`driver-confirm`, `recipient-screen`, `recipient-confirm`) los envía la
   API en cada llamada; el origen público debe ser alcanzable por Twilio.
4. Guardar los secretos de la tabla en el Key Vault del piloto.
5. Resolver GATE-007: aviso de privacidad que nombre a Twilio como encargado, la transferencia internacional de los
   números y la retención de los registros de llamadas de Twilio; registrar la fila `GATE-007-…` en el decision log.
6. Avisar a los repartidores: registran su celular en "Cuenta" aceptando el texto de consentimiento.
7. Encender con un PR que agregue a `apps.bicep` los settings `Voice__*` y los mapeos de Key Vault.

## Pruebas

- Unitarias: `Voice001TwilioBridgeTests` (petición REST, TwiML de ambos tramos con su tecla y sin tecla, `+52`, firma
  con el ejemplo documentado por Twilio y de los pasos de tecla, clasificación de códigos con 5xx ambiguo, timeouts,
  breaker, redacción, desvío entrante con sus propios límites, registro de servicios) y `RecipientCallPolicyTests`
  (descifrado en paralelo, destinatario solo, espera redondeada).
- PostgreSQL: `DriverVoiceBridgePostgreSqlContractTests` (catálogo del carril, idempotencia, deriva, rollback
  protegido, cifrado del celular, ARCO con perfil suspendido, matriz de autorización, estados, 404 uniforme,
  idempotencia, reserva propia tras un commit, solicitud abandonada, límites, callback sin promover `FAILED`, teclas:
  confirmada, sin tecla, repetida, tardía, ajena y número inutilizable).
- HTTP: `DriverVoiceHttpTests`; arquitectura: `VoiceArchitectureTests`; web: `voice.test.ts`, `voice-api.test.ts`,
  `recipient-call-controller.test.ts`, `driver-phone-controller.test.ts`, `driver-voice-components.test.ts`.

## Pendientes (fuera de VOICE-001)

- Borrado del celular por un administrador o en la baja del repartidor cuando su membresía ya no está activa: hoy el
  repartidor lo borra él mismo con cualquier estado de su perfil, pero sin membresía DRIVER activa no tiene sesión.
- Búsqueda de sesión para quien devuelve la llamada al número de la empresa (hoy: mensaje fijo o desvío).
- Limpieza opcional que no se hizo para no tocar código compartido: `VoiceProviderGuard` repite el guard de mensajería
  (`MessagingProviderGuard`) y `send()` de `voice-api.ts` repite la fontanería de las otras API de la PWA.

## Reversión

`Voice:Provider=Disabled`. Los teléfonos guardados se pueden borrar desde "Cuenta" aun con el puente apagado. El down
de la migración se niega mientras haya datos (`VOICE001_DOWNGRADE_BLOCKED_DATA_PRESENT`).
