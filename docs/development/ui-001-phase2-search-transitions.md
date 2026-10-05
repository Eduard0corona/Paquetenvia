# UI-001 fase 2C: búsqueda por guía y acciones válidas de la orden

Decisión: `UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05` (literal del owner: "Sí a los 5 grupos de estado, avanza con
la fase 2"). La fase 2 incluye "Búsqueda y filtros en órdenes. `allowed_transitions` y datos de resumen en la
respuesta de orden" para que "la UI no duplique la máquina de estados"; el detalle muestra "solo las acciones válidas
ahora".

## API

### Búsqueda por guía: `GET /api/v1/orders?public_id=ORD_…` (`listOrders`)

- Coincidencia exacta (sensible a mayúsculas) del número de guía, formato AI-04 `ORD_` + 22 caracteres Base64URL
  (`OrderPublicIdPolicy`). Un valor con otro formato no llega a la base de datos y devuelve la página vacía.
- Se decide después de la capacidad `listOrders`; no agrega capacidad. Se combina con los demás filtros (los acota).
- Aislamiento: la misma RLS de `listOrders` (dueño u operador). Orden de otra organización, inexistente o mal
  formada: la misma respuesta `{"items":[],"next_cursor":null}`.
- Solo se busca por guía; nunca por nombre, teléfono ni otro dato personal. Sin `LIKE`, sin índice nuevo
  (`orders_public_id_key` ya es único).
- Por qué `listOrders` y no el tablero (`GET /operations/dashboard`): el tablero no está declarado en AI-05 (exención
  de `HttpSurfaceOpenApiCoverageTests` bajo OPS-002) y declararlo es otro cambio normativo; `listOrders` ya está en
  AI-05, admite a los roles del tablero y la UI solo necesita el `id` para abrir `/ops/orders/{id}`.

### `allowed_transitions` en `GET /api/v1/orders/{id}` (`getOrder`)

```json
"allowed_transitions": [
  { "target_status": "CONFIRMED", "required_metadata": ["restricted_goods_acknowledged"] },
  { "target_status": "CANCELLED", "required_metadata": [] }
]
```

- Calculado en la misma transacción de lectura por `OrderAllowedTransitionsPolicy` (Orders.Application), que reutiliza
  lo que `transitionOrder` ya ejecuta antes de sus guardas: solo dueño (ORD-002-OPERATOR-DRIVER-EVENTS-2026-10-03; el
  operador recibe `[]`), `OrderTransitionMatrix.EvaluateVersion`, aristas y `Evaluate` de `OrderTransitionMatrix`
  (terminales, ventana de reclamación y finalización de CLOSED al reloj del servidor) y `OrderTransitionAuthorizer` con
  el mismo `IOrderTransitionAuthorizationReader` (rol de la membresía y asignación del repartidor) y el MFA de la
  sesión. DISPATCHER: todas las aristas; PLATFORM_ADMIN: solo con MFA; DRIVER: sus pasos con asignación propia;
  VIEWER/FINANCE/otros: ninguna.
- `required_metadata` sale de `OrderTransitionInputPolicy.RequiredMetadataKeys`, con las mismas constantes que
  `TryNormalizeMetadata`. El motivo (`reason`) siempre es obligatorio y no se repite.
- No evalúa ni expone guardas (cotización, aceptación, asignación, evidencias, custodia, incidencias, COD,
  conciliación). Es orientativo: `transitionOrder` vuelve a validar todo y puede responder 409.

## Web

- Barra superior (`components/app-shell/order-search-box.tsx`): "Buscar guía" para DISPATCHER y PLATFORM_ADMIN
  (`canSearchOrders`: `listOrders` y los roles que abren el detalle). Coincidencia: navega a `/ops/orders/{id}`;
  cualquier otro caso: "No encontramos esa guía". El texto escrito no se guarda ni se registra.
- Detalle (`operations/components/operations-next-step.tsx`): "Siguiente paso" con un botón por transición permitida
  que la pantalla ofrece (Confirmar orden, Liberar para recolección, Cerrar orden, Cancelar orden en estilo de
  peligro). Sin botones deshabilitados. ASSIGNED (selector de repartidor), FAILED_ATTEMPT (requiere `incident_id`,
  desde Incidencias), pasos del repartidor, devoluciones y reclamaciones no se ofrecen aquí.
- Motivo obligatorio (≤500), casilla de artículos prohibidos cuando `required_metadata` la pide, `ConfirmDialog`,
  `transitionOrder` con `expected_version` leído e Idempotency-Key reutilizada solo para reintentar el mismo envío tras
  fallo de red/servidor; después se recarga la orden por REST. 409: "No se pudo cambiar el estado; la orden cambió o
  falta un requisito. Actualiza e intenta de nuevo." (los códigos de guarda llegan en la fase 2D).

## Validación

```bash
dotnet build
dotnet test tests/Paqueteria.UnitTests --no-build
dotnet test tests/Paqueteria.ArchitectureTests --no-build --filter "FullyQualifiedName!~ModuleTemplateTests"
dotnet test tests/Paqueteria.ContractTests --no-build --filter "FullyQualifiedName!~PostgreSql"
dotnet test tests/Paqueteria.ContractTests --no-build --filter "FullyQualifiedName~OrderSearchAndAllowedTransitionsPostgreSqlContractTests|FullyQualifiedName~OrdersPostgreSqlContractTests|FullyQualifiedName~FinancePostgreSqlContractTests"
dotnet test tests/Paqueteria.IntegrationTests --no-build --filter "FullyQualifiedName~Orders.OrderHttp|FullyQualifiedName~HttpSurfaceOpenApiCoverageTests|FullyQualifiedName~Playwright"
cd apps/web && pnpm run typecheck && pnpm run lint && pnpm exec vitest run && pnpm exec next build --webpack
python3 docs/normative/v0.6/tools/validate_contracts.py
```

## Rollback

Revertir el commit. No hay datos, esquema, roles ni grants que deshacer; un cliente anterior ignora el parámetro
`public_id` ausente y el parser web acepta un `OrderDetail` sin `allowed_transitions` (sin acciones).
