# AI-26 — Informe de validación v0.6

**Fecha:** 2026-07-23
**Alcance:** validación estructural, contractual, criptográfica, de integridad y ejecución real del paquete.

## Resultado

La línea base v0.6 pasa las validaciones estáticas incluidas en `tools/validate_contracts.py`:

- 10 archivos YAML analizados correctamente.
- 56 tareas de backlog con IDs únicos, dependencias existentes y grafo acíclico.
- OpenAPI con 23 paths, 48 schemas, operationIds únicos y 183 referencias internas resueltas.
- 17 estados internos consistentes entre producto, dominio, OpenAPI y SQL.
- 17 mapeos internos→públicos consistentes entre AI-04, SQL, SignalR y OpenAPI.
- 36 tablas SQL y todas las referencias FK dirigidas a tablas existentes.
- Ocho funciones de lifecycle de outbox presentes y asociadas a roles dedicados.
- Runtime sin `SELECT`, `UPDATE` ni `DELETE` directo sobre los dos lanes.
- `pgcrypto` en `extensions`, PostGIS en `public` y digest sobre UTF-8 explícito.
- Todas las columnas monetarias SQL en `bigint`; campos OpenAPI de centavos en `int64`.
- `order_acceptances` con tabla, RLS, grants y trigger append-only.
- Lane GPS con `UNIQUE(driver_position_id)` sin FK.
- Campos de aceptación/versionado restaurados en `external_offers`.
- Vectores SHA-256 de token y aceptación recalculados correctamente.
- Escenarios Gherkin v0.6 presentes.
- Delimitadores SQL `$$` balanceados.
- Hashes SHA-256 del MANIFEST verificados después de generarlo.
- Purge real elimina únicamente terminales antiguos, respeta lote y es idempotente.
- Dos conexiones Worker concurrentes purgan sin doble conteo, deadlock ni lock residual.
- Maintenance conserva `SELECT,DELETE` sobre ambos lanes y no recibe `UPDATE`.

## Verificación contra documentación oficial

- PostgreSQL 18 exige privilegio `SELECT` sobre las columnas usadas por `RETURNING`; por ello los inserts runtime del outbox no deben emitir `RETURNING`.
- `pgcrypto.digest` acepta `bytea` y devuelve `bytea`; el contrato usa `convert_to(...,'UTF8')` para evitar ambigüedad de encoding.
- PostgreSQL exige que toda restricción única de una tabla particionada incluya la clave de partición; SCL-006 conserva esta advertencia.
- EF Core permite deshabilitar generación de valores mediante `ValueGeneratedNever`.
- PostGIS permanece normalmente en `public` y su extensión no es relocatable de forma ordinaria.

## Ejecución real ARC-002

AI-06 y AI-18 se ejecutaron, en ese orden, sobre una base efímera y limpia de
Testcontainers con PostgreSQL 18/PostGIS 3.6. Las pruebas usan logins runtime
no-superuser, pooling real y credenciales sintéticas. Bootstrap, extensiones,
roles, RLS, provisioning, outbox, tracking, aceptación, snapshots y dinero
pasaron contra PostgreSQL real. Los cinco jobs del PR pasaron en el run
`29879390854`; ARC-002 queda `DONE`.

La validación no crea migraciones ni aplica los scripts a la base persistente de
FND-002. `SEC-002` y `DBA-001` permanecen tareas independientes.

## Remediación contractual DSP-002

La sincronización
`v0.6-full-canonical-sync-4-dsp002-contract-remediation` formaliza el alcance
incremental de DSP-002 sin modificar AI-06 ni AI-18:

- `POST /orders/{orderId}/assignments` declara exactamente
  `201/401/403/404/409`; el `409` expone únicamente
  `INVALID_REQUEST`, `CONFLICT`, `DRIVER_INELIGIBLE` o
  `DRIVER_DOCUMENT_EXPIRED`.
- `route_id` es UUID nullable, pero DSP-002 sólo admite ausencia o `null`.
- El vocabulario global conserva `OWN`, `EXTERNAL` y `ALLY_CAPACITY`; DSP-002
  habilita sólo `OWN`, reservando los otros valores para sus bloques dueños.
- Recursos inexistentes o no visibles producen el `404` uniforme; `403` queda
  reservado para un actor visible sin capacidad.
- La adopción de `dispatch.assignments` comprueba el check monetario efectivo,
  las cinco FKs completas y sus acciones, el índice parcial, RLS, FORCE RLS y
  la policy exacta. No crea, altera ni elimina objetos canónicos.

Evidencia local reproducible:

- Validador canónico: 73 entradas de manifest verificadas, 10 YAML válidos,
  23 paths, 48 schemas y 183 refs.
