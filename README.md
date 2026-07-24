# Paquetenvia

Paquetenvia es la base técnica de una plataforma local de paquetería. El backend
se construye como monolito modular en .NET y el cliente como PWA con Next.js. La
marca comercial y los proveedores productivos siguen pendientes de sus gates.

## Estado actual

La validación ejecutable de **ARC-002** está **DONE** después de pasar los cinco
jobs de CI. Los contratos se validan de forma estática y contra PostgreSQL
18/PostGIS 3.6 real y efímero, incluida purga real y concurrente de ambos outbox.

**SEC-002** separa la identidad externa (`sub`/MFA) de la autorización tenant,
integra el bootstrap y tracking de AI-06 mediante adaptadores Npgsql de mínimo
privilegio, y aporta `TrackingTokenHasher` y el mapa público de 17 estados. La
integración HTTP es exclusivamente mediante probes de `Testing`; AuthCenter y
el endpoint público productivo continúan fuera de alcance.

**DBA-001** implementa la ruta controlada del baseline de base de datos:
manifiesto con hashes, migrador independiente, catálogo de 15 schemas,
assertions de ownership/privilegios y mapeos EF mínimos de ambos outbox. Se
valida exclusivamente con PostgreSQL 18/PostGIS 3.6 efímero en Testcontainers.

El repositorio implementa **FND-001**, la plantilla arquitectónica de
**ARC-001** y el entorno local reproducible de **FND-002**. Incluye la solución
compilable, API y Worker mínimos, Building
Blocks pequeños, los módulos vacíos Orders y Pricing, reglas ejecutables de
aislamiento, el workspace web, infraestructura local y CI.

La implementación vive fuera de `docs/normative/v0.6/`. Esa carpeta contiene la
línea base normativa v0.6 y es la fuente de verdad. La revisión sync 3 fue
reemitida mediante una remediación controlada de purge; futuras modificaciones
siguen requiriendo autorización normativa explícita.

## Requisitos

- .NET SDK 10.0.101, fijado por `global.json`.
- Node.js 24.13.0, fijado por `.nvmrc`.
- pnpm 11.15.1, fijado por `packageManager`.
- Python 3 para el validador canónico.
- PowerShell 7 y Docker Engine con Docker Compose V2 para la infraestructura.

## Infraestructura local

FND-002 levanta PostGIS, Redis, MinIO y Mailpit; no levanta la API, el Worker ni
el cliente web. Desde la raíz del repositorio:

```powershell
Copy-Item .\deploy\.env.example .\deploy\.env.local
pwsh .\tools\local-environment.ps1 Up
pwsh .\tools\local-environment.ps1 Status
pwsh .\tools\local-environment.ps1 Smoke
pwsh .\tools\local-environment.ps1 Down
```

`Down` conserva los datos. Para eliminar de forma explícita los contenedores,
la red y los cuatro volúmenes del proyecto local:

```powershell
pwsh .\tools\local-environment.ps1 Reset -Force
```

Consulta la [guía del entorno local](docs/development/local-environment.md) para
los puertos, healthchecks, persistencia, diagnóstico y alternativas de backup.

## Backend

Desde la raíz del repositorio:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

Para crear un módulo con las cuatro capas canónicas:

```powershell
dotnet new install .\templates\Paqueteria.Module --force
dotnet new paquetenvia-module --name Example --output .\src\Modules\Example
```

Consulta la [guía de arquitectura modular](docs/development/module-architecture.md)
para agregarlo a la solución, registrarlo en el catálogo y validar sus límites.

La API expone `GET /health/live` y los endpoints de Locations incorporados por
GEO-001. OpenAPI del framework se publica solo en Development. SEC-001/SEC-002 agregan probes internos exclusivamente
bajo el environment `Testing`; no agregan endpoints públicos de login,
identidad o tracking. El
Worker inicia y espera cancelación sin conectarse a servicios externos.

