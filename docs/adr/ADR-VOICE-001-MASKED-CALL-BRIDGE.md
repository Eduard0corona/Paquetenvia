# ADR-VOICE-001: llamadas enmascaradas repartidor–destinatario con Twilio

- Estado: aceptado (VOICE-001-MASKED-CALLS-2026-10-11, VOICE-001-PROVIDER-TWILIO-2026-10-11,
  VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11)
- Fecha: 2026-10-11
- Alcance: Drivers (columna cifrada del celular del repartidor, tabla `drivers.recipient_call_requests`, servicios y
  endpoints), puerto `IVoiceBridgeProvider` con adaptador Twilio y fake determinista, PWA del repartidor ("Cuenta" y
  "Llamar al destinatario"), guardas del piloto. Ítem de backlog: VOICE-001 (AI-08), puerta GATE-007.

## Contexto

El repartidor no ve el teléfono del destinatario (AI-07 `driver_stop`: "masked phone or relay"). Preguntas al project
owner, literales:

1. 2026-10-10: "App del repartidor: hoy no ve el teléfono del destinatario. ¿Debe poder llamarle desde la app?" →
   "Sí, con número enmascarado" (descripción de la opción: "Requiere contratar un servicio de llamadas puente, con costo
   mensual y decisión de proveedor. Lo preparo cuando lo tengas.").
2. 2026-10-10: "Número enmascarado: … ¿Cómo seguimos?" → "Investiga y propón (Recommended)".
3. 2026-10-11: "Número enmascarado: comparé proveedores. Azure Communication Services queda descartado: no vende
   números mexicanos para llamar y Microsoft lo va a retirar. ¿Con cuál seguimos?" → "Twilio (Recommended)"
   (descripción: "El sistema llama al celular del repartidor desde el número de la empresa y lo enlaza con el
   destinatario; nadie ve el número del otro. Unos US$35–43 al mes con 5 llamadas al día, US$177–185 con 30. Tú creas
   la cuenta y entregas documentos de la empresa (CSF y comprobante de domicilio, entre otros).").
4. 2026-10-11 (revisión de código del PR #220): "Llamadas enmascaradas: si contesta un buzón de voz, el saludo del
   buzón puede decir el número del repartidor o del destinatario, y eso rompe el enmascarado. ¿Cómo lo evitamos?" →
   "Confirmar con una tecla (Recommended)" (descripción: "Al repartidor le dice "Presiona 1 para llamar al
   destinatario". Al destinatario: "Tiene una llamada sobre su envío; presione 1 para contestar" (texto neutro, sin
   marca). Si nadie presiona, se cuelga sin enlazar. Gratis y no falla con buzones.").

Es un cambio MAJOR (AI-01 §7): cambia la experiencia del repartidor, agrega un encargado externo de datos personales
(Twilio) y un costo mensual. Restricciones: los teléfonos son datos personales (ADP-001: sobre cifrado con Key Vault);
nunca en logs, snapshots, outbox, eventos ni auditoría; RLS forzado; solo los flujos atómicos de AI-13 §4; AI-06
y AI-18 no cambian sin necesidad estricta.

Precios verificados el 2026-10-10/11 ("as of August 2026"): número local US$6.25/mes, móvil US$15/mes, toll-free
US$30/mes; saliente a móviles de México US$0.0473/min, a fijos US$0.016/min; entrante US$0.01/min.

## Decisión

1. **Llamada puente del lado del servidor (Programmable Voice), confirmada con una tecla en cada tramo.** Un
   `POST /2010-04-01/Accounts/{AccountSid}/Calls.json` llama al celular del repartidor desde el número mexicano de la
   empresa. Su TwiML en línea solo dice "Presiona 1 para llamar al destinatario." y espera un dígito
   (`<Gather numDigits="1" actionOnEmptyResult="true">`, `Voice:Twilio:DriverConfirmSeconds`, 10 s por defecto); el
   número del destinatario no va en esa petición. Twilio envía el resultado, también sin tecla, al webhook firmado
   `confirmTwilioDriverKeypress`. Solo el primer 1 a tiempo de la llamada de la plataforma hace que el webhook descifre
   en memoria el teléfono del destinatario y responda `<Dial callerId="{empresa}" timeout timeLimit
   record="do-not-record"><Number url="…/recipient-screen">`. Cuando el destinatario contesta, `screenTwilioRecipient`
   le dice solo "Tiene una llamada sobre su envío; presione 1 para contestar." (texto neutro, sin marca, GATE-001) y
   espera un dígito (`Voice:Twilio:RecipientConfirmSeconds`); los dos tramos se conectan solo después de ese 1
   (`confirmTwilioRecipientKeypress` responde un TwiML vacío). Sin tecla (buzón, tiempo agotado u otro dígito) se cuelga
   sin enlazar: el saludo de un buzón, que puede decir un número, nunca llega a la otra parte. Ambos ven solo el número
   de la empresa. Números como `+52` y diez dígitos (el primero 2–9). Sin grabación (`Record=false`), sin
   transcripción, sin detección de contestadora (costo extra). No se usa Twilio Proxy (Public Beta sin SLA) ni llamadas
   en el navegador (los navegadores móviles no mantienen WebRTC en segundo plano).
2. **Puerto y adaptadores.** `IVoiceBridgeProvider` (Paqueteria.Application) con resultados cerrados
   (`VoiceBridgeResultCodes`) y valores redactados (`VoicePhoneNumber.ToString()` es `[redacted]`). Adaptador Twilio
   con timeout por intento, circuit breaker y bulkhead (AI-03 §16, el mismo breaker de GATE-004) y sin reintentos
   (crear una llamada no es idempotente). Un timeout, una respuesta rota o cualquier 5xx pueden llegar después de que
   Twilio creó la llamada: son `Ambiguous`, la solicitud queda `UNCONFIRMED`, conserva su llave y cuenta para los
   límites. Solo un fallo que prueba que no se creó nada (sin conexión antes de enviar, circuito abierto, límite de
   concurrencia, 4xx con código de Twilio, 401, 403 o 429) termina `FAILED` y libera la llave. Fake sintético determinista para pruebas, CI y DEV_SYNTHETIC. El cliente HTTP no
   registra solicitudes (`RemoveAllLoggers`). El mensaje de error de Twilio nunca se lee (puede repetir el número);
   solo su `code`.
3. **Configuración y puertas.** `Voice:Provider` es `Disabled` por defecto. `Twilio` exige `AccountSid`, `AuthToken`
   (y opcionalmente API key y secreto), `CompanyNumber`, `WebhookBaseUri`, y `Gate007DecisionId` con forma
   `GATE-007-…`; se rechaza en Development, Testing, DevSynthetic y en despliegues `DEV_SYNTHETIC`, y el sintético solo
   se acepta en Development/Testing/DEV_SYNTHETIC. Con Twilio, los teléfonos usan el sobre ADP-001 de Key Vault
   (`PiiProtection:AzureKeyVault`). Las credenciales llegan solo por `KeyVaultSecrets:Mappings`. En el piloto `Voice__`
   es una configuración administrada por la plataforma (`tools/azr-001/env001_pilot_guards.py`): no se enciende desde
   el archivo de settings.
4. **Celular del repartidor.** `drivers.driver_profiles` gana `phone_ciphertext`, `phone_pii_key_version`,
   `phone_consent_version` y `phone_consented_at` (todas nulas o todas presentes, `driver_profiles_phone_check`). El
   repartidor lo registra en "Cuenta" con el consentimiento `VOICE-001-CONSENT-V1` (texto en AI-07), solo con el puente
   habilitado y con su perfil `ACTIVE`, y lo puede consultar y borrar siempre, con cualquier estado de su perfil
   (ARCO: un repartidor `SUSPENDED`, `INACTIVE` o `PENDING` conserva el derecho a borrarlo). Se cifra con el sobre
   ligado a (organización, perfil, `drivers.phone`) fuera de toda transacción; la API nunca lo devuelve
   (`getMyDriverPhone` solo dice si existe y desde cuándo).
5. **Solicitud de llamada.** `requestRecipientCall` (`POST /driver/me/stops/{orderId}/recipient-call`): solo DRIVER
   (`x-capability-matrix.voice_call_operations`, 403 uniforme antes de leer estado), sin cuerpo ni query, con un único
   `Idempotency-Key`. El id de la solicitud se elige antes de la transacción con reintentos. Transacción 1 (tenant):
   perfil DRIVER activo (403), idempotencia (repetición o `IDEMPOTENCY_CONFLICT`; si la reserva es la propia de este
   intento, porque el commit se confirmó pero su respuesta se perdió, se sigue con la llamada reservada), asignación
   propia `ACCEPTED`/`ACTIVE` de una orden visible para la organización bajo RLS (404 uniforme: orden desconocida, ajena
   o de otro repartidor), orden en `DELIVERING`, ambos teléfonos guardados y límites (3 por orden en 15 min y 20 por
   hora por repartidor; 429 con `Retry-After`, y `retry_after_seconds` en la disponibilidad `RATE_LIMITED`); luego la
   reserva de idempotencia, la fila `REQUESTED` y la auditoría `RECIPIENT_CALL_REQUESTED`. Sin transacción abierta:
   descifrado en memoria y una llamada al proveedor. Transacción 2: resultado (`PLACED`, `UNCONFIRMED` o `FAILED` con su
   código) una sola vez, idempotencia (202 guardado; un `FAILED` libera la llave) y auditoría del resultado. Una
   solicitud que quedó `REQUESTED` entre sus transacciones (por ejemplo, por un reinicio) se cierra `UNCONFIRMED` con
   `VOICE_OUTCOME_UNKNOWN` en la siguiente repetición de su llave pasados `Drivers:RecipientCalls:StaleRequestSeconds`
   (300 s por defecto). La respuesta es solo `{call_request_id, status}`.
6. **Webhooks firmados.** `receiveTwilioCallStatus`, `answerTwilioInboundCall`, `confirmTwilioDriverKeypress`,
   `screenTwilioRecipient` y `confirmTwilioRecipientKeypress` son anónimos, sin CSRF, inertes (404) salvo con Twilio
   configurado, y exigen `X-Twilio-Signature` (HMAC-SHA1 del URL público configurado más los campos ordenados) y el
   `AccountSid` configurado; si no, 403. El callback de estado guarda solo el CallSid, el estado final y la duración
   (auditoría `RECIPIENT_CALL_STATUS_REPORTED` sin actor) y promueve a `PLACED` solo una solicitud `REQUESTED`, `PLACED`
   o `UNCONFIRMED`, nunca una `FAILED`. Las teclas cuentan solo para una solicitud que puede sonar, dentro de 15 minutos
   y ligadas a la llamada de la plataforma (`CallSid`; `ParentCallSid` en el tramo del destinatario); una tecla
   repetida, tardía o ajena cuelga sin cambiar nada; un fallo del almacén también cuelga. Quien llama al número de la
   empresa escucha un mensaje fijo o se desvía a la línea de despacho configurada con su propio timbrado y límite
   (`Voice:Twilio:InboundForwardRingSeconds`, `InboundForwardMaximumSeconds`), nunca los del puente; no hay búsqueda por
   teléfono.
7. **Datos.** `drivers.recipient_call_requests` (carril Drivers `20261011000100_AddDriverVoiceBridge`) guarda quién,
   orden, asignación, proveedor, estado, código, CallSid, estado final, duración y hasta dónde llegó la confirmación
   con tecla (`bridge_stage`: `DRIVER_CONFIRMED`, `DRIVER_NOT_CONFIRMED`, `BRIDGE_FAILED`, `RECIPIENT_CONFIRMED`,
   `RECIPIENT_NOT_CONFIRMED`); ningún número. FORCE RLS por `org_id`, sin privilegios del Worker, sin `DELETE` en
   runtime, sin llave foránea hacia Orders o Dispatch. AI-06 y AI-18 no cambian (objetos propios del carril, como
   NTF-001). El rollback se niega mientras haya solicitudes o teléfonos guardados.
8. **Sin flujo atómico nuevo.** Cada transacción escribe solo filas de Drivers más idempotencia y auditoría
   compartidas; Dispatch, Orders y Locations se leen bajo RLS y nunca se escriben.

## Alternativas descartadas

- Azure Communication Services: no vende números mexicanos para llamadas y el owner registró su retiro.
- Twilio Proxy: Public Beta sin SLA.
- Llamada desde el navegador (Voice JS SDK/WebRTC): se corta en segundo plano en móviles y expone tokens al cliente.
- Mostrar el número o abrir `tel:`: contradice AI-07 ("masked phone or relay").
- Grabar llamadas: no se pidió y agrega datos personales sensibles.
- Detección de contestadora de Twilio (AMD) para el buzón: cuesta por llamada y se equivoca con algunos buzones; el
  owner eligió la confirmación con tecla.
- Enlazar al contestar sin confirmación: el saludo de un buzón puede decir el número del repartidor o del destinatario.

## Consecuencias

- Twilio pasa a ser encargado de datos personales: recibe los dos números en cada llamada y guarda sus propios
  registros. Antes de usar números reales hace falta resolver GATE-007 con un aviso de privacidad que lo nombre, la
  transferencia internacional y la retención de esos registros.
- Costo mensual: un número (US$6.25–15) más minutos; US$35–43 al mes con 5 llamadas diarias, US$177–185 con 30. La
  confirmación con tecla no agrega costo de Twilio (solo los segundos del aviso).
- El repartidor debe presionar 1 y el destinatario también; si alguno no lo hace, la llamada se cuelga sin enlazar y el
  repartidor puede pedir otra dentro de los límites.
- Pendientes fuera de VOICE-001: borrado del celular por un administrador o en la baja del repartidor cuando su
  membresía ya no está activa (hoy el repartidor lo borra él mismo con cualquier estado de su perfil), y búsqueda de
  sesión para quien devuelve la llamada al número de la empresa.

## Reversión

`Voice:Provider=Disabled` (por defecto): no se llama, no se recolectan teléfonos y los webhooks responden 404. El
rollback de la migración se niega con `VOICE001_DOWNGRADE_BLOCKED_DATA_PRESENT` mientras existan datos.
