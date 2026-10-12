# UI-001 fase 3: Rutas, "Crear ruta" con selectores

Decisión: `UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11` (cambio MAJOR de AI-01 §7; la fila de `decision-log.md` es su
registro ADR). Fase 3 aprobada con "avanza con la fase 3"; la propuesta UX aprobada (UI-001) nombra "Las rutas piden el
UUID del repartidor" como problema por corregir. El 2026-10-11 el owner eligió "Sí, en el paso (Recommended)" para
"Crear ruta nueva" en la bandeja (`ui-001-phase3-inbox.md`) y no "Solo en Rutas" ("El paso sigue mandando a Rutas para
crearla; ahí cambio los IDs por selectores de repartidor y ciudad."); después se le dijo que el mismo cambio sustituye
también los campos de ID del formulario "Crear ruta" de Rutas por selectores, y no objetó.

Contrato: AI-07 ruta `/ops/routes` y `screen_contracts.route_planner.create_form`. AI-05 no cambia: se usan
`listOrganizationDrivers`, `listCities`, `listServiceAreas` y `createRoute` tal como están publicados.

## Qué cambia

El formulario "Crear ruta" de `/ops/routes` (`components/create-route-form.tsx`, estado en
`state/create-route-form-controller.ts` y `state/use-create-route-form.ts`) ya no pide pegar "ID del repartidor (flota
propia)", "ID de la ciudad" ni "ID del área de servicio (opcional)":

| Campo | Fuente | Qué muestra |
| --- | --- | --- |
| Repartidor (flota propia) | `listOrganizationDrivers` (`GET /api/v1/drivers`), todas las páginas (máximo 10, una tras otra), con el mismo cliente que Repartidores (`api/drivers-api.ts`) | Solo repartidores ACTIVE (`createRoute` pide un repartidor OWN ACTIVE), por referencia: "DRV-0a1b2c3d · Motocicleta · Activo", con las etiquetas de vehículo y estado de Repartidores |
| Ciudad | `listCities` (`GET /api/v1/cities`, `api/locations-api.ts`) | Nombre y estado: "Culiacán, SIN", en el orden del servidor |
| Área de servicio (opcional) | `listServiceAreas` (`GET /api/v1/service-areas?city_id=`), al elegir ciudad | Solo áreas ACTIVE de esa ciudad, más "Toda la ciudad" (sin área); al cambiar de ciudad vuelve a "Toda la ciudad" |
| Fecha | sin cambio | El mismo campo de fecha |

- Ningún id se escribe ni se muestra: los ids solo viajan como `value` de las opciones. Solo se puede elegir lo que la
  lista trajo (un repartidor que no está ACTIVE, un área INACTIVE o de otra ciudad no cuentan como elección).
- Estados por lista, en español: cargando ("Cargando repartidores…", "Cargando ciudades…", "Cargando áreas de
  servicio…"), vacía ("Tu flota no tiene repartidores activos; la ruta necesita uno.", "No hay ciudades disponibles.",
  "Esta ciudad no tiene áreas de servicio activas; la ruta cubre toda la ciudad.") y error ("No fue posible cargar los
  repartidores.", "No fue posible cargar las ciudades.", "No fue posible cargar las áreas de servicio de esa ciudad.",
  con "Intentar de nuevo"). Sesión, rol y MFA usan los textos de `describeFailure`; si solo falta el MFA aparece
  "Verificar identidad" de regreso a `/ops/routes`. Si hay más de 10 páginas de repartidores, el campo dice que algunos
  no aparecen; un cursor repetido hace fallar la lista.
- "Crear ruta (borrador)" queda deshabilitado hasta elegir repartidor y ciudad (y mientras se guarda otro cambio).
  Después, `createRoute` se envía igual que antes (`use-manual-routes.ts`, `routes-api.ts`).
- Las listas viven en memoria; un cambio de sesión u organización empieza de cero. Nada se guarda en el navegador ni se
  registra en consola.

## Lo que no cambia

El resto de Rutas queda igual en este cambio: la lista de rutas, el encabezado del detalle, el campo "ID de la orden",
las paradas, el reordenamiento y los mensajes de error de las mutaciones (incluido el de un 409 al crear).

## Validación

```bash
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm exec vitest run && pnpm run build
python3 docs/normative/v0.6/tools/validate_contracts.py
dotnet test tests/Paqueteria.IntegrationTests --filter "FullyQualifiedName~ManualRoutesPwaPlaywrightTests"
```

La prueba de navegador `ManualRoutesPwaPlaywrightTests` elige repartidor y ciudad en los selectores (con
`/api/v1/drivers`, `/api/v1/cities` y `/api/v1/service-areas` simulados) y comprueba que `createRoute` recibe el id del
repartidor, el de la ciudad y `service_area_id` nulo.

## Rollback

Revertir el commit. El formulario vuelve a pedir los IDs pegados y el paso de la bandeja vuelve a mandar a Rutas para
crear la ruta. No hay datos, esquema, API ni migración que deshacer; las rutas ya creadas quedan como cualquier ruta en
borrador.
