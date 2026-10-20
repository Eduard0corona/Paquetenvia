# DP-INDEXES-ORDERS-DISPATCH-2026-10-20: índices de lectura de órdenes y despacho

Decisión del owner (G3, 2026-10-20): "Ahora, en un PR (Recommended)" — cambio a AI-06 y migración con subida y bajada
probadas. Origen: evaluación 2026-10-19, `04-data-performance` DP-01, DP-02 y DP-04. Ronda I, sobre lo que un índice solo
no resolvía: I1 "Reescribir la consulta (Recommended)" (la lista de órdenes por defecto en dos partes, en este mismo PR),
I2 "Dejarlo para el piloto (Recommended)" (conteos de la bandeja sin cambio) e I3 "Dejarlo por ahora (Recommended)"
(filtro de cobros pendientes sin cambio). Textos literales, contexto y opciones no elegidas en
`docs/normative/v0.6/decision-log.md`.

## Índices

| Índice | Definición (AI-06) | Lectura que atiende |
|---|---|---|
| `orders.order_events_actor_status_time_idx` | `(actor_id,occurred_at) WHERE actor_id IS NOT NULL AND event_type='ORDER_STATUS_CHANGED'` | «Hechas hoy» de `listMyStops` (`PostgreSqlDriverStopsQuery.CountCompletedTodayAsync`): los cambios de estado del repartidor en el día |
| `dispatch.assignments_order_driver_idx` | `(order_id,driver_id)` | El `EXISTS` de esa misma cuenta: la asignación de la orden a ese repartidor en cualquier estado |
| `dispatch.assignments_driver_open_idx` | `(driver_id) WHERE status IN ('ACCEPTED','ACTIVE')` | Paradas (`ReadStopsAsync`), lista de repartidores (`PostgreSqlOrganizationDriversQuery`) y selector (`PostgreSqlAssignableDriversQuery`) |
| `orders.orders_price_review_idx` | `(updated_at DESC,id DESC) WHERE status NOT IN (5 terminados) AND (COALESCE(total_cents < minimum_total_cents_snapshot,false) OR COALESCE(financial_override ?& ARRAY[...],false))` | Filtro `price_review=true` del tablero (`PostgreSqlOperationsDashboardReader`) |
| `orders.orders_owner_created_idx` (I1) | `(owner_org_id,created_at DESC,id DESC)` | Parte «dueña» de la lista de órdenes por defecto, y el filtro `owner_org_id` de la lista |
| `orders.orders_operator_created_idx` (I1) | `(operator_org_id,created_at DESC,id DESC) WHERE operator_org_id IS NOT NULL` | Parte «solo operadora» de la lista de órdenes por defecto |

`assignments_driver_idx` se queda: las liquidaciones leen todas las asignaciones del repartidor. Ningún índice existente
cubría las partes de la lista: `orders_owner_status_idx` y `orders_operator_status_idx` ponen `status` antes de la fecha
(se siguen usando cuando la lista filtra por estado).

El predicado de `orders_price_review_idx` repite el texto del lector; si uno cambia, el otro también (el planificador
solo usa un índice parcial cuando puede probar su predicado). La prueba de planes falla si eso se rompe.

## Migraciones

- Carril Orders `20261020000100_AddOrderReadIndexes` (cuatro índices de `orders`: dos de G3 y dos de I1) y carril Dispatch
  `20261020000100_AddAssignmentReadIndexes` (dos de `dispatch.assignments`): una migración por módulo dueño de la tabla.
- Cada paso adopta el índice que AI-06 ya creó en una instalación nueva, rechaza uno con el mismo nombre y otra forma
  (nunca lo altera), comprueba `pg_get_indexdef`, validez y dueño (`paqueteria_migrator`), y su bajada quita exactamente
  sus índices (cuatro y dos). Ningún código depende de ellos para sus resultados: sin ellos la lista en dos partes da las
  mismas filas, más lenta, y el release anterior lee lo mismo con su consulta de siempre.
- `CREATE INDEX IF NOT EXISTS` normal, sin `CONCURRENTLY`: cada carril corre dentro de la transacción del migrador (EF
  Core, o la transacción del puente E-002 en Azure) y PostgreSQL no permite `CREATE INDEX CONCURRENTLY` dentro de una
  transacción; los índices previos del repo (AI06-PILOT-INDEXES, evidencia de incidencias) siguen la misma regla. Con las
  tablas del piloto casi vacías la construcción es instantánea; con millones de filas bloquearía escrituras de la tabla
  mientras dura.
