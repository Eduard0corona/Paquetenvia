# RTE-001 contractual readiness gap

## Resultado del guard

- Repositorio: `Eduard0corona/Paquetenvia`
- Baseline revisada: `568ede7439fb17ecc1f30a514ce71eed6778fdd3`
- Rama de corrección contractual: `governance/rte-001-route-contract`
- Readiness original: `BLOCKED_BY_NORMATIVE_ROUTE_CONTRACT`
- Gap normativo: `RESOLVED_PENDING_MERGE`
- Implementación funcional iniciada: `false`
- Merge autorizado: `false`

En la baseline, RTE-001 no podía implementarse de forma completa, interoperable
y REST-authoritative: el modelo SQL y el evento SignalR proporcionaban piezas
estructurales, pero AI-05 no exponía operaciones HTTP de Routing. La corrección
contractual de esta rama cierra esas decisiones sin cambiar AI-06 ni iniciar una
implementación funcional.

## Resolución adoptada

El contrato agregado define `POST /routes`, `GET /routes`,
`GET /routes/{routeId}`, `POST /routes/{routeId}/stops`,
`DELETE /routes/{routeId}/stops/{stopId}` y
`PUT /routes/{routeId}/stops/order`.

RTE-001 queda limitado a assignments OWN existentes. El alta de una parada
DELIVERY y la actualización de `assignment.route_id` son atómicas. La exclusión
concurrente entre rutas se serializa por `order_id`; el reorder valida el set
completo de stops y usa optimistic concurrency sobre `route.version`.

La capacidad significa revalidar DSP-001 para cada orden añadida. El costo es la
suma server-side, en bigint cents, de `dispatch.assignments.cost_cents` para
assignments ACCEPTED/ACTIVE vinculados. Las órdenes ya válidas de 4500/5200
cents pueden ligarse sin alterar su snapshot de Pricing, `consolidated_route` o
`financial_override`.

REST/PostgreSQL permanecen como autoridad. `RouteChanged` nace de una mutación
confirmada mediante outbox y únicamente obliga a refetch REST.

## Autoridades revisadas

- `AI-08_BACKLOG.yaml`: alcance y criterios de RTE-001.
- `AI-05_OPENAPI.yaml`: superficie REST y DTOs públicos.
- `AI-06_SCHEMA.sql`: persistencia, constraints y RLS.
- `AI-07_UI_CONTRACTS.yaml`: superficies de Operations y Driver.
- `AI-12_SIGNALR_CONTRACT.yaml`: autoridad REST/PostgreSQL y `RouteChanged`.

También se contrastaron las implementaciones existentes de DSP-001, DSP-002,
OBS-001, RTM-001/002, EXT-001, Driver PWA, Operations dashboard, tenant/RLS,
auditoría y outbox. Esas piezas son infraestructura reutilizable; no constituyen
un contrato Routing implícito ni autorizan inventar una API privada.

## Evidencia del gap original en la baseline

### Alcance de backlog

AI-08 fija que RTE-001 depende de DSP-002 y requiere un módulo route/stop,
reordenamiento drag-and-drop, capacidad validada, costo calculable, órdenes de
$45/$52 ligadas a ruta, route constraints y una prueba Playwright de reorder.
Esos criterios describen el resultado, pero no las operaciones ni sus reglas.

### Modelo SQL disponible

AI-06 define:

- `routes.routes`: `id`, `operator_org_id`, `city_id`, `service_area_id`
  opcional, `driver_id` opcional, estado
  `DRAFT|PLANNED|ACTIVE|COMPLETED|CANCELLED`, `version`,
  `scheduled_for` y timestamps;
- `routes.route_stops`: `id`, `route_id`, `order_id`, `operator_org_id`,
  `sequence`, `stop_type`, `status`; `sequence` es positiva y única dentro de
  cada ruta;
- `dispatch.assignments.route_id`: FK nullable hacia `routes.routes`;
- RLS habilitado y forzado para rutas y paradas, con policies tenant sobre
  `operator_org_id`.

AI-06 también restringe `stop_type` a `PICKUP|DELIVERY|RETURN` y el estado de
una parada a `PENDING|ARRIVED|COMPLETED|FAILED|SKIPPED`. No obstante, el esquema
no determina las reglas de negocio para crear esos tipos ni impide que una orden
se repita en una ruta o aparezca en varias rutas activas.

### DTOs parciales de AI-05

AI-05 contiene únicamente estas piezas relacionadas:

- `Route`: sólo `id`, `status` y `version`;
- `DriverStop`: proyección de una parada derivada de la asignación activa, con
  `order_id`, `aggregate_version`, `order_public_id`, `stop_type`, `status`,
  `address_summary` y un `contact_token` protegido opcional;
- `CreateAssignmentRequest.route_id`: nullable, pero el propio contrato exige
  rechazar todo UUID no nulo hasta que RTE-001 implemente Routing;
