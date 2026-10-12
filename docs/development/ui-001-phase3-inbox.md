# UI-001 fase 3: bandeja de trabajo de despacho

Decisión: `UI-PHASE3-INBOX-2026-10-10` (literal del owner, 2026-10-09: "avanza con la fase 3"; propuesta UX,
sección "Despacho: bandeja de trabajo"). Contrato: AI-07 ruta `/ops/inbox` y `screen_contracts.work_inbox`.

## Qué cambia

- Ruta nueva `/ops/inbox` ("Bandeja de trabajo"), página de inicio de DISPATCHER y PLATFORM_ADMIN
  (`landingPathForRole`) y primer elemento del menú. La ofrece `canOpenWorkInbox`: los roles del tablero (OBS-001)
  y de `getOperationsQueueCounts`; VIEWER y los demás roles quedan fuera igual que allí, y la API responde 403.
- Menú en el orden aprobado hasta donde hay pantallas: Bandeja · Tablero · + Nueva orden · Rutas · Incidencias ·
  Importar CSV · Repartidores (UI-PHASE3-DRIVERS-2026-10-11), y luego los elementos de finanzas del rol. `/ops/dashboard` no cambia y se llama "Tablero";
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

## Agregar estas órdenes a una ruta… (UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11)

Respuesta literal del owner (2026-10-11) a "Bandeja, "Agregar a ruta…" en lote: el servidor solo acepta en una ruta
órdenes que ya tienen repartidor, así que no sirve sobre "Sin asignar". ¿Dónde lo ofrecemos?": "Al terminar el lote
(Recommended)" ("Después de "Asignar seleccionadas…" aparece "Agregar estas órdenes a una ruta…" con las mismas órdenes
y el mismo repartidor, con vista previa."). Contrato: AI-07 `work_inbox.bulk_add_to_route`.

Regla del servidor que lo explica (`PostgreSqlRouteService.AddStopAsync` y `ManualRoutePolicy.CanLinkAssignment`):
`addRouteStop` solo acepta una ruta DRAFT con la versión esperada y una orden con asignación OWN ACCEPTED o ACTIVE del
repartidor de la ruta que aún no está en otra ruta; además revisa ciudad, área de servicio y elegibilidad DSP-001
(DRIVER_INELIGIBLE). Las órdenes que acaba de asignar "Asignar seleccionadas…" cumplen la parte de la asignación.

- Cuándo (`contracts/bulk-route.ts`, `bulkRouteOffer`): solo cuando el lote terminó con al menos una orden asignada.
  El panel muestra "Agregar estas órdenes a una ruta…" con una nota de cuántas órdenes y qué repartidor (`DRV-`). Van
  solo las órdenes que ese lote asignó, en el orden del lote y con su repartidor; no se agregan ni se quitan órdenes.
  Mientras el paso está abierto, "Reintentar las pendientes" de la asignación espera ("Cancelar" lo cierra si aún no
  se envió nada).