- AI-18 no cambia: el índice pertenece al dueño de la tabla y no hay permisos nuevos.

## Por qué no empiezan con la organización

Las políticas de estas tablas son dueña OR operadora. Con índices gemelos `(owner_org_id, …)` y `(operator_org_id, …)`
medidos en el mismo banco: «hechas hoy» del repartidor de un aliado pasó a 25 ms (BitmapOr por cada consulta de la
asignación) y el filtro de revisión de precio nunca los usó (el planificador siguió con el recorrido global de
`orders_updated_at_idx`, 146 ms). Las claves elegidas (actor, repartidor, orden) ya pertenecen a una sola organización y
el índice de revisión de precio solo guarda órdenes sin terminar en revisión; RLS sigue filtrando cada fila leída.

## Planes (antes → después)

PostgreSQL 18.4 / PostGIS 3.6 con AI-06 y AI-18, solo filas sintéticas: 20 organizaciones, 150 000 órdenes en un año
(95 % terminadas, las activas de los últimos dos días), 774 034 eventos y 150 000 asignaciones. `EXPLAIN (ANALYZE,
BUFFERS)` como `paqueteria_app` con RLS forzado y el contexto después de `BEGIN`.

| Lectura | Antes | Después |
|---|---|---|
| «Hechas hoy» (organización dueña) | `Bitmap Index Scan on assignments_driver_idx` (552 asignaciones del repartidor) → 552 búsquedas en `order_events`; 11.7 ms | `Index Scan using order_events_actor_status_time_idx` (126 cambios de hoy) → `Index Scan using assignments_order_driver_idx`; 1.5 ms |
| «Hechas hoy» (aliado operador) | `assignments_driver_idx` (141) → `order_events`; 12.6 ms | los mismos dos índices; 0.8 ms |
| Paradas del repartidor | `assignments_driver_idx` 552 filas | `assignments_driver_open_idx` 26 filas (nodo superior 1.66 → 0.39 ms) |
| Lista de repartidores (100) | `assignments_driver_idx` ~510 filas por repartidor; 55 ms | `assignments_driver_open_idx` ~21 por repartidor; 19 ms |
| Selector de repartidor (100) | igual; 35 ms | `assignments_driver_open_idx`; 5.6 ms |
| Tablero `price_review=true` | `Index Scan Backward using orders_updated_at_idx`, Rows Removed by Filter: 149 993 (todas las organizaciones); 216 ms | `Index Scan using orders_price_review_idx`, Rows Removed by Filter: 16; 0.14 ms |

## Lista de órdenes en dos partes (I1)

`QuoteSnapshotToOrderCoordinator.ListAsync` sin `owner_org_id`, sin `public_id` y sin el filtro de cobros pendientes (la
vista «Todos los estados», también con estado, fechas y cursor) usa `PartyListSql`:

- Por cada organización de `app.current_org_ids` — el mismo ajuste que lee RLS, leído como lo lee
  `security.app_allowed_org` y solo donde esa función la admite; nada fuera del contexto que la consulta ya tenía — dos
  partes: las órdenes que esa organización es dueña (`owner_org_id = org`, `orders_owner_created_idx`) y las que opera y
  cuya dueña la función no admite (`operator_org_id = org AND NOT security.app_allowed_org(owner_org_id)`,
  `orders_operator_created_idx`). Ninguna orden está en las dos.
- Cada parte con el estado, las fechas y el cursor de la lista (mismo texto), `ORDER BY created_at DESC, id DESC` y el
  mismo `LIMIT`; luego se juntan con ese orden y ese límite. `created_at <= cursor` va además en cada parte: lo implica el
  término del cursor y solo deja que el índice empiece ahí. RLS sigue filtrando cada fila de las dos partes.
- Con `owner_org_id`, `public_id` o cobros pendientes la lista usa `FilteredListSql`, el texto de siempre: el filtro de
  dueña ya recorre `orders_owner_created_idx` en orden (184 a 0.12 ms), el número de guía es una búsqueda única y los
  cobros pendientes quedan como están (I3).
- Elección no preguntada: antes de esa consulta, y solo en su transacción, el runtime manda
  `SET LOCAL enable_bitmapscan = off`. PostgreSQL multiplica la estimación de RLS (dueña O operadora) por la de la parte,
  así que para una organización con poca parte de la tabla espera unas cuantas filas y elige un bitmap más un ordenamiento
  de todas sus órdenes: para el aliado del banco fue más lento que la consulta anterior (46-72 ms contra 15-22). Sin bitmap
  cada parte recorre su índice en orden y se detiene en la página, para todas las organizaciones del banco. No cambia
  ninguna fila; la transacción termina con esa consulta.

