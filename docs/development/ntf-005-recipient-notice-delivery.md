# NTF-005: entrega de avisos al destinatario en el Worker

Decisiones: `NTF-RECIPIENT-NOTICES-2026-10-20` y `NTF-RECIPIENT-NOTICE-CAPS-2026-10-23` (ronda L: L1 "Función de conteo
(Recommended)", L2 "Toda la plataforma (Recommended)", L3 "Destinatario inválido (Recommended)", L4 "Aprobarlas todas
(Recommended)"; citadas en `docs/normative/v0.6/decision-log.md`). Serie: [`ntf-recipient-notices.md`](ntf-recipient-notices.md),
PR-4. Contrato: AI-24 `recipient_notices`, AI-06 y AI-18 (paso 6 de avisos y aserción 39), AI-08 NTF-005.

**Estado: implementado y apagado.** `Notifications:RecipientNotices:Enabled` es falso y todos los proveedores están en
`Disabled`; ningún productor escribe todavía `orders.recipient-notice-requested` (NTF-004). GATE-004 y GATE-007 siguen
abiertas: nada llega a un cliente real.

## Flujo en el Worker

`NotificationsOutboxProcessor` entrega dos topics a `RecipientNoticeProcessor`:

- `orders.recipient-notice-requested` (expansión). Apagado: `RECIPIENT_NOTICES_DISABLED` sin leer nada. Encendido: lee
  bajo RLS la orden (número de guía y destino), si el destino tiene copias de aviso y el permiso de WhatsApp, y llama a
  `security.expand_recipient_notice_request` con los canales elegibles (WhatsApp primero, solo con permiso y copia del
  teléfono; correo con copia del correo; nunca un canal cuyo proveedor está `Disabled`).
- `notifications.send-requested` de un aviso `ORDER_DESTINATION` (envío), en el orden de AI-24 y antes de descifrar o
  llamar al proveedor: interruptores (`MESSAGING_CHANNEL_DISABLED`), horario (fuera de 08:00–21:00 la solicitud vuelve
  a `RETRY` a la siguiente apertura, sin intento), caducidad (`NOTICE_STALE`), vigencia con el estado actual
  (`NOTICE_SUPERSEDED`), topes (`NOTICE_LIMIT_REACHED`), contacto, baja (`RECIPIENT_OPTED_OUT`) y enlace
  (`TRACKING_LINK_UNAVAILABLE`). Luego una sola llamada a `IMessagingProvider` sin transacción abierta y el resultado con
  `apply_notification_outcome` v2 bajo el `lease_token`.

Cada lectura (orden, copias, conteo, bajas, reloj de Mazatlán) es su propia transacción corta y confirmada; ninguna
queda abierta durante la llamada. Una falla pasajera de la base o de Key Vault antes de llamar al proveedor vuelve a
`RETRY` con la espera de NTF-001 y sin intento; una solicitud que agota sus intentos termina `MAX_ATTEMPTS_EXHAUSTED`
por el camino existente.

## Topes (L1, L2)

Paso de carril Notifications `20261023000100_AddRecipientNoticeCounts` (ida, vuelta e ida en PostgreSQL 18 y PostGIS
3.6):

- `security.read_recipient_notice_counts(uuid,uuid)`: dueño `paqueteria_outbox_executor`, SECURITY DEFINER con
  `search_path=pg_catalog, platform, notifications, pg_temp`, sin SQL dinámico ni RETURNING, EXECUTE solo para
  `paqueteria_worker`. Responde solo bajo el `lease_token` vigente de una solicitud de envío cuyo aviso sigue `PENDING`, y
  devuelve una fila con dos enteros: los otros avisos de la misma organización, orden y canal con `attempts > 0`, y los
  otros avisos del canal en toda la plataforma con `attempts > 0` cuyo `last_attempt_at` cae en el día actual de
  `America/Mazatlan` (calculado en SQL). Sin lease vigente no devuelve fila.
- Índices parciales `notifications_recipient_order_channel_idx` y `notifications_recipient_attempt_day_idx` (AI-06 los
  declara byte a byte). Ninguna tabla, columna, fila, rol ni privilegio de tabla cambia; el Worker sigue sin privilegio
  sobre `notifications.notifications`.
- El Worker cancela con `NOTICE_LIMIT_REACHED` cuando `order_channel_count >= MaximumPerOrderAndChannel` (8) o
  `day_channel_count >= DailyLimitPerChannel` (500), con una línea de log de dimensiones permitidas.

`attempts > 0` es el conteo del propio `apply_notification_outcome`: un aviso que terminó `FAILED` sin llamar al
proveedor (por ejemplo `MESSAGING_CHANNEL_DISABLED`) también cuenta, así que un tope se alcanza igual o antes que con la
lectura literal.

## Contacto inutilizable (L3)

Si al enviar la copia del canal ya no existe, está alterada, pertenece a otra fila o usa una versión de la llave que el
Worker no abre, el aviso termina `FAILED` con `MESSAGING_RECIPIENT_INVALID` (permanente, sin llamada). Key Vault
inalcanzable no es este caso: se reintenta.

## Decisiones técnicas aprobadas (L4)

1. `UnrecordedRecipientNoticeConsentReader` responde "sin permiso" para toda orden hasta que NTF-004 cree
   `orders.recipient_notice_consents`: no se crea ningún aviso de WhatsApp; el correo no cambia.
2. `RecipientNoticeQuietHours`: 08:00:00 ya sale y 21:00:00 ya no (AI-24 `quiet_hours.window`, "from 08:00 (inclusive)
   to 21:00 (exclusive)"). La conversión de zona horaria se hace en SQL (`AT TIME ZONE`).
3. Fallas pasajeras antes del proveedor: `RETRY` con espera, sin intento.
4. Avisos apagados al enviar: `MESSAGING_CHANNEL_DISABLED`. El Worker no arranca con `MetaCloudApi` o
   `AzureCommunicationServices` en Development, Testing, DevSynthetic o DEV_SYNTHETIC, ni en ningún ambiente sin
   `Notifications:RecipientNotices:Gate004DecisionId` y `Gate007DecisionId`.
5. Solo el Worker abre `recipient-notice-kek` (`IRecipientNoticeKeyUnwrapClient`, solo `unwrapKey` RSA-OAEP-256 sobre la
   versión exacta de la copia; `PiiProtection:RecipientNoticeKey:KeyId`), y solo se construye con avisos encendidos y un
   proveedor real. Con proveedores sintéticos se usan `+520000000002` y `destinatario@example.invalid` sin descifrar.
6. `ContactLookup:Keys` y `CurrentKeyVersion` en el Worker; la baja se busca con el hash de cada versión configurada.
   NTF-008 la conecta a `paquetenvia-contact-lookup-key-1`.
7. Enlace de baja: Base64URL de versión de llave (2 bytes), organización (16), aviso (16) y HMAC-SHA256 con prefijo
   `paquetenvia-unsubscribe-v1` (88 caracteres). Página `{PublicTracking:PublicBaseUrl}/avisos/baja/<token>` (`{{3}}` del
   correo) y un clic `{PublicBaseUrl}/api/v1/recipient-notices/unsubscribe/<token>` en `List-Unsubscribe` con
   `List-Unsubscribe-Post: List-Unsubscribe=One-Click`. NTF-006 lo verifica con la misma regla. Como RFC 8058 exige
   https para ese enlace, con un proveedor de correo el Worker no arranca encendido sin un `PublicBaseUrl` https,
   tampoco en Development o Testing.
8. `RecipientNoticeTexts` tiene los textos de AI-24; `RecipientNoticeTextsContractTests` los compara con el YAML y el
   Worker no arranca si los textos de correo configurados difieren. La línea del aviso de privacidad entra con GATE-007.
9. Plantilla de Meta: botón índice 0 URL (sufijo = token del enlace) y botón índice 1 respuesta rápida con payload
   `RECIPIENT_NOTICE_OPT_OUT`. En ese orden se crean en WhatsApp Manager (guía de NTF-008).
10. Los ids de decisión se validan por formato: `^GATE-004-[A-Z0-9][A-Z0-9-]{2,76}$` y su par GATE-007.

## Lectores y llaves

- Orders publica `IRecipientNoticeOrderReader` (orden y enlace activo bajo RLS, `SET LOCAL ROLE paqueteria_worker`) e
  `IRecipientNoticeTrackingLinks` (deriva con `PublicTracking:LinkKeys` y compara el SHA-256 en tiempo constante; la
  llave sintética solo en Development y Testing).
- Locations publica el lector de las tres columnas de aviso; el Worker no registra el módulo Locations, ni la llave PII
  ni el sellador.
- Los clientes HTTP de Meta y ACS no tienen loggers; el cliente de Key Vault no registra contenido ni trazas.

## Pruebas

- Unitarias: `RecipientNoticeDeliveryTests` (rutas, redacción, horario con 07:59:59.9999999, 08:00:00,
  20:59:59.9999999 y 21:00:00, vigencia, enlace contra el hash, hash de contacto, token de baja, textos, expansión,
  interruptores, topes, bajas, WhatsApp ambiguo, reintento), `RecipientNoticeCountsAndOptionsTests` (paso de conteo,
  AI-06/AI-18/AI-24/AI-08, mapa de rutinas, validación al iniciar) y `Gate004MessagingProviderTests` (JSON de botones y
  encabezados de un clic).
- Contrato PostgreSQL: `RecipientNoticeDeliveryPostgreSqlContractTests` (catálogo de la función e índices, conteo bajo
  lease, por orden y canal, por día de Mazatlán en toda la plataforma, lectores bajo RLS y la corrida sintética de
  extremo a extremo por el `NotificationsOutboxProcessor` compuesto como en el Worker),
  `NotificationsMigrationGuardPostgreSqlTests` (ida, vuelta e ida del paso) y `RecipientNoticeLanePostgreSqlContractTests`
  (filas 08:00 y 21:00 del horario).
- Integración: `RecipientNoticeWorkerCompositionTests` (el Worker arranca encendido con proveedores sintéticos y rechaza
  un proveedor real en Testing).
- Arquitectura: solo el abridor del Worker desenvuelve la llave de avisos y solo su resolutor abre una copia; el Worker
  no registra la llave PII ni el módulo Locations; dimensiones de log sin PII.

## Riesgos residuales

- `attempts > 0` cuenta también los avisos que fallaron antes de llamar al proveedor (ver arriba).
- El tope diario es de toda la plataforma: una organización con muchos avisos puede agotar el día de las demás.
- Sin acuses (NTF-006), `SENT` solo significa aceptado por el proveedor.

## Reversión

`Notifications:RecipientNotices:Enabled=false` (por omisión) sin migración. El `Down` del paso quita la función y los
dos índices; revertir el PR restaura el texto anterior y vuelve a fijar los hashes de AI-06 y AI-18.
