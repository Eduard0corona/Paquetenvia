# RTM-API-REALTIME-DISPATCHER-ROLE: credencial propia y mínima para el tiempo real de la API

Decisión: `RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12` (project owner: "Rol propio y mínimo (Recommended)"). ADR:
[`docs/adr/ADR-RTM-API-REALTIME-DISPATCHER-ROLE.md`](../adr/ADR-RTM-API-REALTIME-DISPATCHER-ROLE.md). Severidad:
BLOCKER de seguridad y privilegio mínimo (AI-01 §7); el cambio operativo del piloto es MAJOR y el ADR lo registra.

## Qué estaba roto

La API del piloto corre los despachadores de outbox de tiempo real en proceso
(`Realtime__OutboxDispatcher__Provider=PostgreSql`, RTM-002-INPROCESS-DISPATCHER-COLOCATION, GATE-013 abierto) y estos
exigían `ConnectionStrings:PaqueteriaWorker`, que la API del piloto nunca recibe de Key Vault. La API no habría
arrancado. Local y CI le daban la conexión del Worker, por eso nadie lo vio. El piloto nunca se ha desplegado.

## Modelo de amenazas

| Amenaza | Sin este cambio (opción "la del Worker") | Con `paqueteria_realtime` |
| --- | --- | --- |
| Ejecución de código en el proceso de la API | lee y escribe todas las tablas de módulo como el Worker, reclama y cierra NOTIFICATIONS y DISPATCH, purga el outbox | solo reclama, cierra y reencola mensajes REALTIME y de ubicación; lee columnas que `paqueteria_app` ya lee |
| Bug de SQL en el despachador | podría escribir cualquier tabla de módulo | sin INSERT, UPDATE ni DELETE en ninguna tabla |
| Fuga de la cadena de conexión | equivale a filtrar la del Worker | solo el rol mínimo; se rota sola con `rotate_runtime_logins` |
| Lectura entre tenants | RLS + contexto del despachador | igual: NOBYPASSRLS, FORCE RLS, una organización por transacción; `row_security=off` se rechaza |
| Acceso directo al outbox | sin SELECT/UPDATE/DELETE, pero con todo lo demás | ningún privilegio sobre las dos tablas de outbox ni USAGE en `platform` |
| Configuración equivocada (la del Worker o la de la API como "realtime") | — | no arranca: el login debe ser distinto; si no es miembro de `paqueteria_realtime`, `SET LOCAL ROLE` falla; la readiness es Unhealthy si el login puede asumir `paqueteria_worker` o `paqueteria_app` |

Fuera del modelo: un atacante con la credencial de `paqueteria_app` ya puede fijar cualquier `app.current_org_ids`; este
cambio no lo empeora, porque cada columna que lee `paqueteria_realtime` también la lee `paqueteria_app`.

## Privilegios exactos

`paqueteria_realtime NOLOGIN NOBYPASSRLS` (AI-18 lo crea sin privilegios). La lane Notifications
`20261012000100_AddRealtimeDispatcherRole` concede y verifica contra el catálogo:

- EXECUTE (lo concede el dueño, `paqueteria_outbox_executor`):
  `security.claim_realtime_outbox(text,integer,interval)`,
  `security.requeue_stale_realtime_outbox(interval,integer,integer)`,
  `security.settle_outbox(uuid,uuid,text,text,timestamptz)`,
  `security.claim_location_outbox(text,integer,interval)`,
  `security.settle_location_outbox(uuid,uuid,text,text,timestamptz)`,
  `security.requeue_stale_location_outbox(interval,integer,integer)`.
- USAGE: `security`, `orders`, `dispatch`, `drivers`, `identity`, `organizations`, `routes` (lo concede
  `paqueteria_migrator`, dueño de los esquemas). `public` (PostGIS `ST_X/ST_Y`) ya es USAGE para PUBLIC.
- SELECT por columna (53), lo que `PostgreSqlRealtimeOutboxEvidenceReader` selecciona, une o filtra:

  | Tabla | Columnas |
  | --- | --- |
  | `orders.orders` | `id, public_id, owner_org_id, operator_org_id` |
  | `orders.order_events` | `id, order_id, owner_org_id, aggregate_version, event_type, payload, public_event_code, occurred_at` |
  | `dispatch.assignments` | `id, order_id, driver_id, owner_org_id, operator_org_id, assignment_type, status` |
  | `dispatch.external_offers` | `id, owner_org_id, status, commission_cents, expires_at, version, accepted_by_driver_id` |
  | `drivers.driver_profiles` | `id, user_id, org_id, driver_type, status` |
  | `drivers.driver_positions` | `id, org_id, driver_id, point, accuracy_m, captured_at, publish_realtime` |
  | `identity.users` | `id, status` (nunca `email_ciphertext`) |
  | `organizations.organization_memberships` | `user_id, organization_id, role, status` |
  | `routes.routes` | `id, operator_org_id, driver_id, version, updated_at` |
  | `routes.route_stops` | `id, route_id, operator_org_id, sequence` |

  Las columnas que usan las políticas RLS (por ejemplo `operator_org_id` de `order_events`) no necesitan privilegio de
  columna: PostgreSQL no las exige para la expresión de la política.

Nada más: sin INSERT, UPDATE ni DELETE, sin privilegio de tabla completa, sin las tablas de outbox ni `platform`, sin
CREATE, sin privilegios por defecto, sin EXECUTE en NOTIFICATIONS, DISPATCH, UNROUTED, `claim_outbox`,
`requeue_stale_outbox`, purgas, mantenimiento ni funciones de otros ejecutores. No hereda roles; ningún rol de grupo
lo recibe; no es dueño de nada.