- AI-06:
  `c7681336856421487b208ea220d05017c4b8f820f1a34e1e7e838d5da09b7b96`.
- AI-18:
  `7b4d263843e3ba49812fedb1167bd8ab92b2e33efa2558abf0833af1c13760dd`.
- Build Debug y Release/CI: 0 warnings, 0 errores.
- Suite .NET: 662 pruebas (272 unitarias, 184 de integración, 71 de
  arquitectura y 135 de contrato).
- PostgreSQL 18/PostGIS real: 88 contratos, incluidos el baseline válido y
  todos los casos de drift de assignments.
- Web: instalación frozen, lint, typecheck, 1 prueba y build correctos.
- Infraestructura: Compose con `.env.example` válido y smoke completo de
  health, APIs, persistencia, diagnósticos y cleanup.
- Dependencias: 0 vulnerabilidades NuGet directas o transitivas. La auditoría
  web conserva 10 advisories preexistentes (5 high y 5 moderate) fuera del
  alcance de DSP-002.

## Visibilidad no enumerable DSP-002

La sincronización
`v0.6-full-canonical-sync-5-dsp002-non-enumerable-visibility` valida
`DSP-002-NON-ENUMERABLE-VISIBILITY`:

- Sin autenticación se devuelve 401.
- Viewer, Driver, membresía suspendida o `PLATFORM_ADMIN` sin MFA recibe 403
  antes de consultar orden o driver.
- Un actor autorizado ejecuta siempre `order_packages` y después
  `driver_profile_documents`. Cada etapa consume dos resultsets del mismo
  comando PostgreSQL aunque no exista la fila principal.
- Orden o driver missing/cross-tenant comparte plan, respuesta 404 y rollback.
- Estado, inelegibilidad y documento vencido visibles conservan 409 y sus
  códigos públicos cerrados.
- Replay reautoriza antes de evidencia y no reevalúa driver, documentos,
  capacidad ni estado actual.
- No se usan delays, sleeps, jitter, busy waits, cronómetros de seguridad,
  retries ficticios ni consultas costosas. No se agregó una prueba temporal:
  la garantía autoritativa es estructural y evita flakes.
- Los 404 no crean métrica ni log informativo de causa.

Evidencia local:

- Suite .NET: 671 pruebas (277 unitarias, 185 de integración, 71 de
  arquitectura y 138 de contrato).
- PostgreSQL 18/PostGIS: 91 contratos; los cuatro 404 ejecutan el mismo catálogo
  y revierten idempotencia, orden, assignment, evento, outbox y auditoría.
- HTTP: mismo content type, propiedades, `type/title/status` y longitud; sólo
  varía el `traceId` propio de cada request, sin resource IDs ni causa.
- Build Debug: 0 warnings, 0 errores.
- AI-06 y AI-18 conservan los hashes canónicos registrados arriba.

## Capability antes de estado persistido DSP-002

La sincronización
`v0.6-full-canonical-sync-6-dsp002-capability-before-state` valida
`DSP-002-CAPABILITY-BEFORE-PERSISTED-STATE`:

- JSON/request/Idempotency-Key inválidos producen `409 INVALID_REQUEST` sin
  invocar el servicio productivo, abrir transacción ni consultar PostgreSQL.
- Cada request válido relee una sola vez usuario, membresía, rol y MFA dentro
  de la transacción tenant-aware.
- Sólo después de autorizar se adquiere el advisory lock y se lee la fila
  idempotente; replay evidence y recursos de negocio permanecen posteriores.
- Viewer, Driver y `PLATFORM_ADMIN` sin MFA reciben 403 uniforme ante key
  inexistente, completada, con hash distinto o incompleta.
- Dispatcher y `PLATFORM_ADMIN` con MFA conservan creación/replay 201, hash
  conflict/fila incompleta/evidencia inconsistente 409 y rollback integral.
- Un recorder sobre los puertos reales prueba exactamente
  `authorization -> idempotency_lock -> idempotency_read`; en denegación sólo
  aparece `authorization`.
- Hash canónico, schema, migración de adopción, AI-06 y AI-18 no cambian.

Evidencia local:

- Suite .NET: 680 pruebas (277 unitarias, 191 de integración, 71 de
  arquitectura y 141 de contrato).
- PostgreSQL 18/PostGIS: 94 contratos, incluida la matriz de estados
  idempotentes, replay histórico, concurrencia, retry, cancelación, pooling,
  fault injection y rollback.
- Endpoint HTTP DSP-002: 29 pruebas; la forma inválida no incrementa el contador
  de invocaciones del servicio productivo.
- Builds Debug/Release: 0 warnings, 0 errores.
- AI-06 y AI-18 conservan sus hashes canónicos.
