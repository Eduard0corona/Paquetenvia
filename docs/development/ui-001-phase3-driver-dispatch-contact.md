# UI-001 fase 3: "Llamar a despacho" en la PWA del repartidor

Decisión: `DRV-DISPATCH-CONTACT-2026-10-12` (fila ADR en `docs/normative/v0.6/decision-log.md`; cambio MAJOR del
contrato de la API según AI-01 §7). Continúa la respuesta "No, llama a despacho (Recommended)" de
`UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12` ([nota](ui-001-phase3-driver-failed-delivery.md)), que pedía llamar a
despacho sin que la app tuviera el número. Contratos: AI-05 `getMyDispatchContact` (esquema `DriverDispatchContact`,
`x-capability-matrix` `dispatch_contact_operations`, delta `DRV-DISPATCH-CONTACT`) y AI-07
`screen_contracts.driver_stop.dispatch_contact`. AI-04, AI-06 y AI-18 no cambian.

## Lo que decidió el owner (literal)

- 2026-10-12, pregunta: "En la app del repartidor, "Llama a despacho" (cuando ya registró un cobro y no puede
  entregar) no muestra ningún número porque la app no tiene uno. ¿Agregamos un teléfono de despacho?" → "Sí,
  configurable (Recommended)": "Un número de despacho por empresa en la configuración del piloto; la app lo muestra
  como botón "Llamar a despacho". Tú me das el número cuando lo tengas." (No se eligió "No, ya lo saben": "Los
  repartidores ya tienen el número de despacho; la app solo dice que llamen.")
- El owner todavía no da el número. No hay ninguno configurado en ningún archivo; las pruebas usan solo números
  sintéticos (`+522000000001`, `+522000000002`). Sin número, la app se ve como hoy.

## Configuración (servidor)

- `Drivers:DispatchContacts` (`DriversOptions.DispatchContacts`): lista de `{ OrganizationId, PhoneE164 }`, vacía
  por omisión. Como variables de entorno: `Drivers__DispatchContacts__0__OrganizationId` y
  `Drivers__DispatchContacts__0__PhoneE164`, luego `__1__`, etc.
- Se valida al arrancar (`ValidateOnStart`); con un valor inválido la API no arranca:
  - `OrganizationId`: UUID canónico (`D`, en minúsculas, con guiones), nunca el UUID nulo. Así una organización
    siempre se escribe igual y un repetido siempre se ve. Un id que no es de ninguna organización simplemente nunca
    coincide (la configuración no consulta la base).
  - `PhoneE164`: número mexicano en E.164, `+52` y diez dígitos ASCII, el primero de 2 a 9 (los números que
    `VoicePhoneNumber.IsDialable` llama). Se eligió México y no E.164 general porque el piloto y todas las reglas de
    teléfono del sistema (ORD-PHONE-PLUS52-LOCATIONS, VOICE-001) son mexicanas. No se aceptan espacios, guiones,
    saltos de línea, otros códigos de país ni dígitos que no sean ASCII. AI-05 publica la misma regla como
    `pattern: '^\+52[2-9][0-9]{9}$'`.
  - Cada organización una sola vez (aunque repita el mismo número). Un mismo número sí puede servir a varias
    organizaciones.
  - El mensaje de error no lleva ningún valor; `DriverDispatchContactOptions.ToString`,
    `DriverDispatchContactResponse.ToString` y el directorio ocultan el número. Nada lo escribe en logs.
- `ConfiguredDriverDispatchContactDirectory` (Drivers.Infrastructure) arma un diccionario congelado
  organización → número; el endpoint usa el puerto `IDriverDispatchContactDirectory` (Drivers.Application). No
  depende de `Drivers:Provider`.

### Piloto

El número es un valor de negocio del owner: va en `deploy/azure/pilot/apps.settings.json` (el parámetro
`businessSettings` de `apps.bicep`, que llega a la API y al Worker; el Worker no lo lee). Hoy el archivo no tiene
ninguna entrada, así que el piloto no tiene número y no se ve el botón. Cuando el owner lo dé se agregan dos
entradas:

