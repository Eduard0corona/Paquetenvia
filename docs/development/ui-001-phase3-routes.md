# UI-001 fase 3: Rutas, "Crear ruta" con selectores, sin IDs internos y con errores por motivo

Decisiones: `UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11` (abajo) y `UI-PHASE3-ROUTES-NO-IDS-2026-10-12` (sección "Rutas sin
IDs internos y con errores por motivo"); la regla de "Crear ruta nueva" por día de la bandeja es
`UI-PHASE3-ROUTES-DAILY-2026-10-12` (`ui-001-phase3-inbox.md`).

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

## Rutas sin IDs internos y con errores por motivo (UI-PHASE3-ROUTES-NO-IDS-2026-10-12)

Pregunta literal al owner (2026-10-12): "En la pantalla Rutas quedaron detalles fuera de lo que pedí: si falla "Crear
ruta" dice "La ruta cambió mientras la editabas…" aunque el motivo sea otro, no ofrece verificar identidad (MFA), la
lista muestra fechas como 2026-10-12 y solo la primera página, "Agregar entrega" pide pegar el ID de la orden y el
detalle muestra IDs cortos. ¿Los corrijo?". Respuesta literal: "Sí, todo (Recommended)", cuya opción decía "Mensaje por
motivo y verificar identidad, fechas legibles y todas las páginas, "Agregar entrega" por número de guía (PQ-…), y el
detalle muestra fecha, DRV- y guía en lugar de IDs." Contrato: AI-07 `route_planner` (`errors`, `list`, `add_order`,
`detail`, `not_supported` y las fuentes `order_lookup` y `order_guides`). AI-05 no cambia.

"PQ-…" venía del mockup del diseño. La guía real de este sistema es el `public_id` de la orden (`ORD_` y 22 caracteres,
el patrón de `listOrders.public_id` en AI-05), el mismo valor que pide "Buscar guía (ORD_…)" de la barra superior
(`apps/web/src/components/app-shell/order-search-box.tsx`); eso es lo que pide y muestra Rutas.

- Cliente (`api/routes-api.ts`): ahora usa el mismo requester y el mismo error que la bandeja (`createTenantRequester`,
  `TenantApiError` de `api/tenant-request.ts`, que admite PUT y DELETE para `reorderRouteStops` y `removeRouteStop`),
  así que el código del problema y MFA_REQUIRED llegan a la pantalla. Cada respuesta se valida contra lo pedido (la ruta
  creada en DRAFT, versión 1, sin paradas, con el repartidor, la ciudad, el área y la fecha pedidos; cada cambio de
  parada una versión después, con la orden agregada, sin la parada retirada o con el orden enviado) y si no, falla
  cerrado. Desaparece `RoutesApiError`.
- Estado (`state/manual-routes-controller.ts`, nuevo, y `state/use-manual-routes.ts`, que queda como enlace con React y
  con el hub de operaciones).
