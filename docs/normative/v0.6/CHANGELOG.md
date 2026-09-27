# Changelog

## Deltas del piloto publicados con su implementación — 2026-09-27

- AI-05 publica, junto con su código y sus pruebas: `listSettlements`
  (`GET /settlements`, filtros `payee_id`, `status`, `period_from` y
  `period_to`, schema `SettlementPage`); `Cache-Control: no-store` en
  `exportSettlementCsv`; `CreateLocationRequest` sin `pii_key_version`; los
  límites aprobados de aceptación (versiones de 1 a 64 caracteres de
  `^[A-Za-z0-9._-]+$`, `accepted_at` entre -72 h y +5 min del servidor) y de
  paquetes (1 a 20); los 503/409/429 que ya se emitían y los 400 de Locations
  (componente `BadRequest`).
- `reconcileCod`, `getOrderFinancials` y `getRouteFinancials` admiten FINANCE con
  MFA (`FINANCE-COD-RECONCILIATION`, `FINANCE-COD-MFA-2026-09-27`); DISPATCHER
  sigue sin MFA. Las entradas correspondientes de `x-pilot-contract-deltas` y
  `finance_operations_status` quedan como implementadas.
- AI-06/AI-18: `resolve_identity_context` exige organización `ACTIVE`, el
  timeline público se ordena por `(occurred_at, aggregate_version)` con su grant
  de bootstrap, y se agregan los índices parciales de purga y los cuatro
  operativos (`AI06-PILOT-INDEXES`). Las instalaciones existentes los reciben
  por la lane `PlatformEvolution`.
- `validate_contracts.py` acepta la matriz DSP-002 con `503`.

## AUTH-001 logout, back-channel y step-up — 2026-09-27

- AI-05: `POST /auth/logout` responde 200 con `BffLogoutResult`
  (`endSessionUrl`, `AUTH-001-RP-INITIATED-LOGOUT`); nuevo
  `POST /auth/backchannel-logout` (anónimo, `logout_token` form-urlencoded, 200 o
  400 `BackchannelLogoutError`, `AUTH-001-BACKCHANNEL-LOGOUT`); `GET /auth/login`
  acepta `mfa=required` (`AUTH-001-MFA-STEP-UP`); el callback documenta
  `access_denied` → `/login?error=access_denied`
  (`AUTH-001-ACCESS-DENIED-MESSAGE`); la respuesta `Forbidden` usa
  `ForbiddenProblem` con el código opcional `MFA_REQUIRED`.
- AI-03 §17.1 y AI-24 `bff_session`: cierre de sesión RP-initiated (única excepción
  a tokens en el navegador: `id_token_hint` en `endSessionUrl`), back-channel logout
  detrás de una interfaz de terminación de sesiones, step-up MFA, reemplazo de sesión
  en cada inicio y mensaje propio para `access_denied`.
- AI-07 `/login`: mensaje de `access_denied`, oferta "Verificar identidad" y regreso
  desde el end-session de AuthCenter.
- Registro: `AUTH-001-RP-INITIATED-LOGOUT`, `AUTH-001-BACKCHANNEL-LOGOUT`,
  `AUTH-001-MFA-STEP-UP` y `AUTH-001-ACCESS-DENIED-MESSAGE` en `decision-log.md`.

## AUTH-001 BFF — 2026-09-27

- AI-05 documenta la superficie BFF fuera de `/api/v1` (`servers: /` por ruta):
  `GET /auth/login`, `GET /signin-authcenter`, `GET /auth/session` y
  `POST /auth/logout`, con el esquema `bffSession` (cookie
  `__Host-Paquetenvia.Session`), el parámetro `X-AuthCenter-CSRF` y el esquema
  `BffSession` (`GATE-002-BFF-001`). La respuesta de autorización exige `iss`
  (RFC 9207).
- AI-07: `/login` es anónima (punto de entrada de sesión), sin aprovisionar
  (`AUTH-FIRST-LOGIN-INVITATION`).