```json
{ "name": "Drivers__DispatchContacts__0__OrganizationId", "value": "<UUID de la organización, minúsculas>" },
{ "name": "Drivers__DispatchContacts__0__PhoneE164", "value": "+52<diez dígitos>" }
```

`env001_pilot_guards.py settings-check` (guarda P17, también en el job `provenance` antes de tocar Azure) aplica las
mismas reglas que la API: nombres `Drivers__DispatchContacts__<n>__OrganizationId|PhoneE164`, índices 0, 1, 2… sin
huecos, las dos entradas de cada índice, UUID canónico, `+52` y diez dígitos, y ninguna organización repetida. Como
.NET no distingue mayúsculas en los nombres de configuración, P17 reconoce esos nombres en cualquier grafía, rechaza
la que no sea exactamente la canónica (y aun así revisa su valor) y cuenta como repetido un nombre que solo cambia en
mayúsculas (también para los prefijos administrados por la plataforma y los ajustes eliminados). Sus mensajes nunca
repiten un valor: una entrada mal formada (un número JSON, un miembro de más) se nombra por su nombre canónico o por su
posición en el archivo, nunca con su contenido. `apps.bicep` no cambia (las pruebas estáticas AZR-001 no fijan el contenido de
`apps.settings.json`; solo validan su forma). Ver
[`docs/operations/env-001-pilot/README.md`](../operations/env-001-pilot/README.md) §4.

## API

`GET /api/v1/driver/me/dispatch-contact` (`getMyDispatchContact`, etiqueta Driver):

| Caso | Respuesta |
| --- | --- |
| DRIVER con membresía ACTIVE en la organización de `X-Organization-Id` y número configurado | 200 `{"phone_e164": "+52…"}` |
| Igual, sin número configurado para esa organización | 200 `{"phone_e164": null}` (nunca el de otra organización) |
| Sin sesión | 401 |
| Sin encabezado, encabezado mal formado, organización ajena, membresía suspendida o revocada, o cualquier otro rol (VIEWER, DISPATCHER, PLATFORM_ADMIN con o sin MFA, FINANCE, ALLY_*, BUSINESS_*) | 403 uniforme, sin leer nada |
| La sesión o la auditoría del contexto tenant no se pueden leer | 503 |

- `DriverDispatchContactEndpoints` sigue a las otras lecturas `/driver/me` (`getMyDriverPhone`):
  `ActiveOrganizationMember`, `RequireTenantContext(403)`, `Cache-Control: no-store` primero, la capacidad
  `TenantCapabilities.GetMyDispatchContact` (DRIVER sin MFA) antes de leer, y la respuesta.
- La membresía ya la resuelve la sesión del request (claims del contexto de identidad), como en `getMyDriverPhone`;
  la lectura es solo configuración: sin base de datos, sin transacción tenant y sin escribir.
- Se mapea con `app.MapDriverDispatchContactEndpoints()` en `Paqueteria.Api`; el Worker no la tiene.

## PWA

| Lugar | Texto (no cambia) | Con número conocido |
| --- | --- | --- |
| Parada con el cobro registrado (`failedDeliveryCopy.codRecorded`) | "El cobro ya está registrado. Si no puedes entregar, llama a despacho: ellos deciden y coordinan la devolución del cobro." | "Llamar a despacho" debajo del texto |
| "Enviar reporte" rechazado porque el cobro ya está registrado (`driverCashRecordedRefusalMessage`, en el formulario y en "Sincronización pendiente") | "El cobro ya está registrado. Llama a despacho: ellos deciden y coordinan la devolución del cobro." | "Llamar a despacho" junto a cada uno |
| Reporte retenido antes de su incidencia con el cobro registrado (`driverCashRecordedReportMessage`) | "El cobro de esta entrega ya está registrado, así que el reporte no se envió. Llama a despacho: …" | "Llamar a despacho" en "Acción que requiere atención" |
| Reporte con incidencia abierta o posiblemente abierta, retenido por el cobro registrado o todavía guardado (`driverFailedReportAttention`) | "…Llama a despacho: …" o "…si no, llama a despacho." | "Llamar a despacho" en "Acción que requiere atención" |

