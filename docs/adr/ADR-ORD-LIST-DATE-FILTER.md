# ADR-ORD-LIST-DATE-FILTER: filtro por fecha de creación y cursor ligado a los filtros en listOrders

- Estado: aceptado (ORD-LIST-DATE-FILTER-2026-10-12)
- Fecha: 2026-10-12
- Alcance: `GET /api/v1/orders` (`listOrders`) y la pantalla "Órdenes" (`/ops/orders`, UI-PHASE3-ORDERS-LIST-2026-10-12)

## Contexto

El project owner aprobó la pantalla "Órdenes" del menú de Despacho (UI-PHASE3-ORDERS-LIST-2026-10-12) con la opción
"Lista de todas las órdenes con búsqueda por guía y filtros por estado y fecha, usando el API que ya existe; abre el
detalle." `listOrders` solo filtraba por estado, dueña, cobro pendiente y guía, así que se le preguntó:

> "Corrección mía: para "Órdenes" dije "filtros por estado y fecha con el API que ya existe", pero la lista de órdenes
> del API no filtra por fecha (solo estado, dueña, cobro pendiente y guía). ¿Cómo la hacemos?"

Respuesta literal: "Agregar filtro de fecha al API (Recommended)", cuya opción decía "Cambio pequeño y aditivo (con
ADR): la lista de órdenes acepta "desde" y "hasta" (hora de Mazatlán). La pantalla filtra por guía, estado y fecha."
No elegida: "Sin filtro de fecha".

El read model del tablero (OBS-001, `GET /operations/dashboard`) ya acepta `created_from` y `created_to`: instantes
RFC3339 en UTC que terminan en `Z` (`TryUtc`), ambos inclusivos, cada uno opcional y con 31 días como máximo entre
ellos. El cursor de `listOrders` era solo la posición (`created_at`, `id`) en Base64URL, sin relación con los filtros:
una posición pedida con otro estado paginaba otra lista sin decirlo, y un cursor ilegible devolvía la página vacía.

## Decisión

1. `listOrders` acepta `created_from` y `created_to`, con las reglas del tablero: texto RFC3339 en UTC que termina en
   `Z`, a lo más una vez cada uno, ambos inclusivos (`created_from <= orders.created_at <= created_to`), cada uno
   usable solo; con los dos, `created_from` no es posterior a `created_to` y entre ellos hay a lo más 31 días
   (`OrderListCreatedRange.MaximumSpan`). Se mantiene el máximo del tablero: el rango no cambia el costo de una página
   (ver índices) pero un límite común evita dos reglas para el mismo filtro; quien necesite más deja una cota abierta.
2. "Hora de Mazatlán" la resuelve la web: "Desde" es el primer instante de ese día en America/Mazatlan y "Hasta" el
   último microsegundo de ese día (`…:59.999999Z`), convertidos con `src/lib/mazatlan-time.ts`; así los dos días
   elegidos quedan incluidos con las cotas inclusivas del servidor, sin depender de la zona del dispositivo. El
   servidor no conoce zonas: recibe instantes.
3. El cursor queda ligado a la organización seleccionada y a todos los filtros de la solicitud que lo recibió
   (`status`, `owner_org_id`, `cod_pending_reconciliation` leído como booleano, `public_id`, `created_from`,
   `created_to`): `OrderCursorCodec` agrega una versión (`o2`) y los primeros 16 bytes del SHA-256 de esos valores en
   JSON canónico; se decodifica solo si la ligadura coincide y el texto es canónico, con 128 caracteres como máximo
   (`maxLength` publicado). La ligadura evita mezclar páginas; no autentica nada: RLS sigue acotando cada lectura.
4. Forma de la solicitud antes de la capacidad: un `created_from` o `created_to` mal formado o repetido, un rango fuera
   de las reglas y un cursor que esta operación no emitió para esta organización y estos filtros (otro filtro, otra
   organización, el cursor sin ligadura de la versión anterior o cualquier otro texto) son `409` con `code`
   `INVALID_REQUEST`, decididos antes de la capacidad y sin abrir la transacción ni leer órdenes (AI-05
   `x-authorization-precedence: shape-validation-then-capability-before-persisted-state`), como `listIncidents`,
   `listSettlements` y `listOrganizationDrivers`. El servicio repite ambas comprobaciones antes de leer.
