# ADR-RTE-001-ROUTE-ASSIGNMENT-FLOW6: parada de ruta ↔ `assignment.route_id` como flujo atómico 6

- Estado: aceptado (RTE-001-ROUTING-ASSIGNMENT-FLOW6-2026-10-20)
- Fecha: 2026-10-20
- Alcance: AI-13 §4 y §15, AI-03 §25.9, AI-04 `transaction_coordinator_allowlist`, AI-01 §4.18; módulo Routes
  (`addRouteStop`, `removeRouteStop`); prueba de arquitectura `CrossSchemaWriteCatalogTests`

## Contexto

Al agregar o quitar una parada, Routes escribe en `dispatch.assignments`, una tabla de Dispatch, dentro de su propia
transacción (`src/Modules/Routing/Routing.Infrastructure/Routes/PostgreSqlRouteService.Persistence.cs`):
`LinkAssignmentAsync` hace `UPDATE dispatch.assignments SET route_id=@route` y `UnlinkAssignmentAsync` hace
`UPDATE dispatch.assignments SET route_id=NULL`. La normativa lo exige: AI-08 RTE-001 pide "route stop and
assignment.route_id are updated atomically", AI-05 `addRouteStop` y `removeRouteStop` lo describen en la misma
transacción, AI-06 declara la FK `dispatch.assignments.route_id -> routes.routes`, y el owner fusionó el código
(RTE-001-ROUTE-CONTRACT-PR46, RTE-001-MERGE-PR53). Pero ningún ADR ni fila de decisión lo registraba como flujo
cross-module: AI-13 §4 enumeraba cinco flujos, AI-03 §25.9 decía "Solo cinco flujos" y AI-01 §4.18 prohíbe un sexto sin
ADR y pruebas. Tampoco existía la prueba que AI-13 §15 y AI-03 §25.9 piden: las guardas parciales (una de Orders hacia
Pricing, una constante de un coordinador de Dispatch y el catálogo de nombres de esquema) no detectan una escritura
nueva en el esquema de otro módulo (evaluación del 2026-10-19, R3/BA-02).

