# UI-001 fase 3: "Órdenes" y filtro por fecha de creación en listOrders

Decisiones: `UI-PHASE3-ORDERS-LIST-2026-10-12` (pantalla) y `ORD-LIST-DATE-FILTER-2026-10-12` (API), ambas MAJOR de
AI-01 §7. La primera tiene su registro ADR en `decision-log.md`; la segunda, además, en
`docs/adr/ADR-ORD-LIST-DATE-FILTER.md`. Literales del owner (2026-10-12):

- "El menú aprobado de Despacho incluye "Órdenes", pero esa pantalla no existe (hoy se llega a una orden desde la
  bandeja, el tablero o "Buscar guía"). ¿La hacemos?" → "Sí, lista de órdenes (Recommended)" ("Lista de todas las
  órdenes con búsqueda por guía y filtros por estado y fecha, usando el API que ya existe; abre el detalle."); no
  elegidas: "No, quitar del menú" y "Después del piloto".
- "Corrección mía: para "Órdenes" dije "filtros por estado y fecha con el API que ya existe", pero la lista de órdenes
  del API no filtra por fecha (solo estado, dueña, cobro pendiente y guía). ¿Cómo la hacemos?" → "Agregar filtro de
  fecha al API (Recommended)" ("Cambio pequeño y aditivo (con ADR): la lista de órdenes acepta "desde" y "hasta" (hora
  de Mazatlán). La pantalla filtra por guía, estado y fecha."); no elegida: "Sin filtro de fecha".