- `Assignment`: no expone `route_id` en la respuesta.

`GET /driver/me/stops` es una lectura de paradas activas del Driver autenticado;
no devuelve una ruta, sus metadatos, su versión ni la secuencia autoritativa de
`routes.route_stops`.

### UI y tiempo real

AI-07 declara `dispatch_dashboard.actions.add_to_route`, pero no define una
pantalla de rutas, una interacción de creación, lista/detalle, retiro o reorder,
ni el tratamiento de conflictos. `/driver/stops` sigue siendo la superficie PWA
existente.

AI-12 declara que la fuente de verdad es `REST API + PostgreSQL` y define
`RouteChanged` para Operations y el Driver afectado con:

- `route_id`;
- `route_version`;
- `changed_stop_ids`;
- `occurred_at`.

El evento es una notificación útil, pero no puede reconstruir el agregado. La
implementación actual ya tiene los contratos SignalR, handlers y publishers de
`RouteChanged`; Operations lo traduce a un refresh. Sin embargo, no existe un
endpoint REST de ruta al que hacer refetch, ni existe todavía un topic/parser/
productor outbox de Routing. La presencia del publisher no autoriza publicación
directa antes del commit.

### Infraestructura reutilizable, no contractual

El runtime actual aporta OrganizationContext transaccional, FORCE RLS,
autorización de Operations, agregación de capacidad de paquetes, elegibilidad
de Driver, auditoría append-only, idempotencia y outbox confiable. DSP-002 y
EXT-001 muestran cómo componer estas capacidades en una transacción. Ninguno
define las decisiones específicas de Routing enumeradas abajo.

## Matriz original del readiness REST

| Requisito | Evidencia vigente | Resultado |
| --- | --- | --- |
| 1. Crear ruta | No hay path ni request/response en AI-05. | Falta |
| 2. Leer ruta y secuencia | `Route` no tiene stops; `DriverStop` no es un agregado Route. | Falta |
| 3. Listar rutas para Operations | No hay colección REST de rutas, filtros ni paginación. | Falta |
| 4. Agregar orden/stop | Sólo existe la acción nominal `add_to_route` en AI-07. | Falta |
| 5. Retirar orden/stop | No está definido en AI-05, AI-07 ni AI-08. | Falta |
| 6. Reordenar stops | AI-08 exige drag reorder, pero no define operación ni payload. | Falta |
| 7. Optimistic concurrency | SQL y el DTO mínimo tienen `version`; no hay `expected_version`, regla de incremento ni error de conflicto para Route. | Falta |
| 8. Asociar `assignment.route_id` | La request actual exige null/omisión y la response no expone el campo; no se define atomicidad con route stops. | Falta |
| 9. Recuperar por REST | No hay GET autoritativo de rutas para load, refresh o reconnect. | Falta |
| 10. `RouteChanged` post-commit | El payload/audience existe, pero no se especifica el productor transaccional/outbox ni el refetch de rutas. | Parcial e insuficiente |

La ausencia de cualquiera de los puntos 1 a 9 ya bloquea una implementación
interoperable. SignalR no puede suplirlos porque AI-12 prohíbe tratar los hubs
como autoridad de escritura o estado.

## Ambigüedades observadas en la baseline

### Autorización

AI-07 permite que `platform_admin`, `dispatcher` y `customer_support` entren al
dashboard y enumera `add_to_route` entre las acciones de esa pantalla, pero no
asigna roles por acción. No queda definido si customer support puede crear o
mutar rutas, ni qué operaciones requieren MFA. La política existente de
Operations permite dispatcher y platform admin con MFA, pero es implementación
previa y no puede decidir por sí sola la autorización de RTE-001.

### Elegibilidad de órdenes y unicidad

No están determinados:

- los estados de orden que admiten incorporación o retiro;
- si una orden produce una o varias paradas dentro de la misma ruta;
- si se permite repetir `(order_id, stop_type)`;
- si una orden puede pertenecer simultáneamente a más de una ruta no terminal;
- cómo interactúan estas reglas con la única asignación activa por orden.

AI-06 sólo garantiza `UNIQUE(route_id, sequence)`, por lo que todas las variantes
anteriores siguen siendo físicamente posibles.

### Semántica de stops

Los enums `PICKUP`, `DELIVERY` y `RETURN` están definidos, pero no cuándo se crea
cada stop, qué transiciones permiten insertarlo o retirarlo, cómo cambia su
estado, si pickup y delivery deben coexistir para una orden, ni qué ocurre con
la secuencia cuando una parada ya está completada.

`GET /driver/me/stops` deriva actualmente un único tipo visible desde el estado
de orden/asignación. Su documentación anticipa que Routing podrá reemplazar esa
proyección con `routes.route_stops`, pero no define cómo hacerlo.

### Capacidad

