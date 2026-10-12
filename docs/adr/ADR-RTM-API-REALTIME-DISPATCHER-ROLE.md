# ADR-RTM-API-REALTIME-DISPATCHER-ROLE: rol propio y mínimo para los despachadores de tiempo real de la API

- Estado: aceptado (RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12)
- Fecha: 2026-10-12
- Severidad: BLOCKER (AI-01 §7: seguridad, privilegio mínimo y aislamiento); el cambio de operación del piloto es
  MAJOR y este ADR lo registra.
- Alcance: credencial de base de datos de los despachadores de outbox de tiempo real que corren dentro de la API
  (RTM-002-INPROCESS-DISPATCHER-COLOCATION), rol AI-18, lane Notifications, Key Vault y login del piloto.

## Contexto

La API del piloto enciende los despachadores de outbox de tiempo real
(`Realtime__OutboxDispatcher__Provider=PostgreSql`, `deploy/azure/pilot/apps.bicep`). Mientras GATE-013 siga abierto,
SignalR usa el backplane InProcess, así que esos despachadores viven en el mismo proceso que los hubs
(RTM-002-INPROCESS-DISPATCHER-COLOCATION). `AddRealtimeOutboxDispatchers` exigía al arrancar
`ConnectionStrings:PaqueteriaWorker` y abría con ella todas sus conexiones (`SET LOCAL ROLE paqueteria_worker`). La API
del piloto solo recibe `ConnectionStrings:Paqueteria` de Key Vault; únicamente el Worker recibe
`pg-worker-runtime-connection`. Resultado: la API del piloto no habría arrancado. En local y en CI la API sí recibía la
conexión del Worker, por eso CI pasaba. El piloto nunca se ha desplegado.

Darle a la API la credencial del Worker la habría ampliado mucho: `paqueteria_worker` tiene SELECT, INSERT, UPDATE y
DELETE sobre todas las tablas de los esquemas de módulo (AI-18), ejecuta las colas NOTIFICATIONS, DISPATCH y UNROUTED,
las purgas y los demás ejecutores del Worker.

Pregunta al project owner (2026-10-12), literal: "Bloqueo del primer despliegue: la API del piloto envía los avisos en
tiempo real (tablero, repartidor) leyendo la cola con la credencial del Worker, que hoy no tiene, así que no
arrancaría. ¿Cómo lo resolvemos?"

Respuesta literal: "Rol propio y mínimo (Recommended)". Texto de la opción: "Un rol de base de datos nuevo que solo
puede tomar y cerrar mensajes de tiempo real, con su propio secreto en Key Vault para la API (ADR + AI-18 + migración).
La API no gana ningún otro permiso."

Opciones no elegidas:

