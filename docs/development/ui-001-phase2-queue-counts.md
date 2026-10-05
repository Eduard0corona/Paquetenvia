# UI-001 fase 2E: conteos reales de la bandeja de operaciones

Decisión: `UI-PHASE2-QUEUE-COUNTS-2026-10-05` (literal del owner: "Sí a los 5 grupos de estado, avanza con
la fase 2"; la fase 2 incluye "Conteos para la bandeja": conteo real del servidor, porque hoy los indicadores
cuentan solo lo cargado).

## API

`GET /api/v1/operations/queue-counts` (`getOperationsQueueCounts`, módulo Reporting):

```json
{
  "generated_at": "2026-10-05T18:00:00+00:00",
  "total": 19,
  "by_status": { "DRAFT": 0, "CONFIRMED": 0, "READY_FOR_PICKUP": 3, "…": 0, "CANCELLED": 0 },
  "queues": {
    "unassigned": 2,
    "needs_attention": 4,
    "price_review": 1,
    "delivered_not_closed": 2,
    "en_route": 5
  }
}
```

- Módulo: Reporting. Es una proyección de lectura del tablero de operaciones (OBS-001) que cruza Orders y Dispatch
  (`dispatch.assignments` para "sin asignar"), igual que `PostgreSqlOperationsDashboardReader`. Orders no lee hoy
  la proyección del tablero ni su alerta de costo. `GET /operations/dashboard` sigue sin declararse en AI-05 (su
  propia exención OPS-002); esta operación sí se declara.
- Alcance: todas las órdenes que la organización activa lee como dueña u operadora por RLS (`orders_tenant`), igual
  que `listOrders` y el tablero; `owner_org_id` y `operator_org_id` siguen separados; otra organización nunca se
  cuenta.
- `by_status`: los 17 estados AI-04, con ceros; `total` es su suma.
- Colas (cada una repite una regla que el tablero ya aplica; ninguna regla nueva; pueden traslaparse):
  - `unassigned`: `READY_FOR_PICKUP` o `RESCHEDULED` sin asignación ACCEPTED/ACTIVE (`unassigned_alert`).
  - `needs_attention`: `FAILED_ATTEMPT`, `RESCHEDULED`, `RETURNING`, `CLAIM_OPEN` (grupo "Requiere atención").
  - `price_review`: lo que el tablero marca "Revisar precio" (`cost_warning` no nulo): total menor al mínimo
    congelado o un `financial_override` autorizado (`actor_id`, `reason`, `valid_until`).
  - `delivered_not_closed`: `DELIVERED`.
  - `en_route`: `IN_TRANSIT`, `DELIVERING` (grupo "En ruta").
- Sin parámetros ni filtros: cualquier query string es `400` antes de la capacidad y de leer órdenes.
- Roles: los del tablero (`OperationsRolePolicy`): DISPATCHER y PLATFORM_ADMIN con MFA (`403 MFA_REQUIRED` si solo
  falta el segundo factor), `x-capability-matrix.operations_queue_operations`. VIEWER, que el tablero no admite,
  recibe el 403 uniforme. El rol se revisa en el endpoint y otra vez dentro de la transacción.
- Una transacción explícita (`set_config` después de `BEGIN`, `SET LOCAL ROLE paqueteria_app`, NOBYPASSRLS) y una
  sola consulta agregada (`GROUP BY status` con `count(*) FILTER`); sin bloqueo de filas ni escrituras. Índices
  existentes: `orders_owner_status_idx`, `orders_operator_status_idx`, `one_active_assignment_per_order`. Sin
  migración, índice, rol, grant ni flujo nuevo.
- Respuesta: solo enteros y una marca de tiempo UTC; sin ids, guías, nombres, direcciones ni montos.
  `OperationsDashboard:Provider=Disabled` responde 503.

## Web

- `contracts/queue-counts.ts`: lector estricto (claves exactas, enteros seguros no negativos, `total` = suma,
  colas de estado = sus estados, `unassigned` ≤ READY_FOR_PICKUP + RESCHEDULED, `price_review` ≤ total, UTC);
  `statusGroupTotals` suma `by_status` por los 5 grupos.
- `state/queue-counts-loader.ts`: la respuesta más nueva gana y aborta la anterior; un fallo limpia los números.
- `use-operations-dashboard.ts`: pide los conteos en el arranque y en cada recarga que reemplaza la lista (botón
  Actualizar, evento en tiempo real, reconexión, visibilidad, sondeo de 30 s, cambio de filtro, oferta externa);
  no en la recarga por posición de repartidor, que no cambia estados. Al cambiar de sesión o perder acceso se
  limpian.
- `operations-dashboard-shell.tsx`: indicadores "Sin asignar", "Requiere atención", "Revisar precio",
  "Entregadas sin cerrar" y "En ruta" con el conteo del servidor ("…" mientras carga, "Sin dato" si falla) y la
  nota "Totales de todas las órdenes de la organización, sin aplicar filtros."; cada columna de grupo muestra las
  tarjetas cargadas y "N en total".
- Espejo de capacidad en `contracts/capabilities.ts` (`getOperationsQueueCounts`).

## Validación

```bash
dotnet build Paqueteria.sln
dotnet test tests/Paqueteria.UnitTests --no-build
dotnet test tests/Paqueteria.ArchitectureTests --no-build --filter "FullyQualifiedName!~ModuleTemplateTests"
dotnet test tests/Paqueteria.ContractTests --no-build --filter "Category!=PostgreSqlContract"
dotnet test tests/Paqueteria.ContractTests --no-build --filter "FullyQualifiedName~OperationsQueueCountsPostgreSqlContractTests"
dotnet test tests/Paqueteria.IntegrationTests --no-build --filter "FullyQualifiedName~OperationsQueueCountsHttpTests|FullyQualifiedName~HttpSurfaceOpenApiCoverageTests|Category=OperationsDashboardPostgreSql"
dotnet test tests/Paqueteria.IntegrationTests --no-build --filter "Category=OperationsDashboardPwa"
cd apps/web && pnpm run typecheck && pnpm run lint && pnpm exec vitest run && pnpm exec next build --webpack
python3 docs/normative/v0.6/tools/validate_contracts.py
```

## Rollback

Revertir el commit. No hay datos ni esquema que deshacer; la web vuelve a los indicadores anteriores.
