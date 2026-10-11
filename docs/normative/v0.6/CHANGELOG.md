# Changelog

## Sin enlace de soporte en el seguimiento público (GATE-001-TRACKING-SUPPORT-LINK-2026-10-11) — 2026-10-11

- Respuesta literal del project owner: "Quitarlo por ahora (Recommended)"; registrada, con su pregunta, en
  `decision-log.md`. GATE-001 sigue abierto.
- Mientras GATE-001 siga abierto, el seguimiento público no muestra enlace de soporte: el workflow del piloto ya no
  usa el host del piloto como valor por omisión de `PILOT_TRACKING_SUPPORT_URL`, un
  `NEXT_PUBLIC_TRACKING_SUPPORT_URL` vacío significa sin enlace (la página indica responder por el mismo canal del
  enlace) y `next build` rechaza un enlace que nombre la marca en cualquier variante.
- AI-07 `public_tracking.branding` agrega la regla del canal de soporte. Sin cambios en API, AI-04, AI-05, AI-06,
  AI-18, roles ni migraciones.

## Nombre neutro en el seguimiento público mientras GATE-001 sigue abierto (GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10) — 2026-10-10

- Respuestas literales del project owner: "Interno sí, público neutro (Recommended)" y "Seguimiento de envío
  (Recommended)"; registradas, con sus preguntas, en `decision-log.md`. GATE-001 sigue abierto: solo fija el texto
  público provisional mientras el nombre comercial no esté validado ante el IMPI.
- AI-07 `public_tracking.branding`: todos los estados del seguimiento abren con "Seguimiento de envío" y ninguno nombra
  la marca; el título de la página es "Seguimiento de envío", `/track` no enlaza el manifest de la PWA del repartidor y
  el 404 al que lleva un enlace mal formado se titula "Página no encontrada". En `show`, `brand` pasa a `heading`.
- `NEXT_PUBLIC_TRACKING_BRAND_NAME` se conserva sin definir (sin marca): el workflow del piloto ya no pasa
  "Paquetenvia" y `next build` rechaza ese nombre en cualquier variante.
- Los mensajes al destinatario que llevan el enlace (TRK-002, plantillas GATE-004) usan el mismo texto neutro; hoy el
  repositorio no contiene textos para destinatarios.
- Pantallas internas, login, onboarding y PWA del repartidor conservan "Paquetenvia". Sin cambios en API, AI-04, AI-05,
  AI-06, AI-18, roles ni migraciones; el dominio `paquetenvia.com` (PILOT-DOMAIN-PRODUCTION) no cambia.

## Bandeja de trabajo de despacho (UI-PHASE3-INBOX-2026-10-10) — 2026-10-10

- Respuesta literal del project owner: "avanza con la fase 3"; registrada en `decision-log.md`.
- AI-07 `/ops/inbox` (contrato `work_inbox`): "Bandeja de trabajo" para DISPATCHER y PLATFORM_ADMIN con MFA, su
  página de inicio y primer elemento del menú (Bandeja · Tablero · + Nueva orden · Rutas · Incidencias · Importar CSV).
  VIEWER y los demás roles quedan fuera, igual que en el tablero y los conteos.
- Colas con los conteos reales de `getOperationsQueueCounts`: Sin asignar, Requiere atención, Precio por revisar (solo
  conteo con nota), Entregadas sin cerrar y En ruta. Cada cola se lee con los filtros existentes de
  `GET /operations/dashboard`; las de varios estados se combinan en el orden del servidor sin saltar ni reordenar filas.
- Tabla (Guía, Estado, Destino por zona, Ventana en hora de Mazatlán, Repartidor) con chips que filtran en el servidor,
  "Cargar más" y las acciones "Abrir" y "Asignar" (selector de repartidor del detalle). La cola y los chips viven en la
  URL; el detalle abierto desde la bandeja ofrece "Volver a la bandeja".
- `/ops/dashboard` sigue disponible como "Tablero", con su vista de posiciones ("Mapa de posiciones" desde la bandeja).
- Pendientes: columna Total (el tablero no trae el total de la orden), lista de Precio por revisar (sin filtro del
  servidor), acciones masivas con vista previa y las entradas Órdenes y Repartidores. Sin cambios en API, AI-04, AI-05,
  AI-06, AI-18, roles ni migraciones.

## Reprogramar, devolver y reclamaciones en "Siguiente paso" (UI-NEXT-STEP-RETURNS-CLAIMS-2026-10-09) — 2026-10-09

- Respuesta literal del project owner: "haz los opcionales mientras decido lo de la fase 3"; registrada en
  `decision-log.md`.
- AI-07 `/ops/orders/:id` next_step: además de confirmar, liberar, cerrar y cancelar, el detalle ofrece "Reprogramar
  entrega", "Iniciar devolución", "Marcar como devuelta", "Abrir reclamación" y "Resolver reclamación", solo cuando
  `allowed_transitions` los incluye, con motivo obligatorio, confirmación y los mensajes de ORD-002-GUARD-CODES.
- Siguen fuera de esta pantalla ASSIGNED (selector de repartidor), FAILED_ATTEMPT (incidencia) y los pasos del
  repartidor. AI-04, AI-05, reglas del servidor y roles sin cambios.

## Conteos reales de la bandeja de operaciones (UI-PHASE2-QUEUE-COUNTS-2026-10-05) — 2026-10-05

- Respuesta literal del project owner: "Sí a los 5 grupos de estado, avanza con la fase 2"; registrada en
  `decision-log.md`. Fase 2: "Conteos para la bandeja" (conteo real del servidor; hoy los indicadores cuentan solo lo
  cargado).
- AI-05: `getOperationsQueueCounts` (`GET /operations/queue-counts`, esquemas `OperationsQueueCounts`,
  `OperationsQueues` y `OperationsCount`, módulo Reporting). Solo conteos enteros sobre las órdenes que la organización
  activa puede leer como dueña u operadora (RLS): `total`, `by_status` con los 17 estados AI-04 (incluye ceros) y
  `queues` (`unassigned`, `needs_attention`, `price_review`, `delivered_not_closed`, `en_route`), cada una con una regla
  que el tablero ya aplica. Sin parámetros ni filtros (cualquier query es 400). Mismos roles que el tablero:
  DISPATCHER y PLATFORM_ADMIN con MFA (`x-capability-matrix.operations_queue_operations`); VIEWER recibe 403.
- AI-07 `/ops/dashboard` `queue_counts`: indicadores "Sin asignar", "Requiere atención", "Revisar precio",
  "Entregadas sin cerrar" y "En ruta", y "N en total" por grupo de estado, recargados junto con la lista.
- Sin migraciones, tablas, índices, roles, grants ni flujos nuevos; AI-04, AI-06 y AI-18 sin cambios.

## Código de la regla incumplida al cambiar el estado (ORD-002-GUARD-CODES-2026-10-05) — 2026-10-05

- Respuesta literal del project owner: "Sí a los 5 grupos de estado, avanza con la fase 2"; registrada en
  `decision-log.md`; ADR `docs/adr/ADR-ORD-002-TRANSITION-REJECTION-CODES.md`.
- AI-05 `TransitionConflictProblem.code` (409 de `transitionOrder`): enum cerrado con los códigos de versión y matriz
  AI-04 (VERSION_CONFLICT, TRANSITION_NOT_ALLOWED, ORDER_TERMINAL, ORDER_FINALIZED, CLAIM_WINDOW_CLOSED) y un código por
  cada guarda AI-04 existente (`x-ord-002-guard-codes`), además de OFFLINE_OPERATION_EXPIRED. Sin guardas ni estados
  nuevos.
- Solo se devuelve para una orden de la organización seleccionada (dueña) y a quien tiene la capacidad de
  `transitionOrder` sobre ella (DISPATCHER, PLATFORM_ADMIN con MFA, DRIVER con la asignación ACCEPTED/ACTIVE de esa
  orden). Orden inexistente, ajena u operada, forma inválida, idempotencia, concurrencia y quien no tiene la capacidad
  siguen recibiendo el mismo 409 uniforme sin código. Sin datos personales, identificadores ni montos.
- AI-07 "Siguiente paso": mensaje es-MX por código desde un solo helper; código ausente o desconocido usa el mensaje
  genérico.
- Sin migraciones, tablas, roles, grants ni flujos nuevos; AI-06 y AI-18 sin cambios.
- AI-04 `guards` documenta `CLAIM_RESOLVED: [claim_resolution_reason_present]`, guarda que el código ya aplicaba;
  el mapa de códigos y AI-04 se verifican ahora en ambas direcciones.

## Búsqueda por guía y acciones válidas de la orden (UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05) — 2026-10-05

- Respuesta literal del project owner: "Sí a los 5 grupos de estado, avanza con la fase 2"; registrada en
  `decision-log.md`.
- AI-05 `listOrders`: parámetro opcional `public_id`, coincidencia exacta del número de guía (`ORD_` y 22 caracteres
  Base64URL); un valor con otro formato no coincide con nada; orden ajena, inexistente o mal formada devuelven la
  misma página vacía (RLS). Solo se busca por guía, nunca por datos personales. Sin capacidad nueva.
- AI-05 `OrderDetail.allowed_transitions` (esquema `OrderAllowedTransition`): transiciones ORD-002 que quien consulta
  podría pedir ahora, calculadas por el servidor con la matriz AI-04, la regla de solo dueño, la versión y las reglas de
  rol de `transitionOrder`; `required_metadata` indica `restricted_goods_acknowledged` o `incident_id`. No evalúa ni
  expone guardas; es orientativa y `transitionOrder` vuelve a validar todo.
- AI-07: "Buscar guía" en la barra superior y "Siguiente paso" en `/ops/orders/:id` (solo acciones válidas, motivo
  obligatorio, confirmación, Idempotency-Key, recarga REST).
- Sin migraciones, tablas, índices, roles, grants ni flujos nuevos; AI-04, AI-06 y AI-18 sin cambios.

## Asignar repartidor desde el detalle de la orden (UI-PHASE2-DRIVER-PICKER-2026-10-05) — 2026-10-05

- Respuesta literal del project owner: "Sí a los 5 grupos de estado, avanza con la fase 2"; registrada en
  `decision-log.md`.
- AI-05: `listAssignableDrivers` (`GET /orders/{orderId}/assignable-drivers`, esquemas `AssignableDriver` y
  `AssignableDriverPage`). Lista por cursor los repartidores OWN de la organización activa (no INACTIVE) con
  `driver_id`, la referencia `DRV-xxxxxxxx` que ya muestra el tablero (no hay nombre de repartidor en AI-06; no se lee
  ni devuelve nombre, correo, teléfono, documentos ni ubicación), vehículo, si `assignDriver` lo aceptaría ahora para
  esa orden con la misma política DSP-001 y los códigos estables cuando no, y sus asignaciones ACCEPTED/ACTIVE.
  Mismos roles que `assignDriver` (`x-capability-matrix.assignable_driver_operations`); 404 uniforme; 409 CONFLICT si la
  orden no admite asignación; solo lectura.
- AI-07 `/ops/orders/:id`: panel "Asignar repartidor" (lista, costo en MXN a centavos enteros, confirmación, misma
  Idempotency-Key en reintentos, recarga REST); "Publicar oferta externa desde el tablero" sigue como alternativa.