La [guía de autenticación y autorización](docs/development/security-authentication.md)
documenta los schemes, perfiles sintéticos, claims, sesión, policies y límites.
La [guía SEC-002](docs/development/security-bootstrap-tracking.md) documenta los
adaptadores PostgreSQL, parsers, roles, tracking, pruebas y rollback.
La [guía DSP-001](docs/development/dsp-001-own-driver-eligibility.md) describe
el contrato interno de elegibilidad de flota propia, sus políticas sintéticas,
aislamiento tenant, migración de adopción y rollback no destructivo.
Ejecuta su matriz con:

```powershell
dotnet test .\tests\Paqueteria.UnitTests\Paqueteria.UnitTests.csproj
dotnet test .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj
dotnet test .\tests\Paqueteria.ArchitectureTests\Paqueteria.ArchitectureTests.csproj
```

## Frontend

El primer install sin lockfile puede ejecutarse sin `--frozen-lockfile`. Una vez
versionado `apps/web/pnpm-lock.yaml`, el flujo reproducible es:

```powershell
pnpm --dir apps/web install --frozen-lockfile
pnpm --dir apps/web lint
pnpm --dir apps/web typecheck
pnpm --dir apps/web test
pnpm --dir apps/web build
```

Para desarrollo local:

```powershell
pnpm --dir apps/web dev
```

La ruta `/health` muestra el estado local del workspace y no consume endpoints
de negocio.

## Validación normativa

Ejecuta siempre desde la raíz:

```powershell
python .\docs\normative\v0.6\tools\validate_contracts.py
(Get-FileHash .\docs\normative\v0.6\database\AI-06_SCHEMA.sql -Algorithm SHA256).Hash.ToLower()
git diff --exit-code -- docs/normative/v0.6
```

El validador debe terminar en `VALIDATION_OK` y el hash de AI-06 debe ser
`c7681336856421487b208ea220d05017c4b8f820f1a34e1e7e838d5da09b7b96`.

## Contratos runtime ARC-002

ContractTests ejecuta la revisión canónica de AI-06 y luego AI-18 exclusivamente en
un PostgreSQL/PostGIS efímero creado por Testcontainers. Requiere Docker y no
usa la base persistente de FND-002:

```powershell
dotnet restore --locked-mode
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj
```

Consulta la [guía de validación ARC-002](docs/development/arc-002-contract-validation.md)
y el [reporte verificable](docs/development/arc-002-validation-report.md). El
reporte registra claim, settle, requeue, dry-run, purge real, idempotencia y
concurrencia sin doble conteo.

ARC-002 aporta validación contractual efímera; no crea migraciones ni una
aplicación funcional.

## Baseline de base de datos DBA-001

La herramienta separada verifica AI-06 y AI-18 antes de conectarse. API y
Worker no aplican migraciones ni usan la credencial privilegiada:

```powershell
pwsh .\tools\database-baseline.ps1 Verify
pwsh .\tools\database-baseline.ps1 Plan -ConnectionEnvironment PAQUETERIA_DEPLOYMENT_DB
pwsh .\tools\database-baseline.ps1 Assert -ConnectionEnvironment PAQUETERIA_DEPLOYMENT_DB
```

`Apply` requiere una conexión privilegiada en la variable indicada y la opción
explícita `-ConfirmInitialBaseline`. Consulta la
[guía DBA-001](docs/development/database-baseline.md) para el flujo completo,
estados clean/applied/partial, seguridad, pruebas y rollback.

## Estructura principal

```text
Paqueteria.sln              Solución .NET 10
src/Paqueteria.Api          Composition root HTTP
src/Paqueteria.Worker       Composition root de procesos
src/BuildingBlocks          Primitivas fundacionales
src/Modules                 Módulos del monolito modular
tests                       Arquitectura, unidad, integración y contratos
apps/web                    Next.js PWA
database                    Reservas para migraciones, seeds y políticas
deploy                      Docker Compose e inicialización local
tools                       Automatización y pruebas del entorno
docs/normative/v0.6         Línea base normativa congelada
.github/workflows           Integración continua
```

## TEN-001

