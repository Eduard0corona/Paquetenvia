# ADR-OBS-001: read model operativo aditivo

- Estado: aceptado para OBS-001
- Fecha: 2026-07-27
- Alcance: dashboard autenticado de despacho

## Contexto

`listOrders` pertenece al contrato normativo AI-05 (modificable solo con
autorización normativa explícita; en la fecha de este ADR no se autorizó
cambiarlo) y no contiene owner/operator
canónicos, cliente, zona de destino, assignment activo, última posición
persistida ni las alertas operativas requeridas por AI-07. Ampliarlo cambiaría
un contrato existente y mezclaría una consulta transaccional con un read model
de operaciones.

PostgreSQL ya conserva las fuentes autoritativas en Orders, Organizations,
Clients, Locations, Dispatch y Drivers. SignalR sólo comunica que puede existir
un cambio; no debe convertirse en almacenamiento ni autoridad.

## Decisión

Se crea el módulo `Reporting` con las cuatro capas canónicas y el endpoint
aditivo `GET /api/v1/operations/dashboard`
(`operationId: getOperationsDashboard`). El baseline AI-05 queda intacto.

El provider `PostgreSql` ejecuta una transacción con contexto RLS local y como
máximo dos comandos Npgsql:

1. establece `app.current_user_id`, `app.current_org_ids`, cambia a
   `paqueteria_app` y autoriza membership activa;
2. obtiene la página mediante una sola consulta CTE, assignment activo y
   posición persistida más reciente.

Sólo `DISPATCHER` y `PLATFORM_ADMIN` con MFA pueden leer. No se infiere ni se
agrega `CUSTOMER_SUPPORT`. Owner/operator usan nombres canónicos visibles bajo
RLS; una ausencia inconsistente falla cerrado. Cliente y zona pueden ser
`null`. Las ventanas permanecen `null` porque no existen como dato persistido.
La referencia de conductor es estable, acotada y no PII (`DRV-` más ocho
hexadecimales derivados del UUID); nunca sustituye un nombre personal.

El cursor opaco ordena por `updated_at DESC, order_id DESC`. El tamaño es fijo
en 50 y el servidor admite como máximo 100. No existe `page_size` controlado por
el cliente.

La web reemplaza su snapshot únicamente mediante REST. Los eventos validados de
OperationsHub se deduplican y disparan refresh con debounce de 250 ms; ubicación
usa 500 ms. Reconexión, polling y cambio de organización también vuelven a
leer REST. Todo estado vive en memoria de la pestaña.

Dashboard y detalle usan un coordinador single-flight común. Un refresh normal
puede agrupar señales y ejecutar como máximo una lectura adicional. El refresh
obligatorio de reconnect tiene prioridad y aplica la estrategia de serializar y
forzar una segunda lectura: espera la request previa, pero siempre inicia otra
lectura REST asociada a la sesión y organización vigentes.

El snapshot de versiones del dashboard se deriva de los items de esa respuesta
obligatoria exacta. El detalle completa conjuntamente orden y proyección
filtrada, exige un item y deriva las versiones de esa misma proyección. Sólo
después de validar y aplicar se reemplaza el guard y se informa `Conectada`.
Errores REST, red, timeout, contrato o cambio de sesión rechazan la
resincronización, informan `Sin conexión` y detienen el Hub.

La vista `Posiciones` normaliza puntos en un panel interno sin proveedor
cartográfico, tiles, geolocalización del navegador o requests externas.
GATE-003 permanece abierto.

## Consecuencias

- El endpoint es aditivo y todavía no forma parte de AI-05 normativo.
- No se agrega DDL, migración, tabla, vista, índice, producer ni paquete.
- La lectura combina módulos sin transferir autoridad de escritura a Reporting.
- La carrera reconnect/request previa y el fallo 503 se verifican sobre el
  pipeline real; las variantes del coordinador se cubren con deferreds.
- `Disabled` devuelve 503 genérico y degrada readiness.
- Las posiciones exactas sólo aparecen a operaciones autorizadas.
- La UI no asigna conductores ni implementa ofertas, rutas o incidencias.
- El despliegue sigue single-instance para realtime/rate limiting; Redis no
  participa y GATE-013 permanece abierto.

## Enmienda 2026-10-10: total de la orden (UI-PHASE3-INBOX-TOTAL-2026-10-10)

