# UI-001 fase 2B: asignar repartidor desde el detalle de la orden

Decisión: `UI-PHASE2-DRIVER-PICKER-2026-10-05` (literal del owner: "Sí a los 5 grupos de estado, avanza con
la fase 2"). El despachador asigna un repartidor propio (OWN) eligiéndolo de una lista; nunca escribe un ID.

## API

`GET /api/v1/orders/{orderId}/assignable-drivers` (`listAssignableDrivers`, módulo Dispatch):

```json
{
  "items": [
    {
      "driver_id": "…",
      "driver_reference": "DRV-1a2b3c4d",
      "vehicle_type": "MOTORCYCLE",
      "eligible": false,
      "ineligibility_reasons": ["DOCUMENT_EXPIRED"],
      "active_assignment_count": 1
    }
  ],
  "next_cursor": null
}
```

- Lista los perfiles OWN de la organización activa que no están `INACTIVE`, ordenados por id; página de 100
  fijada por el servidor; único parámetro `cursor` (Base64URL de versión + id). Otro parámetro, repetido o un
  cursor no emitido: `409 INVALID_REQUEST`, antes de la capacidad.
- Mismos roles que `assignDriver`: DISPATCHER y PLATFORM_ADMIN con MFA (`403 MFA_REQUIRED` si solo falta el
  segundo factor). La capacidad se revisa en el endpoint y otra vez dentro de la transacción antes de leer la orden.
- La orden debe ser visible como dueño u operador (igual que `assignDriver`); cada organización lista solo a sus
  repartidores. Orden mal formada, inexistente o ajena: 404 uniforme. Orden que no está `READY_FOR_PICKUP` /
  `RESCHEDULED`, o con asignación ACCEPTED/ACTIVE: `409 CONFLICT`.
- La elegibilidad usa `DriverEligibilityPolicy` con la ciudad, zona y paquetes de la orden y la versión de política
  de la organización del repartidor, igual que `assignDriver`. La lista es orientativa: `assignDriver` vuelve a
  evaluar en su transacción.
- Privacidad: AI-06 no guarda nombre de repartidor. Se muestra la referencia `DRV-` del tablero (OBS-001), calculada
  en `Paqueteria.Application.Privacy.DriverReference` (compartida con Reporting). No se lee ni devuelve correo,
  teléfono, documentos ni ubicación; los logs solo registran tenant, actor y conteos.
- Solo lectura en una transacción explícita (`set_config` después de `BEGIN`, `SET LOCAL ROLE paqueteria_app`, RLS
  forzado); sin `FOR UPDATE`, sin migración, índice, rol ni flujo nuevo (`assignments_driver_idx` ya existe).

## Web

`apps/web/src/operations/components/operations-driver-assignment.tsx` dentro del detalle de la orden:

- Visible solo si la orden está `READY_FOR_PICKUP`/`RESCHEDULED` sin asignación y el rol tiene
  `listAssignableDrivers` y `assignDriver` (espejo en `capabilities.ts`).
- Lista con radio por repartidor (referencia, vehículo, entregas en curso); los no elegibles quedan deshabilitados
  con el motivo en español. Si nadie es elegible se remite a "Publicar oferta externa desde el tablero".
- Costo en MXN con `parseMxnToCents` (centavos enteros), confirmación con `ConfirmDialog` (referencia y costo),
  `assignDriver` con Idempotency-Key reutilizada solo para reintentar el mismo pago tras fallo de red/servidor y
  recarga REST de la orden después del resultado.

## Validación

```bash
dotnet build
dotnet test tests/Paqueteria.UnitTests --no-build
dotnet test tests/Paqueteria.ArchitectureTests --no-build --filter "FullyQualifiedName!~ModuleTemplateTests"
dotnet test tests/Paqueteria.ContractTests --no-build --filter "FullyQualifiedName~DispatchPostgreSqlContractTests"
dotnet test tests/Paqueteria.IntegrationTests --no-build --filter "FullyQualifiedName~AssignableDriversHttpTests"
cd apps/web && pnpm run typecheck && pnpm run lint && pnpm exec vitest run && pnpm exec next build --webpack
python3 docs/normative/v0.6/tools/validate_contracts.py
```

## Rollback

Revertir el commit. No hay datos ni esquema que deshacer; `Dispatch:Provider=Disabled` responde 403 a la lista.
