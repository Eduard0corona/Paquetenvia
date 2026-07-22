# Paquetenvia

Paquetenvia es la base técnica de una plataforma local de paquetería. El backend
se construye como monolito modular en .NET y el cliente como PWA con Next.js. La
marca comercial y los proveedores productivos siguen pendientes de sus gates.

## Estado actual

La validación ejecutable de **ARC-002** está **DONE** después de pasar los cinco
jobs de CI. Los contratos se validan de forma estática y contra PostgreSQL
18/PostGIS 3.6 real y efímero, incluida purga real y concurrente de ambos outbox.

**SEC-001** implementa el módulo Identity, la abstracción `IIdentityProvider`,
autenticación fail-closed, un mock determinista para Development/Testing,
sesión stateless, claims tenant-aware y policies de autorización verificadas
por pruebas HTTP y SignalR. El mock no es productivo. `GATE-002` está resuelto
normativamente; DBA-001 no integra todavía AuthCenter.

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

La API expone únicamente `GET /health/live`. OpenAPI del framework se publica
solo en Development. SEC-001 agrega probes internos exclusivamente bajo el
environment `Testing`; no agrega endpoints públicos de login o identidad. El
Worker inicia y espera cancelación sin conectarse a servicios externos.

La [guía de autenticación y autorización](docs/development/security-authentication.md)
documenta los schemes, perfiles sintéticos, claims, sesión, policies y límites.
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

## Fuera de alcance

No se implementan persistencia funcional, proveedor OIDC real, login, endpoints de
negocio, Worker de outbox, hubs SignalR productivos, tracking, órdenes, pricing, despacho,
custodia, sellos, ADR-032/ADR-033, proveedores externos, despliegue productivo
ni lógica de negocio de módulos. ARC-001 aporta solamente estructura, catálogo y
reglas de arquitectura; FND-002 aporta solamente dependencias locales.
