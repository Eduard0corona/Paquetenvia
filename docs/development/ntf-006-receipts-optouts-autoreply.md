# NTF-006: acuses de entrega, bajas y respuesta automática

Decisiones: `NTF-RECIPIENT-NOTICES-2026-10-20` (E4-P8, E4-P9, E4-P15) y `NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25`
(ronda O: O2 a O11, citadas en `docs/normative/v0.6/decision-log.md`). Serie:
[`ntf-recipient-notices.md`](ntf-recipient-notices.md), PR-5. Contrato: AI-05 `x-pilot-contract-deltas`
`NTF-RECIPIENT-NOTICES` parte NTF-006 y las cuatro operaciones publicadas, AI-06 (almacén de respuestas automáticas),
AI-18 (paso 3 de avisos, enmendado por O5, y aserción 41), AI-24 `recipient_notices`, AI-07 `/avisos/baja/:token`,
AI-08 NTF-006.

**Estado: implementado y apagado.** `Messaging:WhatsApp:Webhook:Enabled` y `Messaging:Email:Events:Enabled` son falsos
(404 uniforme), `Notifications:RecipientNotices:Enabled` es falso y todos los proveedores siguen en `Disabled`. La baja
por correo no tiene interruptor (O4): sin `ContactLookup:Keys` todo enlace es el 404 uniforme. GATE-004 y GATE-007
siguen abiertas.

## Superficie HTTP (API)

Proyecto nuevo `Notifications.Endpoints` (capa Endpoints de AI-13 §1). Las cuatro operaciones son anónimas (sin sesión,
cookie ni CSRF), responden `Cache-Control: no-store` y ningún problema, respuesta o log lleva un teléfono, un correo, un
token, un secreto, una referencia o un texto recibido.

| Operación | Ruta | Límite SEC-004 (O3, por red y 60 s) | Notas |
| --- | --- | --- | --- |
| `verifyMetaWhatsAppWebhook` | `GET /api/v1/messaging/whatsapp/webhook` | 10 | `hub.challenge` en texto solo con `hub.mode=subscribe` y el token de verificación actual o el anterior, comparados en tiempo constante; si no, 403 |
| `receiveMetaWhatsAppWebhook` | `POST /api/v1/messaging/whatsapp/webhook` | 300 | 256 KiB (413); `X-Hub-Signature-256` con el secreto actual o el anterior, en tiempo constante, antes de leer el JSON (403); 400 si no es un objeto; solo `whatsapp_business_account` y el `phone_number_id` configurado |
| `receiveAcsEmailEvents` | `POST /api/v1/messaging/email/events` | 120 | handshake `SubscriptionValidation` respondido con `validationResponse`; cada entrega lleva `X-Paquetenvia-Event-Secret` (dos ranuras), comparado antes de leer el cuerpo (falta: 401; incorrecto: 403); 64 KiB |
| `unsubscribeRecipientNotices` | `POST /api/v1/recipient-notices/unsubscribe/{token}` | 20, con el 429 uniforme de `publicTracking` | cuerpo vacío, `List-Unsubscribe=One-Click` (formulario o multipart) o `{}` (si no, 400; más de 1 KiB, 413); 204 también si ya estaba; 404 y 503 uniformes sin `traceId`; sin GET ni redirección |

- O4: mientras un webhook está apagado responde siempre el 404 uniforme y nunca 429: su política de SEC-004 tiene
  `RateLimitPolicy.IsActive`, y una petición a un webhook apagado cae en la partición sin límite del registro.
- O11 elección 7: el token de baja (ruta) y toda la consulta de la verificación (`hub.verify_token`) se quitan del
  destino de la petición antes de que el hosting lo registre (`IRequestTargetRedactor`); cada operación lee el original
  de una característica de la petición.

## Qué hace cada entrega

- Acuses (O5, O6): `security.record_messaging_receipt` mueve un aviso `SENT` a `DELIVERED` (con `delivered_at` igual a
  la hora reportada, nunca posterior a la del servidor) o a `FAILED` con su código en `last_provider_attempt_code`; un
  evento de estado y una fila `notifications.status-changed` por cambio, esta vía la función que ya existe
  (`security.emit_notification_status_changed`). Repetidos, contradictorios o de un aviso que ya no está `SENT`: nada
  cambia. Referencia desconocida, también la de un envío aún no liquidado (O11 elección 6): se ignora. Meta: `delivered`
  y `failed` (con el clasificador del envío; sin código, `MESSAGING_PROVIDER_REJECTED`), `sent` y `read` se ignoran.
  ACS: `Delivered` → `DELIVERED`; `Bounced` y `Suppressed` → `MESSAGING_RECIPIENT_UNDELIVERABLE`; `FilteredSpam` y
  `Quarantined` → `MESSAGING_POLICY_BLOCKED`; `Failed` → `MESSAGING_PROVIDER_REJECTED`; `Expanded` se ignora.
- Bajas por WhatsApp: BAJA, ALTO, STOP o CANCELAR (recortado, sin mayúsculas ni acentos), o el botón "No recibir avisos"
  por su payload `RECIPIENT_NOTICE_OPT_OUT` o su texto (O11 elección 4). Se guarda el HMAC del número en cada versión
  configurada de la llave de búsqueda; una baja ya registrada (en cualquier versión) no se repite. La primera baja pide
  en la misma transacción su única confirmación.
- Baja por correo (O11 elección 1): se verifica el token, se lee la copia del correo del aviso con la llave PII de la
  API en una transacción de su empresa (como VOICE-001), se abre en memoria y se registra la baja `EMAIL_LINK`. Sin
  copia: 404 uniforme. O10: las bajas del destinatario no escriben auditoría; la fila de la baja (origen y fecha) es la
  constancia.
