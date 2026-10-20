# DP-INDEXES-ORDERS-DISPATCH-2026-10-20: índices de lectura de órdenes y despacho

Decisión del owner (G3, 2026-10-20): "Ahora, en un PR (Recommended)" — cambio a AI-06 y migración con subida y bajada
probadas. Origen: evaluación 2026-10-19, `04-data-performance` DP-01, DP-02 y DP-04. Texto literal, contexto y opción no
elegida en `docs/normative/v0.6/decision-log.md`.

## Índices

| Índice | Definición (AI-06) | Lectura que atiende |
|---|---|---|
| `orders.order_events_actor_status_time_idx` | `(actor_id,occurred_at) WHERE actor_id IS NOT NULL AND event_type='ORDER_STATUS_CHANGED'` | «Hechas hoy» de `listMyStops` (`PostgreSqlDriverStopsQuery.CountCompletedTodayAsync`): los cambios de estado del repartidor en el día |
| `dispatch.assignments_order_driver_idx` | `(order_id,driver_id)` | El `EXISTS` de esa misma cuenta: la asignación de la orden a ese repartidor en cualquier estado |
| `dispatch.assignments_driver_open_idx` | `(driver_id) WHERE status IN ('ACCEPTED','ACTIVE')` | Paradas (`ReadStopsAsync`), lista de repartidores (`PostgreSqlOrganizationDriversQuery`) y selector (`PostgreSqlAssignableDriversQuery`) |
| `orders.orders_price_review_idx` | `(updated_at DESC,id DESC) WHERE status NOT IN (5 terminados) AND (COALESCE(total_cents < minimum_total_cents_snapshot,false) OR COALESCE(financial_override ?& ARRAY[...],false))` | Filtro `price_review=true` del tablero (`PostgreSqlOperationsDashboardReader`) |

`assignments_driver_idx` se queda: las liquidaciones leen todas las asignaciones del repartidor.

El predicado de `orders_price_review_idx` repite el texto del lector; si uno cambia, el otro también (el planificador
solo usa un índice parcial cuando puede probar su predicado). La prueba de planes falla si eso se rompe.

## Migraciones

- Carril Orders `20261020000100_AddOrderReadIndexes` (dos índices de `orders`) y carril Dispatch
  `20261020000100_AddAssignmentReadIndexes` (dos de `dispatch.assignments`): una migración por módulo dueño de la tabla.
- Cada paso adopta el índice que AI-06 ya creó en una instalación nueva, rechaza uno con el mismo nombre y otra forma
  (nunca lo altera), comprueba `pg_get_indexdef`, validez y dueño (`paqueteria_migrator`), y su bajada quita exactamente
  sus dos índices. Ningún código depende de ellos: el release anterior lee lo mismo, más lento.
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

## Lo que un índice no resuelve (sin cambio aquí)

- Lista de órdenes por defecto («Todos los estados», sin fechas): RLS dueña OR operadora con `ORDER BY created_at DESC,
  id DESC`; un BitmapOr no devuelve ese orden, así que sigue un recorrido y un top-N de las órdenes de la organización
  (104 ms en el banco; índices gemelos por `created_at` no cambiaron el plan). Opciones: reescribir como `UNION ALL` de
  dueña y solo-operadora con esos dos índices, o abrir la pantalla con una ventana de fechas.
- Conteos de la bandeja: `GROUP BY status` de todas las órdenes de la organización, incluidas las terminadas de siempre
  (381 ms en el banco). Opciones: modelo de lectura (GATE-015 lo permite) o acotar «Terminadas» a una ventana.
- Filtro de cobros pendientes de conciliar: su `EXISTS` va dentro de `@cod_pending=false OR …`, así que PostgreSQL no lo
  convierte en semi-join y recorre las órdenes de la organización (233 ms); un índice parcial de `cod_transactions` solo
  abarata la tabla hash. Necesita reescritura.

## Pruebas

- `tests/Paqueteria.ContractTests/PostgreSql/ReadIndexesPostgreSqlContractTests.cs`: AI-06 declara cada índice igual que
  su paso de carril; la base compartida los tiene con la definición exacta; en una base aislada con historia sintética,
  las seis lecturas (texto leído de su fuente, parámetros con sus tipos, como `paqueteria_app`) usan su índice y no
  `assignments_driver_idx`; bajada de ambos pasos como migrador (índices fuera, carriles pendientes, baseline válido),
  subida con el migrador canónico, idempotencia y rechazo de un índice homónimo de otra forma.
- Las pruebas que reescriben el historial de Orders o Dispatch (auto-cierre, ventana de servicio, liquidación del ciclo
  de vida, enlaces de seguimiento, despliegue con puente E-002, OBS-003) incluyen el paso nuevo.
