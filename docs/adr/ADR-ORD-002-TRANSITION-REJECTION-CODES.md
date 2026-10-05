# ADR-ORD-002: código de la regla incumplida en el 409 de transitionOrder

- Estado: aceptado (ORD-002-GUARD-CODES-2026-10-05)
- Fecha: 2026-10-05
- Alcance: `POST /api/v1/orders/{orderId}/transitions` (`transitionOrder`), "Siguiente paso" de `/ops/orders/:id`

## Contexto

La propuesta UI-001 aprobada por el project owner ("Sí a los 5 grupos de estado, avanza con la fase 2") incluye, en
la fase 2, "Devolver el código de la regla incumplida a quien tiene acceso, sin romper el 404 uniforme hacia otros
tenants", porque "El cambio de estado responde un 409 genérico; la UI no puede decir 'falta la foto de recolección'".
La propuesta lo marcó como cambio que requiere ADR y cambio en AI-05.

Hasta ahora `transitionOrder` respondía todo rechazo con el mismo 409 sin `code` (salvo
`OFFLINE_OPERATION_EXPIRED`), aunque el servidor ya decide con códigos precisos: la versión, la matriz AI-04
(`OrderTransitionRuleCode`) y las 24 guardas de `OrderTransitionGuardRegistry`, que leen cotización y aceptación,
asignación, pruebas, custodia, incidencias, COD y conciliación dentro de la misma transacción. Otros módulos (FIN-001,
SET-001, POD-001, INC-001) ya devuelven códigos estables en ProblemDetails.

`transitionOrder` nunca respondió 404. Una orden inexistente, de otra organización o que la organización seleccionada
solo opera (ORD-002 es solo del dueño, ORD-002-OPERATOR-DRIVER-EVENTS-2026-10-03) recibe el mismo 409 que cualquier
otro conflicto: esa es la respuesta uniforme que no revela existencia ajena y que esta decisión debe conservar.

## Decisión

1. AI-05 `TransitionConflictProblem.code` gana un enum cerrado de códigos de regla, después de
   `OFFLINE_OPERATION_EXPIRED`:
   - versión y matriz AI-04: `VERSION_CONFLICT` (versión distinta), `TRANSITION_NOT_ALLOWED` (arista inexistente o
     versión agotada), `ORDER_TERMINAL` (RETURNED, CLAIM_RESOLVED, CANCELLED), `ORDER_FINALIZED` (CLOSED finalizada),
     `CLAIM_WINDOW_CLOSED` (ventana de reclamación vencida);
   - un código por cada guarda AI-04 existente, publicado como `x-ord-002-guard-codes` y aplicado por
     `OrderTransitionRejectionCodes`, el único lugar del código que traduce reglas a códigos:

     | Guarda AI-04 | Código |
     | --- | --- |
     | valid_active_quote | QUOTE_NOT_VALID |
     | payer_acceptance | PAYER_ACCEPTANCE_REQUIRED |
     | restricted_goods_check | RESTRICTED_GOODS_ACK_REQUIRED |
     | eligible_driver, retry_valid_assignment | VALID_ASSIGNMENT_REQUIRED |
     | capacity_available | DRIVER_CAPACITY_EXCEEDED |
     | assignment_cost_present | ASSIGNMENT_COST_REQUIRED |
     | pickup_proof_complete | PICKUP_PROOF_REQUIRED |
     | delivery_proof_complete | DELIVERY_PROOF_REQUIRED |
     | if_cod_expected_then_cod_status_recorded_or_reconciled | COD_NOT_RECORDED |
     | no_unresolved_incident | UNRESOLVED_INCIDENT |
     | if_cod_expected_then_cod_status_reconciled | COD_NOT_RECONCILED |
     | financial_reconciliation_complete | FINANCIAL_RECONCILIATION_INCOMPLETE |
     | claim_window_ends_at_set | CLAIM_WINDOW_NOT_SET |
     | now_before_or_equal_claim_window_ends_at | CLAIM_WINDOW_CLOSED |
     | claim_reason_present, claim_resolution_reason_present, cancellation_reason_present | REASON_REQUIRED |
     | if_from_at_pickup_then_custody_not_acquired | CUSTODY_ALREADY_ACQUIRED |
     | attempt_stage_recorded, custody_acquired_recorded | INCIDENT_REQUIRED |
     | custody_acquired_true, retry_custody_acquired_true | CUSTODY_NOT_ACQUIRED |
     | failed_attempt_next_action_respected | NEXT_ACTION_MISMATCH |

   No se agrega, quita ni cambia ninguna regla, guarda o estado; las guardas siguen evaluándose en el mismo orden y el
   primer fallo decide.
