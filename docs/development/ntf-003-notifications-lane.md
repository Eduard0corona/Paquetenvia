# NTF-003: carril de notificaciones para WhatsApp y correo

Decisión: `NTF-RECIPIENT-NOTICES-2026-10-20` (ya registrada; sin pregunta nueva). Serie: [`ntf-recipient-notices.md`](ntf-recipient-notices.md),
PR-2. Contrato: AI-06 y AI-18 (contrato declarado del paso NTF-003, ahora instalado), AI-12 `NotificationStatusChanged` v2,
AI-24 `recipient_notices`.

**Estado: instalado e inerte.** Ningún productor escribe `orders.recipient-notice-requested` (NTF-004) y ningún
interruptor se enciende. Desde NTF-005 el Worker consume esa fila y envía WhatsApp y correo, apagado por omisión
([`ntf-005-recipient-notice-delivery.md`](ntf-005-recipient-notice-delivery.md)).

## Qué instala el paso `20261020000100_AddRecipientNoticeLane`

| Objeto | Cambio |
| --- | --- |
| `notification_templates` | canal IN_APP, WHATSAPP o EMAIL; `body` solo en IN_APP; `notification_templates_catalog_check` cierra variables y canales por clave; cuatro claves `recipient.*` v1 en WhatsApp y correo, sin texto, por organización |
| `notifications` | `recipient_kind`, `order_event_id`, `delivered_at`; `recipient_user_id` solo en IN_APP; un aviso nunca guarda destinatario; `notifications_order_destination_uq` y `notifications_provider_reference_uq` |
| `notification_status_events` | estados DELIVERED y CANCELLED |
| `notifications.recipient_opt_outs` | hashes de 32 bytes, append-only, FORCE RLS sin política, sin privilegios de runtime; el ejecutor del outbox solo lee |
| `resolve_outbox_consumer` | `orders.recipient-notice-requested` → NOTIFICATIONS |
| `emit_notification_status_changed` | payload `notification-status-changed-v2` (`order_id` y `notice` solo en un aviso) |
| funciones nuevas o v2 | ver abajo; dueño `paqueteria_outbox_executor`, SECURITY DEFINER, EXECUTE solo `paqueteria_worker` |

Las tablas siguen siendo del carril (AI-06 lo dice desde NTF-001): el DDL vive en el paso y AI-06/AI-18 lo declaran con
los mismos nombres; una prueba unitaria comprueba que coinciden.

## Funciones para NTF-005

- `security.expand_recipient_notice_request(source uuid, lease uuid, enabled boolean, maximum_delay interval,
  window_start time, window_end time, public_id text, channels text[]) → text`. La orden, el evento, el aviso y la hora
  salen de la fila con lease; el Worker solo pasa lo que leyó bajo RLS (número de guía y canales elegibles) y los
  interruptores. Resultados: `SOURCE_EXPANDED`, `RECIPIENT_NOTICES_DISABLED` (con `enabled=false` no se lee ni valida
  nada más), `NOTICE_STALE`, `NO_ELIGIBLE_CHANNEL` (todas PROCESSED), `TEMPLATE_*` (DEAD) o `LEASE_LOST`; un payload que
  no es `recipient-notice-requested-v1` o ajustes fuera de rango dan SQLSTATE 22023. El envío queda disponible al inicio
  de la ventana en `America/Mazatlan` (calculado en SQL).
- `security.read_notification_delivery(uuid,uuid)` v2: agrega `recipient_kind`, `order_id`, `order_event_id` y
  `created_at` (hora del evento, para la caducidad); `template_body` es NULL en WhatsApp y correo.
- `security.apply_notification_outcome(..., p_code text, p_provider_reference text, p_occurred_at, p_available_at)` v2:
  códigos SYNTHETIC_* solo IN_APP; `MessagingResultCodes`, `AMBIGUOUS_TIMEOUT`, `TRACKING_LINK_UNAVAILABLE` (permanente) y
  las cuatro razones de cancelación solo WhatsApp y correo; SUCCESS solo con un código aceptado; CANCELLED no cuenta
  intento y deja la solicitud PROCESSED con su razón; un reintento exige `p_available_at`.
- `security.cancel_notification(..., p_code, p_occurred_at)`: el CANCELLED de v2 para `NOTICE_SUPERSEDED`, `NOTICE_STALE`,
  `NOTICE_LIMIT_REACHED` o `RECIPIENT_OPTED_OUT`.
- `security.is_recipient_opted_out(channel text, hashes bytea[]) → boolean`: de 1 a 16 hashes de 32 bytes (uno por versión
  de llave).

## Decisiones de implementación

- El Worker de hoy pasa a `apply_notification_outcome` v2 sin referencia y sigue enviando solo IN_APP; una solicitud de
  envío de WhatsApp o correo, que nada crea aún, termina DEAD con `INVALID_PAYLOAD`. Una fila
  `orders.recipient-notice-requested`, que nada escribe aún, terminaría DEAD con `UNKNOWN_TOPIC` hasta NTF-005.
- `NotificationStatusChanged.v2` es el tipo de evento de una fila v2; una fila v1 sigue publicándose como `.v1`. Las
  propiedades nulas no viajan (`WhenWritingNull`), así que un IN_APP v2 tiene el mismo payload que v1.
- El rollback operativo de NTF-001 (`OperationalRollbackSql`) revoca la firma publicada de 8 argumentos: con este paso
  aplicado hay que revertirlo antes (nota de NTF-001).
- La web no cambia: el panel y su recarga en vivo son de NTF-007.

## Pruebas

- PostgreSQL: `RecipientNoticeLanePostgreSqlContractTests` (catálogo, permisos, FORCE RLS, aislamiento entre
  organizaciones, una notificación por evento, pérdida del lease, lista cerrada de códigos v2, CANCELLED, horario, bajas) y
  `NotificationsMigrationGuardPostgreSqlTests.Recipient_notice_lane_up_down_up_restores_the_published_catalog_and_refuses_while_rows_need_it`.
- Unitarias: `RecipientNoticeLaneMigrationTests` y el parser de tiempo real v1 y v2 (`RealtimeOutboxTests`).

## Reversión

`Down` del paso (se niega con filas que necesitan la forma nueva) o revert del PR; nada que apagar en runtime porque nada
está encendido.
