# Avisos al destinatario (WhatsApp y correo)

Decisión: `NTF-RECIPIENT-NOTICES-2026-10-20` (E4 "Los 4 del plan (Recommended)" y las respuestas E4-P1 a E4-P16,
citadas en `docs/normative/v0.6/decision-log.md`). ADR:
[`docs/adr/ADR-NTF-002-RECIPIENT-NOTICES.md`](../adr/ADR-NTF-002-RECIPIENT-NOTICES.md). Contrato de ejecución:
AI-24 `recipient_notices`. Ítems de backlog: AI-08 NTF-002 a NTF-008, más las extensiones de TRK-002 y REL-001.

**Estado: contratos (PR-0) y, con AI-08 NTF-002 (PR-1), el correo opcional del destinatario, las columnas de
Locations y el sellado con la llave de avisos solo para envolver, apagado por omisión y en el piloto
([`ntf-002-recipient-contact.md`](ntf-002-recipient-contact.md)); con AI-08 NTF-003 (PR-2), el carril de notificaciones
([`ntf-003-notifications-lane.md`](ntf-003-notifications-lane.md)); con AI-08 NTF-005 (PR-4), la entrega en el Worker y
la función de conteo de los topes ([`ntf-005-recipient-notice-delivery.md`](ntf-005-recipient-notice-delivery.md),
`NTF-RECIPIENT-NOTICE-CAPS-2026-10-23`); con AI-08 NTF-004 (PR-3), el productor en ORD-002, la casilla de WhatsApp en
"Nueva orden" y en la carga CSV y la tabla de permisos que lee el Worker
([`ntf-004-recipient-notice-producer.md`](ntf-004-recipient-notice-producer.md), `NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23`).
Nada se envía ni está encendido.** GATE-004 y
GATE-007 siguen abiertas; solo el cambio que registre sus filas de resolución enciende los avisos.

## Arquitectura

```
Transacción ORD-002 (organización dueña, ya existe)          Worker (carril Notifications, transacciones propias)
  UPDATE de la orden + order_events + auditoría                claim_notifications_outbox (lease_token)
  + orders.status-changed (REALTIME)                             │ orders.recipient-notice-requested
  + dispatch.order-status-reaction-requested (DISPATCH, D8)      ▼
  + NUEVO orders.recipient-notice-requested (NOTIFICATIONS) ──►  expandir: interruptores, canales, horario, caducidad
                                                                 → security.expand_recipient_notice_request
                                                                 → una Notification por canal (sin PII) + send-requested
                                                                 │ notifications.send-requested
                                                                 ▼
                                                                enviar: vigencia → contacto en memoria → baja
                                                                 → enlace en memoria → IMessagingProvider.SendAsync
                                                                 → apply_notification_outcome v2 (lease_token)
                                                                 → notifications.status-changed (REALTIME → OperationsHub)
Webhooks de la API (Meta, Event Grid de ACS, baja del correo) ── firmados ──► funciones SECURITY DEFINER
```

- **Sin flujo nuevo** (siguen los seis de AI-13 §4). Orders escribe una fila más de outbox en la misma transacción de ORD-002, con un topic propio y
  un solo consumidor, como `D8-OUTBOX-LANE-DISPATCH` (AI-13 §7.2). Expansión, envío, acuses y bajas escriben solo filas
  de Notifications más el outbox y la auditoría compartidos, y leen Orders y Locations bajo RLS.
- **Sin transacción abierta durante la llamada al proveedor**: leer, confirmar, llamar y liquidar con `lease_token`,
  como NTF-001 y VOICE-001.
- **El productor escribe siempre** que la transición coincide; con los avisos apagados, el Worker liquida la fila al
  momento con `RECIPIENT_NOTICES_DISABLED`. Por eso el consumidor (PR-4) se fusiona antes que el productor (PR-3).

### Los cuatro avisos

| Aviso (plantilla v1) | Transición AI-04 | Se manda solo si al enviarse |
| --- | --- | --- |
| Enlace de seguimiento `recipient.tracking-link` | `DRAFT → CONFIRMED` (E4-P4) | el estado público no es `DELIVERED`, `RETURNED` ni `CANCELLED` |
| Va en camino `recipient.in-transit` | `PICKED_UP → IN_TRANSIT`, "Iniciar traslado" (E4-P3) | la orden sigue `IN_TRANSIT` |
| No se pudo entregar `recipient.delivery-failed` | `IN_TRANSIT → FAILED_ATTEMPT`, `DELIVERING → FAILED_ATTEMPT`; nunca `AT_PICKUP → FAILED_ATTEMPT` | la orden no está `DELIVERING` ni entregada |
| Entregado `recipient.delivered` | `DELIVERING → DELIVERED` | el estado público es `DELIVERED` |

