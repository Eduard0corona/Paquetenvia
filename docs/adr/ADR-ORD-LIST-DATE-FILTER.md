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

El owner pidió un cambio aditivo, así que todo lo que `listOrders` respondía antes se sigue respondiendo igual: las
reglas nuevas solo alcanzan a los parámetros nuevos y a los cursores que se emiten a partir de este cambio.

## Decisión

1. `listOrders` acepta `created_from` y `created_to`, con las cotas del tablero y un formato estricto: exactamente
   `yyyy-MM-ddTHH:mm:ss`, de 0 a 7 decimales y `Z`, sin nada alrededor (`OrderListCreatedRange.TryParseInstant`, un
   `TryParseExact` con cultura invariante), a lo más una vez cada uno, ambos inclusivos
   (`created_from <= orders.created_at <= created_to`), cada uno usable solo; con los dos, `created_from` no es
   posterior a `created_to` y entre ellos hay a lo más 31 días (`OrderListCreatedRange.MaximumSpan`). Se mantiene el
   máximo del tablero: el rango no cambia el costo de una página (ver índices) pero un límite común evita dos reglas
   para el mismo filtro; quien necesite más deja una cota abierta. El lector del tablero (`TryUtc`) no cambia aquí.
2. "Hora de Mazatlán" la resuelve la web: "Desde" es el primer instante de ese día en America/Mazatlan y "Hasta" el
   último microsegundo de ese día (`…:59.999999Z`), convertidos con `src/lib/mazatlan-time.ts`; así los dos días
   elegidos quedan incluidos con las cotas inclusivas del servidor, sin depender de la zona del dispositivo. El
   servidor no conoce zonas: recibe instantes.
3. Cada `next_cursor` emitido desde este cambio queda ligado a la organización seleccionada y a todos los filtros de
   la solicitud que lo recibió (`status`, `owner_org_id`, `cod_pending_reconciliation` leído como booleano,
   `public_id`, `created_from`, `created_to`): `OrderCursorCodec` agrega una versión (`o2`) y los primeros 16 bytes
   del SHA-256 de esos valores en JSON canónico, con 128 caracteres como máximo. `OrderCursorCodec.Read` reconoce un
   cursor `o2` canónico; si su ligadura no coincide con la solicitud la respuesta es `409` con `code`
   `INVALID_REQUEST`. Cualquier otro texto se lee exactamente como antes: un cursor del formato anterior (posición sin
   ligadura) sigue paginando, y un texto ilegible, vacío o demasiado largo sigue dando la página vacía. AI-05 no
   publica `maxLength` en `cursor`, para no cambiar la respuesta a ningún texto. La ligadura evita mezclar páginas; no
   autentica nada: RLS sigue acotando cada lectura.
4. Orden de las comprobaciones. Un `created_from` o `created_to` mal formado o repetido y un rango fuera de las reglas
   son forma de la solicitud: `409 INVALID_REQUEST` antes de la capacidad y sin abrir la transacción ni leer órdenes
   (AI-05 `x-authorization-precedence: shape-validation-then-capability-before-persisted-state`), como
   `listIncidents`, `listSettlements` y `listOrganizationDrivers`; solo los clientes que conocen los parámetros nuevos
   los mandan. El cursor se sigue leyendo donde siempre, en el servicio, después de la capacidad y antes de leer
   órdenes: un rol sin capacidad recibe el mismo `403` de antes con cualquier cursor. El servicio repite la
   comprobación del rango.
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

- El cambio es aditivo: toda solicitud válida antes recibe la misma respuesta. El único `409` nuevo sin parámetros
  nuevos es el de un cursor `o2`, que solo existe desde este cambio, enviado con filtros u organización distintos de
  los que lo emitieron; ningún cliente legítimo lo hace (la web reenvía `next_cursor` con la misma consulta). Una
  pestaña abierta durante el despliegue con un cursor anterior sigue paginando.
- AI-05: parámetros `created_from` y `created_to`, descripción del cursor (sin `maxLength`), respuesta `409`
  (`Conflict`) y la entrada `ORD-LIST-DATE-FILTER` de `x-pilot-contract-deltas`; los conteos de rutas y esquemas no
  cambian y las referencias internas pasan de 645 a 646.
- Sin tabla, columna, índice, rol, permiso, función ni migración; AI-04, AI-06 y AI-18 sin cambios; ningún flujo
  atómico nuevo de AI-13 §4.
- Despliegue: el API antes que la web, o los dos juntos. La web anterior funciona con el API nuevo (nunca manda fechas
  y reenvía cada cursor con su misma consulta). La web nueva con el API anterior mostraría la lista sin filtro de fecha,
  porque el API anterior ignora parámetros que no conoce.
- Rollback: revertir el commit, la web junto con el API o antes que él, por la misma razón; un cursor `o2` que una
  pestaña tenga al revertir el API da la página vacía en el API anterior (su lectura de siempre) y "Actualizar" lo
  resuelve. No hay datos ni esquema que deshacer.
- Seguimiento: el lector `TryUtc` del tablero acepta formatos más amplios que el de `listOrders`; alinearlo es un
  cambio aparte (ver `docs/development/ui-001-phase3-orders-list.md`).
