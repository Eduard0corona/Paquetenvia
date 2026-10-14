# Repartidor: la foto en vista previa se perdía al confirmarse la acción anterior

Severidad: **MAJOR** (AI-01 §7, operación). Es un defecto real de la PWA, no sólo una carrera de la prueba: un
repartidor en línea que elegía la foto de evidencia mientras la acción anterior de la misma parada terminaba de
sincronizar veía desaparecer la vista previa, un aviso de conflicto por un instante y el paso de la foto vacío otra
vez; tenía que tomarla de nuevo. No se envía ni se pierde ninguna acción guardada, no cambia ninguna regla visible y
el backend sigue siendo la barrera; el cambio es reversible. Sin cambio normativo.

## Síntoma

`MainJourneyEndToEndTests` (OPS-005, `Category=PlatformEndToEnd`) falló de forma intermitente en CI (PR #267, run
38506623366, job 115665555980; #267 no toca este código; el re-run del mismo job sobre el mismo commit pasó). En
`PickUpAndDeliverAsync`, tras "Llegué a recolección" la prueba esperó "Confirmar recolección", eligió la foto y vio
"Vista previa de la foto"; el click en "Usar esta foto" encontró `<html>` interceptando el puntero, luego el botón
desprendido del DOM y agotó los 30 s. El texto de la página al fallar: "Sincronización pendiente 0 acciones en el
dispositivo", "Estado confirmado En punto de recolección", "Siguiente acción Confirmar recolección" y el campo "Foto de
evidencia" sin vista previa.

## Causa

La pantalla de la parada proyecta dos fuentes que llegan por separado: las paradas de la última lectura REST
(`DriverStopsController`) y la cola que publicó `DriverOperationsController`. En cada pase del scheduler, la lectura
de confirmación posterior a la transición (`refreshForSync`) actualiza primero las paradas; el scheduler borra la
acción confirmada de IndexedDB y publica la cola nueva después (dos transacciones IndexedDB más tarde). Entre ambos
momentos la pantalla tenía la parada ya en AT_PICKUP versión 4 y la cola todavía con CHECK_IN (origen ASSIGNED,
versión 3). `projectDriverOperations` no aplicaba ni reconocía esa acción, la marcaba `NEEDS_ATTENTION`
(`VERSION_CONFLICT`) y la parada quedaba con `attentionCount` 1. `StopAction` no dibuja el siguiente paso cuando hay
algo en atención, así que `DriverProofCapture` se desmontaba y, como al desmontar libera la foto (`release()`, UI-001
fase 3: "El object URL se revoca … al desmontar el paso"), la foto elegida se perdía. Al publicarse la cola vacía el
paso volvía a montarse, nuevo y vacío. Mientras tanto se mostraba "1 acción en el dispositivo; 1 requiere atención" y
el panel "Acción que requiere atención".

Qué volvía a dibujar la parada, de los candidatos: la sincronización de la cola ("Llegué a recolección") y la lectura
REST que la confirma. No fue un aviso de tiempo real, un refresco periódico, un cambio de ruta ni una `key` nueva: la
`key` de `StopAction` (el estado proyectado) se quedó en AT_PICKUP; lo que cambió fue `attentionCount`.

Es intermitente porque la foto tiene que estar en vista previa justo cuando llega esa lectura de confirmación. El
pase de CHECK_IN empieza al guardarla (lectura previa, POST de la transición, lectura de confirmación) y la prueba
elige la foto en paralelo: si la confirmación llega antes de elegir la foto, el paso nuevo ya está montado y pasa; si
llega después del click, la foto ya está en la cola. En campo pasa igual con "Llegué al destino" y la foto de
entrega, y con más probabilidad al recuperar la conexión con varias acciones de la parada en cola: la confirmación de
cada una es un momento en que la foto en vista previa se pierde.

## Evidencia

- Vitest, `driver-operations-controller.test.ts` ("the stop screen while the previous action syncs"): un
  `DriverOperationsController` con un `DriverSyncScheduler` real, cola en memoria y API falsa registra lo que la
  pantalla proyecta en cada cambio durante el pase de CHECK_IN. Sin la corrección: `AT_PICKUP/attention=0` tres veces,
  `AT_PICKUP/attention=1` justo después de la lectura de confirmación y `AT_PICKUP/attention=0` tras publicar la cola.
  Con la corrección, todas `AT_PICKUP/attention=0`.
- Playwright, `DriverOfflineOperationsPwaPlaywrightTests.Previewed_photo_survives_the_sync_of_the_previous_action`
  (`Category=DriverOfflineOperationsPwa`): el backend sintético retiene la lectura de paradas posterior a la transición
  hasta que la foto está en vista previa y luego la suelta. Sin la corrección falló 3 de 3 en la aserción de la vista
  previa. Con un registro temporal de cambios del DOM (no incluido en el cambio) la pantalla pasó por
  "1 acción … | vista previa", "1 acción en el dispositivo; 1 requiere atención | sin vista previa | sin paso de foto |
  panel de conflicto" y "0 acciones … | sin vista previa | paso de foto": el mismo estado final que en CI. Con la
  corrección pasa y la secuencia queda "1 acción … | vista previa" → "0 acciones … | vista previa"; "Usar esta foto"
  encola y envía esa misma foto (un solo upload con su SHA-256, transiciones AT_PICKUP y PICKED_UP).
- Proyección pura, `operation-projection.test.ts`: con la foto de recolección confirmada por REST (PICKED_UP v5) y
  START_TRANSIT y START_DELIVERY en cola, la parada proyecta DELIVERING v7 sin atención ("Confirmar entrega" sigue);
  sin la corrección quedaba PICKED_UP con la acción en atención y las dos siguientes bloqueadas.
- La suite OPS-005 sin la corrección pasó en local la vez que se corrió completa: el fallo depende del momento,
  igual que en CI. Con la corrección (sobre `development` 6259b17), `Category=PlatformEndToEnd` 5 de 5 corridas
  consecutivas con 2 de 2 pruebas (recorrido de 19 a 25 s); `Category=DriverOfflineOperationsPwa` 28 de 28 y
  `Category=DriverStopsPwa` 25 de 25.

## Los dos 403 de la consola

Son `GET /api/v1/driver/me/external-offers` (`listMyEligibleExternalOffers`), uno por cada carga de la PWA del
repartidor: la lista `/driver/stops` y el detalle `/driver/stops/<id>` ("Ver detalle" es un enlace normal, así que la
app arranca de nuevo). El repartidor sintético de OPS-005 es de flota propia y el servicio sólo acepta un perfil
`driver_type='EXTERNAL'` (`PostgreSqlExternalOfferService.ResolveExternalDriverIdAsync`), así que responde 403 por
diseño (EXT-001). `ExternalOffersController` toma ese 403 como "no aplica": no muestra nada y deja de consultar.
Chromium escribe en la consola "Failed to load resource" para cualquier respuesta 4xx. No tienen relación con la foto:
ese controlador sólo alimenta el panel de ofertas de la lista y no toca la parada. En las corridas que pasan también
aparecen 409 en `POST /api/v1/orders/<id>/proofs` mientras el Worker valida la foto (la cola espera en
`WAITING_VALIDATION`, DRV-002), esperados.

## Corrección

`projectDriverOperations` (`apps/web/src/driver/offline/operation-projection.ts`) ya trataba como asentado un reporte
"No se pudo entregar" que REST confirma: no se proyecta, no se muestra como conflicto y sale de la cola en el
siguiente pase. Ahora lo mismo aplica a cualquier otra transición que REST ya muestra aplicada
(`driverOperationConfirmedByStop`: su estado destino en una versión posterior) mientras no esté retenida para el
repartidor (`NEEDS_ATTENTION` o `BLOCKED`). Es la misma regla con la que el scheduler la borra sin reenviarla (DRV-002,
"Si REST ya confirma el estado y la versión esperados, elimina la operación sin repetir la mutación"), así que la
pantalla deja de contradecir lo que el scheduler hará un instante después. El paso siguiente no se desmonta y la foto
en vista previa se conserva para la misma parada y la misma acción. Sin esperas ni reintentos nuevos; la prueba
OPS-005 no cambia.

Lo que no cambia (no se inventa comportamiento):

- Una acción retenida para el repartidor sigue retenida aunque REST la muestre aplicada; la resuelve el repartidor como
  antes.
- Si REST va más allá del destino de la acción (otro dispositivo avanzó la parada), sigue siendo conflicto, como antes.
- Si la parada ya no espera esa acción (otro dispositivo la avanzó, despacho la movió o la parada desapareció), el
  paso se reemplaza y la foto se libera como ya lo hacía el código (`key` por estado proyectado y `release()` al
  desmontar).
- Cobros (`COD_COLLECTION`, no son transición) y reportes `FAILED_DELIVERY` (su propia regla) quedan igual.

## Riesgo residual

- Corregido aparte (DRV-STALE-STOP-READ-2026-10-14, sección siguiente): visto al revisar, no observado en una
  corrida. `DriverStopsController.refresh` (lecturas completas por aviso de tiempo real o reconexión) y
  `refreshForSync` no se ordenaban entre sí. Una lectura completa que el servidor respondió antes de una transición y
  que llegaba después de la lectura de confirmación regresaba la parada a su estado anterior, con el mismo efecto sobre
  el paso de la foto.
- Cuando la acción confirmada sale, desaparece la fila "Estado por sincronizar" y el paso de la foto sube unos
  píxeles. Un toque justo en ese instante puede caer fuera del botón; Playwright reintenta el click. Ya ocurría antes.
- Entre la lectura de confirmación y el borrado en IndexedDB (milisegundos), "Sincronización pendiente" ya no cuenta la
  acción asentada, igual que con el reporte "No se pudo entregar".

## Lecturas viejas de la parada (DRV-STALE-STOP-READ-2026-10-14)

Respuestas literales del owner en `decision-log.md`: Q6 "Sí, en un PR aparte (Recommended)" ("La app descarta una
lectura de la parada más vieja que la última aceptada (por versión). No cambia lo que ve el repartidor salvo evitar ese
parpadeo. Con prueba de regresión.") y Q6b "Se aplica, como hoy (Recommended)" ("La app solo descarta una lectura que
ya iba en camino cuando aceptó una versión más nueva de esa parada; una lectura pedida después siempre se aplica. Evita
igual el parpadeo. Tras una restauración, el repartidor ve lo que dice el servidor desde la siguiente lectura."). En la
revisión del PR #271 (comentario 4267541135) se preguntó Q6c: "Solo paradas ya vistas (Recommended)" ("Una lectura en
camino ignora la parada solo si la lectura que la quitó salió cuando la app ya tenía esa parada. Corrige el caso del
revisor (una parada quitada ya no regresa con datos viejos) y una parada recién asignada nunca queda oculta más que
hoy. Queda un hueco raro: con tres lecturas cruzadas, la parada quitada podría reaparecer una vez, hasta la siguiente
lectura.").
Severidad **MAJOR** (AI-01 §7, operación), cambio reversible; sin cambio de API, base de datos, cola ni reglas de
DRV-002.

### Causa

- Dos lecturas de paradas de `DriverStopsController` llegan por caminos que no se ordenan entre sí:
  - la lectura completa `refresh` (con datos protegidos): al abrir, al reintentar, por aviso de tiempo real
    (`scheduleRefresh`, 250 ms) o al reconectar (`resynchronizeFromRest`). Sólo cancela la lectura completa anterior;
  - la lectura de confirmación `refreshForSync` (`details=false`), que el scheduler hace antes y después de cada
    transición.
- Las dos terminaban en `acceptAuthoritativeStops`, que reemplazaba las paradas en pantalla y la instantánea con lo
  que llegara, sin mirar la versión. Entre la mezcla y el `setState` había además esperas (lectura y escritura de la
  instantánea), así que dos respuestas que llegaban casi juntas podían mezclarse contra el mismo estado anterior.
- El orden que falla:
  1. la lectura completa sale con la parada en ASSIGNED v3 y el servidor la responde antes de aplicar el check-in. Es
     la lectura lenta: descifra los datos protegidos y espera hasta 10 s;
  2. mientras tanto terminan la lectura previa del scheduler, la transición (v4) y la lectura de confirmación
     (AT_PICKUP v4), y el check-in sale de la cola;
  3. llega la lectura completa: la parada vuelve a ASSIGNED v3 en pantalla y en la instantánea;
  4. la proyección muestra "Llegué a recolección", `StopAction` cambia de `key` y `DriverProofCapture` se desmonta:
     `release()` y la foto en vista previa se pierde.
- No era sólo un instante: la parada quedaba así hasta la siguiente lectura (aviso, sincronización o reintento), y la
  reconexión entregaba v3 como cursor del tiempo real.
- Visto en la revisión del PR #271: la primera versión de la corrección sólo comparaba las paradas que trae cada
  lectura. Una parada que salía de la lista (despacho la quita o la reasigna) no dejaba nada anotado en las lecturas
  que seguían en camino:
  1. una lectura completa lenta sale con la parada A en v5;
  2. despacho quita A y la lectura de confirmación, que ya no la trae, se toma: A sale de la pantalla;
  3. llega la lectura lenta con A en v5 y, sin nada anotado para A, la vuelve a poner en pantalla y en la instantánea
     con su estado y sus datos viejos, hasta la siguiente lectura.
- Una parada no siempre sale de la lista con una versión nueva: tras FAILED_ATTEMPT→RESCHEDULED, D8 cierra la
  asignación después y sin tocar la orden (`PostgreSqlAssignmentLifecycleReactor.CloseAsync`), así que la parada
  sale en la misma versión que la app tiene. Una asignación nueva sí sube la versión
  (`PostgreSqlAssignmentToOrderCoordinator`).

### Evidencia

- Vitest, `driver-operations-controller.test.ts` ("a stale stop read that lands after the confirmation (photo step
  kept)"), con `DriverStopsController`, `DriverOperationsController` y `DriverSyncScheduler` reales: sin la corrección
  la pantalla pasa de `AT_PICKUP/attention=0` a `ASSIGNED/attention=0` y la instantánea queda `ASSIGNED/v3`. Con la
  corrección todas quedan `AT_PICKUP/attention=0` y la instantánea `AT_PICKUP/v4`.
- Playwright, `DriverOfflineOperationsPwaPlaywrightTests.Previewed_photo_survives_a_stale_stop_read`
  (`Category=DriverOfflineOperationsPwa`): el check-in se guarda sin conexión; la página nueva que lo envía abre con
  una lectura completa que el backend sintético responde en ASSIGNED v3 y entrega tarde, y las transiciones esperan a
  que esa lectura haya llegado. Se suelta con la foto ya en vista previa.
  - Sin la corrección falló 3 de 3: la instantánea pasa a `ASSIGNED/3`, y la pantalla muestra "Estado confirmado
    Asignada", "Siguiente acción Llegué a recolección" y ninguna vista previa.
  - Con la corrección pasó 3 de 3: la instantánea se vuelve a guardar con `AT_PICKUP/4`, sigue "Confirmar
    recolección" con la misma vista previa (el mismo object URL), y "Usar esta foto" envía esa misma foto (un solo
    upload con su SHA-256, transiciones AT_PICKUP y PICKED_UP).
- Pruebas unitarias, `driver-stops-controller.test.ts` ("stale stop reads"): versión igual, parada que salió de la
  lista, parada que falta en la lectura vieja, reinicio de versión y una respuesta vieja que llega mientras otra
  espera su escritura. Esta última falla si las respuestas no se toman de una en una.
- Pruebas unitarias de Q6c ("a stop removed while another read is on its way"):
  - el orden del revisor: la parada quitada no vuelve ni en pantalla ni en la instantánea. Sin la regla de Q6c falla,
    igual que la prueba de una parada que salió y que la lectura vieja trae con su última versión aceptada (antes se
    tomaba; ahora no vuelve);
  - una lectura pedida después de quitarla que la trae otra vez (reasignada) se toma, y la lectura lenta sigue sin
    tomarla;
  - una lectura ya en camino que la trae en la versión que la app tenía + 1 (cerrada por D8 sin subir la versión y
    asignada de nuevo) se toma;
  - una parada recién asignada que una lectura vieja quitó vuelve con la lectura de confirmación que ya iba en camino,
    como hoy.
  - La de la parada recién asignada falla con la opción no elegida de Q6c (anotar cualquier parada que falte); la del
    cierre de D8 falla si también se descarta la versión que la app tenía + 1.
- Playwright no cubre la parada quitada: el backend sintético de `DriverOfflineOperationsPwa` sirve una sola orden y no
  puede quitarla de la lista. Quitarla durante la sincronización llevaría además la acción en cola al camino de parada
  faltante de DRV-002, que la prueba tendría que modelar. Lo cubren las pruebas de `DriverStopsController` de arriba.

### Corrección

- Cada lectura en camino (completa o de confirmación) anota, por orden, la versión más alta que la app aceptó de otras
  lecturas mientras ella iba en camino (`withoutStaleStops`). Al llegar, una parada con una versión menor que esa no
  se toma: la parada que la app tiene se queda como está, con sus datos protegidos, y una parada que ya salió de la
  lista no vuelve. Lo demás de esa lectura se toma como antes.
- Una parada quitada (Q6c): cuando se toma una lectura que ya no trae una parada que la app tenía, esa parada cuenta
  como quitada sólo si esa lectura salió cuando la app ya la tenía, es decir, si la lectura no tiene nada anotado para
  esa orden. Entonces cada otra lectura en camino anota esa orden en la versión que la app tenía + 1, y la compara como
  cualquier otra versión anotada:
  - con la versión que la app tenía o una menor, la parada no vuelve;
  - con esa versión + 1 o una mayor, se toma. Así entra una parada asignada de nuevo, incluso tras el cierre de D8
    que no sube la versión.
  - Si la lectura que no la trae sí tiene algo anotado para esa orden, la app no sabe si la parada se quitó o si la
    lectura salió antes de que se asignara. La parada sale de la lista como antes (DRV-001), pero no se anota nada, así
    que la siguiente lectura la trae de vuelta, también una que ya iba en camino.
- Las respuestas se toman de una en una, en el orden en que llegan: cada una se compara y se mezcla con lo que dejó la
  anterior, y la instantánea y la pantalla se escriben en ese mismo orden.
- Queda igual que antes:
  - con versión igual de una parada que la app tiene, la lectura se toma. Lo que cambia es una parada que ya salió de
    la lista: una lectura que ya iba en camino no la regresa con su última versión aceptada (Q6c, arriba);
  - una parada que la lectura no trae sale de la lista (DRV-001);
  - una lectura pedida después de aceptar la versión más nueva se toma siempre, aunque traiga una versión menor. Eso
    sólo pasaría tras restaurar la base desde un respaldo (Q6b);
  - la memoria vive en el controlador de la sesión: cerrar sesión, cambiar de organización o de repartidor, o un
    401/403 la terminan;
  - el scheduler sigue confirmando con la respuesta de REST tal como llegó (DRV-002 sin cambios);
  - "Hoy · N paradas · K hechas" y la hora de sincronización siguen la lectura como antes;
  - el cursor del tiempo real recibe las versiones que la app conservó.

### Riesgo residual

- Una lectura vieja a la que le falta una parada recién asignada (que llegó con otra lectura mientras ella iba en
  camino) la quita hasta la siguiente lectura. No lleva versión de esa parada, así que la regla no aplica y sale de la
  lista como dice DRV-001. Perder una foto así exigiría abrir esa parada, llegar y previsualizar la foto antes de que
  esa lectura llegue (máximo 10 s). Q6c no lo alarga: esa lectura vieja no anota la parada como quitada, así que la
  siguiente lectura la trae de vuelta, también una que ya iba en camino.
- Huecos de la regla de Q6c. Son raros y cada uno se corrige con la siguiente lectura:
  - con tres lecturas cruzadas, una lectura que no trae una parada aceptada por otra mientras ella iba en camino no la
    anota como quitada. Una tercera lectura, más vieja y todavía en camino, podría regresarla una vez;
  - si la app tenía una versión de la parada anterior a la de RESCHEDULED, D8 la quita en esa versión más nueva sin
    subirla. Una lectura vieja con la versión de RESCHEDULED queda en la versión que la app tenía + 1 o más arriba, así
    que podría regresarla una vez;
  - una parada que el parser del cliente deja fuera (una parada que rompe el contrato, `droppedCount`) cuenta como
    quitada. Si una lectura que ya iba en camino la trae válida con la misma versión, no se toma.
- Con versión igual, una lectura vieja puede traer un dato que cambió sin subir la versión (por ejemplo el cobro
  registrado, que no cambia la versión de la orden); se toma como antes.

### Rollback

Revertir los commits de DRV-STALE-STOP-READ-2026-10-14. Vuelve el riesgo descrito arriba: una lectura completa que
llega después de la confirmación regresa la parada y la foto en vista previa se pierde. Revertir sólo el commit de Q6c
devuelve el caso del revisor: una parada quitada puede volver con datos viejos hasta la siguiente lectura.

## Rollback

Revertir el commit. La pantalla vuelve a mostrar en atención, por un instante, la acción recién confirmada; la foto en
vista previa se pierde en ese momento y la suite OPS-005 vuelve a ser intermitente.
