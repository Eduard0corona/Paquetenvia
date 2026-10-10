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
- Sin cambios de API ni de backend: la bandeja solo usa operaciones existentes.

## Colas

| Pestaña | Conteo (`getOperationsQueueCounts`) | Lista (`GET /operations/dashboard`) |
| --- | --- | --- |
| Sin asignar | `queues.unassigned` | `unassigned=true` |
| Requiere atención | `queues.needs_attention` | `status=` FAILED_ATTEMPT, RESCHEDULED, RETURNING, CLAIM_OPEN (4 consultas) |
| Precio por revisar | `queues.price_review` | ninguna: solo conteo con nota |
| Entregadas sin cerrar | `queues.delivered_not_closed` | `status=DELIVERED` |
| En ruta | `queues.en_route` | `status=` IN_TRANSIT, DELIVERING (2 consultas) |

- Los conteos cubren toda la organización y nunca aplican los chips (la pantalla lo dice); "Sin dato" si fallan,
  como en el tablero (UI-PHASE2-QUEUE-COUNTS-2026-10-05). Se vuelven a leer con cada lectura completa de la lista.
- "Precio por revisar" no tiene filtro en el servidor y su conteo incluye órdenes terminadas: queda como conteo con
  nota. Nunca se arma su lista filtrando filas cargadas.

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

## Pantalla (`components/operations-inbox-shell.tsx`)

- Pestañas de colas con conteo; chips de filtro que son filtros del servidor (estado de la cola, servicio, zona de
  entrega vista en las filas cargadas) y "Limpiar filtros". La búsqueda sigue siendo "Buscar guía" del encabezado.
- Tabla: Guía, Estado (estado exacto con el color de su grupo y la marca "Precio por revisar" si hay
  `cost_warning`), Destino (zona de entrega), Ventana de entrega (hora de Mazatlán, "Horario de la zona" si no hay)
  y Repartidor (`DRV-`). Sin ids internos, datos personales ni montos.
- Acciones por fila: "Abrir" (o clic en la fila) y, solo si el servidor marca `unassigned_alert`, "Asignar", que
  abre el mismo selector del detalle (UI-PHASE2-DRIVER-PICKER-2026-10-05: lista, centavos enteros, confirmación con
  repartidor y costo). Tras asignar se vuelven a leer la cola y los conteos. No hay acciones masivas.
- Estados: cargando, vacía por cola, error conservando filas, sin sesión y acceso no disponible.
- Actualización: igual que el tablero (ADR-OBS-001): evento en tiempo real con 250 ms, reconexión obligatoria por
  REST, visibilidad y sondeo de 30 s; las lecturas en segundo plano esperan si la página está oculta. 401/403
  limpia todo y detiene el hub. Todo vive en memoria; un cambio de organización empieza de cero.

## Pendiente (no se simula)

- Columna Total: el read model del tablero no trae el total de la orden; mostrarlo requiere cambiar ese read model.
- Lista de "Precio por revisar": requiere un filtro de servidor por `cost_warning`.
- Acciones masivas, siempre con vista previa (AI-07 prohíbe "bulk status change without preview").
- Entradas "Órdenes" y "Repartidores" del menú aprobado: aún no hay pantallas.

## Validación

```bash
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm run test && pnpm run build
python3 docs/normative/v0.6/tools/validate_contracts.py
dotnet test tests/Paqueteria.ArchitectureTests --filter "FullyQualifiedName!~ModuleTemplateTests"
```

## Rollback

Revertir el commit. No hay datos, esquema ni API que deshacer; DISPATCHER y PLATFORM_ADMIN vuelven a entrar al
tablero.