- Sin migraciones, tablas, índices, roles, grants ni flujos nuevos; AI-06 y AI-18 sin cambios.

## Cinco grupos de estado en la interfaz (UI-STATUS-GROUPS-2026-10-05) — 2026-10-05

- Respuesta literal del project owner: "Sí a los 5 grupos de estado, avanza con la fase 2"; registrada en
  `decision-log.md` con el mapeo aprobado.
- Solo presentación en `apps/web` (UI-001): el tablero agrupa las órdenes en Por preparar, En recolección, En ruta,
  Requiere atención y Terminadas, sin ocultar el estado exacto de AI-04. Máquina de estados, terminalidad, ventana de
  reclamo, API, tracking público y PWA del repartidor sin cambios.

## El repartidor del operador recibe los cambios de estado del dueño (ORD-002-OPERATOR-DRIVER-EVENTS-2026-10-03) — 2026-10-03

- Respuesta literal del project owner: "Solo avisar a su repartidor"; registrada en `decision-log.md`.
- ORD-002 sigue siendo solo del dueño. Si la orden tiene operador distinto del dueño y la asignación vigente
  (OWN/EXTERNAL, ACCEPTED o ACTIVE) es de ese operador, el `orders.status-changed` (etiquetado con el dueño) nombra a
  su repartidor; Realtime solo lo entrega tras verificar en el contexto del propio operador la asignación, la orden y
  el repartidor exactos, que el operador de la asignación siga siendo el de la orden y que perfil, usuario y membresía
  DRIVER estén activos. Ningún repartidor de un tercero y ninguna otra audiencia cambian.
- AI-04 `outbox_invariants` registra la regla. Sin migración, rol, grant ni función nuevos; AI-06, AI-12 y AI-18 sin
  cambios.

## Outbox y auditoría a nombre del dueño cuando asigna el operador (DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03) — 2026-10-03

- Respuestas literales del project owner: "Función segura a nombre del dueño" y "Sí, el dueño lo ve"; registradas en
  `decision-log.md`.
- Corrige un fallo confirmado: cuando la organización operadora de un pedido ajeno asignaba a su repartidor (DSP-002),
  RLS rechazaba las filas de outbox y auditoría etiquetadas con el dueño (42501) y la asignación se revertía.
- AI-18: nuevo rol `paqueteria_operator_outbox_executor NOLOGIN BYPASSRLS` con `SELECT`/`INSERT` por columna exactos
  (sin UPDATE/DELETE, sin CREATE, sin membresía runtime) y aserciones 29-31. AI-06: solo una nota junto a
  `audit_logs_tenant`/`outbox_tenant` (políticas sin cambio). AI-03 §25.2 y AI-04 `outbox_invariants` registran la regla.
- Lane Dispatch `20261003000100_AddOperatorOwnerOutboxExecutor`: `security.append_operator_order_outbox` y
  `security.append_operator_order_audit` (SECURITY DEFINER, `search_path=pg_catalog, pg_temp`, EXECUTE solo para
  `paqueteria_app`); escriben una fila cada una solo si el contexto es exactamente el operador de la orden, el actor es
  despachador o admin activo del operador y la fila corresponde a la asignación recién hecha (tema/acción, audiencia,
  payload y versión en lista permitida, sin repetidos); si no, 42501. Rollback: solo elimina las dos funciones.
- DSP-002 usa esas funciones únicamente cuando actúa el operador; el dueño conserva los inserts directos. Realtime
  autoriza como audiencia de conductor al repartidor del operador de esa asignación exacta; no se amplía otra audiencia.
- ORD-002 sin cambios (solo el dueño transiciona); extenderlo a operadores queda como decisión aparte.

## AI-02 y AI-15 alineados con la excepción WhatsApp de leases vencidos (DOC-AI02-AI15-STALE-LEASE-SYNC-2026-10-03) — 2026-10-03

- Respuesta literal del project owner: "Sí, actualizarlo"; registrada en `decision-log.md`
  (`DOC-AI02-AI15-STALE-LEASE-SYNC-2026-10-03`).