2. El servicio adjunta el código solo cuando se cumplen las dos condiciones, ambas dentro de la transacción tenant:
   - la orden quedó bloqueada (`SELECT … FOR UPDATE`) con `owner_org_id = organización seleccionada` bajo RLS;
   - el actor tiene la capacidad de `transitionOrder` sobre esa orden
     (`IOrderTransitionAuthorizer.HoldsTransitionCapability`): DISPATCHER; PLATFORM_ADMIN con MFA satisfecha; DRIVER
     solo con la asignación ACCEPTED o ACTIVE de esa orden (la PWA del repartidor usa el mismo endpoint). Es la misma
     tabla de roles que `IsAuthorized`, sin la lista de aristas del repartidor; los fallos de guarda solo se alcanzan
     después de `IsAuthorized`.
3. El endpoint emite `code` solo para valores del enum cerrado; cualquier otro valor, o ninguno, es el 409 uniforme de
   siempre.

## Por qué no filtra entre tenants

- Una orden inexistente, de otra organización o solo operada no pasa el bloqueo por dueño: el servicio lanza
  `OrderUnavailable` sin código y el cuerpo es idéntico al de cualquier 409 sin código (lo prueba
  `OrderTransitionHttpTests` comparando el ProblemDetails completo, sin `traceId`, contra un rechazo de forma).
- Los rechazos de forma, idempotencia (incluida una llave reutilizada para otra orden) y concurrencia se deciden antes
  del bloqueo o fuera de él y siguen sin código.
- Quien pertenece a la organización dueña pero no tiene la capacidad (VIEWER, PLATFORM_ADMIN sin MFA, DRIVER sin la
  asignación) recibe el mismo 409 sin código que hoy: no aprende versión ni estado por esta vía.
- El código nombra una regla, nunca la evidencia: no lleva identificadores (de prueba, incidencia, asignación, COD o
  repartidor), montos, estados de otras entidades ni datos personales. Quien lo recibe ya puede leer la orden por
  `getOrder` o, si es repartidor, opera esa entrega y registra su COD.
- La consulta de membresía que decide la divulgación es la misma que ya decidía la autorización; solo se lee antes de
  la comprobación de versión, sobre una orden ya visible para el dueño, así que no agrega una diferencia observable
  para otros tenants.

## Idempotencia

Un rechazo revierte la transacción, incluida la reserva de la Idempotency-Key: no se guarda respuesta. Reintentar con
la misma llave vuelve a evaluar las reglas (y puede tener éxito si ya se cumplieron); solo un 200 completado se
reproduce, igual que antes.

## Consecuencias

- AI-05 cambia de forma aditiva (enum y descripciones); AI-07 documenta los mensajes es-MX. AI-04, AI-06 y AI-18 no
  cambian; no hay migración, rol, grant ni flujo cross-module nuevo.
- La web traduce cada código a un mensaje es-MX en un solo helper; un código desconocido usa el mensaje genérico.
- La cola offline del repartidor sigue guardando `VERSION_CONFLICT` como estado de atención: su esquema persistido en
  IndexedDB no cambia en esta decisión.
- `allowed_transitions` sigue siendo orientativo y no evalúa guardas.
- Reversión: revertir el cambio devuelve el 409 sin código; los clientes ya caen al mensaje genérico.
