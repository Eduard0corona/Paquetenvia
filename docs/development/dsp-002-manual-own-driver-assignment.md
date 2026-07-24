# DSP-002: asignación manual a repartidor propio

DSP-002 incorpora la asignación manual de una orden a un repartidor propio y
la consulta autenticada de las paradas del repartidor actual. La asignación,
la transición, el evento, el outbox, dos auditorías y la respuesta idempotente
se confirman en una sola transacción PostgreSQL.

Depende de TEN-001, AUD-001, GEO-001, ORD-001, ORD-002,
ORD-002-DEF-001 y DSP-001. Reutiliza el contexto transaccional tenant, RLS,
`OrderTransitionMatrix`, `OrderTransitionGuardRegistry`,
`DriverEligibilityPolicy` y los contratos de capacidad de Drivers. No invoca
los servicios PostgreSQL de Orders o Drivers porque abrirían otra transacción.

## Arquitectura, propiedad y adopción

El módulo tiene cuatro proyectos:

- `Dispatch.Domain`: enums, invariantes monetarias, política de assignment y
  proyección pura de paradas.
- `Dispatch.Application`: comando, resultados, puertos, autorización,
  canonicalización idempotente y contratos estrechos.
- `Dispatch.Infrastructure`: EF Core, adopción, SQL tenant-aware, coordinador
  `assignment_to_order_status_event`, elegibilidad, paradas y auditoría.
- `Dispatch.Endpoints`: binding estricto, autorización de frontera, Problem
  Details y únicamente las dos rutas DSP-002.

Dispatch es propietario exclusivamente de `dispatch.assignments`. No mapea
`dispatch.external_offers`, `routes.routes`, `routes.route_stops` ni
`drivers.driver_positions`. No implementa `EXTERNAL` ni `ALLY_CAPACITY`.

`DispatchDbContext` mapea las once columnas canónicas, enums como texto, costo
como `bigint`, UUID/timestamps con `ValueGeneratedNever`, filtro tenant por
owner u operator y el índice parcial único
`one_active_assignment_per_order`, filtrado por
`status IN ('ACCEPTED','ACTIVE')`. El `DbSet` permanece interno.

La migración única
`20260723_AdoptCanonicalDispatchAssignmentsBaseline` usa
`platform.__ef_migrations_history_dispatch`. Verifica schema, tabla, columnas,
PK, checks exactos de enums y `cost_cents >= 0`, y las cinco FKs por columna
local, tabla/columna referenciada, cardinalidad y acciones `NO ACTION`.
También valida el índice parcial exacto, RLS, FORCE RLS y la única policy
`assignments_tenant`. No crea, altera ni elimina objetos; `Down` es
deliberadamente no destructivo. El migrador la ejecuta después de Drivers y
Orders. API y Worker no migran al iniciar; Worker no referencia Dispatch.

## POST de assignment

`POST /api/v1/orders/{orderId}/assignments` requiere autenticación,
`X-Organization-Id` e `Idempotency-Key` única, sin whitespace exterior y de
16 a 128 caracteres.

Request:

```json
{
  "driver_id": "00000000-0000-0000-0000-000000000001",
  "assignment_type": "OWN",
  "cost_cents": 0,
  "route_id": null
}
```

`driver_id`, `assignment_type` y `cost_cents` son obligatorios. El costo es
`int64`, puede ser cero y no puede ser negativo. `route_id` solo puede faltar o
ser nulo. Propiedades desconocidas, tipos fuera de alcance o UUID vacíos
producen conflicto uniforme.

AI-05 conserva el vocabulario global `OWN`, `EXTERNAL`, `ALLY_CAPACITY`, pero
declara que DSP-002 habilita únicamente `OWN`. `EXTERNAL` permanece reservado
para EXT-001 y `ALLY_CAPACITY` para ALY-004. AI-05 declara `route_id` nullable:
ausencia o `null` son válidos; todo valor no nulo se rechaza hasta RTE-001.

Response 201 de creación o replay:

```json
{
  "id": "00000000-0000-0000-0000-000000000002",
  "order_id": "00000000-0000-0000-0000-000000000001",
  "driver_id": "00000000-0000-0000-0000-000000000003",
  "status": "ACCEPTED",
  "cost": {
    "currency": "MXN",
    "amount_cents": 0
  }
}
```

No expone owner/operator, route, versión, actor, timestamps, documentos,
capacidad, direcciones, teléfono ni otra PII.