- AI-03 §17.1 y AI-24 `bff_session` registran el patrón BFF, el almacén de
  sesiones en PostgreSQL (`BFF-SESSION-STORE-POSTGRESQL`, cambio AI-06/AI-18
  pendiente; en memoria y una sola réplica hasta entonces) y el enrutamiento del
  mismo origen del piloto (`PILOT-SAME-ORIGIN-ROUTING`).
- Registro: `AUTH-001-BFF-CONTRACT-TRANSLATION` en `decision-log.md`.

## Finance en AI-05: regla vigente y delta pendiente — 2026-09-27

- Las descripciones de `reconcileCod`, `getOrderFinancials` y
  `getRouteFinancials` vuelven a la regla implementada (DISPATCHER y
  PLATFORM_ADMIN con MFA). FINANCE con MFA (`FINANCE-COD-RECONCILIATION`,
  `FINANCE-COD-MFA-2026-09-27`) queda como delta pendiente en
  `x-pilot-contract-deltas` y en `x-capability-matrix`; lo publica
  `feature/pilot-contract-deltas` junto con el código. Corrige la deriva señalada
  en la revisión del PR #100. Paths y schemas no cambian.

## Decisiones del owner sobre los contratos del piloto — 2026-09-27

- Respuesta literal del project owner: "Bloquean el MVP 1 — Apruebo 2, 3, 4, 5,
  6". Quedan decididos `D8-OUTBOX-LANE-DISPATCH`,
  `AI12-ASSIGNMENT-TERMINAL-STATES`, `BFF-SESSION-TABLE-SHAPE`,
  `OPS-003-CLEANUP-ROLE` y `OPS-003-SERVER-72H-REJECTION`.
- AI-12, AI-13 §7.1, la adenda de ADR-004, AI-08 (OPS-003 y AUTH-001) y AI-10
  pasan de PROPOSED a DECIDED; la traducción a AI-06/AI-18 llega con su migración en el PR de implementación.
- Se elimina de AI-04 el bloque `proposed_amendments`: sus enmiendas de
  intento, custodia e incidencia se decidieron el mismo día y el PR #91 las
  escribe en AI-04 y en el decision-log.
- Respuesta literal del project owner: "Apruebo Contrato de la API (AI-05)".
  Quedan decididos `AI05-LIST-SETTLEMENTS`, `AI05-REMOVE-PII-KEY-VERSION`,
  `AI05-INPUT-LIMITS`, `AI05-DECLARE-EMITTED-ERRORS`, `AI05-TIMELINE-ORDER`
  (incluido el grant de bootstrap sobre `orders.order_events.aggregate_version`)
  y `AI05-EXPORT-NO-STORE`. La superficie de AI-05 cambia con su
  implementación, porque las `*OpenApiImplementationTests` comparan AI-05 con el
  código; este PR no modifica paths ni schemas.
- Respuestas literales del project owner: "Acepto todas las sugerencias para
  Base de datos y pagos contra entrega" y "Aprobado todo". Quedan decididos
  `AI06-PILOT-INDEXES`, `IDENTITY-ORG-ACTIVE-REQUIRED`,
  `FINANCE-COD-RECONCILIATION`, `QUOTE-NO-COD` y `AI08-PILOT-ITEMS`. Los siete
  ítems nuevos de AI-08 quedan aprobados y REL-001 pasa a depender también de
  AUTH-001, ENV-001 y UI-001. El inventario MVP-0/P0 sigue en 29.
- El owner eligió "Declararlos en AI-05 (Recomendado)" para las respuestas 400
  de Locations y del header de contexto tenant (`AI05-DECLARE-400-ERRORS`): se
  declaran sin cambiar el comportamiento, en el PR de implementación
  `feature/pilot-contract-deltas`. `PILOT-CONTRACTS-2026-09` pasa a
  `resolved_decisions` en AI-10; no queda ningún punto abierto.

## Contratos del piloto — 2026-09-26 (propuesta y decisiones)