Para la columna Total de la bandeja de trabajo (UI-PHASE3-INBOX-2026-10-10), cada item agrega `total`: el
`orders.total_cents` persistido de la orden, con IVA incluido, en la forma `Money` de AI-05 `Order.total`
(`currency` MXN, `amount_cents` int64). Se lee en la misma consulta CTE, dentro de la misma transacción con RLS; un
valor negativo o en otra moneda es una proyección inconsistente y responde 503. Los lectores no cambian (DISPATCHER y
PLATFORM_ADMIN con MFA) y ya reciben ese total por `listOrders`/`getOrder` y `getOrderFinancials`; FINANCE, VIEWER y
los demás roles siguen con 403. No se agregan tarifa, desglose, costo de assignment, margen ni cobro contra entrega.
El endpoint sigue fuera de AI-05.

## Enmienda 2026-10-11: filtro `price_review` (UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11)

Para la lista "Precio por revisar" de la bandeja (respuesta del owner: "Solo órdenes activas (Recommended)"), el
endpoint acepta el filtro booleano `price_review`, que se lee igual que `unassigned`: `true` deja solo las órdenes con
`cost_warning` (total menor que `minimum_total_cents_snapshot` o `financial_override` autorizado, la misma regla de la
proyección) que no han terminado, es decir, cuyo estado no es DELIVERED, RETURNED, CLAIM_RESOLVED, CLOSED ni CANCELLED
(grupo "Terminadas" de UI-STATUS-GROUPS-2026-10-05); `false` o ausente no filtra; otro valor o un parámetro repetido
responde 400. Se evalúa en el WHERE de la misma consulta CTE, dentro de la misma transacción con RLS, y se combina con
los demás filtros y con el cursor, que pagina las filas ya filtradas. Si la consulta devolviera una fila fuera de la
regla, la proyección es inconsistente y responde 503. No cambian la forma de la respuesta ni los lectores (DISPATCHER y
PLATFORM_ADMIN con MFA); los demás roles siguen con 403. `getOperationsQueueCounts` (AI-05) cuenta `queues.price_review`
con el mismo predicado. El endpoint sigue fuera de AI-05 y es la única excepción de `HttpSurfaceOpenApiCoverageTests`.

## Enmienda 2026-10-12: la otra parte de la orden sin nombre (OBS-001-OPERATED-OWNER-NAME-2026-10-12)

Defecto preexistente (riesgo residual del PR #221): cuando la organización seleccionada es el `operator_org_id` de una
orden de otra dueña, AI-06 `organizations_tenant` le oculta la fila de la dueña (`app.current_org_ids` solo contiene la
seleccionada), el nombre llegaba `NULL` y "una ausencia inconsistente falla cerrado" respondía 503 para toda la
página: cada cola de la bandeja y el Tablero, mientras `getOperationsQueueCounts` sí contaba esas órdenes. El caso
espejo fallaba igual: la dueña que lee una orden que opera otra organización no ve el nombre del operador.

Instrucción del owner (2026-10-11): "Pick the minimal option consistent with `owner_org_id` ≠ `operator_org_id`
semantics (CLAUDE.md invariant 2), and document the choice." Se elige no devolver nombre, para las dos partes:

- `owner` y `operator` conservan la forma `{organization_id, display_name}`. `display_name` es el nombre propio de la
  organización seleccionada y `null` para la otra parte de la orden (la dueña de una orden que solo opera, o el
  operador de una orden propia). Los ids se conservan: el operador ya lee `owner_org_id` en la fila de la orden
  (`orders_tenant`, AI-05 `getOrder`).
- "Visible bajo RLS" queda exacto: solo el nombre propio está presente. Falla cerrado (503) una fila que la
  organización seleccionada no posee ni opera, su propio nombre ausente o un nombre que RLS debía ocultar.
- Cliente, zona y demás filas de la dueña siguen ausentes para el operador, como antes. Misma transacción RLS, los
  mismos dos comandos, los mismos lectores (DISPATCHER y PLATFORM_ADMIN con MFA) y el mismo 403 para los demás roles.
- La PWA acepta `display_name: null`, exige una parte con nombre por orden, muestra "Otra organización" (nunca el id)
  en la tarjeta del Tablero y en el detalle, y los filtros Dueño/Opera solo ofrecen organizaciones con nombre
  visible, igual que clientes y zonas.

Se descarta una función SECURITY DEFINER que devuelva el nombre de la dueña al operador: ampliaría lo que el operador
lee de otra organización (decisión de privacidad y aislamiento, BLOCKER según AI-01 §7) y exigiría función, rol o
grant nuevos en AI-06/AI-18 con su propia decisión; el trabajo del operador no necesita ese nombre. Sin cambios en
AI-04, AI-05, AI-06, AI-18, migraciones, roles, grants ni flujos cross-module. Rollback: revertir el commit.

## Enmienda 2026-10-13: límite de tasa del tablero y su 429 (OBS-001-DASHBOARD-429-2026-10-12)

SEC-004-RATE-LIMITS-2026-10-12 limita "tablero y conteos 60/min" y pide que cada operación limitada declare su 429 en
el API, pero este endpoint no está en AI-05. Pregunta literal al owner (2026-10-12): "El tablero (GET
/operations/dashboard) no está en el contrato del API (AI-05): lo cubre la decisión de observabilidad ADR-OBS-001.
Para que también "declare su 429", ¿dónde se registra?". Respuesta: "Enmendar ADR-OBS-001 (Recommended)", cuya
opción decía "Se agrega a esa decisión el 429, con la misma forma de error que el resto del API. Es un cambio
pequeño.". No elegida: "Declararlo en AI-05" ("Se documenta el tablero completo en el contrato: ruta, respuesta y
errores. Más trabajo, pero todo queda en un solo lugar.").