Si ya no aplica termina `CANCELLED` con `NOTICE_SUPERSEDED`, sin llamar al proveedor. "No se pudo entregar" nunca dice
el motivo (E4-P5). Cada mensaje lleva solo el número de guía y el enlace de seguimiento.

### Canales, permiso y horario

- WhatsApp solo con la casilla "El destinatario aceptó recibir avisos de su envío por WhatsApp" marcada al crear la
  orden (E4-P1; también una casilla para todo el archivo en la carga CSV). Queda quién y cuándo, en una tabla
  append-only.
- Correo solo si el destino tiene correo (campo opcional nuevo en "Dónde"). Con los dos, se mandan los dos (E4-P11).
- De 8:00 a 21:00 hora de `America/Mazatlan`; lo de fuera espera a las 8:00 y se manda solo si sigue aplicando
  (E4-P10). Un aviso de más de 12 h (sin contar esa espera) no se manda. Topes: 8 por orden y canal, 500 por canal al
  día en toda la plataforma, contados con `security.read_recipient_notice_counts` (`NTF-RECIPIENT-NOTICE-CAPS-2026-10-23`).

### Datos personales y llaves

- Al cotizar, la API sella el teléfono y el correo del destino con la llave PII (como hoy el teléfono) y además una
  copia con la llave de avisos `recipient-notice-kek` (E4-P2). La API solo envuelve con esa llave; el Worker solo la
  desenvuelve y nunca tiene la llave PII.
- El Worker descifra en memoria, después de revisar vigencia y baja, y suelta los valores tras la llamada. Nunca van a
  logs, outbox, notificaciones, eventos, auditoría ni trazas.
- El enlace se deriva en el Worker con `public-tracking-link-key` y se compara con el hash guardado; nunca se guarda.
- Las bajas se guardan como HMAC-SHA256 con `paquetenvia-contact-lookup-key-1`, nunca el número ni la dirección.

### Bajas, respuesta automática y acuses

- Bajas globales (E4-P8): responder BAJA, ALTO, STOP o CANCELAR, o tocar "No recibir avisos" en WhatsApp; el enlace de
  cada correo (y `List-Unsubscribe` de un clic); "Detener avisos" en la orden.
- Confirmación de la baja (E4-P15): una baja por WhatsApp recibe una sola vez "Listo. Ya no recibirás avisos en este
  número.", por la misma vía que la respuesta automática (texto libre dentro de la ventana de atención, número sellado
  que el Worker manda y borra, descarte a las 23 h), solo cuando ese mensaje registra la baja y fuera del límite de una
  respuesta automática cada 24 h. La página de baja del correo la confirma en pantalla; "Detener avisos" no manda nada.
- La baja es definitiva durante el piloto (E4-P16): ni una casilla, ni una orden, ni despacho, ni un mensaje del
  destinatario la reactivan. Volver a darse de alta se revisa al terminar el piloto.
- Respuesta automática (E4-P9): una vez por contacto cada 24 h, como texto libre dentro de la ventana de atención:
  "Este número solo envía avisos. Para dudas sobre tu envío contacta a quien te lo envió." La manda el Worker; la
  confirmación de la baja no cuenta en ese límite.
- Acuses: Meta (`X-Hub-Signature-256`) y Event Grid de ACS marcan `DELIVERED` o `FAILED` solo hacia adelante, con un rol
  nuevo `paqueteria_notification_receipt_executor`. Antes de los acuses, `SENT` solo significa aceptado por el
  proveedor.
- El despachador ve "Avisos al destinatario" en el detalle de la orden y recibe `NotificationStatusChanged` v2.

### Cómo quedan los contratos en PR-0

| Archivo | Qué registra |
| --- | --- |
| AI-24 | Sección `recipient_notices` completa (avisos, productor, canales, permiso, horario, llaves, textos, bajas, respuesta automática, acuses, interruptores, reversión); la llave del enlace también la lee el Worker |
| AI-05 | Entrada `DECIDED` `NTF-RECIPIENT-NOTICES` de `x-pilot-contract-deltas`: correo en `DestinationAddressInput`, `recipient_notices` en `createOrder` y en la carga CSV, cuatro operaciones de webhooks y baja, dos del despachador y su fila de capacidades. La superficie HTTP no cambia hasta que cada ítem la publique con código y pruebas |
| AI-06, AI-18 | Contrato declarado en comentarios; el DDL entra con cada paso de carril y sus aserciones de línea base |
| AI-12 | `decided_amendment` de `NotificationStatusChanged` (payload v2) |
| AI-04 | Correo como dato sensible de `Location`, invariantes de privacidad y de outbox; sin estados nuevos |
| AI-07 | Campo de correo, casillas de permiso, panel "Avisos al destinatario" y la página pública `/avisos/baja/:token` |
| AI-08, AI-09, AI-03, AI-13 | Ítems NTF-002 a NTF-008, TRK-002 y REL-001; escenarios de aceptación; puerto e interruptores; adenda §7.2 |

