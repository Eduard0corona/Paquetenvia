# UI-001 fase 3: bandeja de trabajo de despacho

Decisión: `UI-PHASE3-INBOX-2026-10-10` (literal del owner, 2026-10-09: "avanza con la fase 3"; propuesta UX,
sección "Despacho: bandeja de trabajo"). Contrato: AI-07 ruta `/ops/inbox` y `screen_contracts.work_inbox`.

## Qué cambia

- Ruta nueva `/ops/inbox` ("Bandeja de trabajo"), página de inicio de DISPATCHER y PLATFORM_ADMIN
  (`landingPathForRole`) y primer elemento del menú. La ofrece `canOpenWorkInbox`: los roles del tablero (OBS-001)
  y de `getOperationsQueueCounts`; VIEWER y los demás roles quedan fuera igual que allí, y la API responde 403.
- Menú en el orden aprobado hasta donde hay pantallas: Bandeja · Tablero · + Nueva orden · Rutas · Incidencias ·
  Importar CSV, y luego los elementos de finanzas del rol. `/ops/dashboard` no cambia y se llama "Tablero";
  `?view=positions` abre su vista de posiciones ("Mapa de posiciones" desde la bandeja). El detalle de una orden
  pertenece a Bandeja en el menú.
- Sin endpoints nuevos: la bandeja solo usa operaciones existentes (el Total agrega un campo al read model del
  tablero y "Precio por revisar" un filtro, ver abajo).

## Total de la orden (UI-PHASE3-INBOX-TOTAL-2026-10-10)

La columna Total de la tabla aprobada ("Guía, Estado, Destino, Ventana, Repartidor, Total") quedó pendiente en la
primera entrega porque el read model del tablero no traía el total. Ahora:

- `GET /api/v1/operations/dashboard` (OBS-001, ADR-OBS-001, fuera de AI-05) agrega `total` a cada orden: el
  `orders.total_cents` guardado, con IVA incluido (GATE-011-VAT-INCLUDED-2026-09-29), en la forma `Money` de AI-05
  `Order.total` (`{"currency":"MXN","amount_cents":<int64>}`). Se lee en la misma transacción con RLS que el resto de
  la proyección; un total negativo o en otra moneda falla cerrado (503 genérico).
- Sin cambio de autorización: el tablero sigue admitiendo solo DISPATCHER y PLATFORM_ADMIN con MFA, que ya leen ese
  mismo total (como dueña u operadora, con la misma RLS) en `listOrders`/`getOrder` (`Order.total`) y en
  `getOrderFinancials` (`revenue_cents`). FINANCE, VIEWER y los demás roles siguen recibiendo el 403 uniforme antes de
  leer órdenes. No se agrega tarifa, desglose, costo de asignación, margen ni cobro contra entrega.
- La web exige `total` en el parser (llaves exactas, MXN, centavos enteros seguros y no negativos) y la tabla muestra
  "Total (IVA incluido)" después de Repartidor con el componente `Money` (centavos enteros, sin punto flotante),
  alineado a la derecha.

## Colas

| Pestaña | Conteo (`getOperationsQueueCounts`) | Lista (`GET /operations/dashboard`) |
| --- | --- | --- |
| Sin asignar | `queues.unassigned` | `unassigned=true` |
| Requiere atención | `queues.needs_attention` | `status=` FAILED_ATTEMPT, RESCHEDULED, RETURNING, CLAIM_OPEN (4 consultas) |
| Precio por revisar | `queues.price_review` | `price_review=true` (solo órdenes que no han terminado) |
| Entregadas sin cerrar | `queues.delivered_not_closed` | `status=DELIVERED` |
| En ruta | `queues.en_route` | `status=` IN_TRANSIT, DELIVERING (2 consultas) |

- Los conteos cubren toda la organización y nunca aplican los chips (la pantalla lo dice); "Sin dato" si fallan,
  como en el tablero (UI-PHASE2-QUEUE-COUNTS-2026-10-05). Se vuelven a leer con cada lectura completa de la lista.
- Ninguna lista se arma filtrando filas cargadas: cada cola es una consulta del servidor (ver "Precio por revisar"
  abajo).

## Precio por revisar y asignación en lote (UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11)

Respuestas literales del owner (2026-10-11), con la fase 3 aprobada con "avanza con la fase 3":

1. "Bandeja, "Precio por revisar": hoy es solo un número y cuenta también órdenes ya terminadas, así que nunca baja a
   cero. ¿Cómo la quieres?" → "Solo órdenes activas (Recommended)" ("Lista y conteo solo de órdenes que no han
   terminado; al terminar, salen solas. Nadie tiene que marcar nada.").
2. "Bandeja, "Asignar seleccionadas…": cada asignación lleva un costo. Al asignar varias órdenes a un repartidor, ¿cómo
   se pone el costo? Siempre hay vista previa antes de ejecutar." → "Costo por orden (Recommended)" ("La vista previa
   muestra cada orden con su costo para capturar; cuando exista el tabulador, se llena solo.").

### Precio por revisar: solo órdenes activas

