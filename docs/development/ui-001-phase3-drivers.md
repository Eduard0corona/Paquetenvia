# UI-001 fase 3: repartidores del despacho y selector de repartidor en liquidaciones

Decisión: `UI-PHASE3-DRIVERS-2026-10-11` (cambio MAJOR de AI-01 §7; la fila de `decision-log.md` es su registro ADR).
Literales del owner: fase 3 aprobada el 2026-10-09 ("avanza con la fase 3") y, el 2026-10-11, "Lista de consulta
(Recommended)" ("Referencia DRV-, vehículo, estado, paradas activas de hoy y hora de su última ubicación. Sin nombres
ni teléfonos (siguen pendientes por GATE-007) y sin altas ni bajas.") y "Sí, con referencia DRV- (Recommended)"
("Finanzas ve los repartidores de su organización como DRV- y vehículo, solo para elegir a quién liquidar. Periodos:
semana pasada, quincena pasada o fechas a mano."). No hay nombres de repartidor (UI-PHASE2-DRIVER-PICKER-2026-10-05,
"DRV- por ahora"): se muestra la referencia `DRV-`.

Contratos: AI-05 `listOrganizationDrivers` y `listSettlementPayees` (`x-capability-matrix.driver_list_operations`);
AI-07 ruta `/ops/drivers` (`screen_contracts.driver_roster`) y `/finance/settlements` (`screen_contracts.settlement_desk`).

## Por qué dos operaciones

El despacho y Finanzas no ven los mismos repartidores ni los mismos campos, y cada lista sirve a una operación distinta:

| | `listOrganizationDrivers` (`GET /api/v1/drivers`) | `listSettlementPayees` (`GET /api/v1/settlement-payees`) |
| --- | --- | --- |
| Módulo | Dispatch | Finance |
| Roles | DISPATCHER; PLATFORM_ADMIN con MFA (los del selector de asignación) | FINANCE; PLATFORM_ADMIN con MFA (los de `createSettlement`) |
| Repartidores | OWN de la organización activa que no están INACTIVE (los del selector) | Todos los perfiles de la organización, de cualquier tipo y estado: exactamente los que `createSettlement` acepta |
| Campos | `driver_id`, `driver_reference`, `vehicle_type`, `status`, `active_stop_count`, `last_position_at` | `driver_id`, `driver_reference`, `vehicle_type` |