TEN-001 agrega el contexto tenant activo, el modulo `Organizations`, adopciones
EF no destructivas y RLS transaccional. Consulta
[`docs/development/tenant-context-rls.md`](docs/development/tenant-context-rls.md)
para el header, autorizacion por organizacion, migrador, provisioning, pruebas y
rollback.

## TEN-002

TEN-002 agrega evidencia independiente del orden transaccional, retry completo,
parametro PostgreSQL `uuid[]`, contexto vacio `{}` y aislamiento bajo pooling
Npgsql. Consulta
[`docs/development/ten-002-transactional-rls-validation.md`](docs/development/ten-002-transactional-rls-validation.md)
para la matriz de trazabilidad, comandos del harness, riesgos residuales y
rollback. No se agregaron migraciones ni PgBouncer.

## AUD-001

AUD-001 centraliza las escrituras append-only en `platform.audit_logs`, aplica
redaccion estructurada antes de persistir y conserva accion y auditoria dentro
de la misma transaccion tenant. Consulta
[`docs/development/aud-001-append-only-audit.md`](docs/development/aud-001-append-only-audit.md)
para el contrato, los grants de app/Worker, atomicidad, retry, pruebas y
rollback. No se agregaron migraciones ni se conecto el proceso Worker a
PostgreSQL.

## GEO-001

GEO-001 agrega el modulo `Locations`, catalogos tenant-scoped, creacion
idempotente de ubicaciones, geocodificacion manual/mock, proteccion PII
fail-closed, adopcion EF no destructiva y cobertura real con PostGIS. Consulta
[`docs/development/geo-001-locations-zones-postgis.md`](docs/development/geo-001-locations-zones-postgis.md)
para endpoints, convencion `ST_Covers`, configuracion, gates, pruebas y
rollback. Los mocks solo funcionan en Development/Testing y no representan
proveedores productivos.

## PRC-001

PRC-001 agrega el motor basico de cotizacion sintetica: evaluacion determinista
de tarifas, ubicaciones protegidas de GEO-001, snapshots redactados,
idempotencia, expiracion, RLS y adopcion EF no destructiva. Consulta
[`docs/development/prc-001-basic-quote-engine.md`](docs/development/prc-001-basic-quote-engine.md)
para arquitectura, configuracion, pruebas, riesgos y rollback. El provider queda
`Disabled` por defecto y no habilita precios ni cobertura reales.

## ORD-001

ORD-001 agrega creación atómica de órdenes desde quotes single-use, consulta
tenant-scoped, idempotencia, public IDs criptográficos, evidencia legal
append-only, evento, outbox, auditoría y adopción EF no destructiva. Consulta
[`docs/development/ord-001-create-query-orders.md`](docs/development/ord-001-create-query-orders.md)
para arquitectura, lock order, configuración, pruebas, riesgos y rollback. El
provider queda `Disabled` por defecto y no habilita transiciones ni Worker.

## ORD-002

ORD-002 agrega la máquina de estados autenticada e idempotente de 17 estados y
30 aristas, versión optimista, autorización por rol/MFA/asignación, guards
tenant-scoped, evento, outbox y auditoría atómicos. Consulta
[`docs/development/ord-002-order-state-machine.md`](docs/development/ord-002-order-state-machine.md)
para la matriz, lock order, redacción, pruebas, riesgos y rollback. El provider
permanece `Disabled` por defecto y no habilita integraciones futuras ni Worker.

## Fuera de alcance

Fuera del soporte tecnico de Identity/Organizations, Locations y la cotizacion
sintetica de PRC-001, no se
implementan otros casos de uso comerciales, proveedor OIDC real,
login, endpoint público de tracking, Worker de outbox,
hubs SignalR productivos, pricing avanzado, despacho,
custodia, sellos, ADR-032/ADR-033, proveedores externos, despliegue productivo
ni lógica de negocio de módulos. ARC-001 aporta solamente estructura, catálogo y
reglas de arquitectura; FND-002 aporta solamente dependencias locales.