- "Terminada" es el grupo "Terminadas" de UI-STATUS-GROUPS-2026-10-05: DELIVERED, RETURNED, CLAIM_RESOLVED, CLOSED y
  CANCELLED (`OperationsQueuePolicy.FinishedStatuses`, `activeOrderStatuses` en la web). La terminalidad de AI-04, el
  plazo de reclamación y LIF-001 no cambian; una orden en CLAIM_OPEN vuelve a estar activa.
- `GET /api/v1/operations/dashboard?price_review=true` (ADR-OBS-001, enmienda 2026-10-11): solo órdenes con
  `cost_warning` (total menor que el mínimo de referencia o envío de bajo monto autorizado) cuyo estado no está
  terminado. Se evalúa en el WHERE de la misma consulta con RLS, así que el cursor pagina las filas ya filtradas; una
  fila fuera de la regla responde 503 (el lector lo verifica con `OperationsQueuePolicy.IsPriceReview`). Se lee como
  `unassigned`: `false` o ausente no filtra; otro valor o repetido es 400.
- `getOperationsQueueCounts` (AI-05) cuenta `queues.price_review` con el mismo predicado SQL; un conteo de precio en un
  estado terminado falla cerrado, y la web rechaza un `price_review` mayor que las órdenes no terminadas.
- La pestaña "Precio por revisar" es una lista más: una consulta, sin chips de estado (serían doce); servicio y zona sí
  filtran. La web vuelve a comprobar cada fila (`isPriceReviewRow`): si el servidor ignorara el filtro, la lectura falla
  con "No fue posible actualizar la bandeja." y no lista nada fuera de la regla.
- La marca "Precio por revisar" de la columna Estado sigue saliendo con cualquier `cost_warning`, también en órdenes
  terminadas de otras colas (por ejemplo, Entregadas sin cerrar); no se cambió sin decisión.

### Asignar seleccionadas… (costo por orden)

- Casillas solo en filas con `unassigned_alert` (las que el servidor marca como asignables) y "Seleccionar todas" en el
  encabezado para las filas cargadas. Máximo 20 órdenes por lote (`bulkSelectionLimit`), y la pantalla lo dice. Pie de
  la tabla como en el diseño: "Mostrando 4 de 6 sin asignar · seleccionadas: 0" ("de M" es el conteo del servidor, solo
  sin chips y si no es menor que lo mostrado).
- "Asignar seleccionadas…" toma la selección en ese momento (el lote) y congela las casillas y los "Asignar" de fila
  mientras el panel está abierto. El lote pertenece a la sesión: cambiar de cola no lo detiene.
- Repartidores: `listAssignableDrivers` por orden, todas las páginas (máximo 10), una orden tras otra. Solo se pueden
  elegir los elegibles para todas las órdenes (`intersectEligibility`); los demás aparecen con las órdenes que los
  bloquean y el motivo en español. Una orden cuya lista no se pudo leer lo dice y se puede reintentar o quitar del lote.
- Vista previa: una fila por orden con Guía, Destino (zona), Total (IVA incluido) y "Costo de la asignación (MXN)",
  vacío y obligatorio, convertido con `parseMxnToCents` a centavos enteros (cero permitido, como en el selector de una
  orden). Nada se llena solo: el tabulador aún no existe. La suma se calcula con `sumCents` (entero seguro o se
  rechaza).
- Confirmación (ConfirmDialog): repartidor (`DRV-`), número de órdenes y suma de los costos.
- Ejecución (`state/bulk-assignment-controller.ts`): `assignDriver` una orden tras otra, cada una con su Idempotency-Key
  (`PendingSubmissions`, por orden, con el mismo cuerpo) que se conserva hasta una respuesta definitiva y se reutiliza
  al reenviar. Un 409 o 404 responde solo por esa orden y se sigue con la siguiente; cualquier otra falla (sesión, rol,
  MFA, red, servidor o respuesta inesperada) detiene el lote y las demás quedan "Sin enviar". "Reintentar las
  pendientes" reenvía, con las mismas llaves, solo las que una falla de red o servidor dejó sin respuesta.
- Resultado por fila ("Asignada a DRV-…" o el mensaje del código de AI-05, los mismos del selector de una orden) y un
  resumen que no oculta fallas parciales. Después de cada envío se vuelven a leer la cola y los conteos.
- Es una secuencia de operaciones existentes de una sola orden: no es un flujo atómico nuevo de AI-13 §4 ni cambia
  estados de orden. Nada se guarda en el navegador ni se registra en consola.

## Combinación de consultas (`state/inbox-merge.ts`)

Una cola de varios estados es una consulta por estado, cada una con su cursor y el orden del servidor
(`updated_at DESC, order_id DESC`, con precisión de microsegundos). Las filas se combinan en ese orden y una fila
solo se muestra cuando todas las consultas que aún tienen páginas ya llegaron a ella (el "borde" es la última fila
más reciente entre las consultas no terminadas). "Cargar más" pide la página siguiente solo de las consultas con
cursor; así nunca se inserta una fila más antigua encima de una mostrada ni se salta una. Una orden que aparece en
dos consultas (cambió de estado entre lecturas) se muestra una vez, con la versión más alta. Un cursor repetido
detiene la paginación con un aviso.