- Finanzas no recibe estado, paradas ni ubicaciones: para elegir a quién pagar no hacen falta ("solo para elegir a quién
  liquidar"). Su lista incluye repartidores suspendidos, dados de baja, EXTERNAL y ALLY porque `createSettlement` los
  acepta como beneficiarios y un repartidor que ya se fue todavía puede tener trabajo por pagar; sin ellos el selector
  dejaría a alguien sin poder cobrar.
- `driver_id` va en las dos listas como clave estable y, en Finanzas, porque `createSettlement` y el filtro `payee_id` lo
  piden; la web nunca lo muestra. El despacho ya recibe esos ids en `listAssignableDrivers`.
- Lo común: un solo parámetro `cursor` (al menos una vez, emitido por la misma operación; otro parámetro, uno repetido o
  un cursor de otra lista es `409 INVALID_REQUEST` antes de la capacidad), página de 100 fijada por el servidor, orden
  por id, capacidad en el endpoint y otra vez dentro de la transacción antes de leer, 403 uniforme (`MFA_REQUIRED` si
  solo falta el segundo factor), sin 404 (no tienen recurso propio) y 503 si la base falla o un valor sale del
  vocabulario publicado.
- Los cursores comparten el formato del selector (`Paqueteria.Application.Paging.IdKeysetCursorCodec`: byte de versión
  e id en 16 bytes, Base64URL sin relleno) con versión propia: 1 selector, 2 repartidores, 3 beneficiarios. Así ninguna
  lista acepta el cursor de otra.

## Paradas activas y última ubicación

- Una parada activa es una asignación ACCEPTED o ACTIVE del repartidor, de una orden que la organización tiene como
  dueña u operadora, cuya orden está en un estado de parada de `DriverStopPolicy` (ASSIGNED, AT_PICKUP, PICKED_UP,
  IN_TRANSIT, DELIVERING, FAILED_ATTEMPT, RESCHEDULED o RETURNING): las mismas paradas que `listMyStops` muestra al
  repartidor. `DriverStopPolicy.OrderStatuses` es la única lista; una prueba de contrato la compara con la consulta de
  `listMyStops`. "De hoy" se lee como el trabajo en curso ahora, sin filtro por día: una asignación aceptada ayer que
  sigue activa es trabajo de hoy, y un filtro por fecha la escondería.
- La última ubicación es el `captured_at` de la posición más reciente (`captured_at`, luego `received_at`, luego id,
  igual que el tablero). Solo se lee esa hora; el punto, la precisión y las coordenadas nunca salen de PostgreSQL.
- Los índices `assignments_driver_idx` y `driver_positions_tenant_time_idx` ya cubren ambas lecturas.

## Privacidad y seguridad

- Solo lectura en una transacción explícita (`set_config` después de `BEGIN`, `SET LOCAL ROLE paqueteria_app`, RLS
  forzado), sin `FOR UPDATE`, sin migración, tabla, índice, rol, grant ni flujo cross-module nuevo. AI-06 y AI-18 no
  cambian.
- No se lee ni devuelve nombre, correo, teléfono (tampoco las columnas de VOICE-001), documentos ni coordenadas; las
  consultas no tocan `identity.users`, membresías ni documentos. Los logs solo registran tenant, actor y conteo.

## Web

### Repartidores (`/ops/drivers`)

- Elemento "Repartidores" del menú justo después de "Importar CSV", para los roles del selector (`canPerform(role,
  "listOrganizationDrivers")`); FINANCE, VIEWER y DRIVER no lo ven y la API responde 403.
- Tabla: Repartidor (`DRV-`), Vehículo, Estado (punto y texto: Pendiente de activar, Activo, Suspendido), Paradas
  activas y Última ubicación (hora de Mazatlán, "Sin ubicación registrada" si no hay). Orden por referencia.
- Estados: cargando, vacía, error que conserva las filas, sin sesión, acceso no disponible y "Verificar identidad" si
  solo falta el MFA. "Cargar más" sigue el cursor; un cursor repetido detiene la paginación con aviso.
- Se vuelve a leer al cargar, con "Actualizar", al volver a ser visible y al recuperar el foco (nunca oculta ni dos
  veces en 5 s). Sin acciones: no hay altas, bajas, asignaciones ni enlaces por fila. Todo vive en memoria.

### Liquidaciones (`/finance/settlements`)

- "Calcular liquidación" ya no pide pegar el ID: el repartidor se elige de `listSettlementPayees` ("DRV-1a2b3c4d ·
  Motocicleta", por referencia). La lista se lee completa al abrir y con "Actualizar" (hasta 10 páginas; si hay más, la
  pantalla lo dice). El filtro "Repartidor" usa la misma lista más "Todos".
- Periodo (`finance/contracts/settlement-period.ts`), siempre con la fecha de hoy en America/Mazatlan y nunca con la
  zona del dispositivo:
  - "Semana pasada": lunes a domingo de la semana anterior.
  - "Quincena pasada": del 1 al 15 o del 16 al fin de mes, la mitad anterior a la actual (fin de mes de 28, 29, 30 o 31).
  - "Fechas a mano": dos fechas capturadas.
  - La pantalla escribe las fechas de cada opción ("1–7 sep 2026", "28 sep – 4 oct 2026") y se vuelven a calcular al
    enviar. Las dos opciones terminan antes de hoy, así que siempre son periodos cerrados.
- Se conserva el `ConfirmDialog`: ahora nombra al repartidor por `DRV-` y vehículo y el periodo exacto que se envía; los
  montos y las reglas de MFA no cambian.
- Cada liquidación (lista, detalle y confirmaciones de aprobar, pagar, ajustar y anular) nombra a su repartidor por la
  referencia `DRV-` de la lista de beneficiarios y muestra el periodo en fechas legibles. Si esa lista falló o no
  tiene al repartidor (por ejemplo, porque se cortó en 10 páginas), se muestra "sin referencia" y un aviso; nunca el id.
  El CSV exportado conserva `payee_id` tal como lo publica AI-05.

## Validación

```bash
dotnet build Paqueteria.sln
dotnet test tests/Paqueteria.UnitTests --no-build
dotnet test tests/Paqueteria.ArchitectureTests --no-build --filter "FullyQualifiedName!~ModuleTemplateTests"
dotnet test tests/Paqueteria.ContractTests --no-build --filter "Category!=PostgreSqlContract"
dotnet test tests/Paqueteria.ContractTests --no-build --filter "FullyQualifiedName~DispatchPostgreSqlContractTests.Organization_drivers|FullyQualifiedName~SettlementWorkflowPostgreSqlContractTests.Payees"
dotnet test tests/Paqueteria.IntegrationTests --no-build --filter "FullyQualifiedName~OrganizationDriversHttpTests|FullyQualifiedName~SettlementHttpTests|FullyQualifiedName~HttpSurfaceOpenApiCoverageTests"
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm exec vitest run && pnpm run build
python3 docs/normative/v0.6/tools/validate_contracts.py
```

## Riesgos y pendientes

- La lista del despacho no incluye repartidores EXTERNAL ni dados de baja: no estaban en la lista aprobada y los
  EXTERNAL no reportan ubicación (DRV-003 solo acepta perfiles OWN). Si se quieren, es otra decisión.
- "Paradas activas de hoy" se interpreta como trabajo en curso ahora (ver arriba).
- Nombres y teléfonos siguen pendientes por GATE-007.
- La libreta de direcciones de "Nueva orden" queda en MVP-2 (`UI-PHASE3-ADDRESS-BOOK-MVP2-2026-10-11`).

## Rollback

Revertir el commit. No hay datos, esquema ni migración que deshacer; `Dispatch:Provider=Disabled` responde 403 a la
lista del despacho y `Finance:Provider=Disabled` responde 503 a la de beneficiarios. Sin la lista de beneficiarios, la
web anterior vuelve a pedir el ID pegado.