- "Darle a la API la del Worker" ("La API lee también el secreto del Worker. Es rápido, pero la API queda con todos
  los permisos del Worker (otras colas y mantenimiento).").
- "Sin tiempo real en el piloto" ("Se apaga el despachador en la API; las pantallas se actualizan por consulta cada
  30 s y el repartidor no recibe avisos al instante.").

## Inventario: lo que corren los despachadores

Todo lo que se construía con la fábrica de conexiones del Worker (`RealtimeWorkerConnectionFactory`, ahora
`RealtimeDispatcherConnectionFactory`), siempre en una transacción explícita con `SET LOCAL ROLE` tras `BEGIN`:

| Componente | SQL | Antes lo concedía |
| --- | --- | --- |
| `PostgreSqlRealtimeOutboxStore` | `security.claim_realtime_outbox(text,integer,interval)`, `security.requeue_stale_realtime_outbox(interval,integer,integer)` | lane Notifications NTF-001: EXECUTE solo a `paqueteria_worker` |
| `PostgreSqlRealtimeOutboxStore` | `security.settle_outbox(uuid,uuid,text,text,timestamptz)`, `security.claim_location_outbox(text,integer,interval)`, `security.settle_location_outbox(uuid,uuid,text,text,timestamptz)`, `security.requeue_stale_location_outbox(interval,integer,integer)` | AI-06 (funciones) y AI-18 (EXECUTE solo a `paqueteria_worker`); dueño `paqueteria_outbox_executor` |
| `PostgreSqlRealtimeOutboxEvidenceReader` | lecturas tenant (`set_config('app.current_user_id','',true)` y `app.current_org_ids` = una organización) de `orders.orders`, `orders.order_events`, `dispatch.assignments`, `dispatch.external_offers`, `drivers.driver_profiles`, `drivers.driver_positions` (con `public.ST_X/ST_Y`), `identity.users`, `organizations.organization_memberships`, `routes.routes`, `routes.route_stops` | AI-18: SELECT/INSERT/UPDATE/DELETE de tabla completa a `paqueteria_worker` |
| `RealtimeOutboxHealthCheck` | `pg_roles`, `to_regprocedure`, `has_function_privilege` de las seis funciones | catálogos públicos |

Ninguno hace SELECT, UPDATE ni DELETE directo sobre `platform.outbox_events` o `platform.location_outbox_events`
(invariante 3): reclamar, cerrar y reencolar se hace solo con las funciones con `lease_token`. Las lecturas de
evidencia no son funciones: son consultas bajo FORCE RLS que vuelven a derivar cada audiencia desde el estado
confirmado (AI-12: nunca se confía en el payload). Nada más en la API usa `ConnectionStrings:PaqueteriaWorker`.

## Decisión

1. **Rol `paqueteria_realtime NOLOGIN NOBYPASSRLS`**, como `paqueteria_app` y `paqueteria_worker`. AI-18 lo crea sin
   ningún privilegio, lo revoca de `paqueteria_app`, `paqueteria_worker` y `paqueteria_bootstrap` y lo agrega a la
   lista de roles que no pueden heredar roles privilegiados. No hereda ningún rol, ningún rol de grupo lo recibe, no es
   dueño de nada y no tiene privilegios por defecto.
2. **Privilegios exactos, concedidos por la lane Notifications** (`20261012000100_AddRealtimeDispatcherRole`), que es
   dueña de las funciones REALTIME (NTF-001) y corre después de todas las lanes que tocan las tablas leídas:
   - EXECUTE en las seis funciones de la tabla (las concede su dueño, `paqueteria_outbox_executor`);
   - USAGE en `security`, `orders`, `dispatch`, `drivers`, `identity`, `organizations` y `routes`;
   - SELECT por columna, exactamente en las 53 columnas que las lecturas de evidencia seleccionan, unen o filtran
     (lista en AI-18 y en `DatabaseBaselineAssertions.RealtimeDispatcherColumnGrants`), ninguna con GRANT OPTION.
   Sin INSERT, UPDATE ni DELETE en ninguna tabla, sin privilegio de tabla completa, sin nada sobre las dos tablas de
   outbox ni USAGE en `platform`, sin CREATE y sin EXECUTE en las colas NOTIFICATIONS, DISPATCH o UNROUTED, en
   `claim_outbox`/`requeue_stale_outbox`, en purgas, mantenimiento ni funciones de otros ejecutores.
3. **Por qué SELECT por columna y no funciones definer.** Las lecturas de evidencia corren bajo FORCE RLS con una sola
   organización en contexto; convertirlas en funciones `SECURITY DEFINER` exigiría un ejecutor `BYPASSRLS` que lea
   entre tenants, más amplio que lo que se concede aquí. Cada columna concedida también la puede leer
   `paqueteria_app`: la migración y las aserciones de baseline lo verifican, así que **la API no gana ninguna lectura**.
   Su única capacidad nueva es tomar y cerrar mensajes de tiempo real, que es lo que el owner aprobó.
4. **`settle_outbox` compartido.** Es la función canónica de cierre del outbox de negocio. Solo cambia la fila cuyo
   `lease_token` vigente presenta el llamador, y este rol solo obtiene tokens de `claim_realtime_outbox` y
   `claim_location_outbox`; no puede leer el outbox para conocer los de otras colas. Por eso en la práctica solo
   cierra mensajes de tiempo real. Una versión de `settle` limitada a REALTIME sería una defensa adicional; queda
   anotada como riesgo residual, no como bloqueo.
5. **Credencial propia.** `ConnectionStrings:PaqueteriaRealtime` (validada al arrancar cuando el despachador PostgreSql
   está encendido; su login debe existir, parsear y ser distinto del de `ConnectionStrings:Paqueteria` y
   `ConnectionStrings:PaqueteriaWorker`). Cada transacción hace `SET LOCAL ROLE paqueteria_realtime`; un login que no
   es miembro falla antes de cualquier sentencia. La readiness `realtime_outbox_dispatcher` exige
   `current_user = paqueteria_realtime`, NOBYPASSRLS, las seis funciones ejecutables y que el login **no** pueda
   asumir `paqueteria_worker` ni `paqueteria_app`.
6. **Piloto.** El workflow genera, como para la API y el Worker, `pv_pilot_realtime` (contraseña de 64 hex, nunca
   impresa), guarda `pg-realtime-runtime-connection` y `pg-realtime-login-verifier` en Key Vault; el job de logins crea el
   login o renueva su clave (`LOGIN NOINHERIT NOBYPASSRLS`, miembro solo de `paqueteria_realtime`) desde el verificador SCRAM.
   La identidad de la API tiene *Key Vault Secrets User* solo sobre `pg-realtime-runtime-connection` y lo mapea a
   `ConnectionStrings:PaqueteriaRealtime`; nunca mapea `ConnectionStrings:PaqueteriaWorker`. Guardas P06 y P23.

No es un flujo atómico cross-module nuevo (AI-13 §4) ni cambia AI-04, AI-05, AI-06 ni AI-12.

## Consecuencias

- La API del piloto arranca con su despachador de tiempo real sin recibir la credencial del Worker.
- Local, compose y CI usan un tercer login de runtime (`paqueteria_local_realtime`, `*_realtime_login_test`,
  `paqueteria_sec002_realtime`).
- Las aserciones de baseline, el mapa de rutinas E-002 y `validate_contracts.py` fijan el conjunto exacto y fallan
  cerrado ante cualquier ampliación.

## Rollback

- Operativo: `Realtime__OutboxDispatcher__Provider=Disabled` en la API (las pantallas quedan sin avisos al instante).
- Base de datos: el Down de `20261012000100_AddRealtimeDispatcherRole` revoca cada privilegio que concedió (cada uno por
  su otorgante) y verifica que el rol quedó sin nada; el rol permanece porque AI-18 lo declara, inerte. Ninguna fila se
  escribe ni se borra.
- Credencial: borrar el login `pv_pilot_realtime` y los secretos `pg-realtime-*` solo después de revertir la API.
- No se revierte a la credencial del Worker: esa fue la opción no elegida.

## Riesgos residuales

- `settle_outbox` no comprueba la cola; ver punto 4 de la decisión.
- En Azure la lane Notifications solo se migra desde historial vacío (`MigrateNotificationsAsync` con el puente E-002
  exige el estado `Pending`); limitación previa a este cambio. El piloto no se ha desplegado, así que su primer
  despliegue aplica la lane completa.
- Las lecturas de evidencia siguen viendo, bajo RLS, las columnas que ven hoy (por ejemplo `orders.order_events.payload`
  y `dispatch.external_offers.commission_cents`); son las mismas que ya lee `paqueteria_app`.