Pregunta al project owner, literal (2026-10-20, pregunta G1): "Rutas: al agregar o quitar una parada, el módulo de
Rutas escribe directo en las asignaciones de Despacho, en la misma transacción. Lo exige RTE-001, pero no está entre los
5 flujos permitidos (AI-13 §4) y no hay prueba que detecte escrituras nuevas entre módulos. ¿Qué hacemos?". Respuesta
literal: "Registrarlo como flujo 6 (Recommended)" (descripción de la opción: "Fila de decisión y ADR que lo agregan a
la lista, más la prueba que cataloga cada escritura entre módulos y falla con cualquiera nueva. Sin cambio de
comportamiento."). No elegida: "Separarlo con el outbox" ("Rutas solo escribe la ruta y Despacho actualiza la
asignación después, por evento. Ya no sería atómico como pide RTE-001 (cambio de contrato) y es más trabajo.").

## Decisión

1. **Flujo 6 de AI-13 §4: route stop ↔ assignment.route_id (RTE-001).** Lo coordina Routes (`PostgreSqlRouteService`).
   En una sola transacción tenant:
   - `addRouteStop` inserta la parada en `routes.route_stops` y fija `dispatch.assignments.route_id` en la asignación
     `OWN` `ACCEPTED`/`ACTIVE` de la orden, solo si aún no tiene ruta;
   - `removeRouteStop` borra la parada, compacta la secuencia de `routes.route_stops` y limpia ese `route_id`;
   - ambas incrementan `routes.routes.version` y escriben auditoría, outbox `routes.route-changed` e idempotencia en
     `platform`.

   Su única escritura en otro módulo es la columna `dispatch.assignments.route_id`. Las lecturas con bloqueo de
   `orders.orders` y `dispatch.assignments` no son escrituras. Nombre en AI-04 `transaction_coordinator_allowlist`:
   `route_stop_to_assignment_route_id`. Los otros cinco flujos no cambian.
2. **Cuenta en la normativa.** AI-13 §4 lista seis flujos y §15 pide la lista blanca de seis; AI-03 §25.9 dice "Solo
   seis flujos"; AI-01 §4.18 y AI-11 prohíben ahora un séptimo sin ADR y pruebas.
3. **Catálogo ejecutable.** `tests/Paqueteria.ArchitectureTests/CrossSchemaWriteCatalogTests.cs` lee el SQL de
   runtime de `src/` (sin migraciones ni la línea base de base de datos) y encuentra cada `INSERT INTO`, `UPDATE`,
   `DELETE FROM` o `MERGE INTO` con esquema calificado (con o sin comillas, con o sin `ONLY`) en un esquema de módulo
   distinto del propio del archivo. El esquema propio sale del módulo de AI-03 §8 en `DatabaseSchemaCatalog` (Routing
   es Routes; Realtime, la API, el Worker y los BuildingBlocks no tienen esquema propio); los esquemas compartidos
   `platform`, `security` y `extensions` no son de ningún módulo. Compara el conjunto (archivo, sentencia, tabla), una
   entrada por sentencia, con un catálogo en el que cada entrada nombra su flujo de AI-04 o su ADR. Falla ante un sitio
   nuevo, un sitio que ya no existe o un sitio sin flujo, con un mensaje que pide el ADR. También comprueba que los
   flujos permitidos son exactamente los seis de AI-04 y AI-13 §4, que cada directorio de `src/Modules` tiene su
   esquema, que ningún código de runtime cambia el `search_path` y que ningún destino de escritura se arma en tiempo
   de ejecución.
4. **Los ocho sitios de hoy** (los mismos que encontró la evaluación):

   | Archivo | Sentencia | Permitido por |
   | --- | --- | --- |
   | Orders `QuoteSnapshotToOrderCoordinator` | `UPDATE pricing.quotes` | flujo 1 `quote_snapshot_to_order` |
   | Dispatch `PostgreSqlAssignmentToOrderCoordinator` | `UPDATE orders.orders` | flujo 2 `assignment_to_order_status_event` |
   | Dispatch `PostgreSqlAssignmentToOrderCoordinator` | `INSERT INTO orders.order_events` | flujo 2 |
   | Dispatch `PostgreSqlExternalOfferService` | `UPDATE orders.orders` | flujo 5 `external_offer_acceptance_to_assignment` |
   | Dispatch `PostgreSqlExternalOfferService` | `INSERT INTO orders.order_events` | flujo 5 |
   | Routes `PostgreSqlRouteService.Persistence` (`LinkAssignmentAsync`) | `UPDATE dispatch.assignments` | flujo 6 `route_stop_to_assignment_route_id` |
   | Routes `PostgreSqlRouteService.Persistence` (`UnlinkAssignmentAsync`) | `UPDATE dispatch.assignments` | flujo 6 |
   | Organizations `PostgreSqlInitialOrganizationProvisioner` | `INSERT INTO identity.users` | ADR-031 (provisioning, AI-01 §4.4) |

   Los flujos 3 y 4 no tienen hoy escrituras SQL directas en el esquema de otro módulo: Orders solo lee
   `custody.proofs` y `finance.cod_transactions` como guardas.

## Consecuencias

- Sin cambio de comportamiento: no cambia código de `src/`, SQL, migraciones, roles, permisos, RLS, outbox, AI-05,
  AI-06, AI-12 ni AI-18.
- Una escritura nueva en el esquema de otro módulo rompe la prueba hasta que un ADR la agregue a AI-13 §4 y a AI-04, o
  hasta que se catalogue con un flujo o ADR que ya la cubra (por ejemplo, si una escritura del flujo 2 se mueve a otro
  archivo de Dispatch). Quitar una escritura también rompe la prueba hasta borrar su entrada.
- Límites conocidos: la prueba lee el texto SQL. No cubre escrituras por EF (`SaveChanges`) sobre un mapeo de otro
  esquema; hoy el único mapeo de ese tipo es la proyección de solo lectura `clients.client_accounts` de Pricing
  (`AsNoTracking`, `ExcludeFromMigrations`). Tampoco cubre lo que escriben las funciones `SECURITY DEFINER` de
  `security`, que gobiernan AI-18 y sus ADR, ni SQL armado a trozos que separe el verbo del nombre de la tabla.

## Alternativas descartadas

- **Separarlo con el outbox.** Rutas escribiría solo la ruta y Dispatch actualizaría la asignación después, por evento.
  Ya no sería atómico como pide RTE-001 (cambio de contrato) y es más trabajo. El owner no la eligió.
- **Dejarlo sin registrar.** La normativa seguiría contradiciéndose y nada detectaría la siguiente escritura entre
  módulos.

## Despliegue y rollback

- Nada que desplegar: el cambio es normativo, documental y una prueba de arquitectura.
- Rollback: revertir el PR. El código de Routes no cambia en ningún sentido; volver a cinco flujos exigiría otra
  decisión del owner, porque RTE-001 sigue pidiendo la escritura atómica.