- El enlace es `DriverDispatchCall` (`components/driver-dispatch-call.tsx`): `<a href="tel:+52…">Llamar a
  despacho</a>` con la clase `dispatchCall` (al menos 44 × 44 px, foco visible). El número nunca se escribe en
  pantalla. Es el único lugar que arma un `tel:`; la pantalla de paradas no tiene ninguno (la prueba de VOICE-001
  sigue vigente).
- Sin número conocido (ninguno configurado, sin respuesta todavía en este teléfono, API anterior con 404, 401 o 403)
  el componente no dibuja nada y los textos quedan como hoy. No hay mensaje de error: no saber el número no es una
  falla para el repartidor.
- `driverFailedReportAttention` devuelve el mensaje de siempre (`driverFailedReportAttentionMessage` lo usa) y
  `callDispatch`, verdadero solo en los dos casos de la tabla de atención; `driverMessageCallsDispatch` reconoce el
  rechazo del controlador. No se agregó el botón en ningún otro lugar (ver "Otros textos").

### Lectura y caché

- `DriverDispatchContactController` (`state/driver-dispatch-contact-controller.ts`) se crea con la sesión del
  repartidor, igual que `DriverRolesController`: una sesión nueva (otro inicio de sesión u otra organización) es otro
  controlador y otra partición. Usa primero la respuesta guardada para la partición y luego pregunta; una respuesta
  correcta (número o null) la reemplaza; 401 o 403 la borra y no vuelve a preguntar; cualquier otra falla (sin
  conexión, timeout, 5xx, 404 de una API anterior, respuesta fuera de contrato) conserva la guardada y vuelve a
  preguntar con `online` o `visibilitychange`, solo en línea, una a la vez, hasta 5 veces por sesión y nunca después
  de cerrarse ("Cerrar sesión" lo detiene con los demás controladores).
- El cliente (`api/dispatch-contact-api.ts`) manda `X-Organization-Id`, `cache: "no-store"` y las credenciales de la
  sesión (token o cookie BFF); el parser (`contracts/dispatch-contact.ts`) acepta solo `{"phone_e164": …}` con el
  patrón de AI-05 o null.
- IndexedDB `paquetenvia-driver-stops-v1`, store nuevo `dispatch_contacts` con llave `partitionKey` (esquema 1: la
  llave de partición, el esquema y el número o null; nada más). Se usó un store nuevo y no uno existente: el único
  parecido, `organization_roles`, valida su registro con exactamente tres miembros de roles, y meter ahí el número
  cambiaría su forma para las apps de la versión 4.
