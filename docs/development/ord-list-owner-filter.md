# listOrders: `owner_org_id` de la propia organización también traía las órdenes que solo opera

Severidad: **MAJOR** (AI-01 §7, operación). Corrección de error, sin cambio de contrato: AI-05 `listOrders` ya dice
que otra `owner_org_id` da la página vacía y nunca dijo que la propia trajera órdenes de otra dueña. Sin fila en
`decision-log.md`, sin migración y sin cambio en la web. Ninguna organización ve órdenes que antes no veía.

## Decisión del owner (2026-10-13), literal

- Pregunta: "Lista de órdenes, filtro por empresa dueña (owner_org_id): hoy, si filtras por tu propia empresa, la
  lista también trae las órdenes que solo operas (es un error). El contrato publicado dice que filtrar por otra
  empresa como dueña da la página vacía. Cuando una empresa operadora filtra por la empresa dueña de las órdenes que
  opera, ¿qué debe ver?"
- Respuesta: "Solo arreglar el error (Recommended)", cuya opción decía: "Tu propia empresa da solo las órdenes que
  posees; cualquier otra empresa sigue dando la página vacía, como dice el contrato hoy. Es una corrección de error:
  sin cambio de contrato. Ninguna pantalla usa hoy ese filtro."
- No elegida: "Filtro real por dueña" ("Cualquier empresa X da las órdenes de X que ya puedes ver (propias, o de X
  que operas). Nunca muestra órdenes que hoy no ves. Cambia el texto del contrato (AI-05) y lleva su fila de
  decisión; es un cambio MAJOR.").

## Causa

`QuoteSnapshotToOrderCoordinator.ListAsync` devuelve la página vacía cuando `owner_org_id` es otra organización, pero
cuando es la organización activa la dejaba pasar sin usarla: `ListWithinTransactionAsync` no recibía el valor y su
SQL no tenía predicado por dueña. Decidía solo RLS (dueña u operadora en `app.current_org_ids`), así que la lista
"filtrada por mi empresa como dueña" traía también las órdenes que la organización solo opera, incluida la lista de
cobros pendientes de FINANCE (FIN-PENDING-COD-LIST-FINANCE-2026-10-02 permite acotarla con `owner_org_id`). No había
fuga entre organizaciones: eran órdenes que la organización ya ve sin el filtro. El cursor ya incluía `owner_org_id`
en su ligadura (`OrderCursorCodec.Binding`).

Ninguna pantalla web manda `owner_org_id` a `listOrders` ("Órdenes", "Buscar guía", Rutas y "Cobro contra entrega"
no lo usan; el `owner_org_id` del tablero es otro endpoint); lo podía recibir un cliente del API.

## Corrección

- El SQL de la lista agrega `AND (@owner::uuid IS NULL OR owner_org_id=@owner)` y `ListAsync` le pasa el valor, que
  en ese punto solo puede ser nulo o la organización activa.
- Sin cambios: la página vacía para cualquier otra `owner_org_id` (antes de abrir la transacción), la transacción
  explícita, `set_config` después de `BEGIN`, `current_org_ids` como `uuid[]`, RLS, la capacidad del endpoint y la
  re-verificación de FINANCE dentro de la transacción, el cursor y su ligadura, y el orden `created_at DESC, id DESC`.
- Índices: sin migración. `orders_owner_status_idx (owner_org_id, status, created_at DESC)` ya atiende el predicado.

## Pruebas

`OrderListOwnerFilterPostgreSqlContractTests` (PostgreSQL real con RLS, categoría `PostgreSqlContract`): una
organización dueña de cinco órdenes que opera tres de otra dueña.

- (a) Sin filtro: las propias y las operadas, de la más reciente a la más antigua.
- (b) `owner_org_id` = ella misma: solo las propias (la dueña ve las suyas con o sin filtro).
- (c) `owner_org_id` = la dueña de las que opera (y al revés): página vacía, como dice AI-05.
- (d) Una organización ajena o un id de ninguna: página vacía.
- (e) Con `status`, `created_from`/`created_to` y cursor (páginas de 2): la segunda página sigue filtrada; el cursor
  emitido con el filtro se rechaza sin él y al revés (`409 INVALID_REQUEST`, sin cambio); `public_id` de una orden
  operada no aparece con el filtro y sí sin él.
- (f) FINANCE con MFA y `cod_pending_reconciliation=true`: sin filtro, los dos cobros pendientes (propio y operado);
  con `owner_org_id` = ella misma, solo el propio (también con fechas); con la otra dueña, página vacía; sin MFA, 403
  dentro de la transacción como antes.

Con el predicado reemplazado por `true` las tres pruebas fallan; con la corrección pasan.

## Rollback

Revertir el commit. `owner_org_id` de la propia organización vuelve a traer también las órdenes que solo opera. No
hay datos, esquema, contrato ni web que deshacer; los cursores emitidos siguen siendo válidos (la ligadura no cambia).