- Durante la revisión: "Pantalla Órdenes: el API de la lista no trae la fecha de creación de cada orden, así que la
  columna "Creada" no se puede mostrar (los filtros por fecha sí funcionan). Agregarla cambia el API y la web actual
  rechaza campos que no conoce. ¿Qué hacemos?" → "En dos pasos (Recommended)" ("Este PR deja la web lista para aceptar
  la fecha; en un PR siguiente el API la agrega y se muestra la columna. Así ningún orden de despliegue rompe nada.");
  no elegida: "Sin columna de fecha" ("La lista queda con guía, estado y total, de la más reciente a la más antigua; la
  fecha se ve al abrir la orden.").
- Durante la revisión: "El menú aprobado de Finanzas incluye "Órdenes (lectura)", pero hoy Finanzas solo puede listar
  órdenes con cobro pendiente de conciliar. ¿Ampliamos su acceso?" → "No por ahora (Recommended)" ("Finanzas sigue
  viendo solo los cobros pendientes en su pantalla; no aparece "Órdenes" en su menú. Ampliar lectura es un cambio de
  permisos (ADR)."); no elegida: "Sí, solo lectura". Solo se registra; no cambia código.

Contratos: AI-05 `listOrders` (parámetros `created_from`, `created_to`, cursor ligado, respuesta 409 y la entrada
`ORD-LIST-DATE-FILTER` de `x-pilot-contract-deltas`); AI-07 ruta `/ops/orders` y `screen_contracts.order_list`.

## API: `GET /api/v1/orders` (cambio aditivo)

Toda solicitud válida antes recibe la misma respuesta. Las reglas nuevas solo alcanzan a los parámetros nuevos y a los
cursores emitidos desde este cambio.

| Parámetro | Antes | Ahora |
| --- | --- | --- |
| `status`, `owner_org_id`, `cod_pending_reconciliation`, `public_id` | igual | igual; un valor fuera de contrato sigue dando la página vacía después de la capacidad |
| `created_from`, `created_to` | — | instantes RFC3339 estrictos en UTC (`yyyy-MM-ddTHH:mm:ss`, 0 a 7 decimales, `Z`), inclusivos, cada uno solo o ambos con 31 días como máximo; otro texto, repetido o fuera de reglas = `409 INVALID_REQUEST` antes de la capacidad |
| `cursor` | posición sin ligadura; ilegible = página vacía | igual para todo cursor anterior y todo texto ilegible, vacío o largo; cada `next_cursor` nuevo (`o2`) va ligado a la organización y a los filtros, y usado con otros = `409 INVALID_REQUEST` después de la capacidad |

- Formato de `created_from`/`created_to`: `OrderListCreatedRange.TryParseInstant`, un `TryParseExact` con cultura
  invariante sobre `yyyy-MM-ddTHH:mm:ss` con 0 a 7 decimales y `Z`, sin espacios ni nada alrededor. Los límites (31
  días, inclusivos) son los del tablero, repetidos en `OrderListCreatedRange` porque los módulos no se referencian entre
  sí. Se conserva el máximo de 31 días: el rango no cambia el costo de una página (ver índices), pero un mismo filtro
  con dos reglas distintas confundiría; una cota sola sigue permitida.
- Orden de las decisiones en el endpoint: sesión y organización (403); forma de las fechas (409 `INVALID_REQUEST`:
  cota mal formada o repetida, rango fuera de reglas), que solo mandan los clientes que conocen los parámetros nuevos;
  capacidad (403 o `MFA_REQUIRED`, incluida la excepción de FINANCE); y el servicio, que repite el rango y lee el cursor
  donde siempre (después de la capacidad, antes de leer órdenes). Un rol sin capacidad recibe el mismo 403 de antes con
  cualquier cursor. Ninguna comprobación de forma lee estado persistido.
- Cursor (`OrderCursorCodec`): un `next_cursor` nuevo es `o2:{ticks}:{id}:{ligadura}` en Base64URL (128 caracteres como
  máximo), donde la ligadura son los primeros 16 bytes del SHA-256 del JSON canónico de la organización y los filtros
  (`status` tal como llegó, `owner_org_id`, `cod_pending_reconciliation` como booleano —ausente y `false` son el mismo
  filtro—, `public_id`, `created_from` y `created_to` en ticks UTC). `OrderCursorCodec.Read` devuelve `Position`,
  `BoundToOtherFilters` (solo un `o2` canónico cuya ligadura no coincide: 409) o `Unreadable` (página vacía); todo lo
  que no es un `o2` canónico se decodifica con la lógica anterior, así que un cursor del formato anterior sigue
  paginando. AI-05 no publica `maxLength` en `cursor`. La ligadura no es secreta ni autentica: evita paginar otra lista;
  RLS sigue decidiendo qué órdenes se leen.
- Capacidad sin cambios: DISPATCHER, PLATFORM_ADMIN y VIEWER; FINANCE con MFA solo con
  `cod_pending_reconciliation=true`, que puede acotar con fechas (estrecha, no amplía). DRIVER y los demás, 403.
- RLS sin cambios: la organización ve las órdenes de las que es dueña u operadora. Defecto previo a este cambio, ya
  corregido (`docs/development/ord-list-owner-filter.md`): `owner_org_id` igual a la organización activa no excluía
  las órdenes que solo opera (el SQL no filtraba por dueña); ahora trae solo las de las que es dueña. Otra dueña sigue
  dando la página vacía.
- `created_at` en la `Order`: no cambia aquí. El PR siguiente lo agrega al API ("En dos pasos").

### Índices

Sin migración. La consulta conserva su forma y agrega `created_at >= @created_from` y `created_at <= @created_to`.
`orders_owner_status_idx` y `orders_operator_status_idx` (`(org, status, created_at DESC)`) ya atienden el predicado
de RLS por organización y toman el rango como condición de índice (PostgreSQL 18 hace skip scan sobre `status` cuando
no hay filtro de estado). `EXPLAIN (ANALYZE, BUFFERS)` en PostgreSQL 18.4 con AI-06 y AI-18 aplicados y 200 000
órdenes sintéticas (20 organizaciones, 365 días), como `paqueteria_app` con RLS: sin filtros 10 000 filas y 31 ms (igual
que antes); 7 días 192 filas y 0.7 ms; 31 días con estado 0.1 ms; solo `created_from` (9 meses) 7 773 filas y 17 ms;
segunda página de 7 días 0.4 ms. El orden lo sigue dando un top-N sort sobre las filas de la organización, como antes.
Detalle en el ADR.

## Web: "Órdenes" (`/ops/orders`)

- Menú: "Órdenes" justo después de "Bandeja" y antes de "Tablero" (menú aprobado "Bandeja · Órdenes · + Nueva orden ·
  …"), para `canPerform(role, "listOrders")`: DISPATCHER, PLATFORM_ADMIN y VIEWER (`canOpenOrderList`). FINANCE no la
  ve ("No por ahora").
- Aterrizaje: VIEWER, que lista órdenes y no tiene Bandeja, entra a "Órdenes" después del login y al cambiar a una
  organización donde su página no aplica (`landingPathForRole`); antes entraba a `/ops/dashboard`, que no puede leer.
  Los demás roles conservan su aterrizaje.
- Resaltado: "Órdenes" es dueña de `/ops/orders`. El detalle `/ops/orders/{id}` sigue bajo "Bandeja" para quien la
  tiene (el prefijo `/ops/orders/` es más largo que `/ops/orders`, así que gana) y cae en "Órdenes" para VIEWER, que no
  tiene Bandeja; `/ops/orders/new` sigue siendo "Nueva orden" e `/ops/orders/import` "Importar CSV".
- Búsqueda "Guía (ORD_…)": solo el número exacto, con `normalizePublicIdInput` (la validación de la barra superior);
  un valor que no puede ser guía muestra "No encontramos esa guía" sin llamar al API. La guía se busca junto con los
  demás filtros (el API la combina) y no va a la URL.
- Filtros: "Estado" (uno o "Todos los estados", agrupados en los cinco grupos con las etiquetas exactas), "Creada desde"
  y "Creada hasta (hora de Mazatlán)" (`<input type="date" min="2000-01-01">`). Estado y fechas viven en la URL
  (`status`, `from`, `to`): el controlador toma el cambio al instante y la URL se reemplaza en su lugar
  (`history.replaceState`, sin entrada nueva), así que "atrás" desde el detalle vuelve a la misma lista.
- Fecha escrita con el teclado (`DateFilterInput`, `readDateFilterEntry`): lo escrito queda como borrador en el campo y
  el filtro cambia solo cuando el campo tiene un día completo desde 2000-01-01 o se vacía. Chromium reporta un año a
  medio escribir como `0002-…`, `0020-…`, `0202-…` y una fecha con partes faltantes como vacía con `badInput`; ninguno
  de los dos se aplica, así que el filtro ya no se borra al escribir el año. Al salir del campo, una fecha incompleta
  dice "Escribe una fecha completa (día, mes y año, desde 2000)." (`aria-invalid`); el mismo mensaje sale si una fecha
  no se puede convertir a hora de Mazatlán, y no se envía nada.
- Fechas: "Desde" es el primer instante de ese día en Mazatlán y "Hasta" el último microsegundo de ese día
  (`2026-10-01`–`2026-10-07` → `created_from=2026-10-01T07:00:00.000Z`, `created_to=2026-10-08T06:59:59.999999Z`), con
  `src/lib/mazatlan-time.ts`, sin importar la zona del dispositivo. "Desde" posterior a "Hasta" o más de 31 días se
  rechaza en pantalla y no se envía nada.
- Tabla: Guía, Estado (grupo, punto y texto), "Creada" y "Total (IVA incluido)" (`Order.total`, centavos enteros con
  `Money`), de la más reciente a la más antigua. "Creada" (fecha y hora de Mazatlán) aparece solo cuando el API manda
  `created_at` en alguna orden de la página; una orden sin él dice "Sin dato". Para DISPATCHER y PLATFORM_ADMIN la fila
  abre el detalle (clic en la fila; el enlace de la guía es el camino por teclado); para VIEWER la fila no es enlace,
  porque el detalle lee el tablero, que VIEWER no puede leer, y la pantalla lo dice.
- "Cargar más" manda el `next_cursor` con exactamente la consulta que lo emitió; un cursor repetido o una página
  rechazada detiene la paginación con aviso.
- Foco: nunca cae al cuerpo de la página. "Buscar guía" lo conserva (el formulario ya no se vuelve a montar con
  `key`; el campo se vacía por estado cuando la búsqueda termina en otro lado); "Quitar búsqueda" lo regresa al campo
  de guía; "Cargar más" usa `aria-disabled` mientras carga y, cuando la última página quita el botón, el foco pasa al
  resumen ("Se muestran N órdenes.", `tabIndex=-1`).
- Estados: cargando, vacía ("Sin órdenes", "Sin órdenes con estos filtros", "No encontramos esa guía"), error que conserva
  las filas (409 `INVALID_REQUEST`: "El servidor no aceptó estos filtros…"), periodo inválido, sin sesión, acceso no
  disponible (401/403 vacían la lista) y "Verificar identidad" si solo faltara el MFA.
- Se vuelve a leer al cargar, con "Actualizar" y con cada cambio de filtro (las filas anteriores se quitan al instante).
  Al volver a ser visible o recuperar el foco se relee solo mientras hay una sola página (nunca oculta ni dos veces en
  5 s), para no tirar lo que la persona cargó con "Cargar más".
- Todo vive en memoria; nada en `localStorage`, `sessionStorage`, IndexedDB ni consola; ningún ID interno a la vista.

## Web: `created_at` opcional en la `Order` ("En dos pasos")

Los parsers estrictos de la web rechazan campos desconocidos, así que un API que agregara `Order.created_at` antes de
esta web rompería varias pantallas. Desde este cambio, todo lector web de la `Order` de AI-05 acepta un `created_at`
opcional con una sola regla (`src/lib/utc-instant.ts`): ausente se ignora (`null`); presente debe ser un instante UTC
estricto (`YYYY-MM-DDTHH:mm:ss`, 0 a 7 decimales, `Z` o `+00:00` —lo que escribe System.Text.Json para un
`DateTimeOffset` en UTC—, con fecha y hora reales) o la respuesta se rechaza como cualquier otro desvío. Lectores:

- `operations/contracts/create-order.ts` (`parseCreatedOrder`): "Nueva orden", "Cobro contra entrega" (lista de
  pendientes) y "Órdenes".
- `operations/api/order-actions-api.ts` (`parseOrderSearchPage`, `parseTransitionedOrder`): "Buscar guía", "Agregar
  entrega" de Rutas y la respuesta de cada transición.
- `operations/contracts/operations-parsers.ts` (`parseOperationsOrderDetail`): el detalle de la orden.
- `driver/offline/driver-sync-api.ts` (`parseTransitionReceipt`): las transiciones fuera de línea del repartidor, para
  que un despliegue escalonado no detenga la cola.

El API y el servidor no cambian aquí; el PR siguiente agrega `created_at` a AI-05 y al API, y entonces aparece la
columna "Creada".

## Validación

```bash
dotnet build Paqueteria.sln
dotnet test tests/Paqueteria.UnitTests --no-build
dotnet test tests/Paqueteria.ContractTests --no-build --filter "Category!=PostgreSqlContract"
dotnet test tests/Paqueteria.ContractTests --no-build --filter "FullyQualifiedName~OrderListDateFilterPostgreSqlContractTests|FullyQualifiedName~OrderSearchAndAllowedTransitionsPostgreSqlContractTests|FullyQualifiedName~OrdersPostgreSqlContractTests|FullyQualifiedName~FinancePostgreSqlContractTests"
dotnet test tests/Paqueteria.IntegrationTests --no-build --filter "FullyQualifiedName~Orders.OrderHttpTests|FullyQualifiedName~OrderCapabilityMatrixHttpTests|FullyQualifiedName~HttpSurfaceOpenApiCoverageTests"
dotnet test tests/Paqueteria.IntegrationTests --filter "FullyQualifiedName~OrdersListPwaPlaywrightTests|FullyQualifiedName~RoutesPwaPlaywrightTests"
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm exec vitest run && pnpm run build
python3 docs/normative/v0.6/tools/validate_contracts.py
```

## Seguimiento

- El lector de `created_from`/`created_to` del tablero (`OperationsDashboardEndpoints.TryUtc`) es más permisivo que el
  de `listOrders`: exige solo que el texto termine en `Z` y usa `DateTimeOffset.TryParse` con `AllowWhiteSpaces`, así
  que acepta, por ejemplo, espacios alrededor y otros formatos que `TryParse` reconoce. No se cambió aquí (cambiarlo altera respuestas del
  tablero); alinearlo con `OrderListCreatedRange.TryParseInstant` es un cambio aparte.
- `created_at` en la `Order` de AI-05 y del API, y la columna "Creada" con datos: PR siguiente ("En dos pasos").
  Hecho en `ORD-ORDER-CREATED-AT-2026-10-12` (`docs/development/ord-order-created-at.md`).
- Un detalle de orden para VIEWER (solo `getOrder`): hoy el detalle lee el tablero y VIEWER no lo abre.

## Rollback

Revertir el commit, la web junto con el API o antes que él (una web nueva con un API anterior mostraría la lista sin
filtro de fecha). No hay datos, esquema ni migración que deshacer.
