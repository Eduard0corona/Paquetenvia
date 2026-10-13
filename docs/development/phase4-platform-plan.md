# UI-001 fase 4: plataforma (plan de PR)

Decisiones (`decision-log.md`, 2026-10-12): `PHASE4-PLATFORM-APPROVAL-2026-10-12` (fase 4 aprobada: "Sí, avanza con
la fase 4 (Recommended)"), `OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12` ("Application Insights (Recommended)"),
`SEC-004-RATE-LIMITS-2026-10-12` ("Lista corta y generosa (Recommended)") y `OBS-003-TENANT-PSEUDONYM-2026-10-12`
("Seudónimo de empresa (Recommended)"). Las preguntas, respuestas y opciones no elegidas están literales en esas filas.

Backlog (AI-08): OBS-003 (trazas y correlación), SEC-004 (límites de tasa), SCL-007 (un pool por rol y espera
adaptativa del outbox) y OPS-005 (pruebas de punta a punta de la plataforma). Todos MVP-1, P1, compuerta
`owner_technology_operations`.

Este documento solo ordena el trabajo. El PR que lo agrega cambia documentos y nada de código.

## Punto de partida

Revisión de solo lectura de `development` antes de la fase 4:

- OBS-002 está implementado: logs JSON por consola, eventos 4601-4603, cinco alertas de logs, un libro de trabajo y la
  prueba de la lista permitida de dimensiones (`obs-002-pilot-observability.md`).
- Nada exporta telemetría: los `Meter` existen, pero nadie los exporta.
- No hay `ActivitySource`. El único identificador de correlación es `HttpContext.TraceIdentifier`, que hoy va a
  `platform.audit_logs.request_id` y al `traceId` de los problemas de Identity.
- Solo hay límites de tasa en el seguimiento público, en los hubs de SignalR y en la ingesta de GPS. AI-05 declara
  429 en tres operaciones: `publicTracking`, `publishDriverLocation` y `requestRecipientCall`.
  `HttpSurfaceOpenApiCoverageTests` exige que todo 429 emitido esté declarado.
- Cada módulo registra su propio `AddRateLimiter`. El único `OnRejected` es el del seguimiento público y solo escribe
  cuerpo para esa ruta. Por eso la ingesta de GPS y el negotiate de los hubs responden 429 sin cuerpo, contra el
  `TooManyRequests` de AI-05 (`application/problem+json`).
- El código arma 22 `NpgsqlDataSource` separados. El PostgreSQL B1ms del piloto admite unas 50 conexiones.
- Los despachadores del outbox consultan cada 250-500 ms, sin espera progresiva.
- AI-03 §18 pedía empresa y actor en cada traza, pero OBS-002 prohíbe el tenant en las dimensiones de telemetría.
  `OBS-003-TENANT-PSEUDONYM-2026-10-12` lo resuelve: AI-03 §18 y AI-13 §10 ya están enmendados.

## Secuencia de PR

Todos los PR son borradores. Van hacia `development`, salvo el PR de dependencias del paso 5, que va hacia `main`.
Cada PR que cambie AI-05, AI-06, AI-07 u otro documento normativo lleva su fila en `decision-log.md`, su entrada en
`CHANGELOG.md` y la integridad regenerada.

| PR | Ítem | Contenido | Depende de | Decisiones pendientes |
| --- | --- | --- | --- | --- |
| 1 | OBS-003 | Correlación W3C, sin paquetes | este plan | ninguna |
| 2 | SEC-004 | Plomería compartida de límites y cuerpo del 429 | PR 1 | ninguna |
| 3 | SEC-004 | Límites en los endpoints pesados y 429 en AI-05 | PR 2 | mapeo de la lista, 429 del tablero, textos ante un 429 |
| 4 | OBS-003 | Entrada de admisión de los paquetes OpenTelemetry y Azure Monitor | este plan | lista exacta de paquetes (la admite el owner) |
| 5 | OBS-003 | PR de dependencias hacia `main`, back-sync y cambio a `MERGED` | PR 4 promovido a `main` | ninguna (fusiona el owner) |
| 6 | OBS-003 | Exportación OpenTelemetry a Application Insights | PR 1, PR 5 | correo de alertas; ninguna alerta nueva sin decisión |
| 7 | OBS-003 | Contexto de traza a través del outbox | PR 1 (PR 6 para verlo) | columna, payload o ninguno |
| 8 | SCL-007 | Un pool de conexiones por rol | ninguno (mejor después del PR 6) | alerta de conexiones de PostgreSQL y correo |
| 9 | SCL-007 | Espera adaptativa del outbox | ninguno (mejor después del PR 8) | espera máxima por lane |
| 10 | OPS-005 | Pruebas de punta a punta de la plataforma | PR 1 a PR 9 | recorridos de negocio de la suite |

Los PR 1 a 6 van primero, como pidió el owner ("Empiezo por OpenTelemetry y correlación, y por los límites de tasa en
endpoints pesados"). Los PR 7 a 10 vienen después.

### PR 1: correlación W3C, sin paquetes (OBS-003)

- W3C Trace Context con `System.Diagnostics.Activity` y un `ActivitySource` propio, sin paquetes nuevos.
- Se ignoran `traceparent`, `tracestate` y `baggage` de entrada: cada petición a la API empieza una traza nueva.
- Ninguna petición de salida lleva esos encabezados: ni Google Maps, ni los proveedores de mensajes y voz, ni
  AuthCenter, ni los servicios de Azure.
- Un solo identificador, el trace id W3C, va:
  - en cada línea de log de la petición;
  - en `platform.audit_logs.request_id`, en lugar de `HttpContext.TraceIdentifier`;
  - en el `traceId` de todo `application/problem+json`.
- AI-05 documenta `traceId` como miembro opcional de `Problem`. Es un cambio aditivo, con su fila y su CHANGELOG.
- Cada ciclo de job del Worker y cada despacho del outbox corre en su propia traza, y su id va en sus logs. El enlace
  con la traza que produjo el mensaje espera al PR 7.
- Pruebas:
  - encabezados de entrada ignorados y ninguno de salida (handler HTTP falso);
  - el mismo id en los logs, la fila de auditoría y el `traceId` del problema (PostgreSQL).

### PR 2: plomería compartida de límites y cuerpo del 429 (SEC-004)

- Un solo registro de límites:
  - las políticas con nombre;
  - sus llaves de partición: el hash del sujeto autenticado, como hoy en GPS, y la red del cliente
    (`ClientNetworkPartition`) para lo anónimo;
  - un solo escritor de rechazos.
- Todo 429 sale como `application/problem+json`, con la forma de `TooManyRequests` de AI-05, el `traceId` del PR 1 y
  `Retry-After` en segundos enteros. Eso incluye la ingesta de GPS y el negotiate de los hubs.
- El seguimiento público conserva su cuerpo uniforme y su contador.
- Los límites actuales (seguimiento público, conexiones SignalR, GPS y llamadas al destinatario) conservan sus números.
- Los rechazos se cuentan solo por nombre de política. Nunca se registran la llave de partición, el usuario ni la IP.
- Pruebas: cuerpo y `Retry-After` de cada política existente; la cobertura de AI-05 sigue en verde.

### PR 3: límites en los endpoints pesados (SEC-004)

Aplica la lista decidida, por usuario: "CSV 10/min (1 importación a la vez), exportar liquidación 10/min, crear
dirección (cuesta Google) 30/min, fotos 30–60/min, tablero y conteos 60/min, lista de órdenes 120/min, cotizar 60/min,
repartidores asignables 60/min; inicio de sesión 20/min por red".

- Cada operación limitada declara 429 en AI-05. Se actualizan los conteos fijados de OpenAPI y AI-07 lleva el texto de
  cada pantalla.
- Los números son configuración validada al arrancar. "Se ajusta tras 2 semanas con métricas": una decisión posterior
  del owner los fija con los rechazos por política que se vean en el piloto.

El owner decidió los tres puntos el 2026-10-12 (preguntas Q1 a Q6, literales en `decision-log.md`:
`SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12` y `OBS-001-DASHBOARD-429-2026-10-12`; también
`SEC-004-RETRY-AFTER-2026-10-13` y `SEC-004-REJECTION-SUMMARY-2026-10-13`). Lo aplicado está en
`docs/development/sec-004-heavy-endpoint-limits.md`. El PR 3 sale en dos partes: primero servidor, contratos y el
resumen de rechazos en los logs; después los textos y el comportamiento de la web (AI-07).

Los tres puntos que el owner tenía que confirmar:

1. El mapeo operación por operación (tabla siguiente).
2. Cómo registra su 429 el tablero. `GET /operations/dashboard` no está en AI-05: lo cubren ADR-OBS-001 y la
   excepción `KnownUndeclaredOperations`. La decisión pide que "cada uno declara 429 en el API", y el tablero no puede
   declararlo en AI-05 tal como está. Opciones: declarar el tablero en AI-05, o una enmienda de ADR-OBS-001 que
   registre su 429.
3. El texto en español y el comportamiento de cada pantalla ante un 429 (AI-07). Incluye qué ve el navegador si
   `/auth/login` responde 429, porque es una navegación y no una llamada `fetch`.

Mapeo propuesto (confirmado con las respuestas Q1, Q2, Q3 y Q5: CSV compartido y una confirmación a la vez, 60 cada
paso de fotos, un presupuesto para el tablero y otro para los conteos, solo `/auth/login`):

| Texto decidido | Operaciones propuestas | Punto abierto |
| --- | --- | --- |
| CSV 10/min (1 importación a la vez) | `previewOrderCsv`, `commitOrderCsv` | ¿Comparten los 10/min? ¿"1 a la vez" es una confirmación en curso por usuario? |
| exportar liquidación 10/min | `exportSettlementCsv` | — |
| crear dirección (cuesta Google) 30/min | `createLocation` | — |
| fotos 30–60/min | `createProofUploadSession`, `finalizeProof` | ¿Cuáles cuentan como fotos y qué número lleva cada una dentro de 30–60? |
| tablero y conteos 60/min | `GET /operations/dashboard`, `getOperationsQueueCounts` | ¿Un presupuesto compartido o uno cada una? 429 del tablero (punto 2) |
| lista de órdenes 120/min | `listOrders` | — |
| cotizar 60/min | `createQuote` | — |
| repartidores asignables 60/min | `listAssignableDrivers` | — |
| inicio de sesión 20/min por red | `startAuthCenterSignIn` (`GET /auth/login`) | ¿También `completeAuthCenterSignIn` (`/signin-authcenter`)? |

El estado del limitador vive en el proceso de la API. Es exacto mientras el piloto tenga una sola réplica de API
(GATE-013). Con una segunda réplica, los límites y "1 importación a la vez" necesitarían estado compartido o una
decisión registrada.

### PR 4: entrada de admisión de paquetes (OBS-003)

- Entrada nueva en `dependency_admissions` de `tools/rel-000/security-remediation-policy.json` (formato v3,
  `status: ACTIVE`):
  - cada paquete directo y transitivo, con su versión exacta y su hash de contenido NuGet;
  - la rama de origen autorizada;
  - los archivos permitidos y requeridos, y los `.csproj` que reciben el `PackageReference`;
  - la decisión `OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12`.
- La decisión habla de "7 paquetes nuevos" y de subir Azure.Core de 1.61 a 1.63. Azure.Core 1.61.0 ya está admitido
  como transitivo por ADP-001-AZURE, así que el cambio se declara como enmienda exacta.
  - Si el formato v3 no puede expresar el cambio de un nodo ya admitido (no de línea base), el PR se detiene y lo
    reporta, en lugar de relajar REL-000.
- Va hacia `development` y después se promueve `development` → `main`. `main_source_guard.py` y REL-000 leen la
  admisión desde la base probada, así que el PR de dependencias no puede admitirse a sí mismo.
- Pendiente: la lista exacta de paquetes (ids, versiones y hashes) la admite el owner al aprobar este PR, como pide
  GOV-DEPENDENCY-ADMISSION-001.

### PR 5: dependencias por `main` (OBS-003)

Son tres pasos, con su propio PR cada uno (GOV-DEPENDENCY-ROUTE-001, `branching-and-ci.md`):

1. PR de dependencias desde la rama autorizada hacia `main`. Solo lleva `Directory.Packages.props`, los lock files y
   los `PackageReference` admitidos. Foundation 13/13 en el PR y después en el push a `main`.
2. Back-sync `main` → `development` (`MAIN_BACKSYNC`).
3. PR hacia `development` que pasa la admisión a `status: MERGED`.

### PR 6: exportación a Application Insights (OBS-003)

- SDK de OpenTelemetry en la API y el Worker: trazas y métricas por el exportador oficial de Azure Monitor. Los logs
  siguen por consola hacia Log Analytics, como en OBS-002.
- Exportación apagada por defecto, encendida solo por configuración, con muestreo de 10 % de trazas.
- Application Insights basado en workspace, en México Central, sobre el workspace de Log Analytics del piloto:
  - queda dentro del tope diario de ingesta;
  - cuesta aproximadamente 0–5 USD/mes, según la opción elegida.
- Lista permitida de atributos de span. Nada de:
  - query de URL, encabezados, cookies, cuerpos, texto o parámetros SQL;
  - tokens de seguimiento;
  - id de usuario o sujeto, nombres, correos, teléfonos, direcciones o coordenadas;
  - id real de la organización.
- Seudónimo de la organización (HMAC con llave en Key Vault, con versión de llave) y tipo de rol del actor, solo en
  trazas y logs y con contexto de organización. Nunca como etiqueta de métrica, dimensión de alerta o de libro de
  trabajo, ni dentro de `TelemetryDimensions.Allowed`.
  - La llave es un secreto nuevo de Key Vault que la API y el Worker leen al arrancar.
  - Development y Testing usan una llave sintética.
  - Fuera de Development y Testing el arranque falla sin llave, porque los logs llevan el seudónimo aunque la
    exportación esté apagada.
- Bicep:
  - el recurso `Microsoft.Insights/components`;
  - la cadena de conexión hacia la API y el Worker;
  - el secreto del seudónimo;
  - los ajustes de las guardas de `env001_pilot_guards.py` (P00, P01 y P21).
- No se agrega ninguna alerta nueva sin su propia decisión. El correo de alertas (`alertEmailAddress`) sigue pendiente
  del owner.
- Pruebas:
  - exportación apagada por defecto;
  - muestreo y lista permitida (exportador de prueba);
  - vector fijo del seudónimo;
  - prueba de arquitectura: el seudónimo y el tipo de rol no llegan a métricas, a eventos OBS-002 ni a consultas de
    alertas.

### PR 7: contexto de traza a través del outbox (OBS-003, pendiente)

Decisión pendiente del owner, sin recomendación todavía:

- **Columna:** columna nueva en `platform.outbox_events` y `platform.location_outbox_events`.
  - Es un cambio de AI-06/AI-18 con migración por módulo.
  - El productor da el valor (`ValueGeneratedNever`, sin `RETURNING`) y las funciones de claim lo devuelven.
- **Payload:** un miembro dentro del `payload` inmutable.
  - No cambia el esquema.
  - El valor queda dentro del contrato del evento, y hay que evitar que llegue a clientes SignalR o a notificaciones.
- **Ninguno:** el Worker empieza una traza nueva por mensaje.
  - Se correlaciona por el id del outbox, que ya es el `event_id` de SignalR (RTM-002-STABLE-OUTBOX-ID-AS-EVENT-ID), y
    por el agregado.

Llevar el contexto hasta los clientes SignalR toca AI-12: `correlation_id` es `uuid optional` y un trace id W3C son 32
dígitos hexadecimales.

AI-03 §18 pide `trace_id` y `correlation_id` en cada evento. Con "ninguno", cada evento lleva su propia traza, pero no
la de la petición que lo originó. Si el owner elige esa opción, el PR 7 revisa si §18 necesita una aclaración.

### PR 8: un pool de conexiones por rol (SCL-007)

Lo aplicado está en `docs/development/scl-007-pool-per-role.md` (`SCL-007-POOL-PER-ROLE-2026-10-13`): tres pools
(`paqueteria_app` y `paqueteria_realtime` en la API, `paqueteria_worker` en el Worker), 10 + 4 + 10 = 24 conexiones.

- Un `NpgsqlDataSource` por rol de base de datos en cada proceso (`paqueteria_app` en la API, `paqueteria_worker` en el
  Worker), compartido por todos los módulos, en lugar de los 22 actuales.
- Presupuesto de conexiones: la suma de los máximos configurados queda debajo del límite del B1ms (unas 50), con margen
  para el migrador, el mantenimiento y la administración.
- Lo que no cambia:
  - toda consulta tenant sigue en transacción explícita con `set_config(..., true)` después de `BEGIN`;
  - ningún estado de sesión sobrevive al regresar la conexión al pool;
  - cada pool se autentica con un solo rol `NOBYPASSRLS`;
  - el `pooling_contract` de AI-15 sigue igual, y PgBouncer sigue detrás de GATE-014.
- Pendiente: una alerta de métrica de Azure Monitor sobre conexiones de PostgreSQL y el correo de alertas que el owner
  fija al desplegar.
  - El umbral se compararía con `connection_pool_utilization` de AI-15 (0.8 aviso, 0.95 crítico).
  - El costo mensual se compararía con PILOT-BUDGET-ACCEPT-98-108.
- Pruebas:
  - un data source por rol, resuelto por todos los módulos (arquitectura);
  - regresión de contexto tenant con conexiones reutilizadas (PostgreSQL);
  - conteo de conexiones dentro del presupuesto durante el flujo sintético de OPS-001.

### PR 9: espera adaptativa del outbox (SCL-007)

Lo aplicado está en `docs/development/scl-007-adaptive-outbox-wait.md` (`SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13`):
los despachadores de tiempo real de la API esperan 250 ms, 500 ms y luego 1 s (negocio) o 500 ms y luego 1 s
(ubicación) mientras no hay trabajo; los pollers del Worker no cambian.

- Mientras los lotes regresan llenos, el despachador vuelve a reclamar de inmediato. Mientras regresan vacíos, espera
  cada vez más, hasta una espera máxima. Al encontrar trabajo, vuelve a empezar.
- Solo usa las funciones existentes de claim, settle y requeue con `lease_token`: sin SQL nuevo ni acceso directo al
  outbox.
- Los lanes de negocio y de ubicación conservan esperas independientes.
- El owner decidió la espera máxima el 2026-10-13: "1 segundo en ambos (Recommended)". Cumple las tres cosas que se
  pedían:
  - mantener el latido por minuto de `OutboxLaneSummary` (OBS-002);
  - quedar muy debajo del aviso `oldest_outbox_age_seconds` de AI-15 (60 s);
  - en los lanes de tiempo real, respetar el aviso `event_delivery_p95_seconds` de AI-15 (2 s).
- `MaxClaimAgeMs` incluye esa espera: se mide al volver el claim, como `now - min(available_at)`.
- Pruebas: espera progresiva, reinicio al haber trabajo, latido en reposo y cancelación (reloj falso).

### PR 10: pruebas de punta a punta de la plataforma (OPS-005)

- Suite en `tests/Paqueteria.EndToEndTests`: API, Worker y Web armados contra PostgreSQL/PostGIS real.
- Comprueba:
  - un trace id de punta a punta;
  - el 429 con `problem+json` y `Retry-After`, y el texto aprobado en la web;
  - conexiones dentro del presupuesto;
  - un evento después de un rato sin trabajo llega dentro del presupuesto de AI-15;
  - nada sale del proceso con la exportación apagada.
- Solo datos sintéticos (SEC-003). Sin pruebas de relleno: lo prohíbe el README del proyecto.
- Corre dentro de un job de CI existente: un job nuevo cambiaría la topología de 13 jobs de Foundation y necesitaría su
  propia decisión.
- Pendiente del owner: los recorridos de negocio que corre la suite además de esas comprobaciones.

## Decisiones pendientes del owner

| Decisión | Bloquea | Dónde está registrada |
| --- | --- | --- |
| Lista exacta de paquetes (admisión explícita por paquete) | PR 4 | AI-08 OBS-003, `OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12` |
| Correo de alertas (`alertEmailAddress`) | despliegue de las alertas (ya pendiente desde OBS-002) | AI-08 OBS-003 y SCL-007 |
| Contexto de traza a través del outbox: columna, payload o ninguno | PR 7 | AI-08 OBS-003 |
| Alerta de métrica sobre conexiones de PostgreSQL | PR 8 (la alerta, no el pool) | AI-08 SCL-007 |
| Espera máxima del outbox por lane | PR 9 | AI-08 SCL-007 |
| Recorridos de negocio de la suite de punta a punta | PR 10 | AI-08 OPS-005 |

## Invariantes que ningún PR de la fase 4 cambia

- Transacción explícita, `set_config(..., true)` después de `BEGIN` y `current_org_ids` como `uuid[]`.
- API y Worker siguen `NOBYPASSRLS`.
- Outbox solo por funciones con `lease_token`.
- Sin PII, tokens ni payloads en logs, trazas, métricas o eventos.
- El seguimiento público mantiene su 404 uniforme.
- Siguen los cinco flujos atómicos de AI-13 §4, sin un sexto.
- Dinero en centavos `long`.
- Nada de NestJS, BullMQ, microservicios ni Kubernetes.