## Vista en la URL

`/ops/inbox?queue=…&status=…&service_type=…&zone=…` (`contracts/inbox.ts`). Lo desconocido se descarta: cola
inválida es "Sin asignar"; estado fuera de la cola, servicio desconocido o zona que no es un UUID canónico no
filtran. Cada fila abre `/ops/orders/{id}?inbox=<vista>` y el detalle muestra "Volver a la bandeja" hacia la URL
canónica reconstruida (ruta fija, vista parseada otra vez: el parámetro no puede llevar a otro sitio).

"Mapa de posiciones" abre `/ops/dashboard?view=positions` (`contracts/dashboard-view.ts`). El tablero lee la vista de
la URL al renderizar (`useSearchParams`, con la página dentro de `Suspense`): el primer HTML ya es la vista de
posiciones, sin pintar la lista antes, y una navegación posterior a la misma ruta con otra query (por ejemplo
"Tablero" en el menú) muestra la vista que nombra. Los botones Lista/Posiciones reescriben la URL en su lugar con
`history.replaceState` (Next.js sincroniza `useSearchParams`): sin petición al servidor, sin entrada nueva en el
historial y sin recargar las órdenes. Corrige el hallazgo de revisión del PR #212 (efecto con `[]` y `setTimeout`).

## Pantalla (`components/operations-inbox-shell.tsx`)

- Pestañas de colas con conteo; chips de filtro que son filtros del servidor (estado de la cola, servicio, zona de
  entrega vista en las filas cargadas) y "Limpiar filtros". La búsqueda sigue siendo "Buscar guía" del encabezado.
- Tabla: Guía, Estado (estado exacto con el color de su grupo y la marca "Precio por revisar" si hay
  `cost_warning`), Destino (zona de entrega), Ventana de entrega (hora de Mazatlán, "Horario de la zona" si no hay),
  Repartidor (`DRV-`) y Total (IVA incluido). Sin ids internos ni datos personales; el único monto es el total de la
  orden.
- Acciones por fila: "Abrir" (o clic en la fila) y, solo si el servidor marca `unassigned_alert`, "Asignar", que
  abre el mismo selector del detalle (UI-PHASE2-DRIVER-PICKER-2026-10-05: lista, centavos enteros, confirmación con
  repartidor y costo). Tras asignar se vuelven a leer la cola y los conteos.
- Acción en lote: "Asignar seleccionadas…" (ver arriba), siempre con vista previa y confirmación. No hay cambio de
  estado en lote.
- Estados: cargando, vacía por cola, error conservando filas, sin sesión y acceso no disponible.
- Actualización: igual que el tablero (ADR-OBS-001): evento en tiempo real con 250 ms, reconexión obligatoria por
  REST, visibilidad y sondeo de 30 s; las lecturas en segundo plano esperan si la página está oculta. 401/403
  limpia todo y detiene el hub. Todo vive en memoria; un cambio de organización empieza de cero.

## Pendiente (no se simula)

- "Agregar a ruta…" en lote, del diseño aprobado: `addRouteStop` solo acepta órdenes ya asignadas (OWN) al repartidor
  de la ruta y ninguna cola de la bandeja lista esas órdenes (una orden asignada sale de "Sin asignar"). Queda para un
  cambio posterior; no se muestra un botón deshabilitado.
- El costo sugerido por el tabulador: el owner aún no envía sus montos; hoy cada costo se captura a mano.
- Entradas "Órdenes" y "Repartidores" del menú aprobado: aún no hay pantallas.

## Validación

```bash
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm run test && pnpm run build
python3 docs/normative/v0.6/tools/validate_contracts.py
dotnet test tests/Paqueteria.ArchitectureTests --filter "FullyQualifiedName!~ModuleTemplateTests"
dotnet test tests/Paqueteria.UnitTests --filter "FullyQualifiedName~Reporting"
dotnet test tests/Paqueteria.ContractTests --filter "Category!=PostgreSqlContract"
dotnet test tests/Paqueteria.ContractTests --filter "FullyQualifiedName~OperationsQueueCountsPostgreSql|FullyQualifiedName~OperationsPriceReviewPostgreSql"
dotnet test tests/Paqueteria.IntegrationTests --filter "Category=OperationsDashboardPostgreSql|Category=OperationsDashboardPwa"
```

## Rollback

Revertir el commit. No hay datos, esquema ni API que deshacer; DISPATCHER y PLATFORM_ADMIN vuelven a entrar al
tablero. Revertir el commit de "Precio por revisar" y la asignación en lote quita el filtro `price_review`, devuelve a
`queues.price_review` el conteo de todas las órdenes con aviso y deja la pestaña como conteo; las asignaciones ya hechas
quedan como cualquier asignación individual (no hay datos, esquema ni migración que deshacer). Revertir el commit del Total quita `total` del read model del tablero y la columna de la bandeja a la vez
(web y API se despliegan juntas); no hay datos, esquema ni migración que deshacer.