- AI-05: `x-capability-matrix` (DECIDED D5) y `x-pilot-contract-deltas`, con
  entradas DECIDED (D6, D7) y PROPOSED (`listSettlements`, `pii_key_version`,
  límites de aceptación, códigos no declarados, orden del timeline). La
  superficie HTTP no cambia hasta que la tarea de implementación publique
  cada delta junto con su código y sus pruebas.
- AI-08: 63 ítems. Se agregan AUTH-001, ENV-001, ADP-001, UI-001, OBS-002,
  MDM-001 y TRK-002, marcados `PROPOSED`. Se incorporan criterios DECIDED en
  OPS-003 (72 h), CSV-001 y FIN-001 (D6) y SET-001 (D7).
- AI-04 y AI-12: enmiendas `PROPOSED` tomadas de los PRs #91 y #90. Las
  guards y los eventos vigentes no cambian.
- AI-04 `decided_pending_translation`, AI-13 §7.1 y AI-08 AUTH-001/ENV-001
  recogen las decisiones del 2026-09-26/27: reasignación con asignación nueva,
  primer ingreso por invitación, sesiones BFF en PostgreSQL, enrutamiento del
  mismo origen, claim `amr` y dominio `authcenter.info`. La forma exacta de la
  tabla de sesiones BFF en AI-06/AI-18 se propone en el PR.
- D7: `Cache-Control: no-store` pasa a PROPOSED porque no forma parte del texto
  decidido.
- AI-13 §7.1 y ADR-004: adenda D8 (dirección DECIDED; mecanismo PROPOSED).
- AI-10: decisión abierta `PILOT-CONTRACTS-2026-09`.
- AI-06 y AI-18 no cambian. Sus propuestas SQL se describen en el PR y
  requieren una migración antes de entrar a los archivos canónicos.

## ORD-002: reglas del intento en la máquina de estados — 2026-09-27

- AI-04 registra las reglas de ORD-002 (PR 91) aprobadas por el project owner
  ("Aprobar las 5"): la acción siguiente de la incidencia decide la salida de
  `FAILED_ATTEMPT` (guard `failed_attempt_next_action_respected`); la custodia
  solo se deriva del evento `PICKED_UP`; las pruebas valen solo para el intento
  vigente y nunca si son evidencia de una incidencia; una incidencia justifica un
  solo `FAILED_ATTEMPT`; el intento vigente empieza en la última entrada al estado
  de captura (`ORD-002-ATTEMPT-RULES-2026-09-27`, `ORD-002-ATTEMPT-BOUNDARY`).
- Se agrega `order_state_machine.guard_semantics`. Los 17 estados y las
  transiciones no cambian.

## Reconciliación de gobernanza — 2026-09-26

- Registro documental posterior de los ítems fusionados desde el 2026-08-04
  (entradas siguientes y `decision-log.md`). Cada entrada cita el PR y la
  autorización que consta en su cuerpo; no se infieren autorizaciones.