La matriz normativa es 201/401/403/404/409. El 404 uniforme cubre orden o
conductor ausente/cross-tenant. El 409 usa Problem Details con uno de los
códigos públicos `INVALID_REQUEST`, `CONFLICT`, `DRIVER_INELIGIBLE` o
`DRIVER_DOCUMENT_EXPIRED`; nunca expone el motivo interno.

## Autorización, owner y operator

La autorización relee usuario y membresía internos dentro de la transacción;
no confía en roles emitidos por AuthCenter.

- `PLATFORM_ADMIN`: usuario y membresía activos, con MFA actual.
- `DISPATCHER`: usuario y membresía activos.
- cualquier otro rol: denegado.

Orden o driver ausente, extranjero o no visible recibe 404 indistinguible.
Actor visible sin capacidad recibe 403. Configuración Disabled falla cerrado
con 403 porque no existe capacidad operativa habilitada.

La orden conserva `owner_org_id`. Cuando el tenant activo es el owner,
`assignment.operator_org_id` es nulo; cuando coincide con el operator ya
registrado, conserva ese operator. Nunca se introduce uno nuevo.

## Idempotencia y replay actual

Scope: `DSP-002:ASSIGN_OWN_DRIVER`.

El SHA-256 canónico contiene tenant, order ID, driver ID, tipo uppercase,
costo `int64` y route nula. Excluye actor, MFA, request ID, tiempo, roles,
documentos, elegibilidad, estado/versión y PII.

El lock order es:

1. advisory lock por tenant, scope y key;
2. `orders.orders FOR UPDATE`.

Misma key y hash devuelve el mismo 201 sin reescrituras. Hash distinto devuelve
409 antes de autorización. Para replay completado se valida resource ID y
response; después una fila assignment visible coherente en `ACCEPTED` o
`ACTIVE`, y exactamente un evento histórico
`READY_FOR_PICKUP|RESCHEDULED -> ASSIGNED` asociado por `assignment_id`.
Finalmente se releen rol y MFA y se autoriza al actor actual.

El replay no reevalúa documentos, capacidad, estado actual ni guards. Una
suspensión posterior del driver no invalida la evidencia histórica para otro
dispatcher todavía autorizado.

## Orden transaccional, paquetes y elegibilidad

Cada intento valida, captura un solo `occurred_at`, normaliza, calcula el hash,
abre la transacción y aplica el contexto tenant. Dentro de ella:

1. adquiere el advisory lock y lee la fila idempotente;
2. si el hash difiere, devuelve 409 antes de leer autorización;
3. si el registro está completado, valida resource/response, assignment y
   evento histórico; después relee autorización/MFA y devuelve el 201;
4. si existe un registro incompleto, devuelve 409 sin mutarlo;
5. para una request nueva, relee y valida primero la autorización actual;
6. solo después inserta la reserva idempotente;
7. bloquea la orden con `FOR UPDATE`, verifica visibilidad, tenant y estado;
8. comprueba que no exista assignment activo;
9. lee y agrega `orders.package_items`;
10. lee el driver y ejecuta `DriverEligibilityPolicy`;
11. inserta el assignment `OWN/ACCEPTED`;
12. ejecuta matriz y guards de Orders;
13. actualiza optimistamente la orden a `ASSIGNED`;
14. inserta order event, outbox y las dos auditorías;
15. completa la fila idempotente;
16. ejecuta el fault-injection point previo a commit y confirma.

Los únicos estados fuente son `READY_FOR_PICKUP` y `RESCHEDULED`; no se agrega
una arista. El update exige ID, estado y versión, afecta una fila e incrementa
versión una vez. La unique parcial y `(order_id, aggregate_version)` respaldan
las carreras.

Los paquetes nunca vienen del cliente. Se exige al menos uno y se calcula con
aritmética checked: cantidad, peso total `int64`, máximo individual y máximos
de `length_mm`, `width_mm`, `height_mm`. Dimensión ausente permanece nula;
JSON/tipo/peso inválido u overflow falla cerrado.

DSP-001 se reevalúa con la misma conexión/transacción: perfil OWN activo,
usuario, membresía DRIVER, ciudad, área opcional, documento más reciente por
tipo y capacidad. Expiración igual a `occurred_at` ya está vencida. Una
evaluación positiva anterior no reserva capacidad.

Los códigos públicos seguros son `DRIVER_DOCUMENT_EXPIRED` para
`DSP-001 DOCUMENT_EXPIRED` y `DRIVER_INELIGIBLE` para otras categorías. Nunca
se filtran object keys, hashes, fechas documentales o identidad.