- Respuesta automática (O7): cualquier otro mensaje pide una respuesta, como máximo una por contacto cada 24 h; un
  mensaje con más de 23 h no pide nada. El remitente se normaliza a +52 (otro país se omite) y se sella con la mitad
  de solo envolver de la llave de avisos, ligado al identificador fijo de plataforma
  `b32d80c1-ecb9-4b22-a049-ffb64e63f38a` y al id de la solicitud, propósito `notifications.auto_reply_phone` (O11
  elección 10). En pruebas y DEV_SYNTHETIC el sello es aleatorio y no se deriva del número.

## Worker: el almacén de respuestas automáticas

`RecipientAutoReplyDispatcher` (en el carril Notifications, con su `Provider`, lote, lease y espera adaptativa; una
traza raíz por ciclo) toma lotes con `security.claim_recipient_auto_replies` (un `lease_token` por lote) y cada
solicitud se resuelve en `RecipientAutoReplyProcessor`, sin transacción abierta durante la llamada:

- Avisos o WhatsApp apagados: `DROPPED` con `MESSAGING_CHANNEL_DISABLED`, sin llamada y con el número borrado (O7).
- Más de 23 h: `DROPPED` con `AUTO_REPLY_STALE`. No espera al horario 08:00–21:00 (O7) y no tiene tope diario propio.
- Número inutilizable: `DROPPED` con `MESSAGING_RECIPIENT_INVALID`. Key Vault no disponible antes de llamar: vuelve a
  `PENDING` con su número.
- Una llamada a `IMessagingProvider` con el texto fijo (`FreeFormText`, tipo `text` de Meta, sin plantilla); después el
  número se borra sea cual sea el resultado. Un resultado ambiguo y un lease vencido terminan `FAILED` con
  `AMBIGUOUS_TIMEOUT`, nunca se reintentan.

## Paso de carril `20261029000100_AddRecipientNoticeReceipts`

Ida, vuelta e ida probadas en PostgreSQL 18 y PostGIS 3.6 (`NotificationsMigrationGuardPostgreSqlTests`):

- El rol `paqueteria_notification_receipt_executor` es DDL de AI-18 (NOLOGIN BYPASSRLS, revocado de `paqueteria_app`,
  `paqueteria_worker` y `paqueteria_realtime`); el paso lo crea idempotente para instalaciones previas.
- `notifications.recipient_auto_replies` (AI-06, byte a byte), FORCE RLS sin política y sin privilegios para los roles
  de runtime.
- Cinco funciones del ejecutor, SECURITY DEFINER con `search_path` fijo, sin SQL dinámico ni RETURNING: tres para
  `paqueteria_app` y dos para `paqueteria_worker`. Sus 59 privilegios de columna y su EXECUTE sobre el escritor del
  cambio de estado son exactamente los de AI-18 (paso 3), verificados contra el catálogo por el paso, por
  `DatabaseBaselineAssertions` y por `validate_contracts.py`.
- `Down`: se niega (`NTF006_DOWNGRADE_BLOCKED_DATA_PRESENT`) mientras exista una solicitud; si no, quita las funciones y
  el almacén y revoca todos los privilegios del rol, que queda inerte.

## Web: `/avisos/baja/:token`

Página pública con el encabezado "Seguimiento de envío", el botón "Dejar de recibir correos" y los cuatro mensajes de
O9. Abrirla no hace nada; el botón manda `{}` una vez, sin credenciales, referrer ni caché, y nunca sigue una
redirección. El token se lee solo en el navegador (la reescritura `/avisos/baja/:token` → `/avisos/baja` sirve una página
genérica), no se registra en logs de Next, la página es `no-store`, `noindex`, sin manifiesto, sin almacenamiento y el
Service Worker la deja solo en red. El aviso de espera es el común de SEC-004.

## Pruebas

- Unitarias: `RecipientNoticeReceiptsTests` (lectores de Meta y Event Grid, palabras de baja, firmas y secretos con dos
  ranuras, validación al iniciar, servicios de la API, baja por correo y procesador del Worker).
- Contrato: `RecipientNoticeReceiptsNormativeTests` (AI-06 y AI-18 byte a byte, AI-24, AI-05, AI-07, AI-08, ADR y
  decision-log), `RecipientNoticeReceiptsPostgreSqlContractTests` (acuses solo hacia adelante con código, bajas una vez
  en cualquier versión de llave, límites de la respuesta automática, lease, liquidación, lease vencido y solicitud
  vieja, privilegios y lectura de la baja bajo RLS) y `NotificationsMigrationGuardPostgreSqlTests` (ida, vuelta e ida).
- Integración: `RecipientNoticeWebhookHttpTests` (404 sin 429 apagado, verificación, vectores de firma válida, inválida,
  duplicada y de más de 256 KiB, handshake y secreto de Event Grid, formas de la baja, límites y que ningún secreto o
  dato llegue a un log).
- Web: `src/recipient-notices/unsubscribe/*.test.ts` (ruta, cliente, vistas y política de la página).

## Riesgos residuales

- Un acuse que llega antes de que el envío quede registrado se pierde y el aviso queda `SENT` (O11 elección 6).
- La purga de solicitudes liquidadas queda para una función de mantenimiento posterior; hasta entonces las filas
  (sin número) se acumulan.
- El handshake de Event Grid se responde sin el secreto (es como Event Grid valida la suscripción); solo repite un
  código con forma de GUID y no registra nada.
- Meta cobra estas respuestas como mensajes de servicio pasando 1,000 gratis al mes (O7).

## Reversión

Interruptores en falso (por omisión), sin migración. El `Down` del paso se niega mientras haya solicitudes; revertir
el PR restaura los textos anteriores y vuelve a fijar los hashes de AI-05, AI-06, AI-07, AI-08, AI-18 y AI-24.