- Errores por motivo (`routeMutationFailure`), sin códigos ni identificadores:

  | Cambio | Código | Mensaje |
  | --- | --- | --- |
  | Crear ruta | DRIVER_INELIGIBLE | "No se creó la ruta: el repartidor no puede tomar rutas en esa ciudad o área de servicio. Puede que su ciudad sea otra o que hoy no esté habilitado (su estado o sus documentos)." |
  | Crear ruta | INVALID_REQUEST | "No se creó la ruta: el servidor no aceptó la solicitud. Revisa los datos e intenta de nuevo." |
  | Crear ruta | 404 uniforme | `createRouteNotFoundMessage` ("No se creó la ruta: el repartidor o la ciudad ya no están disponibles.") |
  | Crear ruta | CONFLICT, otro código, red, 5xx, respuesta inesperada | "No se pudo confirmar si se creó la ruta. Usa Actualizar para revisar la lista antes de volver a intentarlo." |
  | Agregar entrega | DRIVER_INELIGIBLE, CONFLICT | `routeStopConflictMessages` de la bandeja |
  | Agregar entrega | otro código | `routeStopDefaultConflictMessage` ("La orden no se pudo agregar a esta ruta.") |
  | Agregar entrega, Retirar | 404 uniforme | `routeStopNotFoundMessage` ("La ruta o la orden ya no están disponibles.") |
  | Retirar | CONFLICT u otro código | "No se pudo retirar la parada: la ruta ya no está en borrador o cambió. Usa Actualizar para revisarla." |
  | Subir, Bajar, arrastrar | INVALID_STOP_SET | "El nuevo orden no coincide con las paradas actuales de la ruta. Usa Actualizar y vuelve a ordenarlas." |
  | Subir, Bajar, arrastrar | CONFLICT u otro código | "No se pudo cambiar el orden: la ruta ya no está en borrador o cambió. Usa Actualizar para revisarla." |
  | Subir, Bajar, arrastrar | 404 uniforme | "La ruta ya no está disponible." |
  | Agregar, Retirar, ordenar | INVALID_REQUEST | "El servidor no aceptó la solicitud. Actualiza la ruta e intenta de nuevo." |
  | Agregar, Retirar, ordenar | VERSION_CONFLICT | "La ruta cambió mientras la editabas. Se cargó la versión más reciente; revisa y vuelve a intentar." |
  | Agregar, Retirar, ordenar | red, 5xx, respuesta inesperada | "No hubo respuesta válida del servidor y no se sabe si el cambio se guardó. Usa Actualizar para revisar la ruta antes de volver a intentarlo." |
  | Todos | 401, 403, 403 MFA_REQUIRED | Textos de `describeFailure`; MFA_REQUIRED con "Verificar identidad" |

  - Solo VERSION_CONFLICT dice que la ruta cambió, y solo él vuelve a leer la ruta; si esa lectura falla, lo dice ("…y
    no se pudo cargar la versión más reciente. Usa Actualizar para reintentar.").
  - Se reutilizan los mensajes existentes donde su texto sirve en Rutas. Los de la bandeja que nombran la bandeja o
    "estas órdenes" (INVALID_REQUEST de agregar y de crear, DRIVER_INELIGIBLE de crear) tienen aquí su versión de Rutas,
    y retirar y ordenar, que no tenían mensajes, tienen los suyos. `describeFailure` se usa solo para sesión, rol y MFA:
    su texto de red promete reenviar "la misma solicitud sin duplicarla", y Rutas no reenvía con la misma llave.
  - "Verificar identidad" regresa a `/ops/routes?routeId=<ruta mostrada>` (o a `/ops/routes` sin ruta), que vuelve a
    abrir la ruta si está en la lista.
  - Sin cambios de idempotencia: cada cambio sigue mandando su propia Idempotency-Key nueva, como antes.
- Lista: todas las páginas de `listRoutes`, una tras otra, en el orden del servidor (sin reordenar), con un máximo de 10
  páginas (500 rutas); si hay más, la lista dice "Hay más rutas de las que esta lista muestra; algunas no aparecen." y un
  cursor repetido hace fallar la lectura ("No fue posible cargar las rutas. Usa Actualizar para reintentar."). Cada ruta
  se lee "Ruta del 12 de octubre de 2026" o "Ruta sin fecha" (`routeName` en `contracts/manual-route.ts`), con su
  estado, paradas y costo total; una fecha que no es de calendario hace fallar la lectura.
- "Agregar entrega" por guía: el campo es "Guía (ORD_…)". Primero la validación de la barra superior
  (`normalizePublicIdInput`): un valor que no puede ser guía no llama a nada y dice "No encontramos esa guía". Luego
  `listOrders?public_id=` con el cliente de la barra superior (`findOrderIdByPublicId`): ninguna orden dice "No
  encontramos esa guía"; más de una o un `public_id` distinto fallan cerrado ("No pudimos buscar la guía. Intenta de
  nuevo."). Con la orden encontrada, `addRouteStop` con su id y la `expected_version` actual de la ruta. Si mientras se
  buscaba se abrió otra ruta, no se manda nada. Al guardarse, el campo se vacía.
- `?orderId=` (el enlace "Agregar a ruta" del tablero) sigue funcionando sin mostrar el id: se lee esa orden con
  `getOrder` y el campo se llena con su guía, con el aviso de siempre "Selecciona una ruta para agregar la orden
  indicada."; si no se puede leer, el campo queda vacío.
- Detalle sin IDs:
  - Encabezado: el nombre por fecha de la ruta, "Repartidor DRV-…" (de `listOrganizationDrivers`, la misma lectura del
    formulario, con cualquier estado; un repartidor que esa lectura no trae, por ejemplo uno inactivo, es "Repartidor no
    disponible"), el estado y las paradas; el costo total de asignaciones sigue igual. Ya no muestra "versión N".
  - Cada parada muestra su guía, leída con `getOrder` una orden tras otra (máximo 100 paradas por ruta, cancelable al
    abrir otra ruta o cerrar la pantalla, en memoria por orden mientras la pantalla está abierta); mientras se lee dice
    "Cargando guía…" y, si no se puede leer, "Guía no disponible", que se vuelve a intentar con la siguiente lectura de
    la ruta.
  - Las etiquetas accesibles usan la guía ("Mover orden ORD_… arriba", "Retirar orden ORD_… de la ruta") o la
    posición si no hay guía ("Mover parada 2 arriba"); la confirmación de retiro también.
  - `shortId` ya no se usa en esta pantalla.
- Avisos: el de un cambio guardado es informativo y el de una falla es de advertencia (antes todos se veían como
  críticos).
- Nada se guarda en localStorage, sessionStorage ni consola; sin datos personales. Los ids solo son llaves y valores de
  opciones, nunca texto.

## Pendiente

- Terminar o descartar rutas: AI-05 no tiene operación que saque una ruta de DRAFT (`RouteStatus` tiene PLANNED,
  ACTIVE, COMPLETED y CANCELLED, pero nada llega a ellos), así que toda ruta sigue en borrador y en la lista. Queda para
  después del piloto, por decisión del owner (UI-PHASE3-ROUTES-DAILY-2026-10-12, "Terminar o descartar rutas viejas
  queda para después del piloto."); no se construye nada para terminarlas ni descartarlas.
- Con rutas que nunca salen de borrador, la lista de Rutas (máximo 500) acabará diciendo que algunas no aparecen; la
  lista conserva el orden del servidor (de la más reciente a la más antigua) y no pone primero las de hoy.

## Validación

```bash
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm exec vitest run && pnpm run build
python3 docs/normative/v0.6/tools/validate_contracts.py
dotnet test tests/Paqueteria.IntegrationTests --filter "FullyQualifiedName~ManualRoutesPwaPlaywrightTests"
```

Las pruebas de navegador `ManualRoutesPwaPlaywrightTests` (build de producción de Next):

- `Planner_uses_authoritative_routes_for_mutations_conflicts_and_tenant_switches` elige repartidor y ciudad en los
  selectores (con `/api/v1/drivers`, `/api/v1/cities` y `/api/v1/service-areas` simulados) y comprueba que
  `createRoute` recibe el id del repartidor, el de la ciudad, `service_area_id` nulo y la fecha; agrega tres órdenes por
  guía (`/api/v1/orders?public_id=` simulado), rechaza una guía desconocida y una con otro formato, reordena, recibe un
  VERSION_CONFLICT (relee la ruta), un DRIVER_INELIGIBLE (sin releer) y un MFA_REQUIRED con "Verificar identidad" de
  regreso a la ruta, y en otra organización muestra "Repartidor no disponible" y la guía de su parada; ningún id se ve.
- `Planner_prefills_the_guia_of_orderId_and_names_every_stop_by_guia` abre Rutas con `?orderId=` y comprueba la guía
  en el campo y en la parada (`/api/v1/orders/{id}` simulado), que se puede agregar y que una orden ilegible deja el
  campo vacío.

## Rollback

Revertir el commit. El formulario vuelve a pedir los IDs pegados y el paso de la bandeja vuelve a mandar a Rutas para
crear la ruta. No hay datos, esquema, API ni migración que deshacer; las rutas ya creadas quedan como cualquier ruta en
borrador.

Revertir el commit de UI-PHASE3-ROUTES-NO-IDS-2026-10-12 (el mismo de UI-PHASE3-ROUTES-DAILY-2026-10-12) devuelve Rutas
a su cliente anterior: el mensaje único "La ruta cambió mientras la editabas…" para todo 409, sin "Verificar
identidad", fechas `YYYY-MM-DD`, solo la primera página, "ID de la orden" y los IDs cortos del detalle; AI-07 vuelve a
su texto anterior. No hay datos, esquema, API ni migración que deshacer.