- AI-02 `outbox_lifecycle.recovery` y AI-15 `outbox_operations.lease_recovery`: el PROCESSING vencido se sigue
  reencolando, salvo el `notifications.send-requested` de WhatsApp, que queda DEAD con su Notification FAILED
  (`AMBIGUOUS_TIMEOUT`) y un `notifications.status-changed` que avisa al despachador, como ya dice AI-04
  `outbox_invariants` (NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03, PR #186).
- Solo redacción: sin cambio de comportamiento ni migración; AI-04, AI-05, AI-06, AI-12 y AI-18 sin cambios.

## WhatsApp con lease vencido: fallido sin reencolar y aviso al despachador (NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03) — 2026-10-03

- Respuesta literal del project owner (GATE-004): "Sí, marcar fallido y avisar"; registrada en `decision-log.md`
  (`NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03`).
- Si el proceso que envía un WhatsApp se cae a mitad del envío, la recuperación de leases vencidos ya no lo reencola:
  el `notifications.send-requested` queda DEAD (`AMBIGUOUS_TIMEOUT`, bajo su propio `lease_token`), la Notification
  FAILED con `AMBIGUOUS_TIMEOUT` y el mismo `notifications.status-changed` (audiencia operations, sin PII) se escribe
  en la misma transacción. Una fila bloqueada por un settle concurrente se omite y nunca se reencola.
- Lane Notifications `20261003000100_FailStaleWhatsAppNotificationLeases`: reemplaza solo el cuerpo de
  `security.recover_stale_notifications_outbox` (misma firma, dueño, search_path y permisos); rollback restaura el
  cuerpo de NTF-001. IN_APP y email conservan la recuperación de NTF-001.
- AI-04 `outbox_invariants`: la excepción WhatsApp en la recuperación de PROCESSING vencido. AI-06, AI-12 y AI-18 sin
  cambios.

## Prefijo +52 en teléfonos y 10 dígitos en ubicaciones (ORD-PHONE-PLUS52-LOCATIONS-2026-10-03) — 2026-10-03

- Respuestas literales del project owner: "Aceptar +52 y quitarlo" y "Exigir 10 dígitos en ubicaciones"; registradas
  en `decision-log.md`.
- AI-05: `AddressInput.phone` (`createQuote`) acepta un prefijo `+52` inicial opcional, que se quita junto con espacios
  y guiones; deben quedar exactamente 10 dígitos ASCII (otro prefijo, `52` sin `+` u otro carácter sigue siendo 422).
  `CreateLocationRequest.phone` (`createLocation`, GEO-001) aplica la misma regla: sigue siendo opcional (`null`) y
  otro valor es el 400 uniforme; se valida solo al escribir, las ubicaciones ya guardadas siguen legibles. Solo los 10
  dígitos normalizados se hashean, protegen y guardan. Nueva entrada `ORD-PHONE-PLUS52-LOCATIONS` en
  `x-pilot-contract-deltas`.
- AI-07: `create_order.phone_and_restricted_goods` menciona el `+52` opcional.
- Sin migración; AI-04, AI-06 y AI-18 sin cambios.

## Límites de la ventana de servicio confirmados (ORD-SERVICE-WINDOW-LIMITS-CONFIRMED-2026-10-03) — 2026-10-03

- Respuestas literales del project owner: "Sí, 12 h y 30 días" (duración máxima de la ventana y anticipación máxima);
  "No por ahora" (columnas de ventana en el CSV de pedidos); "No en el piloto" (validar la ventana contra el horario
  de la zona). Registrado en `decision-log.md`.
- Sin cambios de comportamiento: la API ya aplicaba 12 horas de duración máxima y 30 días de anticipación; el CSV-001
  sigue sin columnas de ventana y la ventana no se compara contra ningún horario de zona en el piloto (las zonas no
  guardan horario). La entrada ORD-SERVICE-WINDOW-OPTIONAL de `x-pilot-contract-deltas` lo documenta.

## Ventana de servicio opcional en la orden (ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02) — 2026-10-02

- Respuesta literal del project owner a "Ventana de servicio (horario de entrega): la pantalla la pide pero la API no
  la guarda. ¿La agrego a la API?": "Sí, opcional". Registrado en `decision-log.md`.
- AI-05: `CreateOrderRequest.service_window` opcional (`from`, `to` en RFC 3339 con zona explícita, segundos enteros,
  normalizados a UTC; `from < to`, máximo 12 horas, `from >= ahora - 5 min`, `to > ahora`, `from <= ahora + 30 días`;
  cualquier otra forma es 409). `Order.service_window` (null si no hay ventana: aplica el horario de la zona). Entra
  en el hash de idempotencia solo si está presente. Nueva entrada ORD-SERVICE-WINDOW-OPTIONAL en
  `x-pilot-contract-deltas`.
- AI-06: `orders.orders` agrega `service_window_from` y `service_window_to` (timestamptz, nulos) con
  `orders_service_window_check`. Lane Orders `20261002000100_AddOrderServiceWindow` adopta la forma de AI-06 y su
  rollback falla cerrado. AI-18 sin cambios.
- AI-04: regla de `Order.service_window`. AI-07: `create_order.service_window` (hora de Mazatlán).
- El CSV de pedidos no cambia: su plantilla no tiene columna de ventana (confirmado por el owner el 2026-10-03,
  "No por ahora").

## Autorización manual de envíos de bajo monto en la cotización (LOW-PRICE-MANUAL-AUTH-2026-10-02) — 2026-10-02

- Respuestas literales del project owner: "Sí, con autorización"; "En la cotización". Registrado en `decision-log.md`
  (`LOW-PRICE-MANUAL-AUTH-2026-10-02`); implementa PRC-002 (flujo de financial override) acotado a esa regla.
- AI-05: `CreateQuoteRequest.low_price_authorization` opcional (`reason` recortado, 1 a 200 caracteres, sin datos
  personales). Solo DISPATCHER o PLATFORM_ADMIN con MFA (`x-capability-matrix.low_price_authorization`); otro rol recibe
  403 (`MFA_REQUIRED` si solo falta el segundo factor) antes de leer estado persistido. Si el precio la necesita (sin ruta
  consolidada y tarifa 52/45 o total de 52 MXN o menos con IVA) la cotización guarda `financial_override` = {actor_id,
  reason, valid_until = expires_at} y la orden lo copia; si no la necesita, 409 uniforme. createQuote declara 409.
  `Quote.low_price_authorization` muestra `valid_until` a todo lector y `actor_id`/`reason` solo a quien tiene
  getOrderFinancials.
- Auditoría append-only `QUOTE_LOW_PRICE_AUTHORIZED` (actor, motivo, cotización, total en centavos) en la misma
  transacción de createQuote; la autorización forma parte de la huella de idempotencia.
- AI-07: `create_order.low_price_guard` deja pasar una cotización autorizada y `create_order.low_price_authorization`
  describe el campo "Autorizar envío de bajo monto". AI-02 y AI-08 (PRC-002) registran la regla.
- Sin migración: `financial_override` y los CHECK de tarifa/ruta y piso ya existen en AI-06. AI-06 y AI-18 sin cambios.

## Reglas del piloto para Google Maps (GATE-003-MAPS-PILOT-RULES-2026-10-02) — 2026-10-02

- Respuestas literales del project owner: "Sí, las 4" (key restringida a la Geocoding API, sin rutas ni ETAs en el
  piloto, reemplazar el pin solo con coincidencia exacta, búsquedas restringidas a México) y, sobre qué es "exacta",
  "Solo ROOFTOP".
- Adaptador Google Maps: el pin del cliente se reemplaza solo con un único resultado, sin `partial_match` y
  `location_type` `ROOFTOP` cuyo `address_components` tenga el país `MX`; todo lo demás conserva el pin manual.
  `components=country:MX` se envía siempre desde una constante y `Locations:GoogleMaps:ComponentsCountry` solo
  admite `MX` (otro valor, o vacío, falla la validación al arrancar).
- Prueba de arquitectura: ningún endpoint de Directions, Routes o Distance Matrix ni puerto de ruteo/ETA en `src`.
- GATE-003 sigue abierto: tope de gasto, cuotas diarias, alertas de facturación y la key restringida en Key Vault
  (`google-maps-api-key`) quedan pendientes del owner; el piloto mantiene `Locations__GeocodingProvider=Manual`.
- Sin cambios en AI-05, AI-06, AI-18 ni migraciones.

## Versiones de política de asignación y de elegibilidad por organización (POLICY-VERSIONS-PER-ORG-2026-10-02) — 2026-10-02

- Respuesta literal del project owner: "Por empresa, piloto-2026-10-v1"; registrada en `decision-log.md`.
- AI-06: `organizations.organizations.assignment_policy_version` y `driver_eligibility_policy_version`, `text NOT NULL
  DEFAULT 'piloto-2026-10-v1'` con el formato `^[A-Za-z0-9._-]{1,64}$`. Toda organización, existente o futura,
  empieza en `piloto-2026-10-v1`.
- AI-04 `Organization`: ambas versiones son de la organización; se aplica la de la organización del repartidor
  asignado o evaluado (la dueña de la orden si asigna a su repartidor, la operadora si lo hace la operadora). No
  existe versión global.
- Se eliminan `Dispatch:AssignmentPolicyVersion` y `Drivers:Eligibility:PolicyVersion`: la API no arranca si siguen
  configuradas y ya no bloquean el despliegue del piloto (el guard las rechaza en `apps.settings.json`). Las reglas
  de elegibilidad (documentos y capacidad por vehículo) siguen siendo configuración compartida.
- Lane Organizations `20261002000100_VersionDispatchPoliciesPerOrganization`: agrega o adopta ambas columnas sin
  reescribir filas; el rollback se niega si alguna organización tiene otra versión. AI-18 sin cambios.

## Confirmación de artículos prohibidos y teléfonos de México (ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02) — 2026-10-02

- Respuesta literal del project owner: "Sí, ambas"; registrada en `decision-log.md`.
- AI-05: `CreateOrderRequest.restricted_goods_acknowledged` obligatorio (solo `true`) y el campo multipart
  `restricted_goods_acknowledged=true` obligatorio en `CsvImportCommitRequest`; cualquier otro valor es 409 uniforme.
  La confirmación entra al hash de idempotencia de ORD-001 y queda en el evento append-only `ORDER_CREATED` y en su
  auditoría. `order_acceptances` y `OrderAcceptanceCanonicalForm v1` no cambian. `AddressInput.phone` (`createQuote`)
  es un número de México de 10 dígitos tras quitar espacios y guiones (sin `+52`); otro valor es 422. Nueva entrada
  `ORD-PROHIBITED-GOODS-PHONE-MX` en `x-pilot-contract-deltas`.
- AI-07: `create_order.phone_and_restricted_goods` y la casilla de confirmación del commit de `csv_order_import`.
- Sin migración; AI-04, AI-06 y AI-18 sin cambios.

## WhatsApp ambiguo: fallido sin reintento y aviso al despachador (NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02) — 2026-10-02

- Respuesta literal del project owner (GATE-004): "Marcar fallido y avisar"; registrada en `decision-log.md`
  (`NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02`).
- Un envío WhatsApp con resultado ambiguo (timeout o respuesta sin `wamid`: Meta pudo aceptarlo) es terminal: la
  Notification queda FAILED con el motivo `AMBIGUOUS_TIMEOUT`, el `notifications.send-requested` queda DEAD y nunca se
  reintenta ni se reencola, así el cliente nunca lo recibe dos veces.
- Aviso: el `notifications.status-changed` que el mismo settle ya escribe (`NotificationStatusChanged.v1`, audiencia
  operations, sin destinatario ni PII). Sin evento, flujo cross-module, estado ni tabla nuevos.
- Lane Notifications `20261002000100_FailAmbiguousWhatsAppNotifications`: reemplaza solo el cuerpo de
  `security.apply_notification_outcome` (misma firma, dueño, permisos y verificación de `lease_token`); rollback
  restaura el cuerpo de NTF-001. IN_APP y email conservan el reintento AMBIGUOUS. AI-06, AI-12 y AI-18 sin cambios.

## Zona horaria del piloto solo America/Mazatlan en el cargador MDM-001 (MDM-001-TZ-MAZATLAN-ONLY-2026-10-02) — 2026-10-02

- Respuesta literal del project owner: "Solo America/Mazatlan"; registrada en `decision-log.md`
  (`MDM-001-TZ-MAZATLAN-ONLY-2026-10-02`).
- Lane Pricing `20261002000100_RequireMazatlanTimeZoneInMasterDataLoader`: `security.load_master_data` rechaza una entrada de
  ciudad con zona distinta de `America/Mazatlan` (`MDM001_CITY_TIMEZONE_NOT_IN_PILOT`, también en dry run); el validador del
  job repite la regla. Una zona fuera de la lista mexicana sigue siendo `MDM001_CITY_TIMEZONE_NOT_ALLOWED`.
- Ninguna fila guardada se reescribe. Sin permisos, tablas ni roles nuevos; rollback restaura la función anterior. AI-06 y AI-18
  sin cambios.

## Alertas OBS-002 del piloto cada 15 minutos (OBS-002-ALERTS-15MIN-COST-2026-10-02) — 2026-10-02

- Respuesta literal del project owner: "Sí, revisar cada 15 min"; registrada en `decision-log.md`
  (`OBS-002-ALERTS-15MIN-COST-2026-10-02`).
- `deploy/azure/pilot/observability.bicep`: la alerta de disponibilidad `sqr-pv-pilot-readiness` pasa de cada 5 minutos
  con ventana de 10 a cada 15 minutos con ventana de 15 (Azure exige ventana ≥ frecuencia). Las cinco reglas cuestan
  ≈ 2.75 USD/mes; el piloto queda en ≈ 101 USD típico y ≈ 111 USD con el tope diario de logs.
- El correo de destino `alertEmailAddress` sigue siendo parámetro obligatorio del owner, sin valor por defecto, pendiente.
- Sin cambios de contrato, esquema, roles ni migraciones.

## Finanzas ve la lista de cobros pendientes de conciliar (FIN-PENDING-COD-LIST-FINANCE-2026-10-02) — 2026-10-02

- Respuesta literal del project owner: "Tope COD 20,000 pesos, finanzas sí ve la lista". Esta entrada cubre solo
  "finanzas sí ve la lista"; registrado en `decision-log.md` (`FIN-PENDING-COD-LIST-FINANCE-2026-10-02`).
- AI-05: FINANCE con MFA puede llamar `listOrders` únicamente con `cod_pending_reconciliation=true` (sin MFA,
  `403 MFA_REQUIRED`); cualquier otra llamada de FINANCE a `listOrders` sigue en 403. Se documentan la descripción
  de `listOrders`, su `x-authorization-precedence` y `x-capability-matrix.cod_pending_reconciliation_filter`; la
  fila `operations.listOrders` no cambia. La lista vuelve a verificar los roles de `getOrderFinancials` dentro de
  la transacción tenant antes de leer órdenes. FINANCE recibe la misma representación `Order` que los demás
  lectores, sin datos personales ni `cod_expected_cents`, y sigue sin crear ni modificar órdenes.
- AI-07 `cod_control.pending_list`: agrega FINANCE con MFA.
- Sin migraciones, esquemas, roles de base de datos ni flujos nuevos.

## Tope de COD declarado por orden: 20,000 MXN (COD-CAP-20000-2026-10-02) — 2026-10-02

- Literal del project owner: "Tope COD 20,000 pesos, finanzas sí ve la lista". Esta entrada cubre solo el tope;
  registrado en `decision-log.md` (`COD-CAP-20000-2026-10-02`).
- AI-05: `CreateOrderRequest.cod_expected_cents` y `CsvImportRowPreview.cod_expected_cents` agregan
  `maximum: 2000000` (20,000.00 MXN, inclusivo). Un monto mayor en `POST /orders` es el mismo 409 uniforme que
  cualquier literal COD inválido; en el CSV es el error de fila `COD_EXPECTED_CENTS_INVALID` (sin código nuevo) y
  bloquea la confirmación. La entrada D6-COD-EXPECTED de `x-pilot-contract-deltas` documenta el tope.
- AI-07: `create_order.cod_expected` y `csv_order_import` muestran el tope.
- Sin migración ni CHECK nuevo: AI-06 conserva `CHECK (cod_expected_cents >= 0)`; el tope vive en
  `OrderInputPolicy` y en el guard de dominio de `Order.Create`. AI-06 y AI-18 sin cambios.

## COD declarado por el despachador en la orden y en el CSV (D6-COD-EXPECTED) — 2026-09-29

- Implementa `D6-COD-PILOT` ("el despachador declara el monto COD al crear la orden (y en el CSV)"), reaprobado para
  el CSV de pedidos el 2026-09-29; registrado en `decision-log.md` (`D6-COD-EXPECTED-IMPLEMENTED-2026-09-29`).
- AI-05: `CreateOrderRequest.cod_expected_cents` opcional (int64 en centavos MXN, mínimo 0; ausente o 0 es sin COD; cualquier literal que no sea un entero simple es 409). `CsvImportPreviewRequest.file` documenta la columna
  opcional `cod_expected_cents` al final del encabezado; `CsvImportRowError` agrega `COD_EXPECTED_CENTS_INVALID` y
  `CsvImportRowPreview` devuelve `cod_expected_cents` en cada fila válida. La entrada D6-COD-EXPECTED de `x-pilot-contract-deltas` queda
  marcada como implementada. `Order` no expone el monto: VIEWER lee órdenes y no tiene lectura financiera.
- AI-07: `create_order.cod_expected` y una validación de `csv_order_import` para mostrar el COD por fila.
- Sin migración: `orders.cod_expected_cents` ya existe en AI-06 con `CHECK >= 0`. AI-06 y AI-18 sin cambios.

## Lectura de incidencias y pruebas; roles de openIncident (API-INC-LIST-PROOFS-2026-09-29) — 2026-09-29

- Respuesta literal del project owner: "El punto I apruebo todos los puntos listados."
- AI-05: `listIncidents` (`GET /incidents`, paginación por cursor, filtros `status` y `order_id`),
  `getIncident` (`GET /incidents/{incidentId}`) y `listOrderProofs` (`GET /orders/{orderId}/proofs`). Devuelven
  las representaciones existentes `Incident` y `Proof` (esquemas nuevos `IncidentPage` y `ProofPage`); nunca la
  descripción, el motivo de resolución, bytes, llaves de almacenamiento, URLs firmadas, punto de captura ni nombre
  del destinatario. Solo DISPATCHER y PLATFORM_ADMIN con MFA, como `resolveIncident`; capacidad antes de leer
  cualquier orden, incidencia o prueba; 404 uniforme para lo ajeno o inexistente; solo lectura.
- `x-capability-matrix.incident_operations` publica quién abre incidencias: DISPATCHER, PLATFORM_ADMIN con MFA y
  DRIVER solo para una orden con asignación ACCEPTED o ACTIVE; es la regla que el servidor ya aplicaba.
- AI-07 `incident_desk`: la pantalla lista y elige incidencias y evidencias en lugar de pedir UUID escritos.
- Sin migraciones, estados, eventos ni roles de base de datos nuevos.

## Presentación de impuestos con IVA incluido (GATE-011-VAT-INCLUDED-2026-09-29) — 2026-09-29

- Respuestas literales del project owner: "Presentación de impuestos, IVA incluido"; "52 con IVA incluido";
  "Igual para todas". AI-10 mueve GATE-011 a `resolved_decisions`.
- Todas las organizaciones cotizan con IVA incluido: la tarifa es el total; `subtotal = floor((100 * total + 58) / 116)`
  (redondeo half-up al centavo, aritmética entera, IVA 16 %), `tax = total - subtotal`, `discount = 0`. El piso
  congelado `minimum_total_cents_snapshot` es el total con IVA. `PLUS_VAT` conserva su aritmética probada y `EXEMPT`
  se conserva en el vocabulario, pero ninguno es seleccionable: una regla seleccionada así falla cerrado.
- El umbral de 52 MXN de AI-07 (`low_price_guard`) compara el total con IVA incluido, no el neto; se conserva el
  límite no estricto (52.00 exactos se bloquean salvo ruta consolidada).
- Lane Pricing `20260929000200_RequireVatIncludedTariffsInMasterDataLoader`: `security.load_master_data` no crea reglas
  `PLUS_VAT` ni `EXEMPT` (`MDM001_TARIFF_TAX_MODE_NOT_ALLOWED`); una regla guardada se puede cerrar. Sin permisos,
  tablas ni roles nuevos; rollback restaura la función anterior. AI-06 y AI-18 sin cambios.
- AI-04, AI-07 y AI-19 registran la resolución. Las cotizaciones y órdenes existentes conservan sus montos congelados.

## Enlaces de tracking sin revocación (TRK-002-NO-REVOCATION) — 2026-09-29

- Decisión literal del project owner registrada en `decision-log.md` (`TRK-002-NO-REVOCATION`): "La liga la puede
  ver el despachador, y el cliente al que le llegará el pedido, la cual se le enviará por WhatsApp y correo
  electronico."; PLATFORM_ADMIN "Sí, con MFA"; quién la anula: "Nadie la anula".
- AI-05: se elimina `revokeTrackingLink` (`POST /orders/{orderId}/tracking-link/revoke`) y su fila en
  `x-capability-matrix.tracking_link_operations`; la decisión de esa sección deja de estar pendiente de
  confirmación (DISPATCHER sin MFA, PLATFORM_ADMIN con MFA). `issueTrackingLink` y `PublicTrackingLink` explican
  que nadie revoca el enlace y que `generation` solo crece al sustituir un token previo a la derivación o de una
  versión de llave ya no configurada.
- Vigencia sin cambios: mientras la orden avanza y 24 horas después de su primer estado público final.
- AI-04, AI-08 y AI-24 registran la ausencia de revocación. AI-06 y AI-18 sin cambios y sin migración: no existía
  función ni grant exclusivos de la revocación; `revoked_at` y `generation` se conservan.
- El envío del enlace por WhatsApp y correo sigue bloqueado por GATE-004, GATE-007 y las plantillas del owner.

## Enlaces de tracking automáticos y derivados (TRK-002-AUTO-LINK) — 2026-09-29

- Decisión literal del project owner registrada en `decision-log.md` (`TRK-002-AUTO-LINK`); sustituye el emitir y
  rotar de `TRK-002-ISSUE-ENDPOINT`.
- Cada orden recibe su enlace dentro de la transacción de `createOrder` (flujo 1 de AI-13 §4; escritura interna de
  Orders, no un sexto flujo), auditado `TRACKING_TOKEN_ISSUED`. El token es Base64URL sin padding de HMAC-SHA256 con
  una llave de Key Vault (`public-tracking-link-key`) sobre `paquetenvia-trk-v1|key_version|order_id|generation`;
  solo se guarda su SHA-256 (regla 6 de AI-01 sin cambio), con `generation` y `key_version`.
- AI-05: `issueTrackingLink` pasa a obtener-o-crear: 200 `no-store` con `url`
  (`{PublicTracking:PublicBaseUrl}/track/{token}`), `generation` y `valid_until`; el mismo enlace para cualquier
  reintento o Idempotency-Key, sin rotar. 409 `TRACKING_LINK_ORDER_FINISHED` para una orden en estado público final
  sin enlace vigente (`TrackingLinkConflict`, `TrackingLinkConflictProblem`). `revokeTrackingLink` retira la
  generación actual; la siguiente lectura deriva la generación siguiente, nunca una revocada.
- Vigencia: mientras la orden avanza (RESCHEDULED incluido) y 24 horas después de su primer evento público
  DELIVERED, RETURNED o CANCELLED, verificado en `security.get_public_tracking_projection`; después, 404 uniforme.
  Se elimina `PublicTracking:TokenLifetimeHours`.
- AI-06: `orders.public_tracking_tokens` gana `generation` y `key_version` y dos índices únicos parciales; la
  proyección pública aplica la vigencia. Lane Orders `20260929000100_AddTrackingLinkGenerations` y lane
  PlatformEvolution `20260929000200_BoundTrackingLinksToOrderLifecycle`, ambas con rollback que falla cerrado.
  AI-18 sin cambios.
- AI-04, AI-08, AI-13 y AI-24 registran la derivación, la vigencia y las pruebas.
- El envío del enlace a clientes sigue bloqueado por GATE-004 y GATE-007.

## Pantallas UI-001 de importación CSV, incidencias y COD (UI-001-SCREENS-CSV-INC-COD) — 2026-09-28

- Respuesta literal del project owner: "Sí, agrégalas (Recommended)".
- AI-07 agrega las rutas `/ops/orders/import`, `/ops/incidents` y `/finance/cod` y sus contratos de pantalla
  `csv_order_import`, `incident_desk` y `cod_control`: roles, fuentes, estados y validaciones, limitados a las
  operaciones AI-05 existentes (`previewOrderCsv`, `commitOrderCsv`, `openIncident`, `resolveIncident`,
  `getOrderFinancials`, `recordCodCollection`, `reconcileCod`). D5-CAPABILITY-MATRIX y el step-up MFA de
  PLATFORM_ADMIN y FINANCE siguen aplicando; el backend sigue siendo la barrera de autorización.
- Sin API, estados, eventos ni migraciones nuevos. Cada contrato declara en `not_supported` lo que AI-05 no
  permite hoy (leer o listar incidencias y pruebas, leer registros COD, filtrar órdenes con COD pendiente).

## Emisión y revocación de enlaces de tracking (TRK-002) — 2026-09-28

- Implementa `TRK-002-ISSUE-ENDPOINT` (respuesta literal del project owner: "Sí, con botón en UI
  (Recommended)").
- AI-05: `issueTrackingLink` (`POST /orders/{orderId}/tracking-link`, 201 con `Cache-Control:
  no-store` y el esquema `PublicTrackingLink`) y `revokeTrackingLink`
  (`POST /orders/{orderId}/tracking-link/revoke`, 204). El token en claro se devuelve una sola vez
  y nunca se persiste ni se registra; solo se guarda su SHA-256. Emitir cuando ya hay un enlace
  lo rota y revoca los anteriores. Auditoría `TRACKING_TOKEN_ISSUED`, `TRACKING_TOKEN_ROTATED` y
  `TRACKING_TOKEN_REVOKED`. 404 uniforme para una orden ajena o inexistente.
- `x-capability-matrix.tracking_link_operations`: DISPATCHER sin MFA y PLATFORM_ADMIN con un reto
  MFA satisfecho (403 `MFA_REQUIRED` cuando solo falta el segundo factor), como `assignDriver`,
  `createRoute` y `createExternalOffer`: emitir o revocar un enlace crea o retira una credencial
  pública al portador. D5 no cubría estas operaciones; es el valor por defecto más seguro según
  AI-01 §7 y queda pendiente de confirmación del owner.
- El envío del enlace a clientes por WhatsApp o correo sigue bloqueado por GATE-004 y GATE-007.

## Endurecimiento del cargador de datos maestros (MDM-001-LOADER-HARDENING) — 2026-09-28

- Seguimiento de tres hallazgos MINOR de la revisión de MDM-001, diferidos del PR de MDM-001; sin API,
  estados, eventos ni UI nuevos.
- X1: `security.load_master_data` rechaza en cada llamada una sesión cuyo login sea miembro de
  `paqueteria_migrator` (`MDM001_DEPLOYMENT_PRINCIPAL_REFUSED`): un miembro con sólo `ADMIN` sobre
  `paqueteria_master_data_loader` podía concederse `SET` y llamar la función directamente.
- X2: `operator_ref` deja de ser un SHA-256 sin sal de un login adivinable y pasa a ser un UUID aleatorio
  por login, en `platform.master_data_operator_refs` (sólo plataforma: FORCE RLS con una única política para
  `paqueteria_migrator`, sin permisos de runtime; el ejecutor sólo `SELECT`/`INSERT` de dos columnas). Sin
  secreto nuevo. Las filas de auditoría anteriores (append-only) conservan el formato SHA-256.
- X3: `master-data-gate` toma `pg_advisory_xact_lock(2026092803)` antes de leer la marca de despliegue, para
  que dos primeras ejecuciones concurrentes no puedan dejar `SYNTHETIC` sobre un `REAL`.
- Lane de Pricing `20260928000400_HardenMasterDataLoaderOperatorBoundary` (dueña de la tabla y de los cuatro
  grants, como el paso de `policy_version`); AI-18 lo documenta en comentarios y en las aserciones 27 y 28.

## Versión de política de precios por organización (PRC-POLICY-VERSION-PER-ORG) — 2026-09-28

- Respuesta literal del project owner: "Versión por organización" ("Cada organización tiene su
  propia versión de política, que se sube cuando cambia sus tarifas, y esa versión se congela en
  cada cotización.").
- AI-06: `pricing.tariff_rules.policy_version text NOT NULL CHECK (policy_version ~
  '^[A-Za-z0-9._-]{1,64}$')`. La cotización congela en `pricing_policy_version` la versión de la
  regla de tarifa seleccionada y la orden la copia sin cambios. Se elimina la configuración global
  `Pricing:PricingPolicyVersion`.
- Lane de Pricing (`20260928000200_VersionPricingPolicyPerOrganization`): adopta la columna en
  instalaciones nuevas y la crea en las existentes sin reescribir filas (NOT NULL si ninguna regla
  carece de versión; si no, CHECK NOT VALID para filas nuevas o actualizadas). El rollback se niega
  mientras alguna regla tenga versión.
- AI-02 (`contract_hardening.quote_to_order.pricing_policy_version`), AI-04 (`TariffRule` y reglas
  de `Quote`/`Order`), AI-05 (descripción de `pricing_policy_version`), AI-08 (PRC-001) y AI-13 §4.
- Integración con MDM-001 (lane de Pricing `20260928000300_StoreTariffPolicyVersionInMasterDataLoader`):
  `security.load_master_data` guarda el `policy_version` de cada regla y rechaza cambiar el de una regla
  guardada (`MDM001_TARIFF_POLICY_VERSION_IMMUTABLE`); el ejecutor recibe `SELECT` e `INSERT` sobre esa
  columna (nunca `UPDATE`). Esos dos grants los posee la lane, no los `GRANT` de AI-18, porque el paso
  MDM-001 publicado verifica exactamente los 116 grants de AI-18 antes; AI-18 lo documenta (aserción 27).

## Carga de datos maestros del piloto (MDM-001) — 2026-09-28

- Decisión del project owner `MDM-001-OPERATOR-LOADER`, respuesta literal: "Herramienta de operador
  (Recommended)". Traducción en `MDM-001-CONTRACT-TRANSLATION`.
- AI-06: `platform.master_data_deployment_gate`, marca global de despliegue (`SYNTHETIC` o `REAL`,
  `gate_007_closed`) con FORCE RLS, escrita sólo por el migrador (cada cambio auditado; nunca de `REAL` a
  `SYNTHETIC`); sin fila vale `REAL` con GATE-007 abierto. AI-18 le da una única política, para
  `paqueteria_migrator`.
- AI-18: `paqueteria_master_data_executor NOLOGIN BYPASSRLS`, con grants exactos por columna sobre las
  seis tablas maestras, lecturas mínimas de usuario, organización (incluido su tipo), membresía y la marca
  de despliegue e `INSERT` en `platform.audit_logs` (sin `DELETE`), y `paqueteria_master_data_loader
  NOLOGIN NOBYPASSRLS`, con sólo `USAGE` sobre `security`: una capacidad de operador de plataforma, no una
  frontera de tenant, que sólo reciben logins de operador con nombre que no sean principales de despliegue.
  Ninguno se concede a
  `paqueteria_app` ni a `paqueteria_worker`; los roles persisten tras el rollback de la lane. Aserciones de
  despliegue 26 a 28; `validate_contracts.py` verifica los grants exactos.
- La lane de Pricing (`20260928000100_AddMasterDataLoader`) instala
  `security.load_master_data(uuid,uuid,json,bytea,boolean)`, SECURITY DEFINER con
  `search_path=pg_catalog, pg_temp`, con `EXECUTE` sólo para el beneficiario del operador. Aplica
  GATE-007 y la clasificación del archivo según la marca de despliegue, sólo deja crear ciudades a una
  organización `PLATFORM` (siempre `ACTIVE`, zonas IANA de México), rechaza vigencias de tarifa
  traslapadas, valida todo el documento (incluidos los mismos límites y tokens de centavos que el job)
  antes de escribir, es idempotente por llave natural, exige el tenant exacto en `app.current_org_ids`,
  escribe una fila de auditoría por carga con un seudónimo del operador (`operator_ref`) y no escribe nada
  en dry-run. La lane elimina la sobrecarga `jsonb` de la primera versión publicada.
- AI-06 no cambia.

## Unirse a una organización existente por correo (REG-002) — 2026-09-27

- Respuestas literales del project owner: "El admin la agrega por correo (Recomendado)",
  "Tabla de invitaciones + HMAC (Recomendado)", "PLATFORM_ADMIN + admins de la org con MFA
  (Recomendado)", "Los de su tipo, incluido admin (Recomendado)", "Cualquier rol; PLATFORM_ADMIN
  sólo en la org de plataforma (Recomendado)", "Sí; el perfil se completa después (Recomendado)",
  "Sólo renueva la vigencia (Recomendado)", "7 días (Recomendado)" y "Acepta todas (Recomendado)".
- AI-06: `organizations.pending_memberships` (HMAC con llave del correo normalizado y versión de
  la llave, nunca el correo; estados PENDING, ACCEPTED y REVOKED; vence a los 7 días), FORCE RLS
  con política de tenant.
- AI-18: `paqueteria_app` sólo lee la tabla bajo RLS y `paqueteria_worker` no tiene privilegios;
  `paqueteria_registration_executor` recibe grants exactos por columna (verificados por
  `validate_contracts.py`) y la aserción 25. Sus cuatro funciones SECURITY DEFINER
  (`add_pending_membership`, `renew_pending_membership`, `revoke_pending_membership`,
  `apply_pending_memberships`) se instalan en la lane de Organizations
  (`20260927000500_AddPendingMemberships`), con `EXECUTE` sólo para `paqueteria_app`.
- AI-05: `addPendingMembership` (202 idéntica exista o no la cuenta), `listPendingMemberships`,
  `renewPendingMembership` y `revokePendingMembership` (409 `IDEMPOTENCY_CONFLICT` o
  `PENDING_MEMBERSHIP_NOT_PENDING`); `x-capability-matrix.membership_operations` con MFA.
- AI-03 §17.1, AI-04 (`PendingMembership`), AI-24 `bff_session` (`pending_memberships`) y
  AI-08 (REG-002).

## Almacén PostgreSQL de sesiones BFF — 2026-09-27

- `BFF-SESSION-STORE-IMPLEMENTATION` (implementa `BFF-SESSION-STORE-POSTGRESQL` y
  `BFF-SESSION-TABLE-SHAPE`): AI-06 agrega `identity.bff_sessions`, previa al
  tenant, con FORCE RLS y sin política; guarda el SHA-256 de la clave opaca, el
  `sub`, el `sid` de AuthCenter y el ticket cifrado con Data Protection, que la
  revocación borra.
- AI-18 agrega `paqueteria_session_executor NOLOGIN BYPASSRLS` con grants por
  columna (sin `DELETE`), revoca todo privilegio de `paqueteria_app` y
  `paqueteria_worker` sobre la tabla y registra las cinco funciones que instala
  el lane de Identity (`20260927000400_AddBffSessionStore`):
  `security.create_bff_session`, `security.resolve_bff_session(bytea)` y
  `security.revoke_bff_session` por clave, por `authcenter_sid` y por `sub`
  anterior a un momento, con `EXECUTE` solo para `paqueteria_app`. Aserciones de
  despliegue 20 a 24.
- La purga `security.purge_bff_sessions(integer)` se suma a
  `paqueteria_cleanup_executor` mediante el lane de Custody
  (`20260927000400_AddBffSessionPurge`) y un job del Worker desactivado por
  defecto. `validate_contracts.py` fija los grants exactos del nuevo rol y que la
  tabla no tenga grants de runtime ni política.
- AI-03 §17.1 y §25.2, AI-24 `bff_session` y AI-08 (OPS-003) describen el
  almacén.
- `BFF-LOGOUT-JTI-PERSISTENCE` (respuesta literal del project owner: "Sí, a
  PostgreSQL (Recomendado)"): AI-06 agrega `identity.bff_logout_jtis` (SHA-256
  del `jti`, retención `exp` + 5 min con tope de un día), previa al tenant, con
  FORCE RLS, sin política ni grants de runtime; AI-18 agrega
  `security.register_bff_logout_jti(bytea,timestamptz)` de
  `paqueteria_session_executor` (`INSERT ... ON CONFLICT DO NOTHING`, verdadero
  solo en el primer registro de cualquier réplica, `EXECUTE` solo para
  `paqueteria_app`). La API registra el `jti` y revoca las sesiones en una sola
  transacción; `security.purge_bff_sessions(integer)` también purga los `jti`
  vencidos. `validate_contracts.py` fija el grant exacto y la ausencia de grants
  de runtime sobre la nueva tabla.
## Membresía por defecto liberada al crear (REG-001) — 2026-09-27

- Respuesta literal del project owner: "Liberarla al crear (Recomendado)"
  (`REG-DEFAULT-MEMBERSHIP-RELEASE`). `create_self_service_organization` pone
  `is_default=false` en las membresías por defecto del usuario cuya organización ya no está
  ACTIVE (por ejemplo, un ALLY rechazado) y después hace por defecto la nueva membresía si no
  queda ninguna por defecto en una organización ACTIVE. Nunca toca una por defecto en una
  organización ACTIVE.
- AI-18: `GRANT UPDATE (is_default) ON organizations.organization_memberships TO
  paqueteria_registration_executor`; `validate_contracts.py` lo agrega a los grants exactos y a
  la lista de grants `UPDATE` por columna.

## Registro abierto y onboarding de organizaciones (REG-001) — 2026-09-27

- Respuestas literales del project owner: "El login y el registro de cuentas no será por
  invitación, cualquiera puede registrarse en Paquetenvia" (`AUTH-OPEN-REGISTRATION`, que
  reemplaza a `AUTH-FIRST-LOGIN-INVITATION`), "Crea su propia organización", "BUSINESS, activa;
  ALLY con aprobación (Recomendado)", "Sí, obligatorio (Recomendado)" (correo verificado),
  "Sólo activar la org (Recomendado)", "Lista + aprobar/rechazar (Recomendado)", "Queda
  cerrada; puede volver a solicitar (Recomendado)", "Endpoint propio de solicitudes
  (Recomendado)" y "solo una organización por persona". Unirse a una organización existente
  ("El admin la agrega por correo (Recomendado)") queda para un PR aparte.
- AI-06: `organizations.status` admite `PENDING_APPROVAL`; nueva columna
  `organizations.self_service_creator_user_id` e índice único parcial
  `organizations_one_open_self_service_uq` (una organización no CLOSED creada por persona).
- AI-18: rol `paqueteria_registration_executor NOLOGIN BYPASSRLS` con grants exactos por
  columna (verificados por `validate_contracts.py`) y aserciones de despliegue 17–19. Sus cinco
  funciones SECURITY DEFINER (`register_identity_subject`, `create_self_service_organization`,
  `list_own_organization_applications`, `list_pending_ally_organizations`,
  `decide_ally_organization`) se instalan en la lane de Organizations
  (`20260927000400_AddSelfServiceRegistration`), con `EXECUTE` sólo para `paqueteria_app`.
- AI-05: `createOnboardingOrganization` (POST /onboarding/organizations, 201; 409
  `IDEMPOTENCY_CONFLICT` u `ORGANIZATION_LIMIT_REACHED`), `listMyOrganizationApplications`,
  `listPendingAllyOrganizations` y `decideAllyOrganization` (409 `ALLY_DECISION_CONFLICT`);
  `x-capability-matrix.platform_operations`; el callback redirige a
  `/login?error=email_not_verified` sin `email_verified == true`.
- AI-03 §17.1/§24, AI-04 (estados de Organization), AI-07 (`/login`, `/onboarding`), AI-24
  `bff_session` (`first_access`, `email_verified`, `onboarding`) y AI-08 (REG-001).
- Límite anti-abuso decidido; sin preguntas abiertas.

## Tope de 72 h para la edad máxima de `openIncident` — 2026-09-27

- Respuesta literal del project owner: "Tope en 72 h (Recomendado)"
  (`OPS-003-INCIDENT-AGE-CAP-72H-2026-09-27`). `Incidents:MaximumOccurrenceAgeHours`
  sólo se configura hacia abajo (1–72 h, por defecto 72); un valor mayor impide el
  arranque. El tope coincide con el piso de 72 h de la purga de llaves de
  idempotencia, así que un replay cuya llave pudo purgarse siempre se rechaza.
- AI-05 `x-offline-operation-age`: `openIncident.maximum_age_range` pasa a
  `PT1H..PT72H`, se agrega la decisión y una regla; la descripción de
  `openIncident` indica el tope. AI-08 (OPS-003) registra la decisión.

## `openIncident` unificado con `OFFLINE_OPERATION_EXPIRED`, límites configurables — 2026-09-27

- Respuesta literal del project owner: "Unificar pero configurable"
  (`OPS-003-INCIDENT-72H-UNIFICATION-CONFIGURABLE-2026-09-27`), que implementa
  `OPS-003-INCIDENT-72H-UNIFICATION`.
- AI-05: `openIncident` describe la regla; `IncidentConflictProblem` agrega
  `OFFLINE_OPERATION_EXPIRED` (sólo `openIncident` lo emite);
  `x-offline-operation-age` agrega las dos decisiones y la entrada
  `openIncident` con `maximum_age_configurable: true`, sus ajustes
  (`Incidents:MaximumOccurrenceAgeHours`, `Incidents:MaximumOccurrenceSkewMinutes`),
  valores por defecto (PT72H, PT5M) y rangos (PT1H..PT720H, luego PT1H..PT72H por el tope; PT0S..PT60M); el reloj
  adelantado y la marca ausente siguen siendo 409 `INVALID_REQUEST`. Las otras
  tres operaciones conservan su política fija.
- AI-08 (OPS-003) registra la decisión y la regla de `openIncident`.

## Matriz de capacidades D5 implementada — 2026-09-27

- AI-05 `x-capability-matrix` queda `IMPLEMENTED` (PR 108): cada operación de la
  matriz decide la capacidad en el servidor antes de leer estado persistido y
  los roles no listados reciben el 403 uniforme; un 403 cuyo único requisito
  pendiente es MFA lleva `MFA_REQUIRED`.
- `listLocations` admite VIEWER con lat/lng redondeadas en el servidor a 2
  decimales (~1.1 km, mitad lejos de cero); DISPATCHER y PLATFORM_ADMIN reciben
  coordenadas exactas (`D5-VIEWER-LOCATION-PRECISION-2026-09-27`,
  `x-viewer-coordinate-precision`, `x-capability-matrix.viewer_location_precision`).
- `D7-SETTLEMENT-MFA` queda implementado: aprobar y pagar liquidaciones exige MFA
  para todo rol permitido, FINANCE incluido.

## Deltas del piloto publicados con su implementación — 2026-09-27

- AI-05 publica, junto con su código y sus pruebas: `listSettlements`
  (`GET /settlements`, filtros `payee_id`, `status`, `period_from` y
  `period_to`, schema `SettlementPage`); `Cache-Control: no-store` en
  `exportSettlementCsv`; `CreateLocationRequest` sin `pii_key_version`; los
  límites aprobados de aceptación (versiones de 1 a 64 caracteres de
  `^[A-Za-z0-9._-]+$`, `accepted_at` entre -72 h y +5 min del servidor) y de
  paquetes (1 a 20); los 503/409/429 que ya se emitían y los 400 de Locations
  (componente `BadRequest`).
- `reconcileCod`, `getOrderFinancials` y `getRouteFinancials` admiten FINANCE con
  MFA (`FINANCE-COD-RECONCILIATION`, `FINANCE-COD-MFA-2026-09-27`); DISPATCHER
  sigue sin MFA. Las entradas correspondientes de `x-pilot-contract-deltas` y
  `finance_operations_status` quedan como implementadas.
- AI-06/AI-18: `resolve_identity_context` exige organización `ACTIVE`, el
  timeline público se ordena por `(occurred_at, aggregate_version)` con su grant
  de bootstrap, y se agregan los índices parciales de purga y los cuatro
  operativos (`AI06-PILOT-INDEXES`). Las instalaciones existentes los reciben
  por la lane `PlatformEvolution`.
- `validate_contracts.py` acepta la matriz DSP-002 con `503`.

## AUTH-001 logout, back-channel y step-up — 2026-09-27

- AI-05: `POST /auth/logout` responde 200 con `BffLogoutResult`
  (`endSessionUrl`, `AUTH-001-RP-INITIATED-LOGOUT`); nuevo
  `POST /auth/backchannel-logout` (anónimo, `logout_token` form-urlencoded, 200 o
  400 `BackchannelLogoutError`, `AUTH-001-BACKCHANNEL-LOGOUT`); `GET /auth/login`
  acepta `mfa=required` (`AUTH-001-MFA-STEP-UP`); el callback documenta
  `access_denied` → `/login?error=access_denied`
  (`AUTH-001-ACCESS-DENIED-MESSAGE`); la respuesta `Forbidden` usa
  `ForbiddenProblem` con el código opcional `MFA_REQUIRED`.
- AI-03 §17.1 y AI-24 `bff_session`: cierre de sesión RP-initiated (única excepción
  a tokens en el navegador: `id_token_hint` en `endSessionUrl`), back-channel logout
  detrás de una interfaz de terminación de sesiones, step-up MFA, reemplazo de sesión
  en cada inicio y mensaje propio para `access_denied`.
- AI-07 `/login`: mensaje de `access_denied`, oferta "Verificar identidad" y regreso
  desde el end-session de AuthCenter.
- Registro: `AUTH-001-RP-INITIATED-LOGOUT`, `AUTH-001-BACKCHANNEL-LOGOUT`,
  `AUTH-001-MFA-STEP-UP` y `AUTH-001-ACCESS-DENIED-MESSAGE` en `decision-log.md`.

## AUTH-001 BFF — 2026-09-27

- AI-05 documenta la superficie BFF fuera de `/api/v1` (`servers: /` por ruta):
  `GET /auth/login`, `GET /signin-authcenter`, `GET /auth/session` y
  `POST /auth/logout`, con el esquema `bffSession` (cookie
  `__Host-Paquetenvia.Session`), el parámetro `X-AuthCenter-CSRF` y el esquema
  `BffSession` (`GATE-002-BFF-001`). La respuesta de autorización exige `iss`
  (RFC 9207).
- AI-07: `/login` es anónima (punto de entrada de sesión), sin aprovisionar
  (`AUTH-FIRST-LOGIN-INVITATION`).
- AI-03 §17.1 y AI-24 `bff_session` registran el patrón BFF, el almacén de
  sesiones en PostgreSQL (`BFF-SESSION-STORE-POSTGRESQL`, cambio AI-06/AI-18
  pendiente; en memoria y una sola réplica hasta entonces) y el enrutamiento del
  mismo origen del piloto (`PILOT-SAME-ORIGIN-ROUTING`).
- Registro: `AUTH-001-BFF-CONTRACT-TRANSLATION` en `decision-log.md`.

## Finance en AI-05: regla vigente y delta pendiente — 2026-09-27

- Las descripciones de `reconcileCod`, `getOrderFinancials` y
  `getRouteFinancials` vuelven a la regla implementada (DISPATCHER y
  PLATFORM_ADMIN con MFA). FINANCE con MFA (`FINANCE-COD-RECONCILIATION`,
  `FINANCE-COD-MFA-2026-09-27`) queda como delta pendiente en
  `x-pilot-contract-deltas` y en `x-capability-matrix`; lo publica
  `feature/pilot-contract-deltas` junto con el código. Corrige la deriva señalada
  en la revisión del PR #100. Paths y schemas no cambian.

## Decisiones del owner sobre los contratos del piloto — 2026-09-27

- Respuesta literal del project owner: "Bloquean el MVP 1 — Apruebo 2, 3, 4, 5,
  6". Quedan decididos `D8-OUTBOX-LANE-DISPATCH`,
  `AI12-ASSIGNMENT-TERMINAL-STATES`, `BFF-SESSION-TABLE-SHAPE`,
  `OPS-003-CLEANUP-ROLE` y `OPS-003-SERVER-72H-REJECTION`.
- AI-12, AI-13 §7.1, la adenda de ADR-004, AI-08 (OPS-003 y AUTH-001) y AI-10
  pasan de PROPOSED a DECIDED; la traducción a AI-06/AI-18 llega con su migración en el PR de implementación.
- Se elimina de AI-04 el bloque `proposed_amendments`: sus enmiendas de
  intento, custodia e incidencia se decidieron el mismo día y el PR #91 las
  escribe en AI-04 y en el decision-log.
- Respuesta literal del project owner: "Apruebo Contrato de la API (AI-05)".
  Quedan decididos `AI05-LIST-SETTLEMENTS`, `AI05-REMOVE-PII-KEY-VERSION`,
  `AI05-INPUT-LIMITS`, `AI05-DECLARE-EMITTED-ERRORS`, `AI05-TIMELINE-ORDER`
  (incluido el grant de bootstrap sobre `orders.order_events.aggregate_version`)
  y `AI05-EXPORT-NO-STORE`. La superficie de AI-05 cambia con su
  implementación, porque las `*OpenApiImplementationTests` comparan AI-05 con el
  código; este PR no modifica paths ni schemas.
- Respuestas literales del project owner: "Acepto todas las sugerencias para
  Base de datos y pagos contra entrega" y "Aprobado todo". Quedan decididos
  `AI06-PILOT-INDEXES`, `IDENTITY-ORG-ACTIVE-REQUIRED`,
  `FINANCE-COD-RECONCILIATION`, `QUOTE-NO-COD` y `AI08-PILOT-ITEMS`. Los siete
  ítems nuevos de AI-08 quedan aprobados y REL-001 pasa a depender también de
  AUTH-001, ENV-001 y UI-001. El inventario MVP-0/P0 sigue en 29.
- El owner eligió "Declararlos en AI-05 (Recomendado)" para las respuestas 400
  de Locations y del header de contexto tenant (`AI05-DECLARE-400-ERRORS`): se
  declaran sin cambiar el comportamiento, en el PR de implementación
  `feature/pilot-contract-deltas`. `PILOT-CONTRACTS-2026-09` pasa a
  `resolved_decisions` en AI-10; no queda ningún punto abierto.

## Contratos del piloto — 2026-09-26 (propuesta y decisiones)

- AI-05: `x-capability-matrix` (DECIDED D5) y `x-pilot-contract-deltas`, con
  entradas DECIDED (D6, D7) y PROPOSED (`listSettlements`, `pii_key_version`,
  límites de aceptación, códigos no declarados, orden del timeline). La
  superficie HTTP no cambia hasta que la tarea de implementación publique
  cada delta junto con su código y sus pruebas.
- AI-08: 63 ítems. Se agregan AUTH-001, ENV-001, ADP-001, UI-001, OBS-002,
  MDM-001 y TRK-002, marcados `PROPOSED`. Se incorporan criterios DECIDED en
  OPS-003 (72 h), CSV-001 y FIN-001 (D6) y SET-001 (D7).
- AI-04 y AI-12: enmiendas `PROPOSED` tomadas de los PRs #91 y #90. Las
  guards y los eventos vigentes no cambian.
- AI-04 `decided_pending_translation`, AI-13 §7.1 y AI-08 AUTH-001/ENV-001
  recogen las decisiones del 2026-09-26/27: reasignación con asignación nueva,
  primer ingreso por invitación, sesiones BFF en PostgreSQL, enrutamiento del
  mismo origen, claim `amr` y dominio `authcenter.info`. La forma exacta de la
  tabla de sesiones BFF en AI-06/AI-18 se propone en el PR.
- D7: `Cache-Control: no-store` pasa a PROPOSED porque no forma parte del texto
  decidido.
- AI-13 §7.1 y ADR-004: adenda D8 (dirección DECIDED; mecanismo PROPOSED).
- AI-10: decisión abierta `PILOT-CONTRACTS-2026-09`.
- AI-06 y AI-18 no cambian. Sus propuestas SQL se describen en el PR y
  requieren una migración antes de entrar a los archivos canónicos.

## ORD-002: reglas del intento en la máquina de estados — 2026-09-27

- AI-04 registra las reglas de ORD-002 (PR 91) aprobadas por el project owner
  ("Aprobar las 5"): la acción siguiente de la incidencia decide la salida de
  `FAILED_ATTEMPT` (guard `failed_attempt_next_action_respected`); la custodia
  solo se deriva del evento `PICKED_UP`; las pruebas valen solo para el intento
  vigente y nunca si son evidencia de una incidencia; una incidencia justifica un
  solo `FAILED_ATTEMPT`; el intento vigente empieza en la última entrada al estado
  de captura (`ORD-002-ATTEMPT-RULES-2026-09-27`, `ORD-002-ATTEMPT-BOUNDARY`).
- Se agrega `order_state_machine.guard_semantics`. Los 17 estados y las
  transiciones no cambian.

## Reconciliación de gobernanza — 2026-09-26

- Registro documental posterior de los ítems fusionados desde el 2026-08-04
  (entradas siguientes y `decision-log.md`). Cada entrada cita el PR y la
  autorización que consta en su cuerpo; no se infieren autorizaciones.
- Los merges de PRs que declaraban no incluir autorización de merge (#42, #43,
  #44, #45, #46, #59, #60, #79, #80, #81, #82) o que no la mencionan (#70,
  #71, #72, #73, #75, #83) se registran como hechos ejecutados por el project
  owner; la autorización formal queda como pregunta abierta
  `GOV-2026-09-MERGE-AUTHORIZATION`. Las fechas de merge están en UTC.
- Decisiones del project owner tomadas en la sesión de trabajo con el owner vía
  Claude Code del 2026-09-26 (texto literal donde consta):
  "Ramas + PR borrador; el owner fusiona" (`GOV-AGENT-INTEGRATION-001`);
  delegación en Claude de la revisión y fusión de PRs en verde desde el
  2026-09-26 (`GOV-AGENT-MERGE-DELEGATION-001`); "Ruta por main" para
  dependencias nuevas, elegida frente a "Relajar PR Gate"
  (`GOV-DEPENDENCY-ROUTE-001`); login AuthCenter con BFF en la API y cookie
  HttpOnly del mismo origen (`GATE-002-BFF-001`); cierre de asignaciones por
  reacción outbox de Dispatch (`D8-DISPATCH-OUTBOX-CLOSURE`); matriz de
  capacidades (`D5-CAPABILITY-MATRIX`); "el despachador declara el monto COD al
  crear la orden (y en el CSV)" (`D6-COD-PILOT`); "Solo se liquidan periodos ya
  cerrados; un total negativo se rechaza; aprobar y pagar requieren MFA; la
  exportación se audita" (`D7-SETTLEMENT-RULES`); "Una operación offline de la
  PWA puede reintentarse hasta 72 horas; después se descarta y se avisa al
  conductor. Las llaves de idempotencia se purgan después de ese plazo."
  (`OPS-003-OFFLINE-72H`).
- Decisiones del 2026-09-26/27 en la misma sesión: reasignación siempre con
  asignación nueva y cierre CANCELLED de la anterior al reprogramar
  (`D8-REASSIGNMENT-NEW-ASSIGNMENT`); primer ingreso por invitación previa
  (`AUTH-FIRST-LOGIN-INVITATION`); sesiones BFF en una tabla PostgreSQL
  (`BFF-SESSION-STORE-POSTGRESQL`); enrutamiento del mismo origen en el ingress
  de Azure (`PILOT-SAME-ORIGIN-ROUTING`); claim `amr` tras MFA en AuthCenter
  (`AUTHCENTER-AMR-MFA`); dominio `authcenter.info` (`AUTHCENTER-DOMAIN`).
- AI-05, AI-04, AI-06, AI-18 y la implementación no cambian con estas
  decisiones; hoy el código no las cumple y su traducción a contratos queda
  pendiente en `governance/pilot-contracts`.
- AI-10 incorpora `REL-000-OWNER-001` y un registro de integración post-MVP-0
  sin cambiar gates abiertos.
- `CANONICAL_SOURCE_OF_TRUTH.md` y `CLAUDE_VALIDATION_HANDOFF.md` citan los
  hashes vigentes de AI-06 y AI-18.
- No cambian AI-02, AI-04, AI-05, AI-06, AI-08, AI-12, AI-18, SQL, roles,
  migraciones ni código de producción.

## OPS-003 limpieza operativa y rechazo de operaciones offline de más de 72 h — 2026-09-27

- `OPS-003-CLEANUP-ROLE`: AI-18 agrega `paqueteria_cleanup_executor NOLOGIN BYPASSRLS`
  con grants exactos por columna (más `DELETE` de tabla sobre
  `platform.idempotency_keys`, que no tiene forma por columna) y las aserciones
  de despliegue 13 a 16. Las funciones
  `security.purge_expired_idempotency_keys(timestamptz,integer,boolean)` (piso
  fijo de 72 h) y `security.expire_proof_upload_sessions(integer)` las instala
  el lane de Custody (`20260927000100_AddOperationalCleanupExecutor`), como
  ADR-034; AI-06 no cambia. `validate_contracts.py` admite exactamente los dos
  grants `UPDATE (...)` por columna y fija los grants del nuevo rol.
- `OPS-003-SERVER-72H-REJECTION`: AI-05 agrega `x-offline-operation-age`,
  `client_occurred_at` opcional en `TransitionRequest` y
  `CreateProofUploadSessionRequest`, y las respuestas `TransitionConflict` y
  `ProofConflict` con el código `OFFLINE_OPERATION_EXPIRED`.
- AI-03 §25.2 y AI-08 (OPS-003) describen el rol, las funciones y los jobs.

## OPS-004 retención acotada del outbox — 2026-09-26 UTC (PR #81)

- Job de Worker sobre las funciones de purga aprobadas de AI-06/AI-18
  (ADR-030), deshabilitado y en dry-run por defecto; sin migración.
- El cuerpo del PR declara "Draft. Do not merge"; merge ejecutado por el
  project owner; autorización formal pendiente.

## LIF-001 finalización de ventanas de reclamación — 2026-09-26 UTC (PR #83)

- ADR-034: `paqueteria_lifecycle_executor NOLOGIN BYPASSRLS` y
  `security.finalize_expired_orders(integer)`; AI-18 y AI-03 actualizados con
  autorización normativa del project owner registrada en el PR.
- El merge no menciona autorización en el cuerpo del PR. El bridge de Azure
  no cubre el nuevo rol; no hay despliegue autorizado.

## SET-001 flujo de liquidaciones y exportación CSV — 2026-09-26 UTC (PR #82)

- AI-05 agrega siete operaciones de liquidación (41 paths, 79 schemas, 371
  refs); no existe `listSettlements`.
- El cuerpo del PR declara que no se autoriza merge ni despliegue; merge
  ejecutado por el project owner; autorización formal pendiente.

## INC-001 resolución de incidencias — 2026-09-25 UTC (PR #80)

- AI-05 agrega `resolveIncident` y `INCIDENT_STATE_CONFLICT` de forma aditiva.
- El cuerpo del PR describe el seguimiento como autorizado por el owner y
  declara "Draft. Do not merge"; autorización formal de merge pendiente.

## SET-001 integridad del ledger — 2026-09-25 UTC (PR #79)

- Primera migración de Finance (`20260925000100`) sobre las tablas canónicas
  de AI-06, sin modificar AI-05, AI-06 ni AI-18.
- El cuerpo del PR declara que merge, auto-merge y despliegue no están
  autorizados; autorización formal de merge pendiente.

## Promoción development → main — 2026-09-24 UTC (PR #75)

- Promueve FIN-001, SCL-001, INC-001 y CSV-001 a `main`.
- El cuerpo del PR no contiene declaración de autorización de merge;
  autorización formal pendiente (`GOV-2026-09-MERGE-AUTHORIZATION`).

## CSV-001 carga CSV de órdenes — 2026-09-24 UTC (PR #70)

- AI-05 agrega `previewOrderCsv` y `commitOrderCsv`. El cuerpo del PR no
  contiene declaración de autorización.

## INC-001 incidencias e intento fallido — 2026-09-24 UTC (PR #71)

- AI-05 alinea `POST /orders/{orderId}/incidents` con autorización explícita
  del owner registrada en el PR; el merge no menciona autorización.

## SCL-001 hosts stateless — 2026-09-24 UTC (PR #73)

- Key ring de Data Protection en PostgreSQL y atribución de claims por
  réplica. Requiere protector externo de claves antes de activación
  productiva. El cuerpo del PR no contiene declaración de autorización.

## FIN-001 COD y economía por orden — 2026-09-24 UTC (PR #72)

- AI-05 agrega `getOrderFinancials` y `getRouteFinancials` y alinea las dos
  operaciones COD; cambio autorizado por el owner y limitado a esas cuatro
  operaciones según el PR. El merge no menciona autorización.

## AZR-001 fase A — 2026-09-21 UTC (PR #61)

- Controles de repositorio para despliegue DEV-SYNTHETIC. Merge controlado
  autorizado por el owner según el PR, condicionado al head `e1ebce8` y a
  Foundation 35568160027 13/13.

## AZR-001 bridge DEV-SYNTHETIC y corrección E-002 — 2026-09-20 UTC (PRs #59 y #60)

- #59 registra implementación autorizada y merge no autorizado; #60 declara
  "Merge is NOT authorized". Ambos fusionados por el project owner;
  autorización formal pendiente. Azure Clean path sin ejercitar.

## SEC-003 política de entorno sintético — 2026-09-15 UTC (PR #56)

- Merge autorizado por el project owner el 2026-09-15 según el PR; no autoriza
  producción ni datos reales.

## RTE-001 rutas manuales — 2026-09-13 UTC (PR #53) y contrato — 2026-08-29 UTC (PR #46)

- #46 agrega a AI-05 las seis operaciones de rutas y alinea AI-07, AI-08 y
  AI-12 (sin autorización de merge en el cuerpo).
- #53 implementa rutas OWN-only con `merge_authorized = true` limitado a
  `415f57a`.

## EXT-001 ofertas externas — 2026-08-28 UTC (PRs #44 y #45)

- #44 agrega `GET /driver/me/external-offers` y `ExternalOffer.expires_at` a
  AI-05 y alinea AI-07/AI-08; #45 implementa las ofertas. Ambos cuerpos
  declaran "No merge authorization is included"; autorización formal pendiente.

## Entorno local de pruebas manuales — 2026-08-27 UTC (PR #43)

- Sin cambios normativos. El cuerpo declara "No merge authorization is
  included"; autorización formal pendiente.

## NTF-001 notificaciones outbox — 2026-08-26 UTC (PR #42)

- Vertical sintética `orders.created` → `OWNER_ORG_DISPATCHERS` → `IN_APP`
  según NTF-001-OWNER-001/002/003; sin cambios normativos. El cuerpo declara
  "No merge authorization is included"; autorización formal pendiente.

## Decisiones funcionales NTF-001 — 2026-08-04

- El Project owner aprobó `orders.created` como trigger inicial,
  `OWNER_ORG_DISPATCHERS` como audiencia lógica inicial e `IN_APP` como canal
  lógico inicial de la primera vertical de NTF-001.
- La vertical es exclusivamente sintética: utiliza un provider fake
  determinista, template neutral, versionado y tenant-scoped, sin fallback
  cross-tenant, conexiones externas, secretos ni PII real.
- `SENT` significa aceptación por el sink sintético, no lectura humana. No se
  autorizan email, SMS, WhatsApp, push, inbox, dashboard, endpoint, read model,
  destinatarios reales ni provider real.
- Esta actualización es únicamente documental y de gobernanza. No modifica
  AI-05, AI-06, AI-08, AI-10, AI-12 o AI-18, ni código, SQL, migrations, roles,
  tests funcionales o composición runtime.
- La implementación todavía no está autorizada; NTF-001 y MVP-1 continúan sin
  iniciar. GATE-001, GATE-004 y GATE-007 permanecen sin resolver.
- El registro prepara una autorización posterior y separada. No autoriza
  deployment, piloto, producción o go-live, y no inicia EXT-001.

## Aprobación REL-000-OWNER-001 — 2026-08-02 (registro posterior)

- El project owner declaró "Apruebo REL-000" (PR #36), limitado a
  `MVP-0_INTERNAL` con datos sintéticos.
- No autoriza piloto, producción, despliegue, go-live, PII real ni el inicio
  automático de EXT-001.
- El valor histórico `rel000_approved: false` de REL-000-DEF-001 (2026-07-31)
  se conserva; AI-10 registra la aprobación posterior como entrada separada.

## Resolución de alcance REL-000-DEF-001 — 2026-07-31

- El project owner aprobó la opción A: FIN-001 pasa completo de MVP-0 a MVP-1.
- FIN-001 conserva prioridad P0, alcance funcional y dependencias DSP-002,
  EXT-001 y RTE-001; no se crea una variante financiera reducida en MVP-0.
- El inventario MVP-0/P0 pasa de 30 a 29 elementos y REL-000 deja de depender
  normativamente de FIN-001.
- EXT-001 continúa dependiendo de REL-000 y no queda autorizado ni iniciado.
- La decisión resuelve REL-000-DEF-001, pero no aprueba REL-000,
  MVP-0_INTERNAL ni la liberación.
- Bundle emitido como `v0.6-full-canonical-sync-7-fin001-mvp1`; no cambian
  contratos funcionales, OpenAPI, SQL, roles ni SignalR.

## Cursores de resincronización RTM-002 — 2026-07-24 (registro posterior)

- Commit `4738861`: AI-05 agrega `order_id` y `aggregate_version` (int64,
  mínimo 1) obligatorios a `DriverStop` y `aggregate_version` obligatorio a la
  proyección pública de tracking.
- AI-06: `security.get_public_tracking_projection` agrega `aggregate_version` (`o.version`)
  al JSON público.
- AI-18: `paqueteria_bootstrap` recibe `SELECT` sobre la columna `version` de
  `orders.orders` (grant de columnas `id,public_id,status,version`).
- Decisiones asociadas en `decision-log.md`:
  `RTM-002-INPROCESS-DISPATCHER-COLOCATION`, `RTM-002-LOCATION-CURSOR-UTC-MS` y
  `RTM-002-STABLE-OUTBOX-ID-AS-EVENT-ID`. El cambio no tenía entrada en este
  changelog; se registra sin alterar los archivos afectados.

## Capability antes de estado persistido DSP-002 — 2026-07-24

- `DSP-002-CAPABILITY-BEFORE-PERSISTED-STATE` separa validación de forma de
  acceso productivo: `INVALID_REQUEST` puede preceder capability sin abrir la
  transacción.
- Todo request válido relee autorización tenant-aware antes del advisory lock,
  fila idempotente, evidencia de replay o recursos de orden/driver.
- Viewer, Driver y `PLATFORM_ADMIN` sin MFA reciben el mismo 403 para key
  inexistente, completada, con hash distinto o incompleta.
- Dispatcher y admin con MFA conservan creación/replay 201 y conflictos 409.
- El hash canónico, replay histórico, schema, migraciones, AI-06 y AI-18 no
  cambian.

## Visibilidad no enumerable DSP-002 — 2026-07-23

- `DSP-002-NON-ENUMERABLE-VISIBILITY` interpreta AI-04/ADR-023 de forma
  capability-first: un actor sin capacidad recibe 403 antes de resource access.
- El actor autorizado ejecuta siempre `order_packages` y
  `driver_profile_documents` antes de decidir el 404 uniforme.
- Orden/driver missing o cross-tenant comparte componente, plan lógico,
  Problem Details y rollback sin efectos.
- No se usan delays, jitter, cronómetros de seguridad, retries ficticios ni
  consultas deliberadamente costosas.
- Replay reautoriza antes de leer evidencia y sigue sin reevaluar elegibilidad.
- AI-06 y AI-18 permanecen byte-identical.

## Remediación contractual DSP-002 — 2026-07-23

- AI-05 declara 409 para conflictos DSP-002 mediante Problem Details con los
  códigos públicos `INVALID_REQUEST`, `CONFLICT`, `DRIVER_INELIGIBLE` y
  `DRIVER_DOCUMENT_EXPIRED`.
- La matriz de `assignDriver` queda 201/401/403/404/409 conforme a AI-04 y
  ADR-023; orden o conductor ausente/cross-tenant usa 404 uniforme.
- `route_id` es nullable, pero DSP-002 solo admite ausencia o `null` hasta
  RTE-001.
- El vocabulario global conserva `OWN`, `EXTERNAL`, `ALLY_CAPACITY`; DSP-002
  habilita solo `OWN`, EXT-001 reserva `EXTERNAL` y ALY-004 reserva
  `ALLY_CAPACITY`.
- AI-06 y AI-18 permanecen intactos. La adopción de Dispatch se endurece para
  rechazar drift de checks, FKs, acciones, índice parcial, RLS y policy.
- La decisión queda registrada como `DSP-002-CONTRACT-REMEDIATION`.

## Resolución de gates — 2026-07-21

- GATE-002 (proveedor de identidad) RESUELTO: se adopta AuthCenter, servidor OIDC propio, con SLA comprometido de 99.5%, hospedaje en Azure Mexico Central y custodia de llave en Azure Key Vault.
- Migración verificada de access tokens a RS256: los consumidores validan contra JWKS público sin poseer material de firma.
- Separación normativa registrada: AuthCenter autentica, Paquetería autoriza la tenencia por organización.
- Pendiente antes de producción: rotación de llaves, rotación de la llave de desarrollo previamente embebida y confirmación de topología para el SLA.
- Actualización únicamente documental: no modifica contratos normativos v0.6, SQL, roles, endpoints ni código.

## Revisión canónica v0.6 sync 3 — 2026-07-21

- ARC-002 pasa de `PARTIAL` a `DONE` después de que la remediación normativa y
  los cinco jobs del PR pasaran.
- `purge_outbox` y `purge_location_outbox` ya no usan `FOR UPDATE SKIP LOCKED`.
- El `DELETE` revalida ID, estado terminal y cutoff sobre la fila objetivo.
- ADR-030 se conserva: maintenance continúa con `SELECT,DELETE`, sin `UPDATE`.
- AI-18 permanece sin cambios y conserva su checksum.
- La ejecución canónica se alinea a PostgreSQL 18/PostGIS 3.6.
- Purga real, límites, idempotencia y concurrencia se validan en Testcontainers.
- Bundle emitido como `v0.6-full-canonical-sync-3-arc002-purge-remediation`.

## Registro de decisiones v0.7 — 2026-07-21

- ADR-032 aceptado como referencia de diseño para v0.7: tracking en vivo, `PICKUP_IN_PROGRESS` y verificación segura de entrega.
- ADR-033 aceptado como referencia de diseño para v0.7: inventario de sellos de integridad y cadena de custodia física.
- Se registran también el contrato conjunto del guard ADR-032↔ADR-033 y su plan de pruebas SQL/Testcontainers.
- Esta actualización es únicamente aditiva y documental: no modifica contratos normativos v0.6, SQL, roles, endpoints, migraciones ni código.
- La implementación continúa bloqueada por GATE-007/GATE-013, el delta normativo coordinado y la ejecución de pruebas en PostgreSQL/PostGIS real.

## v0.6 — 2026-07-20

- `pgcrypto` aislado en `extensions`; PostGIS permanece en `public` protegido.
- Token de tracking con contrato SHA-256 simétrico C#/SQL sobre UTF-8 exacto.
- Outbox business/GPS con `lease_token`, settle, stale requeue, dead-letter y purga.
- Runtime sin SELECT/UPDATE/DELETE directo sobre outbox; inserts sin `RETURNING`.
- Estados públicos normativos y timeline privado por defecto con `public_event_code`.
- `order_acceptances` RLS/append-only con canonicalización legal fija.
- Todo dinero migrado a `bigint`/OpenAPI `int64`.
- Provisioning transaccional de usuarios/organizaciones bajo RLS.
- `external_offers` recupera aceptante, fecha y versión.
- Lane GPS sin FK y con `UNIQUE(driver_position_id)`.
- OPS-004 y ADR-026 a ADR-031.
- Backlog ampliado a 56 tareas.

## v0.5 — 2026-07-20

- Roles runtime NOBYPASSRLS; bootstrap y claim cross-tenant mediante funciones de roles NOLOGIN dedicados.
- Grants append-only y mutabilidad por columna del outbox.
- Contexto tenant transaction-local, retry-safe y preparado para PgBouncer.
- Esquema físico por módulo y FKs cross-schema gobernadas.
- Cotización de uso único, `pricing_tier`, piso congelado y snapshot sin PII.
- Modelo multi-ciudad inicial.
- POD por URL firmada/cuarentena y GPS batch con lane separado.
- Política 401/403/404, COD mínimo, líneas de liquidación y ventana de reclamación.
- Backlog ampliado a 53 tareas y nuevos ADR-016 a ADR-025.

## v0.4 — 2026-07-20

- Corrige contrato Quote→Order con ubicaciones y snapshots persistidos.
- Introduce membresías y roles por organización.
- Endurece RLS con FORCE, roles separados y políticas para tablas sensibles/hijas.
- Corrige máquina de estados de cancelación, custodia y reintentos.
- Añade ingesta REST de posiciones y contrato SignalR consistente.
- Corrige OpenAPI, idempotencia, audit schema y `Order.version`.
- Corrige FK de tarifas, índice de outbox y nombres de tablas de escalabilidad.
- Corrige numeración ADR, plantilla de lectura y referencia a documentos inexistentes.
- Backlog: 48 tareas, incluyendo ARC-002 y DRV-003; OBS-001 asciende a P0.

## v0.3 — 2026-07-19

- Arquitectura y escalabilidad progresiva formalizadas.