- Rutas (`api/bulk-route-api.ts`): `listRoutes` con `status=DRAFT` y `driver_id` del repartidor, todas las páginas
  (máximo 5) una tras otra. Una ruta que no sea DRAFT, de otro repartidor o con fecha imposible hace fallar la lectura.
  Cada ruta se nombra por su fecha ("Ruta del 12 de octubre de 2026" o "Ruta sin fecha", y "la más reciente", "la
  segunda más reciente"… si comparten fecha) y sus paradas actuales, de la más reciente a la más antigua; desde
  UI-PHASE3-ROUTES-DAILY-2026-10-12 las de hoy (Mazatlán) van primero y llevan "(hoy)" (ver "Ruta por día"). Sin
  identificadores ni montos: el costo total de asignaciones de la ruta no se muestra en la bandeja.
- Sin ruta en borrador de hoy: la pantalla lo dice y, si todas las órdenes del lote son de una misma ciudad, ofrece
  "Crear ruta nueva" (ver abajo), debajo de las rutas en borrador anteriores si las hay. Las rutas también se crean en
  Rutas ("Abrir Rutas en otra pestaña", para no perder el lote, y luego "Buscar rutas de nuevo").
- Vista previa: la ruta, el repartidor (`DRV-`) y las órdenes en el orden en que se agregan ("Orden de envío", Guía y
  Destino); se agregan al final de la ruta.
- Confirmación (ConfirmDialog): la ruta con sus paradas actuales, el repartidor y el número de órdenes.
- Ejecución (`state/bulk-route-controller.ts`): `addRouteStop` una orden tras otra, cada una con su Idempotency-Key
  (`PendingSubmissions`, por orden, con la misma ruta y `expected_version`) que se conserva hasta una respuesta
  definitiva y se reutiliza al reenviar. `expected_version` es la versión recién listada y después la de cada respuesta
  (la respuesta es la ruta autoritativa). La primera confirmación fija la ruta para el resto del lote.
  - VERSION_CONFLICT (alguien más cambió la ruta): se detiene el envío, se vuelve a leer la ruta y las órdenes que no
    se agregaron esperan una nueva confirmación con la versión nueva (llaves nuevas, porque cambió el cuerpo). Si la
    ruta ya no está en borrador, no se envía nada más.
  - DRIVER_INELIGIBLE e INVALID_REQUEST responden solo por esa orden, con su mensaje en español, y se sigue con la
    siguiente.
  - Un CONFLICT genérico (o un código desconocido) o el 404 uniforme también responden por esa orden, pero el servidor
    los usa además para una ruta que dejó el borrador o ya no existe (`RouteStateConflict` sale como CONFLICT). Por
    eso, antes de la siguiente orden, `getRoute` debe mostrar la misma ruta en borrador del repartidor con la misma
    versión; si no, o si no se puede leer, el envío se detiene como con VERSION_CONFLICT (hallazgo de la revisión
    automática del PR #225).
  - Sesión, rol, MFA (403 MFA_REQUIRED ofrece "Verificar identidad" de regreso a la bandeja), red, servidor o una
    respuesta inesperada detienen el envío y las demás quedan "Sin enviar". "Reintentar las pendientes" reenvía, con
    las mismas llaves y cuerpos, solo lo que una falla de red o servidor dejó sin respuesta.
- Resultado por fila ("Agregada a la ruta" o el mensaje de su código) y un resumen que no oculta fallas parciales;
  "Ver la ruta en Rutas" al terminar. Después de cada envío se vuelven a leer la cola y los conteos. "Cerrar" del panel
  espera mientras se agregan las órdenes.
- Es una secuencia de una operación existente de una sola orden: no es un flujo atómico nuevo de AI-13 §4 ni cambia
  estados de orden; el backend y RLS deciden cada parada. Nada se guarda en el navegador ni se registra en consola.

### Crear ruta nueva dentro del paso (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11)

Pregunta literal al owner (2026-10-11): "Bandeja, "Agregar estas órdenes a una ruta…": si el repartidor no tiene una
ruta en borrador, hoy hay que ir a Rutas a crearla. ¿Agregamos "Crear ruta nueva" dentro de ese mismo paso?". Respuesta
literal: "Sí, en el paso (Recommended)", cuya opción decía "Crea la ruta en borrador para ese repartidor, con la ciudad
de las órdenes y la fecha de hoy (hora de Mazatlán), y sigue a la vista previa." No se eligió "Solo en Rutas" ("El paso
sigue mandando a Rutas para crearla; ahí cambio los IDs por selectores de repartidor y ciudad."). Después se le dijo al
owner que el mismo cambio sustituye también los IDs del formulario "Crear ruta" de Rutas por selectores
(`ui-001-phase3-routes.md`), y no objetó. Contrato: AI-07 `work_inbox.bulk_add_to_route.create_route`.

- Cuándo (regla original): solo donde el paso dice que el repartidor no tiene rutas en borrador (`listRoutes` vacío y
  ninguna ruta fijada por un envío). Desde UI-PHASE3-ROUTES-DAILY-2026-10-12 cuenta solo la ruta en borrador con fecha
  de hoy y el botón también aparece debajo de las rutas anteriores (ver "Ruta por día" abajo); lo demás de esta sección
  no cambia.
- La ciudad de las órdenes (`state/bulk-route-controller.ts`): cada orden del lote (máximo 20) se lee con el cliente
  existente de `getOrder` (`api/operations-api.ts`), una tras otra y cancelable al cerrar el paso, y se toma
  `Order.city_id`.
  - Todas con el mismo `city_id`: esa es la ciudad; se nombra con `listCities` (`api/locations-api.ts`, "Culiacán,
    SIN"). Si `listCities` falla o no la trae, el texto dice "la ciudad de las órdenes", nunca el id.
  - Ciudades distintas: no hay botón y el paso dice "Las órdenes de este lote están en ciudades distintas y una ruta
    cubre una sola ciudad, así que aquí no se crea ninguna. Las rutas se crean en Rutas." Se conservan "Abrir Rutas en
    otra pestaña" y "Buscar rutas de nuevo".
  - Una orden que no se puede leer: no hay botón y se muestra el mensaje de no disponible del paso ("No fue posible
    cargar las rutas en borrador del repartidor. Intenta de nuevo.").
  - Una orden no cambia de ciudad, así que tras una lectura completa "Buscar rutas de nuevo" vuelve a leer las rutas
    (y el nombre si faltó), no las órdenes.
- Envío (`api/bulk-route-api.ts`, `createRoute`): `driver_id` del lote, `city_id` de las órdenes,
  `service_area_id: null` (el owner no pidió área) y `scheduled_for` con la fecha de hoy en America/Mazatlan
  (`contracts/bulk-route.ts`, `todayInMazatlan`, con el reloj inyectable del controlador; nunca la fecha del navegador
  ni la de UTC: las 05:30 UTC del 12 de octubre siguen siendo el 11 en Mazatlán). La respuesta debe ser esa ruta en
  DRAFT, versión 1, sin paradas ni área; si no, cuenta como respuesta inesperada.
- Idempotency-Key (`PendingSubmissions`, ámbito `create-route`): la misma solicitud (repartidor, ciudad y fecha) conserva
  su llave hasta una respuesta definitiva y solo se reutiliza para reenviarla tras una falla de red o del servidor;
  otra fecha es otra solicitud y otra llave. El botón se deshabilita mientras se envía (y "Buscar rutas de nuevo",
  "Cancelar" y el "Cerrar" del panel esperan), así que la ruta nunca se crea dos veces.
- Al crearla, la ruta queda elegida, el foco pasa a la vista previa y aparece "Se creó «Ruta del 11 de octubre de
  2026» en borrador para DRV-…. Revisa la vista previa y confirma para agregar las órdenes." Desde ahí nada cambia: la
  misma vista previa, el mismo ConfirmDialog y la misma secuencia de `addRouteStop`, con `expected_version` 1 de la
  ruta creada.
- Fallas (`createFailure` en el controlador):
  - Negativas seguras, que conservan el botón y no vuelven a listar: 401, 403 y 403 MFA_REQUIRED (con "Verificar
    identidad" de regreso a la bandeja), con los textos de `describeFailure` después de "No se creó la ruta: "; el 404
    uniforme ("No se creó la ruta: el repartidor o la ciudad ya no están disponibles."); DRIVER_INELIGIBLE ("No se creó
    la ruta: el repartidor no puede tomar rutas en la ciudad de estas órdenes. Puede que su ciudad sea otra o que hoy no
    esté habilitado (su estado o sus documentos).") e INVALID_REQUEST. La llave se descarta.
  - Sin confirmación (red, 503 u otro 5xx, respuesta inesperada, CONFLICT genérico o código desconocido; el servidor
    también responde CONFLICT cuando falla su almacenamiento): nunca se muestra un éxito. Primero se vuelven a listar las
    rutas en borrador; si aparece alguna, se elige como cualquier ruta y el botón no vuelve; si no aparece ninguna, el
    botón vuelve, y tras red o 503 reenvía la misma solicitud con la misma llave ("…se reenvía la misma solicitud sin
    duplicarla."); si la lista no se puede leer, no hay botón hasta leerla. Desde UI-PHASE3-ROUTES-DAILY-2026-10-12 solo
    cuenta como encontrada una ruta en borrador de hoy, con textos nuevos (ver "Ruta por día").
- Es una operación existente (`createRoute`) antes de la secuencia existente de `addRouteStop`: no es un flujo atómico
  nuevo de AI-13 §4 ni cambia estados de orden; el backend y RLS deciden la ruta (repartidor OWN ACTIVE elegible por
  DSP-001 y ciudad coherente con él). Sin identificadores en pantalla; nada se guarda en el navegador ni se registra en
  consola.

### Ruta por día (UI-PHASE3-ROUTES-DAILY-2026-10-12)

Pregunta literal al owner (2026-10-12): "Rutas: el API no tiene forma de terminar ni cancelar una ruta, así que toda
ruta se queda en borrador para siempre. Por eso "Crear ruta nueva" en la bandeja (solo aparece si el repartidor no tiene
ninguna ruta en borrador) dejaría de aparecer desde el segundo día. ¿Qué hacemos?". Respuesta literal: "Ruta por día
(Recommended)", cuya opción decía "Sin cambiar el API: el paso ofrece "Crear ruta nueva" cuando el repartidor no tiene
ruta en borrador con fecha de hoy (Mazatlán), y las rutas de hoy salen primero. Terminar o descartar rutas viejas queda
para después del piloto." No se eligieron "Ruta por día + cerrar rutas" ("Lo anterior, más un ADR con operaciones nuevas
para "Terminar ruta" (sin paradas pendientes) y "Descartar ruta" (borrador sin paradas), para que las viejas salgan de
las listas.") ni "Dejar como está". Contrato: AI-07 `work_inbox.bulk_add_to_route` (`route_order`, `no_route`) y
`create_route` (`offered_when`, `outcome`, `found`). AI-05 no cambia.

Por qué: AI-05 no tiene ninguna operación que saque una ruta de DRAFT (`RouteStatus` lista PLANNED, ACTIVE, COMPLETED y
CANCELLED, pero nada llega a ellos), así que toda ruta se queda en borrador. Con la regla de UI-PHASE3-INBOX-CREATE-ROUTE
("solo sin ninguna ruta en borrador"), el botón no volvía a aparecer después de la primera ruta del repartidor.

- "Hoy" es la fecha de calendario de America/Mazatlan (`todayInMazatlan`, con el reloj inyectable del controlador), nunca
  la del navegador ni la de UTC: las 06:30 UTC del 12 de octubre de 2026 siguen siendo el 11 en Mazatlán. La lectura de
  rutas guarda la fecha para la que se hizo (`routes.today` en `state/bulk-route-controller.ts`).
- Cuándo se ofrece "Crear ruta nueva": cuando ningún envío fijó una ruta y el repartidor no tiene ninguna ruta en
  borrador cuyo `scheduled_for` sea hoy (una ruta sin fecha no cuenta como de hoy; una de mañana tampoco). Siguen todas
  las demás reglas: misma ciudad de las órdenes (ciudades distintas: sin botón y con su mensaje), orden ilegible (sin
  botón y con el mensaje de no disponible, como antes), idempotencia, falla ambigua (se vuelve a listar antes de ofrecer
  otra vez) y vista previa.
- Con rutas anteriores y ninguna de hoy, el paso muestra la lista de rutas en borrador del repartidor y, debajo, "DRV-…
  no tiene una ruta en borrador con la fecha de hoy." con "Crear ruta nueva", así que se puede elegir una ruta vieja.
  Sin ninguna ruta, el texto sigue siendo "DRV-… no tiene rutas en borrador.".
- Orden de la lista (`draftRouteChoices(routes, today)` en `contracts/bulk-route.ts`): primero las rutas de hoy, luego
  las demás en el orden del servidor (de la más reciente a la más antigua). Los nombres no cambian ("Ruta del 12 de
  octubre de 2026", "la más reciente"…); solo la etiqueta de la lista marca las de hoy: "Ruta del 12 de octubre de 2026
  (hoy) · 3 paradas" o "(hoy, la más reciente)" si comparten fecha. La vista previa y la confirmación usan el nombre sin
  "hoy". La nota bajo la lista dice "Primero las de hoy (hora de Mazatlán); las demás, de la más reciente a la más
  antigua." cuando hay alguna de hoy.
- La ruta creada queda primera (es la de hoy y la más reciente), antes de las anteriores, y elegida; si antes se había
  marcado una ruta vieja, al pulsar "Crear ruta nueva" se suelta esa elección.
- Tras una falla ambigua, la nueva lista cuenta como "encontrada" solo si trae una ruta en borrador de ese repartidor
  con fecha de hoy: entonces el botón no vuelve y el aviso pide elegirla ("No se pudo confirmar si la ruta se creó, pero
  DRV-… ya tiene una ruta en borrador con la fecha de hoy: elígela en la lista."); no se elige sola. Si solo aparecen
  rutas de otros días o sin fecha, no es la que se pidió y el botón vuelve ("…DRV-… no tiene ninguna ruta en borrador
  con la fecha de hoy. Puedes intentarlo de nuevo…").
- La regla se revisa también al pulsar: si cambió el día en Mazatlán desde la lectura y el nuevo día ya tiene una ruta
  listada, no se crea nada y se vuelven a leer las rutas.
- Si un envío ya fijó la ruta, nada cambia respecto a antes: no se ofrece crear.
- Rutas (`/ops/routes`) no reordena su lista: conserva el orden del servidor.

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
- Acciones en lote: "Asignar seleccionadas…" y, al terminar ese lote, "Agregar estas órdenes a una ruta…" (ver
  arriba), siempre con vista previa y confirmación. No hay cambio de estado en lote.
- Estados: cargando, vacía por cola, error conservando filas, sin sesión y acceso no disponible.
- Actualización: igual que el tablero (ADR-OBS-001): evento en tiempo real con 250 ms, reconexión obligatoria por
  REST, visibilidad y sondeo de 30 s; las lecturas en segundo plano esperan si la página está oculta. 401/403
  limpia todo y detiene el hub. Todo vive en memoria; un cambio de organización empieza de cero.

## Pendiente (no se simula)

- "Agregar a ruta…" como botón de una cola: `addRouteStop` solo acepta órdenes ya asignadas (OWN) al repartidor de la
  ruta y ninguna cola de la bandeja lista esas órdenes. Por decisión del owner se ofrece al terminar un lote (arriba);
  no se muestra un botón deshabilitado.
- Crear desde la bandeja una ruta para un lote con órdenes de ciudades distintas: una ruta cubre una sola ciudad, así
  que esas rutas se crean en Rutas (ahora con selectores, `ui-001-phase3-routes.md`). Elegir área de servicio o fecha
  al crear desde el paso no se pidió: la ruta se crea para toda la ciudad y con la fecha de hoy.
- El costo sugerido por el tabulador: el owner aún no envía sus montos; hoy cada costo se captura a mano.
- Entrada "Órdenes" del menú aprobado: aún no hay pantalla. "Repartidores" ya existe (`/ops/drivers`,
  UI-PHASE3-DRIVERS-2026-10-11; ver `ui-001-phase3-drivers.md`).
- Terminar o descartar rutas viejas: AI-05 no tiene operación para sacar una ruta de DRAFT, así que las rutas de días
  anteriores siguen en la lista del paso (después de las de hoy) y en Rutas. Queda para después del piloto, por decisión
  del owner (UI-PHASE3-ROUTES-DAILY-2026-10-12, "Terminar o descartar rutas viejas queda para después del piloto."); no
  se construye nada para terminarlas ni descartarlas. Mientras tanto, la lista del paso lee como máximo 5 páginas (250
  rutas en borrador por repartidor); con una ruta por día, el paso diría que no pudo revisar todas las rutas mucho
  después del piloto.

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
quedan como cualquier asignación individual (no hay datos, esquema ni migración que deshacer). Revertir el commit de
"Agregar estas órdenes a una ruta…" quita el paso y su contrato AI-07; las paradas ya agregadas quedan como cualquier
parada agregada desde Rutas (se retiran ahí si hace falta) y no hay datos, esquema, API ni migración que deshacer.
Revertir el commit de "Crear ruta nueva" (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11) devuelve el paso a mandar a Rutas
para crear la ruta y el formulario de Rutas a pedir IDs; las rutas ya creadas quedan como cualquier ruta en borrador
creada en Rutas, y no hay datos, esquema, API ni migración que deshacer.
Revertir el commit de "Ruta por día" (UI-PHASE3-ROUTES-DAILY-2026-10-12, junto con UI-PHASE3-ROUTES-NO-IDS-2026-10-12)
devuelve la regla anterior: "Crear ruta nueva" solo sin ninguna ruta en borrador y la lista en el orden del servidor,
sin "(hoy)"; las rutas creadas por día quedan como cualquier ruta en borrador y no hay datos, esquema, API ni migración
que deshacer.
Revertir el commit del Total quita `total` del read model del tablero y la columna de la bandeja a la vez
(web y API se despliegan juntas); no hay datos, esquema ni migración que deshacer.
