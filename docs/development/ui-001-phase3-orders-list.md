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

Contratos: AI-05 `listOrders` (parámetros `created_from`, `created_to`, cursor ligado, respuesta 409 y la entrada
`ORD-LIST-DATE-FILTER` de `x-pilot-contract-deltas`); AI-07 ruta `/ops/orders` y `screen_contracts.order_list`.

## API: `GET /api/v1/orders`

| Parámetro | Antes | Ahora |
| --- | --- | --- |
| `status`, `owner_org_id`, `cod_pending_reconciliation`, `public_id` | igual | igual; un valor fuera de contrato sigue dando la página vacía después de la capacidad |
| `created_from`, `created_to` | — | instantes RFC3339 en UTC con `Z`, inclusivos, cada uno solo o ambos con 31 días como máximo |
| `cursor` | posición sin ligadura; ilegible = página vacía | ligado a la organización y a los filtros; `maxLength` 128; otro = `409 INVALID_REQUEST` |

- Reglas de `created_from`/`created_to`: las del tablero (`OperationsDashboardEndpoints.TryUtc` y el máximo de 31 días),
  repetidas en `OrderListCreatedRange` porque los módulos no se referencian entre sí. Cotas inclusivas, como el tablero:
  `created_from <= created_at <= created_to`. Se conserva el máximo de 31 días: el rango no cambia el costo de una
  página (ver índices), pero un mismo filtro con dos reglas distintas confundiría; una cota sola sigue permitida.
- Orden de las decisiones en el endpoint: sesión y organización (403), forma (409 `INVALID_REQUEST`: cota mal formada o
  repetida, rango fuera de reglas, cursor no emitido para esta organización y estos filtros), capacidad (403 o
  `MFA_REQUIRED`, incluida la excepción de FINANCE), y el servicio, que repite rango y cursor antes de abrir la
  transacción. Ninguna de las comprobaciones de forma lee estado persistido.
- Cursor (`OrderCursorCodec`, versión `o2`): `o2:{ticks}:{id}:{ligadura}` en Base64URL, donde la ligadura son los
  primeros 16 bytes del SHA-256 del JSON canónico de la organización y los filtros (`status` tal como llegó,
  `owner_org_id`, `cod_pending_reconciliation` como booleano —ausente y `false` son el mismo filtro—, `public_id`,
  `created_from` y `created_to` en ticks UTC). Solo se acepta la forma canónica. La ligadura no es secreta ni autentica:
  evita paginar otra lista; RLS sigue decidiendo qué órdenes se leen.
- Capacidad sin cambios: DISPATCHER, PLATFORM_ADMIN y VIEWER; FINANCE con MFA solo con
  `cod_pending_reconciliation=true`, que puede acotar con fechas (estrecha, no amplía). DRIVER y los demás, 403.
- RLS sin cambios: la organización ve las órdenes de las que es dueña u operadora. Nota previa a este cambio, sin
  tocar: `owner_org_id` igual a la organización activa no excluye las órdenes que solo opera (el SQL no filtra por
  dueña; otra dueña da la página vacía).

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
  ve (ver pendientes).
- Resaltado: "Órdenes" es dueña de `/ops/orders`. El detalle `/ops/orders/{id}` sigue bajo "Bandeja" para quien la
  tiene (el prefijo `/ops/orders/` es más largo que `/ops/orders`, así que gana) y cae en "Órdenes" para VIEWER, que no
  tiene Bandeja; `/ops/orders/new` sigue siendo "Nueva orden" e `/ops/orders/import` "Importar CSV".
- Búsqueda "Guía (ORD_…)": solo el número exacto, con `normalizePublicIdInput` (la validación de la barra superior);
  un valor que no puede ser guía muestra "No encontramos esa guía" sin llamar al API. La guía se busca junto con los
  demás filtros (el API la combina) y no va a la URL.
- Filtros: "Estado" (uno o "Todos los estados", agrupados en los cinco grupos con las etiquetas exactas), "Creada desde"
  y "Creada hasta (hora de Mazatlán)" (`<input type="date">`). Estado y fechas viven en la URL (`status`, `from`, `to`):
  el controlador toma el cambio al instante y la URL se reemplaza en su lugar (`history.replaceState`, sin entrada
  nueva), así que "atrás" desde el detalle vuelve a la misma lista.
