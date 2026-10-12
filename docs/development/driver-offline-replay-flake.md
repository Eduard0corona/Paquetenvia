# Repartidor: la cola offline no se reenviaba al volver la conexión

Severidad: **MAJOR** (AI-01 §7, operación). Un repartidor real podía quedarse sin sincronizar sus acciones guardadas
después de recuperar la conexión hasta que otra cosa disparara la sincronización (cambiar de app o bloquear la
pantalla, guardar otra acción, "Sincronizar ahora" o recargar). No se pierde ni se duplica nada y el backend sigue
siendo la barrera; el cambio es reversible. Sin cambio normativo: el lease de sync no está en AI-07 y las reglas
visibles (72 horas, mensajes, ninguna acción vencida se reintenta) no cambian.

## Síntoma

`DriverOfflineOperationsPwaPlaywrightTests.Expired_offline_operation_is_dropped_once_and_the_driver_is_told` falló de
forma intermitente en CI (PR #239, job 115004088280): tras `SetOfflineAsync(false)` y `navigator.onLine === true`, la
cola seguía con `CHECK_IN` en `PENDING` y `attemptCount` 0 a los 30 s. Nadie intentó el reenvío.

## Causa

`DriverSyncScheduler.run` toma el lease de la partición (`sync_leases`, 15 s) antes de cada pase y lo suelta al
terminar. Si una página se va a mitad de un pase (recarga, pestaña cerrada o descartada), su lease queda vigente
hasta vencer: nada lo borra, porque la limpieza de React no corre al descargar la página. Cuando otra página pedía
un sync en ese intervalo (`online`, arranque, guardar una acción) y no obtenía el lease, `run` regresaba sin más y
**no programaba ningún reintento**: el pedido se perdía. Sólo un evento posterior volvía a intentarlo.

En la prueba: la recarga de `EnsureServiceWorkerControlAsync` deja una página cuyo pase de arranque todavía tiene el
lease cuando `GotoAsync` la reemplaza. La página recargada sin conexión arranca, el `online` pide el sync, el lease de
la página muerta sigue vigente (unos 13 s restantes) y el pedido se descarta. Ninguna otra cosa ocurre en la prueba,
así que la cola nunca se reenvía. Es intermitente porque depende de que la navegación caiga dentro del pase de
arranque, que en un runner cargado tarda más.

Descartados con el código: el listener `online` se registra en `start()` antes del primer `requestSync`, así que
cubre los dos órdenes; el reenvío no depende de roles, número de despacho ni detalles de la parada (sólo de la
lectura de confirmación, que ocurre después de tomar el lease y, si falla la red, deja `RETRY_WAIT` con
`attemptCount` 1; sólo deja la acción intacta si la página se cierra o pierde el lease);
el Service Worker y el build de producción no intervienen (la página offline mostró la cola y el texto esperados).

## Evidencia

- Artefacto `driver-stops-pwa-results` del run 38307108935: no se pudo descargar; la descarga redirige a
  `productionresultssa19.blob.core.windows.net` y el cliente `gh` de esta sesión no sigue redirecciones fuera de
  `api.github.com`. El job (115004088280) sólo publica los `.trx`; los pasos y la anotación confirman el fallo en
  "Execute real offline operation flows".
- Local, sin cambios, la prueba sola pasó 26 de 26 veces: en cada corrida el lease visto al volver la conexión
  tenía menos de 50 ms, es decir, el de la propia página.
- Con una instrumentación temporal (no incluida en el cambio) que lee `sync_leases` y aplica
  `Emulation.setCPUThrottlingRate` 4 vía CDP, falló 11 de 16 veces con exactamente el error de CI. En todas las
  fallidas, antes de ir offline ya había un lease vigente de 0,65–0,81 s que ninguna página viva había escrito: en
  las 5 en que se registró también la edad de la página, el lease era de 3 a 32 ms antes de que la página actual
  empezara a navegar (`performance.timeOrigin`). Al volver la conexión seguía ahí, con 1,1–1,5 s de antigüedad, en
  una página de 0,3–0,4 s: el lease de una página que ya no existía. En las que pasaron no había lease antes de ir
  offline y el visto al volver la conexión era el de la propia página.
- Con la corrección y la misma instrumentación: 10 de 10. En las 5 corridas que encontraron el lease huérfano la
  prueba tardó 20 s en vez de 5 s: el reenvío arrancó solo al vencer el lease.
- Vitest: `a sync lease left by a page that went away` (en `driver-sync-scheduler.test.ts`) reproduce el caso sin
  navegador y con reloj falso. Sin la corrección fallan dos casos con `expected "vi.fn()" to be called once, but got
  0 times`; con ella pasan.

## Corrección

Cuando `acquireLease` no obtiene el lease porque otra página lo tiene vigente, el scheduler ya no descarta el pedido:
programa un nuevo `requestSync` tras una duración de lease (15 s) en el mismo timer que usa el backoff. Toda página
escribe su lease por esa duración, así que para entonces el lease visto venció (y se toma) o una página viva lo
renovó (y se vuelve a mirar después). Igual que el backoff, el reintento no corre con la pestaña oculta ni sin
conexión; `visibilitychange` y `online` vuelven a pedir el sync. Una página viva que tiene el lease sigue siendo la
única que envía; el reintento sólo vuelve a intentar tomar el lease.

Peor caso para el repartidor: hasta 15 s después de recuperar la conexión. Antes, indefinido.

Pruebas Playwright: se quitan las esperas `WaitForNoActiveSyncLeaseAsync` que dos pruebas de la clase añadieron
(commit 60b0730) para esquivar este mismo defecto; ahora esas pruebas también ejercitan la recuperación. La prueba
de la cola vencida queda igual. Las demás pruebas que navegan y luego van offline y vuelven
(`Five_actions_two_proofs_survive_offline_reload_and_sync_once`) compartían la carrera y quedan cubiertas por la
corrección de la app; las que no navegan antes de ir offline no la tienen.

## Riesgo residual

- Si otra pestaña viva mantiene un pase muy largo, esta página consulta el lease cada 15 s (una transacción
  IndexedDB) mientras esté visible y en línea.
- Visto al revisar, fuera de este cambio: `DriverOperationsController.start()` no revisa si fue liberado mientras
  abría IndexedDB. Si la sesión cambia o se cierra en ese instante (en producción, una ventana de milisegundos; en
  `next dev`, siempre, por el doble montaje de StrictMode), su scheduler queda escuchando `online` con la partición
  anterior. No envía nada (su lectura de confirmación responde `cancelled`), pero puede tomar el lease un momento y
  aplicar la regla de 72 h a una acción ya retenida sin que la pantalla lo avise. Con esta corrección la página
  activa ya no pierde su pedido por ese lease.

## Rollback

Revertir el commit. La app vuelve a descartar el pedido cuando el lease está ocupado; las pruebas Playwright que
navegan antes de ir offline vuelven a ser intermitentes.
