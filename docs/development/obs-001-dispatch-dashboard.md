# OBS-001: dashboard básico de despacho

OBS-001 agrega una superficie operativa read-only sobre ORD-001/002,
DSP-001/002, DRV-003 y RTM-001/002. El endpoint y la PWA son aditivos; no
implementan OPS-001 ni modifican la línea base normativa. La decisión completa
está en
[ADR-OBS-001](../adr/ADR-OBS-001-OPERATIONS-READ-MODEL.md).

## Módulo Reporting y endpoint

`Reporting.Domain`, `Reporting.Application`, `Reporting.Infrastructure` y
`Reporting.Endpoints` forman un módulo de lectura sin ownership de tablas. Se
expone:

```text
GET /api/v1/operations/dashboard
operationId: getOperationsDashboard
```

No se amplió `listOrders`: AI-05 no se modificó en OBS-001 (sus cambios exigen
autorización normativa explícita; no está congelado y se ha ampliado después
por EXT-001, RTE-001, CSV-001, INC-001, FIN-001 y SET-001) y su DTO no contiene la
proyección operativa requerida por AI-07. El nuevo endpoint mantiene su propio
DTO exacto y no cambia contratos existentes.

`OperationsDashboard.Provider` admite `Disabled` y `PostgreSql`. El default es
`Disabled`; devuelve 503 genérico y readiness degradado. El provider productivo
usa timeout de 5 segundos, página fija de 50, máximo interno de 100 y rango de
fechas de 31 días.

## Autorización, RLS y consulta

La autenticación precede a la organización activa. Se admiten `DISPATCHER` y
`PLATFORM_ADMIN` únicamente con MFA. Viewer, Driver, Platform Admin sin MFA y
cualquier rol no contratado reciben 403. `CUSTOMER_SUPPORT` no se infiere ni
se agregó. Token inválido produce 401; organización ausente/inválida produce
403; fallas técnicas o de consistencia producen 503 sin detalles internos.

La conexión de aplicación abre una transacción, establece contexto mediante
`set_config(..., true)`, ejecuta `SET LOCAL ROLE paqueteria_app` y deja que RLS
limite todas las filas. Sólo hay dos comandos: autorización/contexto y una
consulta CTE de proyección. El pooling no conserva tenant porque contexto y rol
son locales a la transacción.

Filtros permitidos:

```text
order_id, status, delivery_zone_id, client_account_id, owner_org_id,
operator_org_id, service_type, created_from, created_to, unassigned, cursor
```

Cada parámetro conocido aparece una vez; UUIDs son canónicos y no vacíos,
status/service type son exactos, fechas son UTC y el rango máximo es 31 días.
Valores inválidos devuelven Problem Details 400 sin reflejar input. Los
desconocidos se ignoran. No existe `page_size`.

El cursor Base64URL contiene versión, `updated_at` UTC y order ID. La
ordenación estable es `updated_at DESC, order_id DESC`. `Cargar más` deduplica
por ID y nunca sustituye una versión por otra menor.

## Proyección y privacidad

La respuesta contiene sólo `generated_at`, `items` y `next_cursor`. Cada item
conserva el DTO exacto de OBS-001:

- owner/operator con nombre canónico visible bajo RLS;
- cliente visible o `null`;
- zona operativa de destino o `null`;
- `pickup_window` en `null`; `delivery_window` es la ventana de servicio de la orden (ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02) o `null`, representada como `Horario de la zona`;
- assignment más reciente entre `ACCEPTED`/`ACTIVE`; más de uno falla cerrado;
- `driver_reference` no PII `DRV-xxxxxxxx`; el ID contractual no se renderiza;
- posición persistida más reciente del conductor activo;
- `AUTHORIZED_OVERRIDE` o `BELOW_MINIMUM_SNAPSHOT` cuando aplica;
- alerta sin assignment sólo para `READY_FOR_PICKUP` y `RESCHEDULED`;
- `total`: el `orders.total_cents` de la orden con IVA incluido, en la forma `Money` de AI-05 `Order.total`
  (`currency` MXN, `amount_cents` int64), el mismo valor que esos roles ya leen en `listOrders`/`getOrder`; negativo
  o en otra moneda falla cerrado (UI-PHASE3-INBOX-TOTAL-2026-10-10).

No se incluyen costo de assignment, email, teléfono, dirección, package, proof,
notas, token ni payload SignalR. Telemetría usa categorías de baja cardinalidad.

## Web y autoridad

`/ops/dashboard` muestra organización activa, conexión, última actualización,
resumen, filtros server-side, board de 17 estados, paginación y toggle
Lista/Posiciones. `/ops/orders/{orderId}` combina el endpoint de orden existente
con la proyección dashboard filtrada y muestra timeline en el orden recibido.

Proofs, incidencias y ruta declaran sus limitaciones. Sólo `Abrir orden`,
`Actualizar` y `Volver al tablero` funcionan. Asignar, publicar oferta, agregar
a ruta y abrir incidencia permanecen deshabilitadas con razón; no existe
selector manual por UUID ni discovery de conductores.

Los eventos de status, timeline, assignment y ubicación pasan por los guards
existentes. Route/incident/offer también pueden solicitar refresh. Ningún
payload muta el snapshot: REST/PostgreSQL siempre reemplaza el estado. Debounce
es 250 ms para negocio y 500 ms para ubicación; una señal durante una lectura
genera como máximo una lectura adicional.

