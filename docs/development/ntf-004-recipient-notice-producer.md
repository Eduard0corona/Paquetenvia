# NTF-004: solicitud de avisos desde ORD-002 y permiso de WhatsApp

Decisiones: `NTF-RECIPIENT-NOTICES-2026-10-20` (E4-P1 "Casilla en la orden (Recommended)", E4-P3, E4-P4 y E4-P5) y
`NTF-RECIPIENT-NOTICE-PRODUCER-2026-10-23` (ronda N: N1 "Seis, y corregir los textos (Recommended)", N2 "Mismo texto y
una nota (Recommended)", N3 "Aprobarlas todas (Recommended)"; citadas en `docs/normative/v0.6/decision-log.md`). Serie:
[`ntf-recipient-notices.md`](ntf-recipient-notices.md), PR-3. Contrato: AI-24 `recipient_notices` (`producer`,
`consent`), AI-05 `x-pilot-contract-deltas` NTF-RECIPIENT-NOTICES (parte NTF-004), AI-06 y AI-18 (paso 2 de avisos y
aserción 40), AI-07 `create_order` y `csv_order_import` `recipient_notices`, AI-08 NTF-004.

**Estado: implementado; nada se envía.** Desde este cambio la transición ORD-002 escribe la solicitud de aviso y el
Worker lee el permiso de WhatsApp de la tabla nueva, pero `Notifications:RecipientNotices:Enabled` sigue falso y los
proveedores en `Disabled`: el Worker liquida cada solicitud al momento con `RECIPIENT_NOTICES_DISABLED`
([`ntf-005-recipient-notice-delivery.md`](ntf-005-recipient-notice-delivery.md)). GATE-004 y GATE-007 siguen abiertas.

## Productor en ORD-002

`RecipientNoticeRequestPolicy.NoticeFor(origen, destino)` nombra exactamente las cinco transiciones de AI-24
`recipient_notices.producer.triggers`:

| Transición | Aviso |
|---|---|
| `DRAFT → CONFIRMED` | `TRACKING_LINK` |
| `PICKED_UP → IN_TRANSIT` | `IN_TRANSIT` |
| `IN_TRANSIT → FAILED_ATTEMPT` | `DELIVERY_FAILED` |
| `DELIVERING → FAILED_ATTEMPT` | `DELIVERY_FAILED` |
| `DELIVERING → DELIVERED` | `DELIVERED` |

`AT_PICKUP → FAILED_ATTEMPT` y las demás 23 aristas de AI-04 no escriben nada: la etapa del intento es el origen de la
transición. `PostgreSqlOrderTransitionService` escribe la fila después de la reacción de Despacho
(`D8-OUTBOX-LANE-DISPATCH`), en la misma transacción que el evento de la orden, la auditoría y la idempotencia
(N3, elección 6):

- topic `orders.recipient-notice-requested`, `aggregate_type` `Order`, versión de la orden, prioridad 50, `PENDING`,
  `available_at` = `created_at` = instante de la transición, `trace_parent` del contexto (OBS-003);
- payload `recipient-notice-requested-v1` con solo `schema_version`, `order_event_id`, `order_id`, `notice` y
  `occurred_at` (UTC con microsegundos); sin número de guía, teléfono, correo ni nombre;
- todos los valores los da el productor (`ValueGeneratedNever`), sin `RETURNING`; una repetición idempotente devuelve
  el evento original sin escribir otra fila y una falla posterior (etapa `RecipientNoticeOutboxInserted` o siguientes) la
  deshace con todo lo demás.

La fila se escribe aunque los avisos estén apagados; NTF-005 ya la consume. No hay coordinador ni flujo nuevo: es una
fila más del outbox compartido, como la de Despacho (AI-13 §7.2), y siguen los seis flujos de AI-13 §4 (N1).

## Permiso de WhatsApp

Una casilla opcional y sin marcar, con el texto exacto de AI-24 `consent.text` ("El destinatario aceptó recibir avisos
de su envío por WhatsApp") y la versión `NTF-RECIPIENT-WHATSAPP-CONSENT-V1`, constante de la web (N3, elección 1):

- **"Nueva orden"**, paso "Confirmar", en el grupo "Avisos al destinatario". Marcada, `createOrder` lleva
  `recipient_notices: {whatsapp_consent_attested: true, whatsapp_consent_version: "NTF-RECIPIENT-WHATSAPP-CONSENT-V1"}`;
  sin marcar no se envía nada. Nunca bloquea "Confirmar".
- **Carga CSV**, una casilla para todo el archivo antes de "Confirmar lote", con la nota fija "Aplica a todas las filas
  de este archivo. Márcala solo si cada destinatario lo aceptó." (N2). Marcada, el commit lleva
  `recipient_whatsapp_consent_attested=true` y `recipient_whatsapp_consent_version` una vez cada uno. Otro archivo
  (otro digest) vuelve a mostrarla sin marcar.

La API acepta solo esas formas: en `createOrder`, `recipient_notices` ausente o `null` es "sin permiso",
`{whatsapp_consent_attested: false}` también, y `true` exige la versión V1; en el CSV, ningún campo, `false` sin versión
o `true` con V1. Cualquier otra forma (tipo, miembro o campo repetido, otra versión, versión sin `true`) es el 409
uniforme antes de crear algo (N3, elección 5).

La casilla entra en la huella solo cuando está marcada (N3, elección 3): el hash de `createOrder` agrega
`recipient_whatsapp_consent_version` y la huella del lote CSV agrega `recipient_whatsapp_consent_version` y su valor,
así que las huellas sin casilla no cambian y la otra respuesta con la misma `Idempotency-Key` es `IDEMPOTENCY_CONFLICT`.
La web usa otra llave cuando la respuesta cambia.

## Tabla `orders.recipient_notice_consents`

Paso de carril Orders `20261023000200_AddRecipientNoticeConsents` (N3, elección 2; AI-06 declara la tabla y AI-18 los
permisos byte a byte):

- columnas `order_id`, `owner_org_id`, `channel` (solo `WHATSAPP`), `consent_version` (solo V1), `attested_by` (quien
  crea la orden) y `attested_at` (hora del servidor de la transacción); llave `(order_id, channel)`; sin teléfono,
  correo ni nombre;
- ENABLE y FORCE ROW LEVEL SECURITY con `security.app_allowed_org(owner_org_id)`, disparador append-only
  `platform.reject_runtime_mutation()`;
- `paqueteria_app` solo SELECT e INSERT, `paqueteria_worker` solo SELECT; ningún otro rol;
- la fila se escribe en la transacción de `quote_snapshot_to_order` (flujo 1, su propio módulo), después de la
  aceptación; la auditoría `ORDER_CREATED` guarda solo `whatsapp_consent_attested` (N3, elección 4); en el CSV cada orden
  creada recibe su propia fila.

El paso se niega si la tabla ya existe, y verifica al terminar columnas, restricciones, RLS, dueño, política, disparador
y permisos (el error nombra solo la parte que difiere). Las aserciones de la línea base revisan la tabla en cada
despliegue (AI-18, aserción 40).

## Lectura en el Worker

`PostgreSqlRecipientNoticeConsentReader` (N3, elección 7) responde con `EXISTS` sobre la tabla dentro de una transacción
corta con `app.current_org_ids` de la organización dueña y `SET LOCAL ROLE paqueteria_worker`, así que FORCE RLS decide.
`UnrecordedRecipientNoticeConsentReader` queda solo como respaldo que falla cerrado en un host sin los lectores de
Orders.

## Decisiones propias (no escritas en los contratos)

- Nombres: tabla y restricciones `recipient_notice_consents_*`, política `recipient_notice_consents_tenant`, disparador
  `recipient_notice_consents_append_only`, paso `20261023000200`, mensaje del `Down`
  `NTF004_DOWNGRADE_BLOCKED_CONSENT_PRESENT`.
- `attested_by` es FK a `identity.users` y `NOT NULL`; sin índice adicional (la llave empieza por `order_id`).
- El `Down` comprueba filas con una restricción `CHECK (false)` porque FORCE RLS oculta las filas al migrador.
- Miembro del hash `recipient_whatsapp_consent_version` y, en la huella del lote, las líneas
  `recipient_whatsapp_consent_version` y su valor.
- `recipient_notices: null` se acepta como ausente (como `service_window`); dentro del objeto
  `whatsapp_consent_attested` es obligatorio.
- Leyenda "Avisos al destinatario" en "Nueva orden"; el formulario del CSV se monta de nuevo por digest.
- OPS-001: la simulación liquida las 60 solicitudes nuevas con la expansión apagada y su informe pasa de 380 a 440 filas
  procesadas.

## Pruebas

- Unitarias: `Ntf004RecipientNoticeProducerTests` (29 aristas de AI-04, cinco que escriben, etapa del intento, topic y
  esquema compartidos con Notifications, texto y versión de AI-24, hash y huella con y sin casilla, campos del CSV).
- Arquitectura: `CrossSchemaWriteCatalogTests` (seis flujos y el mismo catálogo; el topic solo en la transición; el
  INSERT del permiso solo en el coordinador y ningún UPDATE o DELETE; Notifications no escribe Orders) y
  `OutboxTraceContextArchitectureTests`.
- Contrato: `RecipientNoticeProducerNormativeTests` (AI-04, AI-05, AI-06, AI-07, AI-08, AI-13, AI-18, AI-24 y la fila del
  registro) y `OrdersOpenApiImplementationTests`.
- PostgreSQL: `OrdersTransitionPostgreSqlContractTests` (cada arista escribe la fila solo cuando toca, con payload sin
  PII; repetición sin fila nueva; falla posterior sin fila; sin RETURNING), `RecipientNoticeConsentPostgreSqlContractTests`
  (ida, vuelta e ida; deriva rechazada; `Down` negado con un permiso; createOrder y CSV; append-only, RLS y lector del
  Worker) y `RecipientNoticeDeliveryPostgreSqlContractTests` (el permiso se registra creando la orden; una corrida va de
  `createOrder` y la confirmación ORD-002 a los dos envíos).
- Las pruebas que simulan una instalación anterior a un paso de Orders (enlace de seguimiento, ventana de servicio,
  cierre automático, ventana de reclamo y el puente de Azure) también quitan la tabla y su fila del historial, porque esa
  instalación no la tenía; `ReadIndexesPostgreSqlContractTests` comprueba que el paso de índices queda justo antes de
  este.
- Integración: `RecipientNoticeConsentHttpTests` (formas válidas e inválidas en `createOrder` y CSV, repeticiones) y
  `RecipientNoticeWorkerCompositionTests`.
- Vitest: `recipient-whatsapp-consent.test.ts` (casilla de "Nueva orden" y del CSV, texto, nota, versión y cuerpo) y
  los controladores de "Nueva orden" y CSV (llave nueva cuando cambia la respuesta).

## Riesgos residuales

- La casilla del CSV vale para todas las filas: si el despachador la marca sin el permiso de cada destinatario, el
  registro lo atribuye a quien la marcó (por eso la nota).
- El permiso no se puede retirar desde la orden: la baja del destinatario llega con NTF-006.

## Reversión

Avisos apagados (por omisión) sin migración. El `Down` del paso se niega mientras exista un permiso; sin permisos borra
solo la tabla. Revertir el PR quita la fila del productor y la casilla y vuelve a fijar los hashes de AI-05, AI-06 y
AI-18.
