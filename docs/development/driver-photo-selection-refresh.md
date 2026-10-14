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

- Visto al revisar, no observado: `DriverStopsController.refresh` (lecturas completas por aviso de tiempo real o
  reconexión) y `refreshForSync` no se ordenan entre sí. Una lectura completa que el servidor respondió antes de una
  transición y que llega después de la lectura de confirmación podría regresar la parada a su estado anterior por un
  momento, con el mismo efecto sobre el paso de la foto. Necesita que esa lectura tarde más que la transición y su
  confirmación juntas. No se corrige aquí; seguimiento sugerido: descartar una lectura más vieja que la última
  aceptada.
- Cuando la acción confirmada sale, desaparece la fila "Estado por sincronizar" y el paso de la foto sube unos
  píxeles. Un toque justo en ese instante puede caer fuera del botón; Playwright reintenta el click. Ya ocurría antes.
- Entre la lectura de confirmación y el borrado en IndexedDB (milisegundos), "Sincronización pendiente" ya no cuenta la
  acción asentada, igual que con el reporte "No se pudo entregar".

## Rollback

Revertir el commit. La pantalla vuelve a mostrar en atención, por un instante, la acción recién confirmada; la foto en
vista previa se pierde en ese momento y la suite OPS-005 vuelve a ser intermitente.