## Evento, outbox y auditorías

Se crea un único `ORDER_STATUS_CHANGED`, con `public_event_code=null`, razón
redactada `MANUAL_OWN_DRIVER_ASSIGNMENT` y previous/new status más assignment
ID. No incluye costo, driver, documentos, capacidad, dirección o PII.

El único outbox usa `orders.status-changed`, aggregate `Order`, nueva versión,
`PENDING`, attempts cero y payload mínimo. No existe topic de assignment. UUID
y timestamps son de aplicación; los INSERT no usan `RETURNING`.

AUD-001 escribe `ASSIGNMENT_CREATED` con costo/owner/operator y versión de
política, y `ORDER_STATUS_CHANGED` con versiones/assignment. Ambas usan writer
y redacción centrales. El objetivo
`assignments_with_cost_owner_audit=1.00` queda cubierto.

## GET de paradas y privacidad

`GET /api/v1/driver/me/stops` requiere autenticación y tenant. Resuelve solo el
perfil OWN activo cuyo `user_id` es el actor, y exige usuario y membresía DRIVER
activos. No recibe driver ID ni filtros. Sin assignments activos devuelve `[]`.

Incluye assignments `ACCEPTED`/`ACTIVE`, órdenes operativas y ordena por
`assignment.created_at`, `assignment.id`:

- `ASSIGNED`, `AT_PICKUP`: PICKUP, origin summary.
- `PICKED_UP`, `IN_TRANSIT`, `DELIVERING`: DELIVERY, destination summary.
- `RETURNING`: RETURN, origin summary.
- `FAILED_ATTEMPT`, `RESCHEDULED`: DELIVERY si pickup photo, incidencia con
  custody o historial alcanzó pickup/in-transit/delivery; de otro modo PICKUP.

Solo retorna `order_public_id`, `stop_type`, estado actual y `address_summary`.
Nunca devuelve ciphertext, nombre, teléfono, coordenadas, costo, IDs internos,
paquetes, descripción de incidencia u object keys. `contact_token` permanece
ausente. Routing podrá reemplazar esta proyección con `routes.route_stops`.

## RLS, observabilidad, rollback y riesgos

Todas las lecturas tenant usan transacción y reaplican contexto por retry. La
policy `assignments_tenant` usa owner u operator; RLS y FORCE RLS siguen
activos y `paqueteria_app` es NOBYPASSRLS. Contexto vacío falla cerrado.
Pooling, retry y cancelación no dejan contexto ni efectos parciales.

Las métricas son `dispatch.assignment.created`, `.conflict`, `.ineligible`,
`.replay` y `dispatch.driver_stops.count`, sin dimensiones de IDs. Logs solo
incluyen IDs técnicos, versiones de política, resultado y duración; nunca
address summary, documentos, hashes, object keys, teléfonos o bodies.

Fault injection cubre reserva, lock, paquetes, elegibilidad, assignment,
update, evento, outbox, auditorías, antes/después de completar idempotencia y
antes de commit. Cada excepción revierte toda la unidad.

Rollback operativo:

1. configurar `Dispatch:Provider=Disabled`;
2. desplegar y verificar fail-closed;
3. revertir primero los tres commits de remediación contractual;
4. si el rollback requerido es de DSP-002 completo, revertir después los tres
   commits originales del módulo;
5. conservar assignments, órdenes/versiones, eventos, outbox, auditorías e
   historial EF;
6. no ejecutar DDL inverso ni eliminar datos.

Convenciones reversibles:

- `DSP-002-CONV-001`: 409 uniforme, formalizado en AI-05 por la remediación
  contractual `DSP-002-CONTRACT-REMEDIATION`.
- `DSP-002-CONV-002`: assignment inicia `ACCEPTED`, no `ACTIVE`.
- `DSP-002-CONV-003`: route ID solo nulo; Routing no se implementa.
- `DSP-002-CONV-004`: paradas directas hasta que exista Routing.

Configuración base:

```json
{
  "Dispatch": {
    "Provider": "Disabled",
    "AssignmentPolicyVersion": "synthetic-v1"
  }
}
```

PostgreSQL exige también `Drivers:Provider=PostgreSql`; `ValidateOnStart`
rechaza la combinación insegura.

GATE-007 y GATE-010 siguen abiertos. No se usan documentos ni PII reales.
AI-05, AI-08 y el decision log se actualizaron de forma controlada, sin cambiar
AI-06 ni AI-18. El issue #5 permanece abierto.
