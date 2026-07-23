# ORD-001: creación y consulta de órdenes

ORD-001 materializa una orden `DRAFT` desde una cotización `ACTIVE`, permite listar y consultar el detalle tenant-scoped y conserva toda la evidencia técnica en una única transacción PostgreSQL. No implementa transiciones, despacho, tracking público ni procesamiento del outbox.

## Arquitectura

- `Orders.Domain`: `Order`, `PackageItem`, enums, invariantes monetarias y política del public ID, sin frameworks, JSON ni dependencias cross-module.
- `Orders.Application`: comandos, resultados inmutables, errores, `IOrderService`, `IOrderPublicIdGenerator` y cursor.
- `Orders.Infrastructure`: `OrdersDbContext`, configuración, persistencia, migración y `QuoteSnapshotToOrderCoordinator`.
- `Orders.Endpoints`: binding HTTP, autorización, validación superficial, errores y DTOs AI-05.

La única coordinación cross-schema es `quote_snapshot_to_order`. Orders no referencia `Pricing.Infrastructure`, `PricingDbContext`, `Locations.Infrastructure` ni `LocationsDbContext`.

## Coordinador, lock order y atomicidad

Cada intento usa una conexión, una transacción, `SET LOCAL ROLE paqueteria_app` y contexto tenant reaplicado por retry:

1. advisory lock de tenant, scope e idempotency key;
2. replay/conflicto de `platform.idempotency_keys` y reserva;
3. `SELECT ... FOR UPDATE` de `pricing.quotes`;
4. validación uniforme de tenant, `ACTIVE`, vigencia, single-use, geografía, dinero y packages;
5. order y package items;
6. acceptance, `ORDER_CREATED`, outbox y auditoría;
7. quote `ACTIVE -> USED` y `consumed_at`;
8. respuesta idempotente y commit.

Cualquier excepción revierte todo: quote `ACTIVE`, `consumed_at` nulo y cero order, packages, acceptance, eventos, outbox, auditoría e idempotencia. Las pruebas inyectan fallos en diez etapas.

## Idempotencia y single-use

El scope es `ORD-001:CREATE_ORDER`; la key usa la política compartida de 16 a 128 caracteres. El SHA-256 canónico contiene tenant, quote ID, payer type, versiones sintéticas, `accepted_at` normalizado y canal. Excluye actor derivado, request ID, headers, PII y tiempos de servidor.

Misma organización, key y hash reproduce la respuesta 201 sin insertar ni consumir otra vez. Hash diferente devuelve 409. Dos keys para una quote compiten bajo `FOR UPDATE`; una sola crea y `orders.quote_id` unique es el backstop. Quote no disponible, expirada, usada, revocada, cross-tenant o inexistente devuelve el mismo 409.

## Copia de quote y packages

Se copian sin recalcular ni inferir desde `breakdown`: quote ID, owner, client account, city, service area, origin, destination, service type, pricing tier, consolidated route, subtotal, discount, tax, total, minimum snapshot, currency, policy version, package snapshot y financial override.

`orders.package_items` se deriva solo de `quote.package_snapshot`. Cada item recibe UUID de aplicación, owner de order y operator nulo; copia descripción redactada, gramos, valor `bigint` y dimensiones JSONB, incluidas nulas. Un snapshot inválido falla cerrado y no consume la quote.

## Public ID

El formato es `ORD_` más 22 caracteres Base64URL sin padding. Se generan 16 bytes (128 bits) con `RandomNumberGenerator`; no incluye tiempo, tenant, quote, secuencias ni PII. PostgreSQL impone unicidad. Una colisión revierte la transacción, genera otro candidato y reintenta hasta el límite configurado.

## Aceptación legal

`Paqueteria.Contracts.Legal` implementa `order-acceptance-v1`: JSON compacto UTF-8 sin BOM en orden fijo (`schema_version`, `order_id`, `quote_id`, `owner_org_id`, `actor_id`, `terms_version`, `privacy_version`, `accepted_at_client`, `acceptance_channel`), UUID `D` lowercase, UTC con siete fracciones y canal uppercase.

```text
SHA-256: 2a09176e270ddcc52e0fee157f3d5bd869f36047f7f946daa7caed4816ae0b37
Base64:  KgkXbicN3MUuD+4Vfz1b2GnzYEf3+Ubap8rtSBauCzc=
```