## Secuencia de PR

Cada PR es un borrador hacia `development`, con un ítem de backlog. Los PR de backend esperan la remediación
SEC-2026-13 (back-sync #286): hasta entonces fallan al restaurar con NU1903.

| # | Ítem | Contenido | Migración (ida y vuelta en PostgreSQL real) | ¿Antes de GATE-004? |
| --- | --- | --- | --- | --- |
| PR-0 | docs | Este cambio: fila de decisión, contratos, ADR, CHANGELOG, integridad | ninguna | sí |
| PR-1 | NTF-002 | `DestinationAddressInput.email` y `RecipientEmailPolicy`; hash de la cotización solo con correo; columnas de Locations y su DDL en AI-06; protector de la llave de avisos; campo del asistente; `docs/legal` | carril Locations; el `Down` se niega con valores | sí (sin datos reales antes de GATE-007) |
| PR-2 | NTF-003 | Carril Notifications: plantillas, columnas, estados, almacén de bajas, `resolve_outbox_consumer`, funciones v2; parser de tiempo real y AI-12 v2 | carril Notifications; el `Down` se niega con filas externas o bajas | sí (inerte, sin productor) |
| PR-4 | NTF-005 | Envío en el Worker: rutas por canal, expansión, horario, caducidad, topes, lectores, descifrado en memoria, enlace, bajas, botón URL y respuesta rápida, correo con `List-Unsubscribe`, ids de decisión al iniciar | carril Notifications `20261023000100_AddRecipientNoticeCounts` (función de conteo de los topes y dos índices, L1; el `Down` solo los quita) | sí (todo apagado) |
| PR-3 | NTF-004 | `RecipientNoticeRequestPolicy`, INSERT en ORD-002, permiso de WhatsApp en `createOrder` y CSV, tabla de permisos, casillas | carril Orders `20261023000200_AddRecipientNoticeConsents` (append-only; el `Down` se niega con filas) | sí, después de PR-4 |
| PR-5 | NTF-006 | Webhooks de Meta y Event Grid, acuses, bajas, respuesta automática, baja por correo y su página, rol `paqueteria_notification_receipt_executor` con su DDL en AI-18 | carril Notifications (acuses) | sí (cargas firmadas falsas) |
| PR-6 | NTF-007 | `listOrderRecipientNotices`, `stopOrderRecipientNotices`, panel y tiempo real | ninguna | sí |
| PR-7 | NTF-008 | `apps.bicep` (secretos y roles del Worker y la API sobre la llave de avisos, rol de ACS, Event Grid, URLs), guardas del piloto, guía del owner | ninguna | solo después de que el owner cree secretos y recursos |
| PR-8 | puerta | Filas de resolución de GATE-004 y GATE-007, interruptores, ids de decisión, nombres de plantillas aprobadas por Meta, textos de correo, `docs/legal` final | ninguna | no: necesita las dos puertas |

Orden: PR-0 → (PR-1 ∥ PR-2) → PR-4 → PR-3 → PR-5 → PR-6 → PR-7 → PR-8. PR-3 y PR-4 pueden ir en una misma promoción.

## Acciones del owner

1. **Antes del 2026-10-23**: crear los recursos Communication Services y Email Communication Services en la
   suscripción del piloto (`NTF-EMAIL-ACS-PILOT-2026-10-11`; desde esa fecha Microsoft limita las altas nuevas). Luego
   verificar `paquetenvia.com` (registros SPF, DKIM y DMARC) y crear el remitente `avisos@paquetenvia.com` con el
   nombre visible "Seguimiento de envío" y sin buzón de respuestas (E4-P12).
2. **Meta**: portafolio de negocio y verificación del negocio, cuenta de WhatsApp Business, un número que no se use en
   la app de WhatsApp, el nombre visible = la razón social (E4-P7), un token de usuario del sistema y el app secret.
   Revisar los cuatro textos de AI-24 `recipient_notices.templates` en este PR y después mandarlos a revisión de Meta
   (categoría utilidad, `es_MX`).
3. **Key Vault**: la llave RSA `recipient-notice-kek`, el secreto `paquetenvia-contact-lookup-key-1`, el token y el id
   del número de WhatsApp, el app secret y el token de verificación del webhook (los nombres exactos los fija PR-7).
   La llave del enlace `public-tracking-link-key` ya existe; PR-7 se la da también al Worker.
4. **Abogado (GATE-007)**: revisar los puntos de abajo y registrar la resolución de GATE-007.

## Puntos legales para GATE-007 (`docs/legal/`)

1. Aviso integral §3.1: agregar "Correo electrónico (opcional)" del destinatario y quitar "Hoy no recabamos del
   destinatario: correo electrónico"; describir qué se manda (guía, enlace y estado; nunca nombre ni dirección).
2. §4.1 finalidad 7 (avisos por WhatsApp y correo) pasa a activa; sin finalidades secundarias ni mercadotecnia
   (E4-P13; plantillas solo de utilidad).
3. §5 base legal para el destinatario y el permiso que exige Meta: el texto de la casilla, su versión
   `NTF-RECIPIENT-WHATSAPP-CONSENT-V1` y la evidencia (quién y cuándo). ¿Los términos deben obligar a la empresa
   cliente a garantizar que puede compartir los datos del destinatario?
4. Primer contacto: el primer mensaje (el enlace) debe llevar o enlazar el aviso simplificado; afecta el texto y su
   largo (`privacy_line` en AI-24).
5. §6.1 encargados: Meta (WhatsApp Cloud API, datos fuera de México, retención de metadatos de Meta) y Microsoft ACS
   (ubicación de datos del recurso); contratos, cláusulas y transferencia internacional.
6. Bajas como mecanismo de oposición: BAJA, el enlace del correo y "Detener avisos"; la lista de bajas guarda un hash
   con llave (registro seudónimo nuevo) y su retención; la baja es definitiva durante el piloto (E4-P16).
7. `reglas-de-retencion-borrador.md`: notificaciones y eventos de estado, hashes de bajas, la tabla de permisos, las
   copias cifradas del contacto (un borrado criptográfico cubre todas) y los registros de Meta y ACS. La retención de
   las notificaciones debe cubrir el tiempo en que el enlace de baja del correo debe seguir funcionando.
8. Identificación del responsable en los mensajes mientras GATE-001 siga neutral (E4-P7 y E4-P12 interactúan).
9. Un mensaje reenviado expone la página de seguimiento mientras la orden vive y 24 h después; decirlo en el aviso.

## Para revisión del owner en este PR

- Los cuatro textos de WhatsApp y los cuatro cuerpos y asuntos de correo (AI-24 `recipient_notices.templates`), antes
  de mandarlos a Meta. La ronda F (E4-P14) corrigió los dos puntos señalados: "No se pudo entregar" ya no empieza con
  "Hoy", y el correo, que no tiene botones, dice "en el enlace de abajo" en lugar de "con el botón Ver seguimiento".
- La casilla de la carga CSV usa el texto aprobado para todo el archivo; en PR-3 el owner eligió el mismo texto (versión
  V1) con la nota fija "Aplica a todas las filas de este archivo. Márcala solo si cada destinatario lo aceptó."
  (`NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23`, N2).
- La página de baja del correo y sus mensajes se revisan en PR-5.

## Costos (estimación del plan, 2026-10-19)

Con 20 entregas al día: unos 1,950 mensajes de WhatsApp al mes a USD 0.0085 (utilidad, México) ≈ USD 16.6; correo
≈ USD 0.49; llave de Key Vault ≈ USD 0.07. Total ≈ USD 17 a 27 al mes. Meta solo cobra mensajes entregados.

## Riesgos residuales

- Permiso de Meta: el teléfono lo da quien envía, no quien recibe; la casilla y su registro lo mitigan (GATE-007).
- Nombre visible o plantillas rechazados por Meta, o reclasificados como mercadotecnia (×4.7 el costo).
- El Worker gana la llave del enlace de seguimiento: puede derivar el enlace de cualquier orden, con el mismo alcance
  que su lectura de la base.
- Sin acuses (hasta PR-5) un fallo asíncrono de WhatsApp (131026) no se ve; un acuse que llega antes de liquidar el
  envío se ignora.
- Órdenes confirmadas antes de encender no reciben el enlace; canceladas o devueltas no reciben aviso.
- Una baja por error (BAJA, el enlace del correo o "Detener avisos") no se puede deshacer durante el piloto (E4-P16).

## Reversión

`Notifications:RecipientNotices:Enabled=false` o un proveedor en `Disabled`, sin migración: lo pendiente termina
`FAILED` con `MESSAGING_CHANNEL_DISABLED` y el despachador lo ve; los webhooks apagados responden 404. Cada `Down` de
carril se niega mientras haya filas que necesiten la forma nueva. PR-0 se revierte con un revert de su commit (y vuelve
a fijar los hashes de AI-05, AI-06 y AI-18).
