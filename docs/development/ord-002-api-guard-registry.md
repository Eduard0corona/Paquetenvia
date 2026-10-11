# ORD-002-API-GUARD-REGISTRY: la API componía el registro de guardas AI-04 vacío

Severidad: **BLOCKER** (AI-01 §7: seguridad, dinero, legal y cobertura). La corrección restaura el comportamiento que
AI-04 ya exige; no hay cambio normativo, de contrato, de esquema ni de datos.

## Defecto

`OrderTransitionGuardRegistry` (`Orders.Application`) tenía dos constructores públicos: el sin parámetros, que carga las
24 guardas AI-04 (`CreateDefaults()`), y `OrderTransitionGuardRegistry(IEnumerable<IOrderTransitionGuard>)`.
`AddOrdersInfrastructure` y `AddDispatchInfrastructure` lo registraban por tipo
(`services.TryAddSingleton<OrderTransitionGuardRegistry>()`). Microsoft.Extensions.DependencyInjection activa el
constructor público más largo que puede satisfacer, y un `IEnumerable<T>` siempre se satisface (vacío si no hay
registros). Ningún módulo registra `IOrderTransitionGuard`, así que la API componía un registro **sin guardas**.

Existe desde el primer commit de ORD-002 (`78328a1`, 2026-07-23), que introdujo a la vez ambos constructores y el
registro por tipo.

## Impacto

- `transitionOrder` en la API (todas las transiciones manuales, incluido "Cerrar orden", y las del PWA del conductor)
  no evaluaba ninguna guarda AI-04: quote y aceptación del pagador, mercancía restringida, asignación vigente y
  elegible, pruebas de recolección y de entrega, COD registrado o conciliado, incidencias abiertas, conciliación
  financiera, ventana de reclamación, motivos de cancelación y de reclamo, custodia, incidencia del intento fallido y
  su siguiente acción. Seguían vigentes la matriz de estados, la versión optimista, la autorización por rol y MFA, RLS
  y la validación de entrada.
- Dispatch: la asignación propia (`PostgreSqlAssignmentToOrderCoordinator`) y la aceptación de oferta externa
  (`PostgreSqlExternalOfferService`) evaluaban el registro vacío. Ambos flujos entregan a las guardas de `ASSIGNED` un
  snapshot constante (`AssignmentGuardSnapshot(true, true, true, true)`) después de sus propias comprobaciones de
  elegibilidad, capacidad y costo, y solo parten de `READY_FOR_PICKUP` o `RESCHEDULED`; por eso no hubo diferencia
  observable y ninguna petición puede producir ahí un rechazo de guarda: allí la restauración es defensa en profundidad.
- El Worker (cierre automático, ORD-AUTO-CLOSE) no estaba afectado: PR #213 ya registraba una fábrica.
- Las pruebas no lo detectaron: construían `new OrderTransitionGuardRegistry()` directamente y los hosts HTTP sustituían
  el servicio de transición por un stub; ninguna resolvía el registro desde la composición de la API.

## Corrección

1. El constructor `IEnumerable<IOrderTransitionGuard>` pasa a `internal` (`InternalsVisibleTo("Paqueteria.UnitTests")`
   para la prueba de unicidad y orden). El único constructor público es el de las guardas AI-04: ningún contenedor
   puede volver a componer el registro sin ellas, aunque se registre por tipo.
2. Todas las composiciones registran la misma fábrica, `TryAddSingleton(static _ => new OrderTransitionGuardRegistry())`:
   Orders con un único helper privado (`AddOrderTransitionGuardRegistry`) que usan la API (`AddOrdersInfrastructure`) y
   el Worker (`AddOrdersAutoClose`); Dispatch con la misma línea, porque `Dispatch.Infrastructure` no puede referenciar
   `Orders.Infrastructure` y `Orders.Application` no depende de la inyección de dependencias.
3. Nada más cambia: las mismas 24 guardas, en el mismo orden, con los mismos códigos AI-05.

## Pruebas

| Nivel | Clase | Qué fija |
| --- | --- | --- |
| Unitaria | `OrderTransitionGuardRegistryCompositionTests` | un solo constructor público, sin parámetros; un registro por tipo compone las 24 guardas; `AddOrdersInfrastructure`, `AddDispatchInfrastructure` y ambos en el orden de `Program.cs` entregan ese registro a la transición, al coordinador de asignación y a las ofertas externas |
| Hosts reales | `OrderTransitionGuardHostCompositionTests` | `Program` (proveedores PostgreSQL de Orders, Drivers y Dispatch): `IOrderTransitionService`, `IAssignmentService` e `IExternalOfferService` tienen el registro completo; el Worker, en `IOrderSystemTransitionService` |
| API + PostgreSQL | `OrderTransitionGuardsPostgreSqlHttpTests` | `DELIVERED -> CLOSED` con incidencia `OPEN`: 409 `UNRESOLVED_INCIDENT` sin escribir nada y, resuelta la incidencia por la API, el mismo cierre da 200 `CLOSED`; `DELIVERING -> DELIVERED` sin prueba: 409 `DELIVERY_PROOF_REQUIRED`; asignación propia por la API: 201 y `ASSIGNED`, con el registro completo en el coordinador de ese host |

Con el código anterior estas pruebas fallan: el registro de la API tiene 0 guardas y la API responde 200 `CLOSED` con
la incidencia abierta y 200 `DELIVERED` sin prueba de entrega.

## Riesgo residual

- Datos: un entorno con datos reales pudo registrar transiciones manuales sin guardas entre el 2026-07-23 y este cambio
  (por ejemplo órdenes cerradas con incidencias abiertas o COD sin conciliar, o entregadas sin prueba). Revisarlo es
  una decisión del owner; esta corrección no toca datos.
- El mapa de evidencia rel-000 de ORD-002 sigue citando solo la prueba de contrato PostgreSQL; puede añadirse la prueba
  de API como evidencia.

## Rollback

Revertir el commit. No hay migraciones ni datos que deshacer; volvería el defecto.