Existe una política de elegibilidad que agrega peso y dimensiones de los
paquetes frente a la capacidad de un Driver. Falta definir para una ruta:

- qué capacidad se agrega y en qué unidad;
- si se usa carga simultánea por tramo o suma total de órdenes;
- qué Driver/vehículo se evalúa cuando `route.driver_id` es null;
- cuándo se revalida al agregar, retirar, reordenar o reasignar;
- el resultado REST que permite explicar el rechazo.

### Driver de ruta y assignment

No hay regla que exija igualdad entre `routes.routes.driver_id` y
`dispatch.assignments.driver_id`, ni se define cuál es autoritativo, cómo se
propaga un cambio de Driver o qué sucede con las assignments al retirar una
orden o cancelar una ruta.

### Costo y tarifas de $45/$52

El contrato comercial contextualiza $52 y $45 como tiers que requieren
`consolidated_route=true` o un override financiero, y AI-05/AI-06 preservan el
snapshot de pricing de quote/order. RTE-001 no define:

- qué magnitud es el “costo calculable” de una ruta;
- fórmula, componentes, redondeo, moneda ni momento de cálculo;
- si el valor es suma de `assignment.cost_cents`, costo operativo de ruta u otra
  proyección;
- cuándo una ruta real satisface y valida `consolidated_route`;
- cómo se liga una order ya cotizada en $45/$52 a una ruta sin confiar en un
  booleano aportado por el cliente ni recalcular pricing histórico.

Por tanto, no debe inventarse pricing nuevo ni interpretarse que el importe por
sí solo prueba consolidación.

### Versionado y concurrencia de reorder

`routes.routes.version` existe, pero no se define:

- si toda mutación de stops incrementa la versión una sola vez;
- la forma de `expected_version` o una precondición HTTP equivalente;
- si reorder envía la secuencia completa o un movimiento incremental;
- el contrato de IDs, duplicados, conjunto completo y atomicidad;
- el código/status de conflicto y la respuesta que permite refrescar;
- la semántica de idempotencia y replay;
- el contenido exacto de `changed_stop_ids`.

## Corrección normativa adoptada

La decisión de owner cierra el gap mediante:

1. AI-05: superficie REST tenant-aware para crear/listar/leer rutas y
   agregar/retirar/reordenar stops, con idempotencia, respuesta uniforme 404 y
   conflicto 409.
2. AI-05: `Route`, `RouteStop`, `RouteDetail`, `RoutePage` y requests de
   creación/mutación con `expected_version`.
3. AI-05: `Assignment.route_id` observable, manteniendo el add-stop como única
   autoridad para ligar un assignment OWN existente.
4. AI-07: `/ops/routes`, roles separados de lectura/mutación, route planner,
   drag más alternativa de teclado y recuperación REST.
5. AI-08: criterios verificables de tenant isolation, elegibilidad DSP-001,
   atomicidad, carreras, costo y órdenes de 4500/5200 cents.
6. AI-12: `RouteChanged` post-commit desde outbox, audiencia server-side y
   refetch de `GET /routes/{routeId}`.

Los archivos normativos modificados son:

- `docs/normative/v0.6/contracts/AI-05_OPENAPI.yaml`;
- `docs/normative/v0.6/specs/AI-07_UI_CONTRACTS.yaml`;
- `docs/normative/v0.6/specs/AI-08_BACKLOG.yaml`;
- `docs/normative/v0.6/contracts/AI-12_SIGNALR_CONTRACT.yaml`;

AI-06 no cambia. El modelo existente permite usar una transacción y
serialización PostgreSQL estable por `order_id`; `UNIQUE(route_id, sequence)`
permanece como backstop de secuencia.

Después de cualquier corrección normativa autorizada deben regenerarse mediante
el validador oficial:

- `docs/normative/v0.6/CHECKSUMS_SHA256.txt`;
- `docs/normative/v0.6/MANIFEST.json`.

Los cambios derivados deben contener únicamente hashes/metadata de los contratos
modificados y el validador debe terminar en `VALIDATION_OK`.

## Límites que la implementación debe preservar

- No habilitar EXTERNAL ni ALLY_CAPACITY routing.
- No aceptar `stop_type` del cliente; RTE-001 crea sólo DELIVERY.
- No crear pricing, margen ni capacidad agregada nuevos.
- No modificar snapshots de Pricing, `consolidated_route` ni
  `financial_override` al ligar órdenes de 4500/5200 cents.
- No duplicar el vínculo de ruta en la creación DSP-002: add-stop sobre el
  assignment existente es la autoridad.
- No usar SignalR como escritura o estado autoritativo, publicar antes del
  commit ni aceptar grupos/audience del cliente.
- No alterar AI-06 para implementar esta decisión contractual.

## Estado final

`RTE-001 normative_gap = RESOLVED_PENDING_MERGE`

`RTE-001 implementation_started = false`

`RTE-001 merge_authorized = false`
