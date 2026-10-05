# UI-001 fase 2D: código de la regla incumplida en `transitionOrder`

Decisión: `ORD-002-GUARD-CODES-2026-10-05` (literal del owner: "Sí a los 5 grupos de estado, avanza con la fase 2").
La fase 2 incluye "Devolver el código de la regla incumplida a quien tiene acceso, sin romper el 404 uniforme hacia
otros tenants". ADR: `docs/adr/ADR-ORD-002-TRANSITION-REJECTION-CODES.md`.

## API

`POST /api/v1/orders/{orderId}/transitions` responde, cuando corresponde, un 409 con `code`:

```json
{ "type": "…", "title": "Conflict.", "status": 409,
  "code": "PICKUP_PROOF_REQUIRED", "traceId": "…" }
```

- Enum cerrado en AI-05 `TransitionConflictProblem.code`: `OFFLINE_OPERATION_EXPIRED` (ya existía) y 23 códigos de
  regla. Mapa guarda AI-04 → código publicado como `x-ord-002-guard-codes`.
- Único lugar del mapeo: `Orders.Application.Orders.OrderTransitionRejectionCodes` (`ForRule` para versión y matriz,
  `ForGuard` para las guardas de `OrderTransitionGuardRegistry`). Las reglas no cambiaron.
- Quién recibe el código: solo si la orden se bloqueó como orden de la organización seleccionada (dueña) y
  `IOrderTransitionAuthorizer.HoldsTransitionCapability` es verdadero: DISPATCHER, PLATFORM_ADMIN con MFA, DRIVER con la
  asignación ACCEPTED/ACTIVE de esa orden.
- Sin código, idéntico a antes: forma inválida, idempotencia, concurrencia, orden inexistente/ajena/solo operada,
  VIEWER, PLATFORM_ADMIN sin MFA, DRIVER sin la asignación. `transitionOrder` nunca respondió 404; el "404 uniforme" de
  la propuesta es este 409 uniforme sin código.
- Idempotencia sin cambios: un rechazo no guarda nada; reintentar con la misma llave reevalúa; solo un 200 se repite.
- El endpoint solo emite valores del enum (`OrderTransitionRejectionCodes.IsDefined`).

| Origen | Código |
| --- | --- |
| versión distinta | VERSION_CONFLICT |
| arista inexistente / versión agotada | TRANSITION_NOT_ALLOWED |
| RETURNED, CLAIM_RESOLVED, CANCELLED | ORDER_TERMINAL |
| CLOSED finalizada | ORDER_FINALIZED |
| ventana de reclamación vencida (matriz o guarda) | CLAIM_WINDOW_CLOSED |
| guardas AI-04 | ver `x-ord-002-guard-codes` en AI-05 |

## Web

- `operations/contracts/order-transitions.ts`: `transitionRejectionMessages` (un mensaje es-MX por código) y
  `transitionRejectionMessage(code)`; código ausente, mal formado o desconocido → mensaje genérico.
- `OrderTransitionController` (panel "Siguiente paso") usa el helper para los 409; sigue recargando la orden después.
- PWA del repartidor: la cola offline marca cualquier 409 de transición como `VERSION_CONFLICT` y persiste ese valor en
  IndexedDB (`DriverOperationSafeError`, `schemaVersion: 1`). Mostrar el código exigiría ampliar ese esquema
  persistido; queda fuera de esta fase para no cambiar la semántica offline (`DriverSyncApiError.publicCode` ya trae el
  código si se decide hacerlo).

## Pruebas

- Unit: `OrderTransitionRejectionCodesTests` (cada guarda como primer fallo → su código; mapa completo; capacidad).
- PostgreSQL: `OrdersTransitionPostgreSqlContractTests.Rejection_codes_follow_the_version_matrix_and_real_guard_reads`
  y `Rejection_codes_stay_hidden_from_foreign_orders_and_callers_without_the_capability`.
- HTTP: `OrderTransitionHttpTests` (códigos al dueño con capacidad; cuerpo idéntico sin código para orden
  inexistente/ajena y VIEWER).
- Contrato: `OrdersOpenApiImplementationTests` (enum y mapa AI-05 = implementación, guardas AI-04 cubiertas).
- Web: `order-transitions.test.ts`, `order-transition-controller.test.ts`, `order-actions-api.test.ts`.