Banco (mismo de G3: PostgreSQL 18.4 / PostGIS 3.6, 150 000 órdenes sintéticas de 20 organizaciones, `EXPLAIN ANALYZE`
como `paqueteria_app` con RLS forzado; página de 50):

| Lista | Antes (consulta de siempre, sin I1) | Después |
|---|---|---|
| Organización con 60 156 órdenes, primera página | `Parallel Seq Scan` + `top-N heapsort`; 166-221 ms | `Index Scan using orders_owner_created_idx` + `orders_operator_created_idx`; 0.23 ms |
| La misma, página 2 y página 41 (cursor) | 153-219 y 141-282 ms | 0.23 y 0.22 ms (`Index Cond: ... created_at <= cursor`) |
| Aliado que opera 11 101 órdenes | `Bitmap Heap Scan` de 11 101 filas + `top-N`; 15-22 ms | las dos partes por índice; 0.29 ms |
| Negocio con 5 348 órdenes | `Bitmap Heap Scan` + `top-N`; 4.1 ms | 0.21 ms |
| Estado `CLOSED` | 39-142 ms | 0.30 ms |
| Últimos 30 días | 23-35 ms | 0.26 ms |
| Estado `IN_TRANSIT` (pocas filas) | 1.0-1.2 ms | 0.26 ms (`orders_owner_status_idx` + `Incremental Sort`) |
| Filtro `owner_org_id` (consulta de siempre) | 72-184 ms | 0.12 ms (`orders_owner_created_idx`) |

Con solo los índices y la consulta de siempre la lista por defecto no mejora (139-140 ms, el planificador sigue con el
recorrido y el top-N): por eso la reescritura.

## Sin cambio (I2, I3)

- Conteos de la bandeja: `GROUP BY status` de todas las órdenes de la organización, incluidas las terminadas (381 ms en el
  banco). I2: se revisa con las alertas de CPU de la base.
- Filtro de cobros pendientes de conciliar: su `EXISTS` va dentro de `@cod_pending=false OR …`, así que PostgreSQL no lo
  convierte en semi-join y recorre las órdenes de la organización (233 ms). I3: lo usa solo finanzas y poco; sigue con la
  consulta de siempre y sin índice de `cod_transactions`.

## Pruebas

- `tests/Paqueteria.ContractTests/PostgreSql/ReadIndexesPostgreSqlContractTests.cs`: AI-06 declara cada índice igual que
  su paso de carril; la base compartida los tiene con la definición exacta; en una base aislada con historia sintética,
  las seis lecturas (texto leído de su fuente, parámetros con sus tipos, como `paqueteria_app`) usan su índice y no
  `assignments_driver_idx`; bajada de ambos pasos como migrador (índices fuera, carriles pendientes, baseline válido),
  subida con el migrador canónico, idempotencia y rechazo de un índice homónimo de otra forma.
- `tests/Paqueteria.ContractTests/PostgreSql/OrderListPartyPostgreSqlContractTests.cs` (I1): lee las dos consultas de la
  lista de su fuente y las compara, las 22 columnas, como `paqueteria_app`: órdenes solo dueña, dueña y operada por otra,
  dueña y operadora a la vez, y solo operadora; empates de `created_at` dentro y entre las dos partes y cortes de página
  dentro de un empate; cada combinación de estado y fechas, páginas de 3 y de 50, todas las páginas, y contextos de una,
  dos, tres y ninguna organización. Luego pagina la lista del runtime (`QuoteSnapshotToOrderCoordinator`, login de la
  app, siguiendo sus cursores) con cada combinación de estado, dueña, número de guía, cobros pendientes y fechas contra
  la consulta de siempre, y con `EXPLAIN` sobre 10 000 órdenes comprueba que cada parte recorre su índice, también desde
  un cursor, mientras la consulta de siempre ordena las órdenes de la organización. Comprueba también que lee
  `app.current_org_ids` con el mismo texto que `security.app_allowed_org` en AI-06.
- Las pruebas que reescriben el historial de Orders o Dispatch (auto-cierre, ventana de servicio, liquidación del ciclo
  de vida, enlaces de seguimiento, despliegue con puente E-002, OBS-003) incluyen el paso nuevo.
