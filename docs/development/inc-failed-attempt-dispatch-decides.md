# El despacho decide el siguiente paso de un intento fallido

Decisión: `INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11` (fila ADR en `docs/normative/v0.6/decision-log.md`; cambio
MAJOR según AI-01 §7). Fase 3 de la propuesta UI-001, aprobada con "avanza con la fase 3". Respuestas literales del
2026-10-11:

- "Despacho decide (Recommended)": el repartidor solo manda motivo y foto. La orden queda en "Requiere atención" y el
  despachador elige reprogramar, reintentar hoy o regresar. Cuando el reporte lo hace despacho, sigue como hoy.
- "Lista cerrada (Recommended)": "Otro…" muestra "Acceso restringido", "Paquete dañado" y "Riesgo de seguridad". El
  sistema arma la descripción con el motivo; no queda texto libre en el teléfono.
- "Según el motivo (Recommended)": alta (8 h) para "Riesgo de seguridad" y "Paquete dañado"; media (24 h) para los
  demás.

Esta entrega cubre contrato, servidor y la pantalla del despachador. La pantalla "No se pudo entregar" de la PWA del
repartidor y su cola sin conexión quedan para una entrega posterior que se construye sobre esta API
([secuencia](#secuencia-de-api-para-la-pwa-del-repartidor)).

## Quién reporta decide qué manda (`openIncident`)

`POST /api/v1/orders/{orderId}/incidents` no cambia de ruta, permisos ni respuestas. Cambia la forma del cuerpo según
quién reporta (AI-05 `OpenIncidentRequest.x-reporter-shape`):

| Quien reporta | Manda | El servidor guarda |
| --- | --- | --- |
| Despacho: DISPATCHER, o PLATFORM_ADMIN con MFA (también si además es el repartidor asignado) | `type`, `severity`, `description`, `reason_code`, `next_action`, `occurred_at`, `evidence_proof_ids`, como hoy | lo que mandó, como hoy |
| DRIVER autorizado solo por su asignación ACCEPTED o ACTIVE | `type`, `reason_code`, `occurred_at`, `evidence_proof_ids` | severidad por motivo, descripción fija cifrada y `next_action` NULL |

- El repartidor **omite** `severity`, `description` y `next_action` (un `null` JSON cuenta como omitido). Si manda
  cualquiera de los tres, aunque sea el valor que el servidor derivaría, recibe 409 `INVALID_REQUEST`. Se eligió
  "ausente" y no "igual al derivado" para que un reporte encolado sin conexión nunca choque con un cambio posterior de
  la regla de severidad.
- El despacho sigue obligado a mandar los tres: sin ellos recibe 409 `INVALID_REQUEST`.
- Un cuerpo que mezcla las dos formas (por ejemplo `next_action` sin `description`) es 409 `INVALID_REQUEST` antes de
  abrir cualquier transacción. La forma que corresponde a quien reporta se decide después de la lectura autorizada de
  la orden (solo lectura) y antes de cifrar o escribir nada.
- Quién reporta lo decide la misma consulta que autoriza la apertura (`IncidentsSql.ReadAuthorizedOrderAsync`):
  despacho si entra por la rama DISPATCHER o PLATFORM_ADMIN con MFA; repartidor si solo entra por la rama de la
  asignación. Si la membresía cambia entre la lectura y la escritura, la petición termina en 409 `CONFLICT`.

### Lo que el servidor deriva para el repartidor

`Incidents.Domain.IncidentDriverReportPolicy`:

| `reason_code` | Etiqueta (lista cerrada) | Severidad | SLA |
| --- | --- | --- | --- |
| `RECIPIENT_ABSENT` | No había nadie | MEDIUM | 24 h |
| `ADDRESS_NOT_FOUND` | Dirección incorrecta | MEDIUM | 24 h |
| `RECIPIENT_REFUSED` | Rechazó el paquete | MEDIUM | 24 h |
| `PAYMENT_UNAVAILABLE` | No pagó el cobro | MEDIUM | 24 h |
| `ACCESS_RESTRICTED` (en "Otro…") | Acceso restringido | MEDIUM | 24 h |
| `PACKAGE_DAMAGED` (en "Otro…") | Paquete dañado | HIGH | 8 h |
| `SECURITY_RISK` (en "Otro…") | Riesgo de seguridad | HIGH | 8 h |

- El SLA sale de la severidad con la misma `IncidentOperationalPolicy` de siempre, medido desde `occurred_at`.
- La descripción es la frase fija `Intento fallido reportado por el repartidor. Motivo: <etiqueta>.`, que se protege
  con el mismo `IIncidentPiiProtector` que cualquier descripción (Key Vault en producción) y se guarda en
  `description_ciphertext` con su `pii_key_version`. Nunca llega a la auditoría, al outbox ni a los logs.
- `next_action` queda NULL: el despacho decide. La respuesta `Incident` lo publica como `"next_action": null`.
- La idempotencia hashea la petición tal como llegó (los campos omitidos como `null`), así que un reintento del mismo
  reporte reproduce la misma respuesta. Un reporte del despacho hashea exactamente como antes.
- La evidencia no cambia: de 1 a 10 pruebas de la misma orden; en DELIVERING el repartidor sube una DELIVERY_PHOTO y la
  usa como evidencia, y una prueba usada como evidencia nunca cuenta para DELIVERED (AI-04).
- Estados de apertura sin cambio: AT_PICKUP, IN_TRANSIT y DELIVERING. Un reporte en AT_PICKUP (sin custodia) solo
  podrá reprogramarse.

## Base de datos

Migración del carril Incidents `20261011000100_AllowDispatchDecidedNextAction`:

- Sube: `ALTER TABLE incidents.incidents ALTER COLUMN next_action DROP NOT NULL`. NULL significa "lo decide despacho".
  `incidents_next_action_check` (solo RESCHEDULED o RETURNING si hay valor) e
  `incidents_returning_requires_custody_check` siguen intactos. No reescribe filas ni toca permisos, políticas,
  triggers ni RLS. Es idempotente y verifica el resultado.
- Baja: restaura NOT NULL solo si ninguna incidencia deja la decisión al despacho. Si existe alguna, se niega con
  `INC_DISPATCH_DECISION_DOWNGRADE_BLOCKED` (P0001). Se eligió negar y no mapear: convertir esos NULL en una siguiente
  acción inventaría una decisión que nadie tomó, y RETURNING podría contradecir la custodia. La prueba de la baja es el
  propio `SET NOT NULL`, que revisa todas las filas aunque `paqueteria_migrator` sea NOBYPASSRLS.
- AI-06 no cambia: `next_action` no está en AI-06, la agregó la migración del módulo INC-001 (precedente NTF-001), y el
  checksum de AI-06 está fijado en la CI. AI-18 no cambia: no hay permisos nuevos.
- `ModuleMigrationCoordinator` registra el paso como el último del carril y verifica que sea solo ese cambio de
  nulabilidad con su baja que se niega; el índice de evidencia queda verificado con su propia regla.

## ORD-002: salir de FAILED_ATTEMPT

AI-04 agrega la guarda `failed_attempt_dispatch_decides` a RESCHEDULED, DELIVERING y RETURNING y amplía
`failed_attempt_next_action_respected` (`Orders.Application.Orders.FailedAttemptDecisionPolicy`):

| Incidencia que justificó el intento | DISPATCHER / PLATFORM_ADMIN con MFA | DRIVER |
| --- | --- | --- |
| `next_action` NULL (reporte del repartidor) | elige RESCHEDULED "Reprogramar", DELIVERING "Reintentar hoy" o RETURNING "Regresar" | 409 `DISPATCH_DECISION_REQUIRED` para cualquier destino |
| `next_action` RESCHEDULED (reporte del despacho) | RESCHEDULED o DELIVERING, como hoy | igual que hoy |
| `next_action` RETURNING (reporte del despacho) | solo RETURNING, como hoy | igual que hoy |
| adoptada por el backfill de INC-001 | no restringe, como hoy | no restringe, como hoy |
| no se puede leer | ninguno (`NEXT_ACTION_MISMATCH`) | ninguno |

- La guarda nueva corre en el orden 195, antes de las guardas propias del destino: al repartidor siempre se le dice
  quién decide. Al despacho le siguen aplicando la custodia para RETURNING (`custody_acquired_true`), la custodia y una
  asignación válida para el reintento (`retry_custody_acquired_true`, `retry_valid_assignment`) y todas las demás.
- El código nuevo `DISPATCH_DECISION_REQUIRED` entra al enum cerrado de AI-05 `TransitionConflictProblem.code` y a
  `x-ord-002-guard-codes`; mensaje es-MX: "El siguiente paso de este intento fallido lo decide el despacho."
- No se agrega ninguna capacidad: DISPATCHER y PLATFORM_ADMIN con MFA ya tenían `transitionOrder` sobre las tres aristas
  (`OrderTransitionAuthorizer`); `transitionOrder` está fuera de `x-capability-matrix`, que no cambia.
- `getOrder.allowed_transitions` aplica la misma política a las salidas de FAILED_ATTEMPT (con la custodia que
  necesitan RETURNING y DELIVERING), leída en la misma transacción que las demás comprobaciones. La asignación del
  reintento la sigue juzgando solo `transitionOrder`. Sin esa lectura no se lista ninguna salida.
- No es un sexto flujo de AI-13 §4: cada paso es una petición propia y la decisión es una transición ORD-002.

## Pantalla del despachador

- Detalle de la orden (`/ops/orders/:id`, AI-07 `failed_attempt`): si la orden está en FAILED_ATTEMPT, la sección
  "Intento fallido" muestra el motivo con la etiqueta de la lista cerrada, el siguiente paso ("Lo decide despacho" o el
  que registró el despacho) y "Atender antes de" (`sla_due_at`, hora de Mazatlán). Lee la incidencia más reciente de la
  orden con `listIncidents?order_id=`; quien no puede leer incidencias (VIEWER, PLATFORM_ADMIN sin MFA) no ve la
  sección y "Siguiente paso" nunca depende de esa lectura. No muestra descripción, evidencias ni identificadores.
- "Siguiente paso": desde FAILED_ATTEMPT ofrece "Reprogramar", "Reintentar hoy" y "Regresar", cada uno solo si
  `allowed_transitions` lo lista, con motivo obligatorio, ConfirmDialog, Idempotency-Key reutilizada en reintentos y
  recarga de la orden. DELIVERING no se ofrece desde ningún otro estado.
- Bandeja de incidencias: un `next_action` null se lee "Lo decide despacho"; los motivos usan la lista cerrada.
- "Requiere atención" de la bandeja ya incluía FAILED_ATTEMPT (`inbox.ts`), así que estas órdenes aparecen ahí.

## Secuencia de API para la PWA del repartidor

La entrega posterior arma "No se pudo entregar" con estas llamadas, cada una con su propia `Idempotency-Key`
reutilizada solo para reintentar el mismo contenido:

1. Con la orden en DELIVERING, `POST /api/v1/orders/{orderId}/proof-upload-sessions` con
   `{"proof_type":"DELIVERY_PHOTO","content_type":"image/jpeg","size_bytes":…,"sha256":"…","client_occurred_at":"…"}`.
2. `PUT` de los bytes de la foto a `upload_url` con los `required_headers` de la sesión (sin credenciales).
3. `POST /api/v1/orders/{orderId}/proofs` con
   `{"upload_session_id":"…","proof_type":"DELIVERY_PHOTO","captured_at":"…","sha256":"…"}` → `id` de la prueba.
4. `POST /api/v1/orders/{orderId}/incidents` con
   `{"type":"FAILED_DELIVERY_ATTEMPT","reason_code":"RECIPIENT_ABSENT","occurred_at":"…","evidence_proof_ids":["<id>"]}`,
   sin `severity`, `description` ni `next_action` → 201 `Incident` con `next_action: null`.
5. `POST /api/v1/orders/{orderId}/transitions` con
   `{"target_status":"FAILED_ATTEMPT","reason":"…","expected_version":<versión>,"metadata":{"incident_id":"<id>"},"client_occurred_at":"…"}`
   → 200, la orden queda en FAILED_ATTEMPT.

Después el repartidor no puede mover la orden (409 `DISPATCH_DECISION_REQUIRED`); el despacho decide. La foto usada
como evidencia nunca sirve para DELIVERED: un reintento ("Reintentar hoy") necesita una foto nueva. Las reglas sin
conexión de 72 horas no cambian.

## Pruebas

- Unit: `IncidentPolicyTests` (forma por quien reporta, severidad y descripción por motivo, estados de apertura),
  `OrderTransitionDomainTests` (la guarda nueva por rol y destino, las demás guardas, decisión ilegible),
  `OrderAllowedTransitionsTests` (salidas de FAILED_ATTEMPT por rol, custodia y decisión; lo listado pasa las
  guardas), `OrderTransitionRejectionCodesTests` (código nuevo).
- PostgreSQL: `OrderAttemptCustodyPostgreSqlContractTests.DispatchDecides` (reporte del repartidor cifrado y graduado,
  FAILED_ATTEMPT, bloqueo del repartidor, cada decisión del despacho, recolección fallida, forma por quien reporta,
  replay, reporte del despacho sin cambios), `IncidentDispatchDecisionMigrationPostgreSqlContractTests` (sube, baja,
  vuelve a subir y la baja se niega con una incidencia pendiente), `IncidentEvidenceIndexMigrationPostgreSqlContractTests`
  e `IncidentsUpgradePostgreSqlContractTests` ajustadas al paso nuevo.
- HTTP: `FailedAttemptDispatchDecisionHttpTests` (la secuencia de la PWA desde el reporte, el bloqueo del repartidor,
  `allowed_transitions` del despacho, cada decisión y la forma por quien reporta).
- Contrato: `IncidentsOpenApiImplementationTests` y `OrdersOpenApiImplementationTests`.
- Web: `order-transitions.test.ts`, `incident.test.ts`, `failed-attempt.test.ts`, `operations-page-policy.test.ts`.

## Rollback

- Mientras no exista ningún reporte de repartidor (ninguna incidencia con `next_action` NULL): revertir el cambio
  (servidor, contratos y web) y bajar `20261011000100_AllowDispatchDecidedNextAction`, que restaura NOT NULL.
- Con reportes de repartidor ya guardados, la baja se niega (`INC_DISPATCH_DECISION_DOWNGRADE_BLOCKED`) y el código
  anterior no sabe leer un `next_action` NULL. Revertir entonces exige que el owner decida qué hacer con esas
  incidencias (decidir un valor para cada una es inventar una decisión); mientras tanto el servidor de esta entrega
  sigue siendo el único que las lee bien.