- Límite: la política `Reporting.OperationsDashboard`, 60 lecturas por minuto por usuario (`RateLimits:OperationsDashboard`),
  con presupuesto propio, separado del de `getOperationsQueueCounts`; respuesta literal del owner a "¿Un presupuesto
  compartido o uno para cada uno?": "Uno cada uno (Recommended)" ("60 por minuto para el tablero y 60 por minuto para
  los conteos. Con varias pestañas abiertas, una no le quita cupo a la otra."). La partición es el hash del sujeto
  autenticado (la red del cliente si no hay sujeto). Ventana fija sin cola, en memoria de la API, exacta con una sola
  réplica (GATE-013).
- 429: la misma forma de error que el resto del API, el `TooManyRequests` de AI-05: `application/problem+json`
  `{"type":"about:blank","title":"Too Many Requests","status":429,"traceId":"<32 hexadecimales>"}`, con `Retry-After`
  en segundos enteros (la ventana completa: 60 con la configuración por defecto) y `Cache-Control: no-store`. Lo
  escribe el único escritor de rechazos de la API y el rechazo cuenta en el resumen OBS-002 `RateLimitSummary` (4604)
  con `Policy=Reporting.OperationsDashboard`.
- El limitador corre después de la autenticación y antes del contexto de tenant y de la autorización: un 429 no abre
  transacción ni lee órdenes. No cambian la forma de la respuesta 200, los filtros, el cursor ni los lectores
  (DISPATCHER y PLATFORM_ADMIN con MFA); los demás roles siguen con 403.

Respuestas de error de `GET /api/v1/operations/dashboard` (las lee `HttpSurfaceOpenApiCoverageTests`): `400`, `401`,
`403`, `429`, `503`.

El endpoint sigue fuera de AI-05 y es la única excepción de `HttpSurfaceOpenApiCoverageTests`; esa prueba ya no lo
omite: exige que cada estado de error que el endpoint emite, el 429 incluido, esté en la línea anterior. Sin cambios en
AI-04, AI-06, AI-18, migraciones, roles, grants ni flujos cross-module. Rollback: revertir el commit (el límite vive en
memoria; no queda nada que deshacer).

## Alternativas descartadas

- Ampliar `listOrders`: modificaría AI-05 y acoplaría consumidores existentes.
- Materialized view o tabla de reporting: requiere DDL y sincronización fuera de
  OBS-001.
- Construir estado desde SignalR: pierde eventos, viola autoridad REST y complica
  reconexión.
- Elegir un proveedor de mapas: anticipa GATE-003 y añade terceros no aprobados.
- Leer el nombre de la otra parte de la orden con una función SECURITY DEFINER: amplía lo que una organización lee
  de otra; ver la enmienda OBS-001-OPERATED-OWNER-NAME-2026-10-12.

## Rollback

Configurar `OperationsDashboard:Provider=Disabled`, verificar 503 genérico,
retirar navegación a `/ops/dashboard`, desplegar la web previa e incrementar el
cache name del rollback. Después se pueden revertir los commits y retirar
Reporting de la solución. Se conservan Orders, Dispatch, Drivers, Locations,
Realtime, outbox, auditoría, órdenes, assignments y posiciones. No se ejecuta
DDL ni se borran datos o caches ajenos.
