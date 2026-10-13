# `created_at` en la `Order` de AI-05 ("En dos pasos", paso 2)

Decisión: `ORD-ORDER-CREATED-AT-2026-10-12` (MAJOR de AI-01 §7; su fila en `decision-log.md` es el registro ADR).
Es el segundo paso de la respuesta literal del owner registrada en `UI-PHASE3-ORDERS-LIST-2026-10-12`:

- "Pantalla Órdenes: el API de la lista no trae la fecha de creación de cada orden, así que la columna "Creada" no se
  puede mostrar (los filtros por fecha sí funcionan). Agregarla cambia el API y la web actual rechaza campos que no
  conoce. ¿Qué hacemos?" → "En dos pasos (Recommended)" ("Este PR deja la web lista para aceptar la fecha; en un PR
  siguiente el API la agrega y se muestra la columna. Así ningún orden de despliegue rompe nada."); no elegida: "Sin
  columna de fecha" ("La lista queda con guía, estado y total, de la más reciente a la más antigua; la fecha se ve al
  abrir la orden.").

El paso 1 salió en #234 (`docs/development/ui-001-phase3-orders-list.md`): todo lector web de la `Order` acepta un
`created_at` opcional y estricto en UTC, y "Creada" aparece solo cuando el API lo manda.

## Contrato (AI-05)

- `Order.created_at`: `string`, `format: date-time`, instante UTC, en `required`. Es obligatorio porque el servidor
  siempre lo tiene: `orders.orders.created_at` es NOT NULL (`DEFAULT now()`; `createOrder` lo escribe) y ningún
  `UPDATE` del runtime lo toca.
- Lo mandan todas las operaciones que devuelven el esquema `Order`: `createOrder` (201), `listOrders` (cada elemento),
  `getOrder` (`OrderDetail`, cuyo `allOf` incluye `Order`) y `transitionOrder` (200), también al repetir una
  solicitud idempotente de `createOrder` o `transitionOrder`. Ninguna otra operación devuelve `Order`.
- `x-pilot-contract-deltas` agrega `ORD-ORDER-CREATED-AT`. Rutas, esquemas y referencias internas siguen en 69, 125
  y 651, así que `OpenApiContractTests` y `HttpSurfaceOpenApiCoverageTests` no cambian; cambia el hash de AI-05 en
  `OpenApiBaselineTests`.

## Servidor

- `OrderResult.CreatedAt` (Orders.Application) lleva el valor de la fila y `OrderResponse`/`OrderDetailResponse`
  agregan `created_at`.
- `listOrders` y `getOrder` ya seleccionaban `created_at` (la lista ordena y pagina por él); ahora también lo devuelven.
- `transitionOrder` lo selecciona junto con la fila bloqueada (`FOR UPDATE`) y lo devuelve sin cambiarlo.
- `createOrder` devuelve el instante de creación truncado a microsegundos (`UtcMicrosecondPrecision.Normalize`), que
  es lo que guarda `timestamptz` (Npgsql descarta los ticks sobrantes), así que coincide con toda lectura posterior.
- Repetición idempotente: la respuesta guardada en `platform.idempotency_keys` es la `OrderResult` serializada. Una
  guardada antes de este cambio no trae `created_at` (se lee como `default`); al repetirla, el servicio lee
  `created_at` de la fila de esa orden en la misma transacción tenant (`createOrder`: por id bajo RLS;
  `transitionOrder`: por id y `owner_org_id`). Si la fila no se puede leer, falla cerrado como cualquier otra
  inconsistencia de la respuesta guardada (`createOrder`: 503; `transitionOrder`: el 409 uniforme). Nunca se responde
  una fecha por omisión.
- Formato: la respuesta convierte el valor a UTC, así que System.Text.Json escribe `+00:00` con 0 a 7 decimales
  (microsegundos desde PostgreSQL), por ejemplo `2026-10-12T17:04:05.123456+00:00`. La regla única de la web
  (`apps/web/src/lib/utc-instant.ts`: `YYYY-MM-DDTHH:mm:ss`, 0 a 7 decimales, `Z` o `+00:00`) lo acepta; una prueba
  de contrato lee esa regla del código de la web y comprueba la salida del servidor contra ella.

## Privacidad

`created_at` no es dato personal, financiero ni secreto. Lo reciben todos los lectores de la `Order`, incluida la
lista de cobros pendientes de FINANCE (`FIN-PENDING-COD-LIST-FINANCE-2026-10-02`), como cualquier otro campo. El
rastreo público tiene su propio mapa AI-04 y no cambia. Ningún evento de orden, payload de outbox, mensaje SignalR,
auditoría ni log cambia (los payloads de eventos y outbox son objetos propios, no la `OrderResult`); la única copia
nueva es la respuesta idempotente guardada, que es la misma respuesta del API.

## Web

Sin cambios de código: la columna "Creada" de "Órdenes" aparece sola y los lectores siguen aceptando que falte (por
si el API se revierte). Pruebas nuevas: la lista conserva el `created_at` que manda el servidor, la tabla muestra
"Creada" para cada fila con ese formato y `isStrictUtcInstant` acepta los formatos del servidor. AI-07 `order_list`
dice que el API ya manda la fecha y quita el `not_supported` de la columna.

## Pruebas

- Contrato: `Order_created_at_is_a_required_UTC_date_time_the_web_accepts` (requerido, `date-time`, propiedades de
  `Order` iguales a `OrderResponse`, operaciones que devuelven `Order`, salida de System.Text.Json contra la regla de la
  web, entrada de `x-pilot-contract-deltas`).
- Integración con PostgreSQL real y el API real (`OrderCreatedAtPostgreSqlHttpTests`): `createOrder`, `listOrders`,
  `getOrder`, `transitionOrder` y sus repeticiones devuelven el `created_at` igual al de la fila, con el formato de la
  web, también al repetir respuestas guardadas sin `created_at`.
- HTTP con servicio simulado (`OrderHttpTests.Every_Order_response_writes_created_at_as_a_strict_UTC_instant`): las
  cuatro operaciones y la repetición escriben `2026-10-12T17:04:05.123456+00:00`.

```bash
dotnet build Paqueteria.sln -c Release
dotnet test tests/Paqueteria.UnitTests -c Release --no-build
dotnet test tests/Paqueteria.ArchitectureTests -c Release --no-build
dotnet test tests/Paqueteria.ContractTests -c Release --no-build --filter "Category!=PostgreSqlContract"
dotnet test tests/Paqueteria.ContractTests -c Release --no-build --filter "FullyQualifiedName~OrdersPostgreSqlContractTests|FullyQualifiedName~OrdersTransitionPostgreSqlContractTests|FullyQualifiedName~OrderListDateFilterPostgreSqlContractTests|FullyQualifiedName~OrderSearchAndAllowedTransitionsPostgreSqlContractTests"
dotnet test tests/Paqueteria.IntegrationTests -c Release --no-build --filter "FullyQualifiedName~Orders.|FullyQualifiedName~HttpSurfaceOpenApiCoverageTests"
cd apps/web && pnpm exec vitest run && pnpm run typecheck && pnpm run lint
python3 docs/normative/v0.6/tools/validate_contracts.py
```

## Rollback

Revertir el commit. Es seguro en cualquier orden: la web ya acepta el campo (paso 1) y tolera que falte, así que un
API anterior solo oculta otra vez la columna "Creada". No hay datos, esquema ni migración que deshacer; las respuestas
idempotentes guardadas con `created_at` se siguen leyendo con el código anterior, que ignora ese miembro.