- **La base sube a la versión 5.** La versión 4 ya llegó a main (PR #229/#231), así que, según las reglas de
  DRV-002 y de "No se pudo entregar", un store nuevo necesita su propia versión. La subida desde la 4 solo crea el
  store: acciones en cola (esquema 3), fotos, leases, instantáneas y roles guardados quedan igual; desde la 2 y la 3
  sigue migrando como antes. Una app de la versión 4 no puede abrir la base 5 (`VersionError`: muestra "La cola local
  no está disponible." y no sincroniza), y si una pestaña anterior tiene la base abierta, esta pide cerrarla y
  recargar, como en la subida a la 4. El shell del Service Worker sube a `paquetenvia-driver-shell-v8` para que el
  shell de la versión 4 no se sirva sin conexión.
- Sale del teléfono con la instantánea de la partición ante 401 o 403 (`clearPartition`, ahora sobre los stores de
  caché que existan: instantáneas, roles y despacho) y con todas las instantáneas al "Cerrar sesión"
  (`forgetDriverStopDetails`). La cola sigue sobreviviendo al cierre de sesión como dice DRV-002.

### Otros textos que mencionan a despacho (sin botón, se reportan para decidir)

No piden llamar a despacho por el cobro, así que no llevan el enlace:

- `driverIncidentMaybeOpenReportMessage`: "…Despacho decide qué sigue: avísales, no lo vuelvas a reportar y descarta
  esta acción."
- Mensajes de rechazo de `rejection-codes.ts` con "Avisa a despacho" (cobro: `CONFLICT`, `COD_NOT_EXPECTED`,
  `COD_ALREADY_RECORDED`, `COD_STATE_CONFLICT`; reporte: `INVALID_REQUEST`, `IDEMPOTENCY_CONFLICT`,
  `INCIDENT_STATE_CONFLICT`, `CONFLICT`).
- `driver-operations-controller.ts`: el aviso de vencimiento "…repórtala a despacho." y "…Avisa a despacho y no lo
  vuelvas a reportar."

## Pruebas

.NET:

- `Paqueteria.UnitTests/Drivers/DriverDispatchContactOptionsTests.cs`: vacío por omisión, cada organización con su
  número, el mismo número para dos organizaciones, entradas inválidas (UUID en mayúsculas, con llaves, sin guiones,
  nulo, con espacio, sin organización; número sin `+52`, con espacios o guiones, primer dígito 0 o 1, 9 u 11 dígitos,
  otro país, salto de línea, dígito no ASCII, sin número), organización repetida, `IStartupValidator` y que ningún
  mensaje ni `ToString` lleve el número.
- `Paqueteria.ContractTests/DriverDispatchContactContractTests.cs`: operación y esquema cerrado de AI-05 (mismas
  respuestas que `getMyDriverPhone`), patrón igual a la validación del servidor, entrada del delta con las citas
  literales, forma JSON de la respuesta (número y null), endpoint con capacidad antes de leer, sin base ni logs, y solo
  en la API. `CapabilityMatrixContractTests` (`dispatch_contact_operations`: solo DRIVER, sin MFA), los conteos de
  `OpenApiContractTests` (69 paths, 125 esquemas, 650 referencias), `HttpSurfaceOpenApiCoverageTests` (69) y el hash
  de AI-05 en `OpenApiBaselineTests`.
- `Paqueteria.IntegrationTests/Drivers/DriverDispatchContactHttpTests.cs` (host real con identidad mock): forma,
  `no-store`, número propio y nunca el de otra organización, null sin número, 401 sin sesión y 403 sin encabezado,
  con encabezado mal formado, con una organización ajena que sí tiene número y para cada otro rol o membresía.

Web (`apps/web`, vitest):

- `driver/contracts/dispatch-contact.test.ts`, `driver/api/dispatch-contact-api.test.ts`,
  `driver/cache/driver-dispatch-contact-cache.test.ts`, `driver/state/driver-dispatch-contact-controller.test.ts`:
  parser y alineación con AI-05, petición y clasificación de respuestas, registro guardado (forma estricta, otra
  partición, base sin el store, 401/403 y cerrar sesión), controlador (uso sin conexión, reemplazo, null, 401/403,
  reintentos y su límite, dispose).
- `driver/components/driver-dispatch-call.test.ts`: enlace `tel:+52…` con su etiqueta, nada sin número, 44 px, junto
  a cada texto y solo ahí, AI-07.
- `driver/offline/failed-delivery-policy.test.ts` (`callDispatch` en los cuatro avisos y en ningún otro),
  `driver/state/failed-delivery-controller.test.ts`, `driver/cache/driver-database-upgrade.test.ts` (subida 4 → 5),
  `driver/offline/driver-offline-queue.test.ts` (base 4 que sube a 5, apps 3 y 4 que no la abren),
  `driver/components/driver-stop-details-policy.test.ts`, `driver/service-worker-policy.test.ts`.

Playwright (`tests/Paqueteria.IntegrationTests/Driver`), las respuestas con la forma de la API
(`DriverDispatchContact.cs`, que serializa `DriverDispatchContactResponse`):

- `Recorded_cash_offers_Llamar_a_despacho_with_the_configured_number_also_offline`: con número sintético, el enlace
  aparece junto al texto del cobro registrado (`tel:+522000000001`, al menos 44 × 44 px, sin el número en pantalla),
  se guarda la respuesta y sigue después de recargar sin conexión.
- `Saved_cash_hides_the_report_until_discarded_and_recorded_cash_asks_to_call_dispatch`: sin número (`null`) el texto
  queda solo, sin enlace ni `tel:`.
- `Report_saved_offline_is_held_when_the_cash_was_recorded_meanwhile`: el aviso de atención lleva el enlace.
- `IndexedDb_v4_upgrade_only_adds_the_dispatch_contact_store`: la base 4 de main sube a 5 conservando la operación,
  la instantánea y los roles guardados. Las lecturas de IndexedDB de las demás pruebas abren la versión 5.

AZR-001: `tools/azr-001/test_env001_pilot_guards.py` (archivo del repositorio sin números; entradas válidas e
inválidas sin repetir ningún valor, también con el número como número JSON, con un miembro de más, con el número
escrito como nombre o como índice; nombres en otra grafía rechazados y con su valor revisado; nombres que solo
cambian en mayúsculas, prefijos administrados y ajustes eliminados comparados sin distinguir mayúsculas).

```bash
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm exec vitest run && pnpm run build
dotnet test tests/Paqueteria.UnitTests --filter "FullyQualifiedName~DriverDispatchContact"
dotnet test tests/Paqueteria.ContractTests --filter "FullyQualifiedName~DriverDispatchContact|FullyQualifiedName~CapabilityMatrixContractTests|FullyQualifiedName~OpenApi"
dotnet test tests/Paqueteria.IntegrationTests --filter "FullyQualifiedName~DriverDispatchContactHttpTests|FullyQualifiedName~HttpSurfaceOpenApiCoverageTests"
dotnet test tests/Paqueteria.IntegrationTests --filter "Category=DriverStopsPwa|Category=DriverOfflineOperationsPwa"
python3 -m unittest discover -s ./tools/azr-001 -p "test_*.py"
python3 docs/normative/v0.6/tools/validate_contracts.py
```

## Riesgos residuales

- El piloto no tiene número hasta que el owner lo dé; mientras tanto la app se ve como hoy.
- El número se lee al arrancar la sesión en la página (inicio de sesión, cambio de organización o recarga con
  conexión): si cambia en el servidor, un teléfono sin conexión sigue con el guardado hasta el siguiente arranque
  con conexión.
- Un teléfono cuyo primer arranque de la sesión fue sin conexión no tiene número guardado (cerrar sesión lo borra)
  hasta que una lectura responda: no ve el enlace, como hoy.
- La subida a IndexedDB 5 deja sin cola local a una pestaña abierta con la versión 4 hasta recargar (mismo
  comportamiento que en la subida a la 4).
- Cambiar el número es un PR a `apps.settings.json` y un redeploy de la API (no hay pantalla para editarlo).

## Rollback

- API: revertir quita la lectura; esta web recibe 404 y no muestra el enlace (los teléfonos conservan su respuesta
  guardada y siguen mostrando el enlace con ese número hasta un 401/403 o cerrar sesión). No hay datos ni esquema que
  deshacer. Quitar el número basta con borrar sus entradas de `apps.settings.json` y redesplegar: la respuesta pasa a
  null y cada teléfono lo reemplaza en su siguiente lectura.
- Web: no es un revert simple. Un revert pondría `DriverDatabaseVersion` en 4 y todo teléfono que ya subió a la 5
  rechazaría abrir la base (`VersionError`): la cola y las instantáneas quedarían no disponibles hasta borrar los
  datos del sitio. La salida es una versión nueva que conserva IndexedDB 5 con el store `dispatch_contacts` y deja de
  dibujar `DriverDispatchCall` (o deja de pedir la lectura), con un nombre nuevo para la caché del Service Worker.
  Nada de la cola se pierde.