- Fechas: "Desde" es el primer instante de ese día en Mazatlán y "Hasta" el último microsegundo de ese día
  (`2026-10-01`–`2026-10-07` → `created_from=2026-10-01T07:00:00.000Z`, `created_to=2026-10-08T06:59:59.999999Z`), con
  `src/lib/mazatlan-time.ts`, sin importar la zona del dispositivo. "Desde" posterior a "Hasta" o más de 31 días se
  rechaza en pantalla y no se envía nada.
- Tabla: Guía, Estado (grupo, punto y texto) y "Total (IVA incluido)" (`Order.total`, centavos enteros con `Money`), de
  la más reciente a la más antigua. La `Order` de `listOrders` no trae fecha de creación (ver pendientes). Para
  DISPATCHER y PLATFORM_ADMIN la fila abre el detalle (clic en la fila; el enlace de la guía es el camino por teclado);
  para VIEWER la fila no es enlace, porque el detalle lee el tablero, que VIEWER no puede leer, y la pantalla lo dice.
- "Cargar más" manda el `next_cursor` con exactamente la consulta que lo emitió; un cursor repetido o una página
  rechazada detiene la paginación con aviso.
- Estados: cargando, vacía ("Sin órdenes", "Sin órdenes con estos filtros", "No encontramos esa guía"), error que conserva
  las filas (409 `INVALID_REQUEST`: "El servidor no aceptó estos filtros…"), periodo inválido, sin sesión, acceso no
  disponible (401/403 vacían la lista) y "Verificar identidad" si solo faltara el MFA.
- Se vuelve a leer al cargar, con "Actualizar" y con cada cambio de filtro (las filas anteriores se quitan al instante).
  Al volver a ser visible o recuperar el foco se relee solo mientras hay una sola página (nunca oculta ni dos veces en
  5 s), para no tirar lo que la persona cargó con "Cargar más".
- Todo vive en memoria; nada en `localStorage`, `sessionStorage`, IndexedDB ni consola; ningún ID interno a la vista.

## Validación

```bash
dotnet build Paqueteria.sln
dotnet test tests/Paqueteria.UnitTests --no-build
dotnet test tests/Paqueteria.ContractTests --no-build --filter "Category!=PostgreSqlContract"
dotnet test tests/Paqueteria.ContractTests --no-build --filter "FullyQualifiedName~OrderListDateFilterPostgreSqlContractTests|FullyQualifiedName~OrderSearchAndAllowedTransitionsPostgreSqlContractTests|FullyQualifiedName~OrdersPostgreSqlContractTests|FullyQualifiedName~FinancePostgreSqlContractTests"
dotnet test tests/Paqueteria.IntegrationTests --no-build --filter "FullyQualifiedName~Orders.OrderHttpTests|FullyQualifiedName~OrderCapabilityMatrixHttpTests|FullyQualifiedName~HttpSurfaceOpenApiCoverageTests"
dotnet test tests/Paqueteria.IntegrationTests --filter "FullyQualifiedName~OrdersListPwaPlaywrightTests"
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm exec vitest run && pnpm run build
python3 docs/normative/v0.6/tools/validate_contracts.py
```

## Pendientes (preguntas al owner)

- Fecha y hora de creación por fila: la representación `Order` de AI-05 no tiene `created_at`. Mostrarla exige
  agregarla al API; como los parsers de la web rechazan campos desconocidos, un API publicado antes que la web rompería
  "Buscar guía", Rutas, Cobro contra entrega y Nueva orden. No se construyó.
- "Órdenes (lectura)" del menú aprobado de FINANCE: `listOrders` solo admite a FINANCE para la lista de cobros
  pendientes (FIN-PENDING-COD-LIST-FINANCE-2026-10-02); no se cambió su acceso.
- VIEWER aterriza hoy en `/ops/dashboard`, que no puede leer; el menú aprobado lo hace aterrizar en "Órdenes". No se
  cambió.
- Un detalle de orden para VIEWER (solo `getOrder`): hoy el detalle lee el tablero y VIEWER no lo abre.

## Rollback

Revertir el commit, la web junto con el API o antes que él (una web nueva con un API anterior mostraría la lista sin
filtro de fecha). No hay datos, esquema ni migración que deshacer.