- Los merges de PRs que declaraban no incluir autorización de merge (#42, #43,
  #44, #45, #46, #59, #60, #79, #80, #81, #82) o que no la mencionan (#70,
  #71, #72, #73, #75, #83) se registran como hechos ejecutados por el project
  owner; la autorización formal queda como pregunta abierta
  `GOV-2026-09-MERGE-AUTHORIZATION`. Las fechas de merge están en UTC.
- Decisiones del project owner tomadas en la sesión de trabajo con el owner vía
  Claude Code del 2026-09-26 (texto literal donde consta):
  "Ramas + PR borrador; el owner fusiona" (`GOV-AGENT-INTEGRATION-001`);
  delegación en Claude de la revisión y fusión de PRs en verde desde el
  2026-09-26 (`GOV-AGENT-MERGE-DELEGATION-001`); "Ruta por main" para
  dependencias nuevas, elegida frente a "Relajar PR Gate"
  (`GOV-DEPENDENCY-ROUTE-001`); login AuthCenter con BFF en la API y cookie
  HttpOnly del mismo origen (`GATE-002-BFF-001`); cierre de asignaciones por
  reacción outbox de Dispatch (`D8-DISPATCH-OUTBOX-CLOSURE`); matriz de
  capacidades (`D5-CAPABILITY-MATRIX`); "el despachador declara el monto COD al
  crear la orden (y en el CSV)" (`D6-COD-PILOT`); "Solo se liquidan periodos ya
  cerrados; un total negativo se rechaza; aprobar y pagar requieren MFA; la
  exportación se audita" (`D7-SETTLEMENT-RULES`); "Una operación offline de la
  PWA puede reintentarse hasta 72 horas; después se descarta y se avisa al
  conductor. Las llaves de idempotencia se purgan después de ese plazo."
  (`OPS-003-OFFLINE-72H`).
- Decisiones del 2026-09-26/27 en la misma sesión: reasignación siempre con
  asignación nueva y cierre CANCELLED de la anterior al reprogramar
  (`D8-REASSIGNMENT-NEW-ASSIGNMENT`); primer ingreso por invitación previa
  (`AUTH-FIRST-LOGIN-INVITATION`); sesiones BFF en una tabla PostgreSQL
  (`BFF-SESSION-STORE-POSTGRESQL`); enrutamiento del mismo origen en el ingress
  de Azure (`PILOT-SAME-ORIGIN-ROUTING`); claim `amr` tras MFA en AuthCenter
  (`AUTHCENTER-AMR-MFA`); dominio `authcenter.info` (`AUTHCENTER-DOMAIN`).
- AI-05, AI-04, AI-06, AI-18 y la implementación no cambian con estas
  decisiones; hoy el código no las cumple y su traducción a contratos queda
  pendiente en `governance/pilot-contracts`.
- AI-10 incorpora `REL-000-OWNER-001` y un registro de integración post-MVP-0
  sin cambiar gates abiertos.
- `CANONICAL_SOURCE_OF_TRUTH.md` y `CLAUDE_VALIDATION_HANDOFF.md` citan los
  hashes vigentes de AI-06 y AI-18.
- No cambian AI-02, AI-04, AI-05, AI-06, AI-08, AI-12, AI-18, SQL, roles,
  migraciones ni código de producción.

## OPS-003 limpieza operativa y rechazo de operaciones offline de más de 72 h — 2026-09-27

- `OPS-003-CLEANUP-ROLE`: AI-18 agrega `paqueteria_cleanup_executor NOLOGIN BYPASSRLS`
  con grants exactos por columna (más `DELETE` de tabla sobre
  `platform.idempotency_keys`, que no tiene forma por columna) y las aserciones
  de despliegue 12 a 15. Las funciones
  `security.purge_expired_idempotency_keys(timestamptz,integer,boolean)` (piso
  fijo de 72 h) y `security.expire_proof_upload_sessions(integer)` las instala
  el lane de Custody (`20260927000100_AddOperationalCleanupExecutor`), como
  ADR-034; AI-06 no cambia. `validate_contracts.py` admite exactamente los dos
  grants `UPDATE (...)` por columna y fija los grants del nuevo rol.
- `OPS-003-SERVER-72H-REJECTION`: AI-05 agrega `x-offline-operation-age`,
  `client_occurred_at` opcional en `TransitionRequest` y
  `CreateProofUploadSessionRequest`, y las respuestas `TransitionConflict` y
  `ProofConflict` con el código `OFFLINE_OPERATION_EXPIRED`.
- AI-03 §25.2 y AI-08 (OPS-003) describen el rol, las funciones y los jobs.

## OPS-004 retención acotada del outbox — 2026-09-26 UTC (PR #81)

- Job de Worker sobre las funciones de purga aprobadas de AI-06/AI-18
  (ADR-030), deshabilitado y en dry-run por defecto; sin migración.
- El cuerpo del PR declara "Draft. Do not merge"; merge ejecutado por el
  project owner; autorización formal pendiente.

## LIF-001 finalización de ventanas de reclamación — 2026-09-26 UTC (PR #83)

- ADR-034: `paqueteria_lifecycle_executor NOLOGIN BYPASSRLS` y
  `security.finalize_expired_orders(integer)`; AI-18 y AI-03 actualizados con
  autorización normativa del project owner registrada en el PR.
- El merge no menciona autorización en el cuerpo del PR. El bridge de Azure
  no cubre el nuevo rol; no hay despliegue autorizado.

## SET-001 flujo de liquidaciones y exportación CSV — 2026-09-26 UTC (PR #82)

- AI-05 agrega siete operaciones de liquidación (41 paths, 79 schemas, 371
  refs); no existe `listSettlements`.
- El cuerpo del PR declara que no se autoriza merge ni despliegue; merge
  ejecutado por el project owner; autorización formal pendiente.

## INC-001 resolución de incidencias — 2026-09-25 UTC (PR #80)

- AI-05 agrega `resolveIncident` y `INCIDENT_STATE_CONFLICT` de forma aditiva.
- El cuerpo del PR describe el seguimiento como autorizado por el owner y
  declara "Draft. Do not merge"; autorización formal de merge pendiente.

## SET-001 integridad del ledger — 2026-09-25 UTC (PR #79)

- Primera migración de Finance (`20260925000100`) sobre las tablas canónicas
  de AI-06, sin modificar AI-05, AI-06 ni AI-18.
- El cuerpo del PR declara que merge, auto-merge y despliegue no están
  autorizados; autorización formal de merge pendiente.

## Promoción development → main — 2026-09-24 UTC (PR #75)

- Promueve FIN-001, SCL-001, INC-001 y CSV-001 a `main`.
- El cuerpo del PR no contiene declaración de autorización de merge;
  autorización formal pendiente (`GOV-2026-09-MERGE-AUTHORIZATION`).

## CSV-001 carga CSV de órdenes — 2026-09-24 UTC (PR #70)

- AI-05 agrega `previewOrderCsv` y `commitOrderCsv`. El cuerpo del PR no
  contiene declaración de autorización.

## INC-001 incidencias e intento fallido — 2026-09-24 UTC (PR #71)

- AI-05 alinea `POST /orders/{orderId}/incidents` con autorización explícita
  del owner registrada en el PR; el merge no menciona autorización.

## SCL-001 hosts stateless — 2026-09-24 UTC (PR #73)

- Key ring de Data Protection en PostgreSQL y atribución de claims por
  réplica. Requiere protector externo de claves antes de activación
  productiva. El cuerpo del PR no contiene declaración de autorización.

## FIN-001 COD y economía por orden — 2026-09-24 UTC (PR #72)

- AI-05 agrega `getOrderFinancials` y `getRouteFinancials` y alinea las dos
  operaciones COD; cambio autorizado por el owner y limitado a esas cuatro
  operaciones según el PR. El merge no menciona autorización.

## AZR-001 fase A — 2026-09-21 UTC (PR #61)

- Controles de repositorio para despliegue DEV-SYNTHETIC. Merge controlado
  autorizado por el owner según el PR, condicionado al head `e1ebce8` y a
  Foundation 35568160027 13/13.

## AZR-001 bridge DEV-SYNTHETIC y corrección E-002 — 2026-09-20 UTC (PRs #59 y #60)

- #59 registra implementación autorizada y merge no autorizado; #60 declara
  "Merge is NOT authorized". Ambos fusionados por el project owner;
  autorización formal pendiente. Azure Clean path sin ejercitar.

## SEC-003 política de entorno sintético — 2026-09-15 UTC (PR #56)

- Merge autorizado por el project owner el 2026-09-15 según el PR; no autoriza
  producción ni datos reales.

## RTE-001 rutas manuales — 2026-09-13 UTC (PR #53) y contrato — 2026-08-29 UTC (PR #46)

- #46 agrega a AI-05 las seis operaciones de rutas y alinea AI-07, AI-08 y
  AI-12 (sin autorización de merge en el cuerpo).
- #53 implementa rutas OWN-only con `merge_authorized = true` limitado a
  `415f57a`.

## EXT-001 ofertas externas — 2026-08-28 UTC (PRs #44 y #45)

- #44 agrega `GET /driver/me/external-offers` y `ExternalOffer.expires_at` a
  AI-05 y alinea AI-07/AI-08; #45 implementa las ofertas. Ambos cuerpos
  declaran "No merge authorization is included"; autorización formal pendiente.

## Entorno local de pruebas manuales — 2026-08-27 UTC (PR #43)

- Sin cambios normativos. El cuerpo declara "No merge authorization is
  included"; autorización formal pendiente.

## NTF-001 notificaciones outbox — 2026-08-26 UTC (PR #42)

- Vertical sintética `orders.created` → `OWNER_ORG_DISPATCHERS` → `IN_APP`
  según NTF-001-OWNER-001/002/003; sin cambios normativos. El cuerpo declara
  "No merge authorization is included"; autorización formal pendiente.

## Decisiones funcionales NTF-001 — 2026-08-04

- El Project owner aprobó `orders.created` como trigger inicial,
  `OWNER_ORG_DISPATCHERS` como audiencia lógica inicial e `IN_APP` como canal
  lógico inicial de la primera vertical de NTF-001.
- La vertical es exclusivamente sintética: utiliza un provider fake
  determinista, template neutral, versionado y tenant-scoped, sin fallback
  cross-tenant, conexiones externas, secretos ni PII real.
- `SENT` significa aceptación por el sink sintético, no lectura humana. No se
  autorizan email, SMS, WhatsApp, push, inbox, dashboard, endpoint, read model,
  destinatarios reales ni provider real.
- Esta actualización es únicamente documental y de gobernanza. No modifica
  AI-05, AI-06, AI-08, AI-10, AI-12 o AI-18, ni código, SQL, migrations, roles,
  tests funcionales o composición runtime.
- La implementación todavía no está autorizada; NTF-001 y MVP-1 continúan sin
  iniciar. GATE-001, GATE-004 y GATE-007 permanecen sin resolver.
- El registro prepara una autorización posterior y separada. No autoriza
  deployment, piloto, producción o go-live, y no inicia EXT-001.

## Aprobación REL-000-OWNER-001 — 2026-08-02 (registro posterior)

- El project owner declaró "Apruebo REL-000" (PR #36), limitado a
  `MVP-0_INTERNAL` con datos sintéticos.
- No autoriza piloto, producción, despliegue, go-live, PII real ni el inicio
  automático de EXT-001.
- El valor histórico `rel000_approved: false` de REL-000-DEF-001 (2026-07-31)
  se conserva; AI-10 registra la aprobación posterior como entrada separada.

## Resolución de alcance REL-000-DEF-001 — 2026-07-31

- El project owner aprobó la opción A: FIN-001 pasa completo de MVP-0 a MVP-1.
- FIN-001 conserva prioridad P0, alcance funcional y dependencias DSP-002,
  EXT-001 y RTE-001; no se crea una variante financiera reducida en MVP-0.
- El inventario MVP-0/P0 pasa de 30 a 29 elementos y REL-000 deja de depender
  normativamente de FIN-001.
- EXT-001 continúa dependiendo de REL-000 y no queda autorizado ni iniciado.
- La decisión resuelve REL-000-DEF-001, pero no aprueba REL-000,
  MVP-0_INTERNAL ni la liberación.
- Bundle emitido como `v0.6-full-canonical-sync-7-fin001-mvp1`; no cambian
  contratos funcionales, OpenAPI, SQL, roles ni SignalR.

## Cursores de resincronización RTM-002 — 2026-07-24 (registro posterior)

- Commit `4738861`: AI-05 agrega `order_id` y `aggregate_version` (int64,
  mínimo 1) obligatorios a `DriverStop` y `aggregate_version` obligatorio a la
  proyección pública de tracking.
- AI-06: `security.get_public_tracking_projection` agrega `aggregate_version` (`o.version`)
  al JSON público.
- AI-18: `paqueteria_bootstrap` recibe `SELECT` sobre la columna `version` de
  `orders.orders` (grant de columnas `id,public_id,status,version`).
- Decisiones asociadas en `decision-log.md`:
  `RTM-002-INPROCESS-DISPATCHER-COLOCATION`, `RTM-002-LOCATION-CURSOR-UTC-MS` y
  `RTM-002-STABLE-OUTBOX-ID-AS-EVENT-ID`. El cambio no tenía entrada en este
  changelog; se registra sin alterar los archivos afectados.

## Capability antes de estado persistido DSP-002 — 2026-07-24

- `DSP-002-CAPABILITY-BEFORE-PERSISTED-STATE` separa validación de forma de
  acceso productivo: `INVALID_REQUEST` puede preceder capability sin abrir la
  transacción.
- Todo request válido relee autorización tenant-aware antes del advisory lock,
  fila idempotente, evidencia de replay o recursos de orden/driver.
- Viewer, Driver y `PLATFORM_ADMIN` sin MFA reciben el mismo 403 para key
  inexistente, completada, con hash distinto o incompleta.
- Dispatcher y admin con MFA conservan creación/replay 201 y conflictos 409.
- El hash canónico, replay histórico, schema, migraciones, AI-06 y AI-18 no
  cambian.

## Visibilidad no enumerable DSP-002 — 2026-07-23

- `DSP-002-NON-ENUMERABLE-VISIBILITY` interpreta AI-04/ADR-023 de forma
  capability-first: un actor sin capacidad recibe 403 antes de resource access.
- El actor autorizado ejecuta siempre `order_packages` y
  `driver_profile_documents` antes de decidir el 404 uniforme.
- Orden/driver missing o cross-tenant comparte componente, plan lógico,
  Problem Details y rollback sin efectos.
- No se usan delays, jitter, cronómetros de seguridad, retries ficticios ni
  consultas deliberadamente costosas.
- Replay reautoriza antes de leer evidencia y sigue sin reevaluar elegibilidad.
- AI-06 y AI-18 permanecen byte-identical.

## Remediación contractual DSP-002 — 2026-07-23

- AI-05 declara 409 para conflictos DSP-002 mediante Problem Details con los
  códigos públicos `INVALID_REQUEST`, `CONFLICT`, `DRIVER_INELIGIBLE` y
  `DRIVER_DOCUMENT_EXPIRED`.
- La matriz de `assignDriver` queda 201/401/403/404/409 conforme a AI-04 y
  ADR-023; orden o conductor ausente/cross-tenant usa 404 uniforme.
- `route_id` es nullable, pero DSP-002 solo admite ausencia o `null` hasta
  RTE-001.
- El vocabulario global conserva `OWN`, `EXTERNAL`, `ALLY_CAPACITY`; DSP-002
  habilita solo `OWN`, EXT-001 reserva `EXTERNAL` y ALY-004 reserva
  `ALLY_CAPACITY`.
- AI-06 y AI-18 permanecen intactos. La adopción de Dispatch se endurece para
  rechazar drift de checks, FKs, acciones, índice parcial, RLS y policy.
- La decisión queda registrada como `DSP-002-CONTRACT-REMEDIATION`.

## Resolución de gates — 2026-07-21

- GATE-002 (proveedor de identidad) RESUELTO: se adopta AuthCenter, servidor OIDC propio, con SLA comprometido de 99.5%, hospedaje en Azure Mexico Central y custodia de llave en Azure Key Vault.
- Migración verificada de access tokens a RS256: los consumidores validan contra JWKS público sin poseer material de firma.
- Separación normativa registrada: AuthCenter autentica, Paquetería autoriza la tenencia por organización.
- Pendiente antes de producción: rotación de llaves, rotación de la llave de desarrollo previamente embebida y confirmación de topología para el SLA.
- Actualización únicamente documental: no modifica contratos normativos v0.6, SQL, roles, endpoints ni código.

## Revisión canónica v0.6 sync 3 — 2026-07-21

- ARC-002 pasa de `PARTIAL` a `DONE` después de que la remediación normativa y
  los cinco jobs del PR pasaran.
- `purge_outbox` y `purge_location_outbox` ya no usan `FOR UPDATE SKIP LOCKED`.
- El `DELETE` revalida ID, estado terminal y cutoff sobre la fila objetivo.
- ADR-030 se conserva: maintenance continúa con `SELECT,DELETE`, sin `UPDATE`.
- AI-18 permanece sin cambios y conserva su checksum.
- La ejecución canónica se alinea a PostgreSQL 18/PostGIS 3.6.
- Purga real, límites, idempotencia y concurrencia se validan en Testcontainers.
- Bundle emitido como `v0.6-full-canonical-sync-3-arc002-purge-remediation`.

## Registro de decisiones v0.7 — 2026-07-21

- ADR-032 aceptado como referencia de diseño para v0.7: tracking en vivo, `PICKUP_IN_PROGRESS` y verificación segura de entrega.
- ADR-033 aceptado como referencia de diseño para v0.7: inventario de sellos de integridad y cadena de custodia física.
- Se registran también el contrato conjunto del guard ADR-032↔ADR-033 y su plan de pruebas SQL/Testcontainers.
- Esta actualización es únicamente aditiva y documental: no modifica contratos normativos v0.6, SQL, roles, endpoints, migraciones ni código.
- La implementación continúa bloqueada por GATE-007/GATE-013, el delta normativo coordinado y la ejecución de pruebas en PostgreSQL/PostGIS real.

## v0.6 — 2026-07-20

- `pgcrypto` aislado en `extensions`; PostGIS permanece en `public` protegido.
- Token de tracking con contrato SHA-256 simétrico C#/SQL sobre UTF-8 exacto.
- Outbox business/GPS con `lease_token`, settle, stale requeue, dead-letter y purga.
- Runtime sin SELECT/UPDATE/DELETE directo sobre outbox; inserts sin `RETURNING`.
- Estados públicos normativos y timeline privado por defecto con `public_event_code`.
- `order_acceptances` RLS/append-only con canonicalización legal fija.
- Todo dinero migrado a `bigint`/OpenAPI `int64`.
- Provisioning transaccional de usuarios/organizaciones bajo RLS.
- `external_offers` recupera aceptante, fecha y versión.
- Lane GPS sin FK y con `UNIQUE(driver_position_id)`.
- OPS-004 y ADR-026 a ADR-031.
- Backlog ampliado a 56 tareas.

## v0.5 — 2026-07-20

- Roles runtime NOBYPASSRLS; bootstrap y claim cross-tenant mediante funciones de roles NOLOGIN dedicados.
- Grants append-only y mutabilidad por columna del outbox.
- Contexto tenant transaction-local, retry-safe y preparado para PgBouncer.
- Esquema físico por módulo y FKs cross-schema gobernadas.
- Cotización de uso único, `pricing_tier`, piso congelado y snapshot sin PII.
- Modelo multi-ciudad inicial.
- POD por URL firmada/cuarentena y GPS batch con lane separado.
- Política 401/403/404, COD mínimo, líneas de liquidación y ventana de reclamación.
- Backlog ampliado a 53 tareas y nuevos ADR-016 a ADR-025.

## v0.4 — 2026-07-20

- Corrige contrato Quote→Order con ubicaciones y snapshots persistidos.
- Introduce membresías y roles por organización.
- Endurece RLS con FORCE, roles separados y políticas para tablas sensibles/hijas.
- Corrige máquina de estados de cancelación, custodia y reintentos.
- Añade ingesta REST de posiciones y contrato SignalR consistente.
- Corrige OpenAPI, idempotencia, audit schema y `Order.version`.
- Corrige FK de tarifas, índice de outbox y nombres de tablas de escalabilidad.
- Corrige numeración ADR, plantilla de lectura y referencia a documentos inexistentes.
- Backlog: 48 tareas, incluyendo ARC-002 y DRV-003; OBS-001 asciende a P0.

## v0.3 — 2026-07-19

- Arquitectura y escalabilidad progresiva formalizadas.