**Por qué columnas y no funciones definer.** Las lecturas de evidencia son las que mantienen el tiempo real cerrado
ante fallas (AI-12: la audiencia sale del estado confirmado, nunca del payload). Pasarlas a funciones `SECURITY
DEFINER` exigiría un ejecutor `BYPASSRLS` que lea entre tenants: más privilegio, no menos. Con columnas, FORCE RLS y una
sola organización en contexto, y cada columna también legible por `paqueteria_app`, la API no gana ninguna lectura.

**`settle_outbox`.** Es el cierre compartido del outbox de negocio. Solo cambia la fila cuyo `lease_token` vigente se
presenta; este rol solo recibe tokens de sus dos funciones de reclamo y no puede leer el outbox, así que en la práctica
solo cierra mensajes de tiempo real. Una variante de `settle` limitada a REALTIME queda como mejora posible.

## Código

- `OutboxDispatcherOptions.ConnectionStringName = "PaqueteriaRealtime"` y `DatabaseRole = "paqueteria_realtime"`.
- `AddRealtimeOutboxDispatchers` valida al arrancar (con el despachador PostgreSql): la cadena existe, tiene login y el
  login es distinto del de `ConnectionStrings:Paqueteria` y `ConnectionStrings:PaqueteriaWorker`. Ya no exige ni usa
  la del Worker.
- `RealtimeDispatcherConnectionFactory` (antes `RealtimeWorkerConnectionFactory`); cada transacción hace
  `SET LOCAL ROLE paqueteria_realtime` después de `BEGIN`.
- Readiness `realtime_outbox_dispatcher`: `current_user = paqueteria_realtime`, NOBYPASSRLS, las seis funciones
  ejecutables y el login sin capacidad de asumir `paqueteria_worker` ni `paqueteria_app`.
- Línea base: `DatabaseBaselineAssertions` (rol, conjunto exacto, subconjunto de `paqueteria_app`), mapa de rutinas
  E-002 (`_PLUS_RTMAPIROLE`, `paqueteria_realtime` como beneficiario de las seis rutinas), ACL del esquema `security`,
  `E002Guards.CanonicalRoles`, coordinador de migraciones y `validate_contracts.py`.
- Login local `paqueteria_local_realtime` (DevSeed y `tools/dev-platform.ps1`): la API local recibe
  `ConnectionStrings__PaqueteriaRealtime` y ya no `ConnectionStrings__PaqueteriaWorker`.

## Piloto

- Key Vault: `pg-realtime-runtime-connection` (`Username=pv_pilot_realtime`, contraseña de 64 hex, `Maximum Pool Size=5`)
  y `pg-realtime-login-verifier` (SCRAM). Los genera el workflow una vez, como los de la API y el Worker, y los rota con
  `rotate_runtime_logins`.
- `apps.bicep`: `KeyVaultSecrets__Mappings__5` de la API → `ConnectionStrings:PaqueteriaRealtime`, y *Key Vault Secrets
  User* de la identidad de la API solo sobre ese secreto. `jobs.bicep`: el job de logins lee el verificador.
- `runtime-logins` crea o renueva `pv_pilot_realtime` (`LOGIN NOINHERIT NOBYPASSRLS`, miembro solo de
  `paqueteria_realtime`).
- Guardas: P06 (mapeos y RBAC exactos) y P23 (la API mapea su conexión de tiempo real, nunca la del Worker; el workflow
  genera los tres logins).

## Pasos del owner

Ninguno manual para el primer despliegue: el workflow genera la credencial, el job de logins crea el login y la lane
concede los privilegios. Si el despliegue se corre **con** el Key Vault ya poblado por una corrida anterior a este
cambio, la primera corrida posterior detecta que faltan `pg-realtime-*` y los genera sin tocar los demás.

## Rollback

1. Operativo: `Realtime__OutboxDispatcher__Provider=Disabled` en `apps.bicep` (las pantallas pierden avisos al instante).
2. Base de datos: Down de `20261012000100_AddRealtimeDispatcherRole`; revoca cada privilegio, verifica que no queda
   ninguno y deja el rol inerte (AI-18 lo declara). No escribe ni borra filas.
3. Credencial: retirar `pv_pilot_realtime` y los secretos `pg-realtime-*` solo después de revertir la API.

## Pruebas

- PostgreSQL (`RealtimeDispatcherRolePostgreSqlContractTests`): reclamar, cerrar y reencolar REALTIME y ubicación;
  rechazo de las otras colas, mantenimiento, purgas, ejecutores, tablas de outbox y columnas no concedidas (42501);
  RLS y `row_security=off`; el login no asume otros roles; aserciones de línea base ante cada ampliación; Up, Down y
  reaplicación, por SQL y por EF sobre una base nueva.
- Integración: entrega del outbox con la conexión nueva (suites `OutboxSignalRDelivery`, PWA del repartidor y del
  tablero), readiness con login correcto, sin privilegios, con BYPASSRLS, con membresía del Worker y con la credencial
  del Worker; arranque de la API con la configuración real del piloto (`PilotApiStartupConfigurationTests`).
- Unitarias: validación de opciones (falta, credencial reutilizada, cadena inválida, despachador apagado).
- Guardas: `tools/azr-001` (P06, P23) y `tools/ci` (`realtime_dispatcher_errors`).