`orders.order_acceptances` guarda una evidencia append-only por order. GATE-007 sigue abierto: solo actor interno, versiones sintéticas, timestamps, canal y hash. No se captura IP, user-agent, fingerprint, ubicación, firma, documentos ni PII adicional.

## Evento, outbox y auditoría

Se inserta solo `ORDER_CREATED`, versión 1, con payload mínimo. El outbox usa topic `orders.created`, aggregate `Order`, status `PENDING`, attempts 0 y valores explícitos. No se implementa claim, dispatch, settle ni Worker PostgreSQL.

AUD-001 registra `ORDER_CREATED` con actor, organización, order/quote IDs, payer type, pricing tier, total y request ID. Evento, outbox, auditoría y replay excluyen acceptance completa, packages, direcciones, contactos, teléfonos, ciphertext y PII.

## Endpoints y paginación

- `POST /api/v1/orders`: autenticación, organización activa, membresía e `Idempotency-Key`; 201/409/401/403.
- `GET /api/v1/orders`: `status`, `owner_org_id`, `cursor`; page size fijo y `created_at DESC, id DESC`.
- `GET /api/v1/orders/{orderId}`: `OrderDetail`; timeline expone solo `event_type` y `occurred_at`; 404 uniforme.

El cursor Base64URL es opaco, sin PII. Owner extranjero o cursor inválido produce página vacía. No hay parámetro `limit`.

## RLS, append-only y migración

`OrdersDbContext` mapea `orders.orders`, `package_items`, `order_events` y `order_acceptances`, con schemas, tipos, índices, relaciones, filtros tenant y valores de aplicación. Los INSERT no usan `RETURNING`.

AI-06/AI-18 conservan `ENABLE/FORCE RLS`, roles runtime `NOBYPASSRLS`, contexto vacío fail-closed y triggers/grants append-only para acceptance y eventos. Las pruebas usan PostgreSQL/PostGIS efímero de Testcontainers, nunca FND-002.

`20260722_AdoptCanonicalOrdersBaseline` usa `platform.__ef_migrations_history_orders`, corre después de Pricing y solo verifica tablas, tipos, índices y constraints. No crea, altera ni elimina; `Down` está vacío. API y Worker no migran al iniciar.

## Configuración

```json
{
  "Orders": {
    "Provider": "Disabled",
    "CommandTimeoutSeconds": 30,
    "PageSize": 50,
    "IdempotencyLifetimeMinutes": 1440,
    "PublicIdCollisionRetryCount": 3
  }
}
```

`Disabled` es el default fail-closed. `PostgreSql` exige conexión y límites válidos. No hay seeds, términos reales, datos reales ni secretos.

## Validación

```powershell
python .\docs\normative\v0.6\tools\validate_contracts.py
dotnet tool restore
dotnet restore --locked-mode
dotnet format Paqueteria.sln --verify-no-changes --no-restore
dotnet build Paqueteria.sln --configuration Debug --no-restore
dotnet test Paqueteria.sln --configuration Debug --no-build
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj --filter "Category=PostgreSqlContract"
dotnet ef migrations list --project .\src\Modules\Orders\Orders.Infrastructure\Orders.Infrastructure.csproj --startup-project .\src\Paqueteria.Api\Paqueteria.Api.csproj --context OrdersDbContext
.\tools\database-baseline.ps1 Verify
```

La matriz completa también ejecuta Release con `CI=true`, suites aisladas, web, Compose, auditoría de paquetes, hashes normativos y diff de `docs/normative/v0.6`.

## Riesgos y rollback

GATE-003, GATE-007, GATE-010, GATE-011, GATE-014 y GATE-017 siguen abiertos. Precios, geografía y textos legales son sintéticos; no hay política fiscal, PII legal, transiciones, dispatcher ni Worker.

Rollback:

1. usar `Orders:Provider=Disabled`;
2. revertir los commits de ORD-001;
3. no ejecutar DDL inverso: la migración adopta y no posee el baseline;
4. conservar órdenes, quotes usadas, evidencia, eventos, outbox, auditoría e idempotencia según retención.

No se debe editar AI-06/AI-18 ni convertir rollback en eliminación de datos.
