# Blueprint de solución .NET para el agente de implementación

**Versión:** 0.6  
**Objetivo:** traducir la arquitectura normativa a una estructura inequívoca y escalable. La IA debe seguirla para Foundation y registrar un ADR antes de desviarse.

## 1. Proyectos mínimos

```text
Paqueteria.sln
apps/web/package.json
src/Paqueteria.Api/Paqueteria.Api.csproj
src/Paqueteria.Worker/Paqueteria.Worker.csproj
src/BuildingBlocks/Paqueteria.Domain/Paqueteria.Domain.csproj
src/BuildingBlocks/Paqueteria.Application/Paqueteria.Application.csproj
src/BuildingBlocks/Paqueteria.Infrastructure/Paqueteria.Infrastructure.csproj
src/BuildingBlocks/Paqueteria.Contracts/Paqueteria.Contracts.csproj
src/Modules/<Module>/<Module>.Domain.csproj
src/Modules/<Module>/<Module>.Application.csproj
src/Modules/<Module>/<Module>.Infrastructure.csproj
src/Modules/<Module>/<Module>.Endpoints.csproj
tests/Paqueteria.ArchitectureTests
tests/Paqueteria.UnitTests
tests/Paqueteria.IntegrationTests
tests/Paqueteria.ContractTests
tests/Paqueteria.EndToEndTests
```

## 2. Configuración global

- `net10.0`, nullable y implicit usings habilitados.
- warnings como errores en CI.
- `Directory.Build.props`, `Directory.Packages.props` y lockfiles.
- analyzers de seguridad/calidad.
- cultura de bordes `es-MX`; persistencia UTC.
- versiones de contratos explícitas.

## 3. Plantilla de módulo

```text
Modules/Orders/
  Orders.Domain/
    Aggregates/
    ValueObjects/
    Events/
    Policies/
  Orders.Application/
    Commands/
    Queries/
    Authorization/
    Ports/
  Orders.Infrastructure/
    Persistence/
    Configurations/
    Repositories/
  Orders.Endpoints/
    Controllers/
    Contracts/
    DependencyInjection.cs
```

Cada módulo define su esquema PostgreSQL y no exporta repositorios/entidades internos. Cross-module usa contratos o eventos.

## 4. Persistencia

- un `DbContext` por módulo, esquema propio;
- un coordinador transaccional permite compartir una transacción Npgsql únicamente para flujos críticos documentados;
- outbox y auditoría se escriben en la misma transacción;
- migraciones por módulo, aplicadas por pipeline;
- RLS configurado y probado por esquema;
- Npgsql + NetTopologySuite;
- pool de conexiones configurable y preparado para PgBouncer;
- queries críticas etiquetadas y observables.

Flujos atómicos permitidos inicialmente:

1. quote snapshot -> order;
2. assignment -> order status/event;
3. POD/custody -> order transition;
4. COD reconciliation -> close;
5. external offer acceptance -> assignment.

## 5. API

- controllers y `/api/v1`;
- middleware: correlation, tenant, idempotencia, exception mapping, rate limit;
- Problem Details RFC 9457;
- health `live`, `ready` y dependencias;
- API stateless;
- OpenAPI generado y comparado con contrato;
- no guardar archivos ni sesiones críticas localmente.

## 6. SignalR

- `OperationsHub : Hub<IOperationsClient>`;
- `DriverHub : Hub<IDriverClient>`;
- `TrackingHub : Hub<ITrackingClient>`;
- publicación mediante `IRealtimePublisher`/`IHubContext` desde Worker/outbox;
- grupos autorizados en servidor;
- eventos con versión y audience;
- cliente web con reconexión, deduplicación y sync REST;
- configuración de Redis backplane detrás de opción cuando existan varias API.

## 7. Worker

Consumers separados lógicamente:

- OutboxDispatcher;
- RealtimePublisher;
- NotificationDispatcher;
- FileProcessor;
- ReportGenerator;
- SettlementProcessor;
- ScheduledTasks.

Cada consumer implementa idempotencia, lease/locking, retries, dead-letter/review state, métricas y graceful shutdown. La concurrencia se configura por consumer para permitir scale-out independiente.

## 8. Frontend

```text
apps/web/src/
  app/
    admin/
    operations/
    business/
    ally/
    driver/
    track/[token]/
  features/
  contracts/
  realtime/
  offline/
  observability/
```

- TypeScript estricto;
- contratos generados desde OpenAPI y SignalR;
- PWA móvil primero;
- cola offline solo para acciones autorizadas y con idempotency key;
- revalidación REST después de reconexión;
- ninguna regla crítica solo en cliente.

## 9. Infraestructura local

Docker Compose incluye:

- PostgreSQL/PostGIS;
- Redis;
- MinIO;
- proveedor OIDC mock;
- mail/messaging fake;
- OpenTelemetry collector opcional;
- API, Worker y Web opcionales por perfil.

## 10. Observabilidad

Crear librería común con:

- ActivitySource/Meter;
- enrichment tenant/correlation sin PII;
- health checks;
- métricas de HTTP, hubs, outbox, workers, DB y proveedores;
- dashboards/runbooks mínimos.

## 11. Preparación para escala