Dashboard y detalle comparten un coordinador single-flight que distingue
refresh normal de `mandatory-reconnect`. Se eligió serializar y forzar una
segunda lectura: una request normal iniciada antes del reconnect puede terminar,
pero nunca satisface la resincronización; la lectura obligatoria comienza
después y tiene prioridad sobre la única lectura normal adicional agrupada.

En dashboard, la respuesta obligatoria reemplaza la primera página, limpia
páginas y cursores anteriores y produce `aggregate_versions` directamente de
sus items. En detalle, `GET /api/v1/orders/{orderId}` y la proyección filtrada
se completan juntos, se exige exactamente un item y las versiones proceden de
esa proyección exacta. La promesa sólo resuelve tras aplicar el snapshot con la
misma sesión y organización.

Al reconectar sólo se marca `Conectada` después de la lectura obligatoria,
validación, aplicación y reemplazo del guard. Cualquier 401, 403, 404 de
detalle, 5xx, fallo de red/timeout/contrato o cambio de sesión rechaza la
resincronización; SignalR pasa a `Sin conexión` y detiene el Hub. Polling corre
cada 30 segundos, pausa al ocultar el documento y refresca al volver visible.
Al cambiar organización se cancelan requests, se detiene el Hub y se limpia A
antes de cargar B.

Todas las fechas usan `America/Mazatlan`. Posiciones utiliza únicamente puntos
persistidos visibles, normalizados en SVG interno, con referencia, captura y
accuracy; no muestra coordenadas en texto ni solicita mapas, tiles o
geolocalización.

## Almacenamiento, seguridad y accesibilidad

Dashboard, órdenes, filtros, timeline, organización, referencias, posiciones,
bearer y namespace viven sólo en memoria. No se crean cookies ni se usa
localStorage, sessionStorage, IndexedDB o Cache API para operaciones.

El Service Worker usa `paquetenvia-driver-shell-v5`. `/ops`, APIs de operations,
orders y contextos, OperationsHub, Authorization y `access_token` son
network-only. El shell Driver offline se conserva.

`/ops/:path*` recibe no-store/private, no-cache, no-referrer, noindex, nosniff,
Permissions-Policy sin sensores y CSP sin wildcard/terceros. La UI usa
main/h1, regiones/headings, fieldsets/labels, links/botones reales, timeline y
lista semánticas, `time datetime`, aria-live/aria-busy, foco visible, targets de
44 px, contraste, reduced motion y safe-area. El board se apila en móvil.

## Pruebas y CI

- Unit: contrato, filtros, cursor, 17 estados, assignments, alertas, referencia,
  ubicación, costo y UTC.
- `OperationsDashboardPostgreSql`: PostgreSQL/PostGIS real, RLS, autorización,
  aislamiento, filtros, respuesta/headers y privacidad.
- Vitest: parsers, API/filtros, labels/zona horaria, SW, CSP, almacenamiento y
  acciones; deferreds cubren single-flight, request previa, lectura obligatoria,
  snapshot exacto, cambio de sesión, fallo y el par de detalle.
- `OperationsDashboardPwa`: PostgreSQL, Kestrel, Worker/outbox, OperationsHub,
  Next y Chromium reales; transición, ubicación, REST refresh, tenant switch,
  reconnect, detalle, privacidad y ausencia de requests cartográficos. Una
  compuerta de transporte retrasa una request real previa, comprueba una segunda
  lectura posterior al reconnect y un provider `Disabled` prueba el 503 sin
  mostrar `Conectada`.
- `Validate operations dashboard` hace restore locked, build, frozen install,
  Chromium, ambas categorías, resultados sólo al fallar y cleanup.

## Riesgos y límites

- El endpoint aditivo `GET /api/v1/operations/dashboard` todavía no está
  declarado en AI-05; `HttpSurfaceOpenApiCoverageTests` lo nombra como única
  excepción. Declararlo requiere una decisión normativa propia.
- La ventana de recolección no está persistida; cliente puede quedar `null`.
- La referencia de conductor no es nombre personal.
- Posiciones no es un mapa; GATE-003 continúa abierto.
- Las posiciones exactas sólo son visibles a operaciones autorizadas.
- Dashboard no asigna; external offers, rutas e incidencias no existen.
- Realtime/rate limit siguen single-instance; Redis no participa y GATE-013
  sigue abierto. SignalR no sustituye REST.
- Issue #5 y GATE-007/010/011/014/015 permanecen abiertos.
- El audit de pnpm puede conservar advisories heredados.

## Rollback

1. Configurar `OperationsDashboard:Provider=Disabled`.
2. Verificar 503 genérico.
3. Retirar navegación hacia `/ops/dashboard`.
4. Desplegar la web anterior.
5. Incrementar el cache name de rollback.
6. Detener OperationsHub del dashboard.
7. Revertir los commits OBS-001.
8. Retirar Reporting de la solución.
9. Conservar Orders, Dispatch, Drivers, Locations y Realtime.
10. Conservar outbox y auditoría.
11. No borrar órdenes.
12. No borrar assignments.
13. No borrar posiciones.
14. No ejecutar DDL.
15. No modificar datos DRV/TRK.
16. No borrar caches ajenas.