5. Lo demás no cambia: un estado desconocido, un `public_id` mal formado, otra `owner_org_id` y un
   `cod_pending_reconciliation` distinto de `true`/`false` siguen devolviendo la página vacía después de la capacidad;
   la capacidad (DISPATCHER, PLATFORM_ADMIN y VIEWER; FINANCE con MFA solo con `cod_pending_reconciliation=true`), la
   representación `Order`, RLS (órdenes que la organización es dueña u opera) y el orden `created_at DESC, id DESC`
   quedan igual. El rango estrecha cualquier forma admitida y no agrega capacidad: FINANCE puede acotar con fechas su
   lista de cobros pendientes y nada más.

## Índices: sin migración

La lectura corre como hoy en la transacción tenant explícita (`set_config` después de `BEGIN`, `SET LOCAL ROLE
paqueteria_app`, NOBYPASSRLS) y agrega `created_at >= @created_from` y `created_at <= @created_to`. Los índices
existentes `orders_owner_status_idx (owner_org_id, status, created_at DESC)` y `orders_operator_status_idx
(operator_org_id, status, created_at DESC)` ya sirven al predicado de RLS (`owner_org_id = ANY(app.current_org_ids)`
o `operator_org_id = ANY(...)`) y aceptan el rango de `created_at` como condición de índice, también sin estado
(PostgreSQL 18 recorre los valores de `status` con skip scan). Se comprobó con `EXPLAIN (ANALYZE, BUFFERS)` en
PostgreSQL 18.4/PostGIS 3.6 (la imagen fijada de los contract tests) con AI-06 y AI-18 aplicados, 200 000 órdenes de
20 organizaciones en 365 días y la consulta tal como la ejecuta el servicio, como `paqueteria_app`:

| Caso | Plan | Filas leídas | Tiempo |
| --- | --- | --- | --- |
| Sin filtros (hoy) | BitmapOr de los dos índices por organización + top-N sort | 10 000 | 31 ms |
| 7 días, sin estado | BitmapOr con `created_at` como condición de índice (5 búsquedas, skip scan) + top-N sort | 192 | 0.7 ms |
| 31 días con estado | BitmapOr con `status` y `created_at` como condiciones de índice | 0 | 0.1 ms |
| Solo `created_from` (9 meses) | BitmapOr con `created_at` como condición de índice + top-N sort | 7 773 | 17 ms |
| 7 días, segunda página | igual que el caso de 7 días; el cursor filtra en el heap | 192 | 0.4 ms |

El orden `created_at DESC, id DESC` lo sigue dando un top-N sort sobre las filas de la organización, igual que antes
de este cambio; el rango solo reduce esas filas. Un índice nuevo (por ejemplo `(owner_org_id, created_at DESC, id
DESC)`) no se justifica para el piloto y queda como mejora si el volumen por organización crece.

## Consecuencias

- Solo hay un efecto que no es aditivo: un cursor que esta operación no emitió para los mismos filtros, incluidos los
  cursores sin ligadura emitidos antes de este cambio y un texto cualquiera, ya no devuelve la página vacía sino `409
  INVALID_REQUEST`. Ningún cliente legítimo cambia filtros con un cursor; la web solo reenvía `next_cursor` con la misma
  consulta. Una pestaña abierta durante el despliegue que pida "Cargar más" en "Cobro contra entrega" con un cursor
  anterior ve un error una vez y lo resuelve con "Actualizar".
- AI-05: parámetros `created_from` y `created_to`, `maxLength: 128` y descripción del cursor, respuesta `409`
  (`Conflict`) y la entrada `ORD-LIST-DATE-FILTER` de `x-pilot-contract-deltas`; los conteos de rutas y esquemas no
  cambian y las referencias internas pasan de 645 a 646.
- Sin tabla, columna, índice, rol, permiso, función ni migración; AI-04, AI-06 y AI-18 sin cambios; ningún flujo
  atómico nuevo de AI-13 §4.
- Despliegue: el API antes que la web, o los dos juntos. La web anterior funciona con el API nuevo (nunca manda fechas
  y reenvía cada cursor con su misma consulta). La web nueva con el API anterior mostraría la lista sin filtro de fecha,
  porque el API anterior ignora parámetros que no conoce, y terminaría la paginación al no decodificar el cursor nuevo.
- Rollback: revertir el commit, la web junto con el API o antes que él, por la misma razón; no hay datos ni esquema que
  deshacer.