Desde FND-001:

- API stateless;
- Data Protection keys externalizables;
- almacenamiento S3-compatible;
- Redis para locks/cache/backplane;
- workers reentrantes;
- migraciones expand/contract;
- configuración por ciudad/zona;
- endpoints y eventos versionados;
- ningún singleton mutable con estado de negocio.

La topología por fases y thresholds se implementa conforme a `AI-15_SCALABILITY_CONTRACT.yaml`.

## 12. Paquetes permitidos inicialmente

- Microsoft.AspNetCore.OpenApi
- Microsoft.EntityFrameworkCore
- Npgsql.EntityFrameworkCore.PostgreSQL
- NetTopologySuite / Npgsql NetTopologySuite
- StackExchange.Redis
- OpenTelemetry.Extensions.Hosting y exporters aprobados
- xUnit
- Microsoft.AspNetCore.Mvc.Testing
- Testcontainers
- FluentAssertions o equivalente
- FluentValidation si se adopta uniformemente

No añadir buses, mediators, ORMs adicionales, repositorios genéricos o frameworks de microservicios sin ADR.

## 13. Comandos de aceptación de Foundation

```bash
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
pnpm --dir apps/web install --frozen-lockfile
pnpm --dir apps/web lint
pnpm --dir apps/web typecheck
pnpm --dir apps/web test
docker compose config
```

El repositorio debe exponer un comando raíz equivalente y generar evidencia reproducible.


## 13. Implementación tenant v0.5

- Cada `DbContext` fija su esquema mediante `HasDefaultSchema`/`ToTable`.
- La unidad de trabajo abre `BeginTransactionAsync`, aplica `set_config(..., true)` y solo entonces ejecuta repositorios.
- La estrategia de reintentos envuelve toda la unidad de trabajo; el contexto se reaplica por intento.
- Un guard de desarrollo/test impide ejecutar comandos tenant sin transacción.
- `X-Organization-Id` selecciona el contexto activo; `/me/organization-contexts` se resuelve por bootstrap.

## 14. Workers y privilegio mínimo

`Paqueteria.Worker` usa `paqueteria_worker NOBYPASSRLS`. Reclama mensajes mediante funciones de `paqueteria_outbox_executor`; después abre una transacción tenant con los orgs del envelope. Telemetría usa consumer y tabla independientes. Mantenimiento elevado requiere consumidor, función y ADR específicos.

## 15. Pruebas de arquitectura adicionales

- catálogo de esquemas/FKs contra matriz permitida;
- lista blanca de los cinco coordinadores transaccionales;
- grants por columna y mutabilidad append-only;
- retry/pooling sin fuga tenant;
- API stateless y POD sin multipart/disco local.


## 14. BuildingBlocks obligatorios v0.6

### 14.1 TenantTransactionContext

La unidad de trabajo abre transacción y ejecuta, con parámetros Npgsql:

```sql
SELECT set_config('app.current_user_id', @user_id::uuid::text, true);
SELECT set_config('app.current_org_ids', @organization_ids::uuid[]::text, true);
```

`organization_ids` es un arreglo deduplicado y ordenado; vacío se envía como `Array.Empty<Guid>()` y produce `{}`. El componente reaplica el contexto dentro de cada intento de `CreateExecutionStrategy().ExecuteAsync`.

### 14.2 Outbox EF mappings

`OutboxEvent` y `LocationOutboxEvent` usan UUID y timestamps generados por aplicación. Todas las propiedades insertadas están configuradas con `ValueGeneratedNever`. Una prueba de interceptor/logger confirma que el INSERT no incluye `RETURNING`.

Los consumers no adjuntan ni modifican entidades outbox mediante `DbContext`; invocan exclusivamente funciones `claim/settle/requeue/purge`.

### 14.3 OrderAcceptanceCanonicalizer

Crear `BuildingBlocks/Paqueteria.Contracts/Legal/OrderAcceptanceCanonicalizer`.

No usar serialización automática de una clase como contrato. Escribir las nueve claves en orden fijo mediante `Utf8JsonWriter`, sin indentación, con formatos:

- UUID: `D` minúscula.
- Timestamp: UTC `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`.
- Canal: mayúsculas.
- `actor_id`: UUID o `null`.
- UTF-8 sin BOM.

Exponer:

```csharp
ReadOnlyMemory<byte> Canonicalize(OrderAcceptanceEvidence value);
byte[] ComputeSha256(OrderAcceptanceEvidence value);
```

Los vectores de prueba son artefactos de compatibilidad y no se regeneran automáticamente.

### 14.4 PublicOrderStatusPolicy

La política C# contiene un `switch` exhaustivo sobre los 17 estados. Un valor no reconocido lanza `PublicStatusMappingException`. Los ContractTests comparan cada resultado con `AI-04` y con `security.map_public_order_status` en PostgreSQL real.

### 14.5 TrackingTokenHasher

Implementar una única utilidad C#:

```csharp
string CreateToken();        // 32 bytes + Base64URL sin padding
byte[] HashToken(string token); // SHA256(UTF8 exacto)
```

No aplicar trim, normalización ni conversión hexadecimal. El repositorio almacena `byte[]` de longitud 32.
