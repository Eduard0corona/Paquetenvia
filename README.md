# Paquetenvia

Paquetenvia es la base técnica de una plataforma local de paquetería. El backend
se construye como monolito modular en .NET y el cliente como PWA con Next.js. La
marca comercial y los proveedores productivos siguen pendientes de sus gates.

## Estado actual

El repositorio implementa **FND-001: Inicializar solución .NET y workspace web**.
Incluye la solución compilable, API y Worker mínimos, Building Blocks pequeños,
el baseline vacío del módulo Orders, pruebas de foundation, el workspace web y CI.

La implementación vive fuera de `docs/normative/v0.6/`. Esa carpeta contiene la
línea base normativa v0.6 congelada y es la fuente de verdad: no se modifica,
reformatea ni regenera como parte del desarrollo.

## Requisitos

- .NET SDK 10.0.101, fijado por `global.json`.
- Node.js 24.13.0, fijado por `.nvmrc`.
- pnpm 11.15.1, fijado por `packageManager`.
- Python 3 para el validador canónico.

## Backend

Desde la raíz del repositorio:

```powershell
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

La API expone únicamente `GET /health/live`. OpenAPI del framework se publica
solo en Development. El Worker inicia y espera cancelación sin conectarse a
servicios externos.

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
`4b5fe5397ff088b63e0c288770903512665c5fe8a8dc7401d7e4d3af64643505`.

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
deploy                      Reserva de despliegue
tools                       Reserva de automatización
docs/normative/v0.6         Línea base normativa congelada
.github/workflows           Integración continua
```

## Fuera de alcance de FND-001

No se implementan tablas, migraciones, RLS, autenticación real, endpoints de
negocio, outbox funcional, SignalR, tracking, órdenes, pricing, despacho,
custodia, sellos, ADR-032/ADR-033, Docker Compose (FND-002), proveedores externos
ni lógica de negocio de módulos. La plantilla modular ampliada y sus reglas de
controllers/hubs continúan en ARC-001.
