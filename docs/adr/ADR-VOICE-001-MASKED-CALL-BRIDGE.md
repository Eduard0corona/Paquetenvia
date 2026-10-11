# ADR-VOICE-001: llamadas enmascaradas repartidor–destinatario con Twilio

- Estado: aceptado (VOICE-001-MASKED-CALLS-2026-10-11, VOICE-001-PROVIDER-TWILIO-2026-10-11)
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

Es un cambio MAJOR (AI-01 §7): cambia la experiencia del repartidor, agrega un encargado externo de datos personales
(Twilio) y un costo mensual. Restricciones: los teléfonos son datos personales (ADP-001: sobre cifrado con Key Vault);
nunca en logs, snapshots, outbox, eventos ni auditoría; RLS forzado; solo los cinco flujos atómicos de AI-13 §4; AI-06
y AI-18 no cambian sin necesidad estricta.

Precios verificados el 2026-10-10/11 ("as of August 2026"): número local US$6.25/mes, móvil US$15/mes, toll-free
US$30/mes; saliente a móviles de México US$0.0473/min, a fijos US$0.016/min; entrante US$0.01/min.

## Decisión

1. **Llamada puente del lado del servidor (Programmable Voice).** Un `POST /2010-04-01/Accounts/{AccountSid}/Calls.json`
   llama al celular del repartidor desde el número mexicano de la empresa; el TwiML en línea dice un saludo fijo en
   español sin datos personales y luego `<Dial callerId="{empresa}" timeout timeLimit record="do-not-record">` al
   destinatario. Ambos ven solo el número de la empresa. Números como `+52` y diez dígitos (el primero 2–9). Sin
   grabación (`Record=false`), sin transcripción. No se usa Twilio Proxy (Public Beta sin SLA) ni llamadas en el
   navegador (los navegadores móviles no mantienen WebRTC en segundo plano).
2. **Puerto y adaptadores.** `IVoiceBridgeProvider` (Paqueteria.Application) con resultados cerrados
   (`VoiceBridgeResultCodes`) y valores redactados (`VoicePhoneNumber.ToString()` es `[redacted]`). Adaptador Twilio
   con timeout por intento, circuit breaker y bulkhead (AI-03 §16, el mismo breaker de GATE-004) y sin reintentos
   (crear una llamada no es idempotente: un timeout es `Ambiguous` y la solicitud queda `UNCONFIRMED`). Fake sintético
   determinista para pruebas, CI y DEV_SYNTHETIC. El cliente HTTP no registra solicitudes (`RemoveAllLoggers`). El
   mensaje de error de Twilio nunca se lee (puede repetir el número); solo su `code`.
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
   habilitado, y lo puede borrar siempre. Se cifra con el sobre ligado a (organización, perfil, `drivers.phone`) fuera
   de toda transacción; la API nunca lo devuelve (`getMyDriverPhone` solo dice si existe y desde cuándo).
5. **Solicitud de llamada.** `requestRecipientCall` (`POST /driver/me/stops/{orderId}/recipient-call`): solo DRIVER
   (`x-capability-matrix.voice_call_operations`, 403 uniforme antes de leer estado), sin cuerpo ni query, con un único
   `Idempotency-Key`. Transacción 1 (tenant): perfil DRIVER activo (403), idempotencia (repetición o
   `IDEMPOTENCY_CONFLICT`), asignación propia `ACCEPTED`/`ACTIVE` de una orden visible para la organización bajo RLS
   (404 uniforme: orden desconocida, ajena o de otro repartidor), orden en `DELIVERING`, ambos teléfonos guardados y
   límites (3 por orden en 15 min y 20 por hora por repartidor; 429 con `Retry-After`); luego la reserva de
   idempotencia, la fila `REQUESTED` y la auditoría `RECIPIENT_CALL_REQUESTED`. Sin transacción abierta: descifrado en
   memoria y una llamada al proveedor. Transacción 2: resultado (`PLACED`, `UNCONFIRMED` o `FAILED` con su código),
   idempotencia (202 guardado; un `FAILED` libera la llave) y auditoría del resultado. La respuesta es solo
   `{call_request_id, status}`.
6. **Webhooks firmados.** `receiveTwilioCallStatus` y `answerTwilioInboundCall` son anónimos, sin CSRF, inertes (404)
   salvo con Twilio configurado, y exigen `X-Twilio-Signature` (HMAC-SHA1 del URL público configurado más los campos
   ordenados) y el `AccountSid` configurado; si no, 403. El callback de estado guarda solo el CallSid, el estado final y
   la duración (auditoría `RECIPIENT_CALL_STATUS_REPORTED` sin actor). Quien llama al número de la empresa escucha un
   mensaje fijo o se desvía a la línea de despacho configurada; no hay búsqueda por teléfono.
7. **Datos.** `drivers.recipient_call_requests` (carril Drivers `20261011000100_AddDriverVoiceBridge`) guarda quién,
   orden, asignación, proveedor, estado, código, CallSid, estado final y duración; ningún número. FORCE RLS por
   `org_id`, sin privilegios del Worker, sin `DELETE` en runtime, sin llave foránea hacia Orders o Dispatch. AI-06 y
   AI-18 no cambian (objetos propios del carril, como NTF-001). El rollback se niega mientras haya solicitudes o
   teléfonos guardados.
8. **Sin sexto flujo atómico.** Cada transacción escribe solo filas de Drivers más idempotencia y auditoría
   compartidas; Dispatch, Orders y Locations se leen bajo RLS y nunca se escriben.

## Alternativas descartadas

- Azure Communication Services: no vende números mexicanos para llamadas y el owner registró su retiro.
- Twilio Proxy: Public Beta sin SLA.
- Llamada desde el navegador (Voice JS SDK/WebRTC): se corta en segundo plano en móviles y expone tokens al cliente.
- Mostrar el número o abrir `tel:`: contradice AI-07 ("masked phone or relay").
- Grabar llamadas: no se pidió y agrega datos personales sensibles.

## Consecuencias

- Twilio pasa a ser encargado de datos personales: recibe los dos números en cada llamada y guarda sus propios
  registros. Antes de usar números reales hace falta resolver GATE-007 con un aviso de privacidad que lo nombre, la
  transferencia internacional y la retención de esos registros.
- Costo mensual: un número (US$6.25–15) más minutos; US$35–43 al mes con 5 llamadas diarias, US$177–185 con 30.
- Si el buzón del repartidor contesta, el destinatario podría quedar conectado a ese buzón; mitigación pendiente
  (confirmación con tecla o detección de contestadora, ver riesgos).

## Reversión

`Voice:Provider=Disabled` (por defecto): no se llama, no se recolectan teléfonos y los webhooks responden 404. El
rollback de la migración se niega con `VOICE001_DOWNGRADE_BLOCKED_DATA_PRESENT` mientras existan datos.
