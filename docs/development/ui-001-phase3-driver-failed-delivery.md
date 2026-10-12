# UI-001 fase 3: "No se pudo entregar" en la PWA del repartidor

Decisión: `UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12` (fila ADR en `docs/normative/v0.6/decision-log.md`; cambio
MAJOR según AI-01 §7). Se construye sobre `INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11`
([servidor y secuencia de API](inc-failed-attempt-dispatch-decides.md#secuencia-de-api-para-la-pwa-del-repartidor)) y
sobre la cola sin conexión de DRV-002 con el cobro de `UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11`
([cola](drv-002-offline-events-pod.md), [cobro](ui-001-phase3-driver-stop-details.md)). Contratos: AI-07
`/driver/stops/:id` y `screen_contracts.driver_stop.failed_delivery`, `cod_collection.not_paid` y
`cod_collection.door_only`. AI-04, AI-06 y AI-18 no cambian; AI-05 y el servidor solo agregan la lectura de roles
propios `GET /me/organization-roles` (`ORG-MY-ROLES-2026-10-12`, ver "Quien también es despacho").

## Lo que decidió el owner (literal)

- Fase 3 aprobada con "avanza con la fase 3". La pantalla de entrega del diseño tiene "Cómo llegar", "Llamar",
  "Llegué al destino", "Foto de entrega", "Cobro $…", "Confirmar entrega", "Vista previa de la foto · Repetir",
  "Usar esta foto" y "No se pudo entregar". "No se pudo entregar" abre "¿Qué pasó?" con el subtítulo "El despacho
  decidirá si se reprograma o regresa"; las opciones son "No había nadie", "Dirección incorrecta", "Rechazó el
  paquete", "No pagó el cobro" y "Otro…"; después "Foto del lugar (obligatoria)" y el botón "Enviar reporte".
- 2026-10-11 (`INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11`): "Despacho decide (Recommended)": el repartidor solo
  manda motivo y foto; la orden queda en "Requiere atención" y el despachador elige reprogramar, reintentar hoy o
  regresar. "Lista cerrada (Recommended)": "Otro…" muestra "Acceso restringido", "Paquete dañado" y "Riesgo de
  seguridad"; el sistema arma la descripción con el motivo; no queda texto libre en el teléfono. "Según el motivo
  (Recommended)": el servidor deriva la severidad.
- 2026-10-12, pregunta: "Repartidor, "No se pudo entregar": la lista cerrada incluye "No pagó el cobro". ¿Se muestra
  también en entregas que no tienen cobro en la puerta?" → "Solo si hay cobro (Recommended)": ""No pagó el cobro"
  aparece solo cuando la parada tiene un cobro COD; en las demás se ven los otros motivos y "Otro…"." (No se eligió
  "Siempre": "La lista se ve igual en todas las entregas, aunque la orden no tenga cobro.")
- 2026-10-12, pregunta: "Repartidor: si ya registró el cobro ("Recibí $X") y luego no puede entregar (por ejemplo, el
  paquete viene dañado), ¿puede reportar "No se pudo entregar" desde la app? Hoy el servidor lo permite y el cobro
  queda registrado en una orden sin entregar." → "No, llama a despacho (Recommended)": "Con un cobro guardado en el
  teléfono o ya registrado, "No se pudo entregar" no aparece. Si fue un error, descarta el cobro guardado; si ya se
  registró, la app le pide llamar a despacho, que decide y coordina la devolución del dinero." (No se eligió "Sí, se
  reporta igual": "El reporte se envía aunque haya cobro; el cobro registrado queda para que finanzas y despacho lo
  revisen después.")
- 2026-10-12, después de la revisión del PR, pregunta: "Si una persona es repartidor y también despacho en la misma
  empresa (por ejemplo, tú haciendo una entrega), el servidor trata su reporte como de despacho y rechaza el reporte
  corto del teléfono, porque a despacho le pide gravedad, descripción y siguiente paso. ¿Qué hacemos?" → "Registrar
  desde Despacho (Recommended)": "La app no le ofrece "No se pudo entregar" y le explica que registre el intento desde
  la orden en Despacho, donde elige motivo y siguiente paso. Sin cambiar el servidor." (No se eligió "Que el servidor
  lo acepte": "ADR para que, si tiene la parada asignada y reporta desde el teléfono, el servidor lo trate como
  repartidor y despacho decida después.")
- 2026-10-12, pregunta: "Si el reporte se interrumpe después de subir la "Foto del lugar" y antes de abrir la
  incidencia, esa foto queda guardada como foto de entrega y el servidor podría aceptarla como prueba de entrega. La
  app puede reducir esos casos pero no eliminarlos. ¿Lo cerramos del lado del servidor?" → "Después, con ADR
  (Recommended)": "Lo registro como riesgo y pendiente (un tipo de foto propio para intento fallido, o que la entrega
  cite su foto). En el uso normal de la app la entrega siempre sube su propia foto, así que no bloquea el piloto."
  (No se eligió "Ahora, antes de fusionar".)
- 2026-10-12, después de la segunda revisión (`ORG-MY-ROLES-2026-10-12`), pregunta: ""Registrar desde Despacho"
  no se puede cumplir sin tocar el servidor: el API solo dice un rol por empresa, así que la app del repartidor no sabe
  que esa persona también es despacho. ¿Qué hacemos?" → "Agregar los roles al API (Recommended)": "Cambio pequeño y
  aditivo: el contexto de cada empresa trae también la lista de roles de la persona (con ADR). La app no le ofrece el
  reporte desde el inicio y no se sube ninguna foto." (No se eligió "Aprender tras el primer rechazo": "Sin tocar el
  servidor: la primera vez se sube la foto, el servidor rechaza el reporte y la app le dice que use Despacho; desde ahí
  ya no se lo ofrece en esa sesión. Queda una foto suelta por sesión.")
- 2026-10-12, después de la tercera revisión (la primera versión puso `roles` dentro de `OrganizationContext`; nunca
  se fusionó ni se desplegó), pregunta: "Agregar "roles" dentro del contexto de cada empresa rompe el inicio de sesión
  de la web actual (la de hoy rechaza campos que no conoce) si el API se publica antes que la web, y deja fallando las
  pestañas ya abiertas hasta recargar. ¿Cómo lo hacemos?" → "Endpoint nuevo de roles (Recommended)": "Los roles salen
  de una consulta nueva del API (solo los de la persona en su empresa activa). La web actual nunca la llama, así que
  nada se rompe en ningún orden de despliegue; mismo resultado en la app del repartidor." (No se eligió "Campo en el
  contexto y orden de despliegue": "Se queda como está y se fuerza publicar la web antes que el API (cambio en la
  infraestructura de Azure); las pestañas abiertas durante el despliegue fallan hasta recargar.")

## Pantalla

| Situación de la parada de entrega | Qué se ve |
| --- | --- |
| DELIVERING (confirmado o por sincronizar), sin cobro guardado ni registrado | "No se pudo entregar" |
| Igual, pero la persona también es DISPATCHER o PLATFORM_ADMIN en la organización | "También eres despacho en esta organización, así que el intento fallido no se reporta desde el teléfono: regístralo desde la orden en Despacho, donde eliges el motivo y el siguiente paso." Sin formulario |
| Igual, mientras la consulta de roles va en camino y no hay respuesta conocida (justo después de cada inicio de sesión, porque cerrar sesión borra la guardada, o durante un reintento) | Nada sobre el rol: el reporte todavía no se ofrece y no se muestra ningún aviso |
| Igual, pero el teléfono no conoce los roles de la persona en la organización y no hay consulta en camino (ninguna respuesta de `GET /me/organization-roles` en esta sesión ni guardada para su partición) | "No pudimos confirmar tu rol. Vuelve a abrir la app con conexión para reportar." Sin formulario; nunca pide cerrar sesión, que borraría lo guardado en el teléfono |
| Cobro guardado en el teléfono (en cola, reintentando o retenido) | "Hay un cobro guardado en el teléfono. Si fue un error, descártalo para poder reportar que no se pudo entregar." y "Descartar cobro guardado" (con la confirmación de siempre); si el cobro está retenido, se descarta desde su aviso |
| Cobro registrado (`DriverStop.cod.recorded`, o el servidor ya aceptó el cobro y falta la confirmación REST) | "El cobro ya está registrado. Si no puedes entregar, llama a despacho: ellos deciden y coordinan la devolución del cobro." Sin número de teléfono: la app no tiene el de despacho |
| Reporte guardado, esperando sincronizar | "Reporte guardado en el teléfono. Se enviará cuando haya conexión." El estado confirmado sigue "En reparto" y "Estado por sincronizar" dice "Intento fallido" |
| Reporte retenido | El aviso de la parada con el motivo en español (ver "Atención") |
| FAILED_ATTEMPT confirmado por REST | "Intento fallido · El despacho decide" en el estado de la lista y del detalle; ninguna acción |
| Recolección, devolución, antes de llegar, otra acción de la parada retenida o una entrega ya guardada | Nada |

- "¿Qué pasó?" es un grupo de radios (`fieldset`/`legend`) con renglones de 44 px. "No pagó el cobro" solo aparece si
  `DriverStop.cod` no es null (la única fuente del cobro; nunca se infiere de otra cosa). "Otro…" es una opción más y
  muestra un segundo grupo, "Otro motivo", con los tres motivos. Las etiquetas son las de
  `Incidents.Domain.IncidentDriverReportPolicy` (en la web, `incidentReasonLabels`).
- "Foto del lugar (obligatoria)": exactamente una foto JPEG o PNG de hasta 10 MiB, con el mismo `DriverProofPreview`
  de la entrega: "Vista previa de la foto", "Repetir" y "Usar esta foto". "Usar esta foto" solo deja la foto lista
  ("Foto lista para el reporte.") y "Repetir" la cambia. El object URL se revoca al repetir, al usarla y al cerrar el
  paso; nada se guarda antes de "Enviar reporte".
- "Enviar reporte" está deshabilitado hasta tener motivo y foto, con la indicación "Elige qué pasó y toma la foto del
  lugar para enviar el reporte." No pide confirmación, igual que "Confirmar entrega" ("Usar esta foto" encola la
  entrega directamente). "Cancelar" cierra el formulario y olvida motivo y foto. Si el teléfono no guarda el reporte,
  el formulario dice por qué (cobro registrado o guardado, ya hay un reporte, la entrega ya está guardada, quien
  también es despacho, la parada cambió, otra acción en curso); "No se pudo guardar el reporte en el teléfono. Vuelve
  a intentarlo." queda solo para una falla al guardarlo.
- No hay ningún campo de texto.
- El estado confirmado FAILED_ATTEMPT deja de contar el cobro de la parada como pendiente ("Cobrar $X" y el
  encabezado): lo siguiente lo decide despacho.
- Quien también es despacho (`ORG-MY-ROLES-2026-10-12`): `GET /me/organization-contexts` devuelve un contexto por
  organización, con un solo `role` (el de la membresía por defecto), así que con eso el teléfono nunca veía a un
  repartidor que también es despacho. Esa respuesta no cambia (cuatro miembros; el parser estricto de la web tampoco).
  La API agrega `getMyOrganizationRoles` (`GET /me/organization-roles` con `X-Organization-Id`): el objeto cerrado
  `{"roles":[...]}` con los roles propios de quien pregunta en la organización elegida, por membresías ACTIVE de una
  organización ACTIVE (sin repetidos, en orden ordinal, nunca vacío); 403 a quien no tiene ahí una membresía activa,
  401 sin sesión, 503 si la lectura falla, `Cache-Control: no-store`. El servidor revisa la capacidad
  (`own_membership_operations`, cualquier rol miembro, sin MFA) antes de leer, lee dentro de una transacción tenant con
  RLS filtrando a la propia persona y nunca escribe.
- En la PWA, `DriverRolesController` pide la consulta al instalarse la sesión del repartidor y en cada sesión nueva
  (otro inicio de sesión u otra organización son otra sesión y otra partición). Primero usa la respuesta guardada para
  la partición, luego pregunta: una respuesta correcta la reemplaza; 401 o 403 la borra; cualquier otra falla (sin
  conexión, timeout, 5xx, el 404 de una API anterior o una respuesta fuera de contrato) conserva la guardada y vuelve
  a preguntar cuando regresa la conexión (evento `online`) o la app vuelve a verse (`visibilitychange`), solo si el
  teléfono está en línea, una consulta a la vez, como máximo 5 veces por sesión, nunca después de 401 o 403 y nunca
  después de cerrar la sesión (los eventos se quitan y la consulta en camino se cancela).
  `driverReportRoleOf` decide con lo que sabe el teléfono: con DISPATCHER o PLATFORM_ADMIN, "dispatch" (el aviso de
  registrar desde Despacho; el formulario no aparece y no se sube ninguna foto); con DRIVER y sin esos, "driver" (se
  ofrece, también sin conexión); sin respuesta conocida y con una consulta en camino, "loading": todavía no se ofrece y
  no se dice nada del rol (pasa después de cada inicio de sesión, porque cerrar sesión borra la respuesta guardada);
  sin respuesta conocida ni consulta en camino (una respuesta fuera de contrato nunca se usa ni se guarda), "unknown":
  no se ofrece (falla cerrado) y la parada dice "No pudimos confirmar tu rol. Vuelve a abrir la app con conexión para
  reportar." Ningún aviso de rol pide cerrar sesión: cerrar sesión con mala cobertura borra lo que el teléfono guarda
  sin conexión. El controlador de operaciones aplica la misma regla antes de guardar nada ("loading" responde "Estamos
  confirmando tu rol. Intenta de nuevo en un momento."). Se usa PLATFORM_ADMIN aunque el servidor solo lo trate como
  despacho con MFA: el teléfono no sabe si hay MFA y así falla cerrado.
- La respuesta se guarda en el IndexedDB particionado de siempre (`paquetenvia-driver-stops-v1`), store
  `organization_roles` con llave `partitionKey` (esquema 1: la llave de partición, el esquema y los códigos de rol; nada
  más). El store lo crea la misma subida a la versión 4 que agrega `FAILED_DELIVERY`: esa versión no ha llegado a
  ningún teléfono (este PR no se ha fusionado ni desplegado), así que no sube a 5. El código tolera una base sin el
  store (lee "sin respuesta" y no falla). Sale del teléfono junto con la instantánea de la partición ante 401 o 403
  (`clearPartition`) y con todas las instantáneas al "Cerrar sesión" (`forgetDriverStopDetails`); la cola sigue
  sobreviviendo al cierre de sesión como dice DRV-002. Ninguna regla de DRV-001/DRV-002 choca con guardarla: son
  códigos de rol de la propia persona, sin PII, y se borran con la partición.
- Despliegue: no hace falta ningún orden. La web anterior nunca llama la consulta nueva, así que una API nueva no la
  afecta (tampoco a las pestañas abiertas). Esta web con una API anterior recibe 404: el inicio de sesión, las paradas,
  la entrega, el cobro y las demás acciones siguen igual, y solo "No se pudo entregar" no se ofrece (con el aviso de rol
  no confirmado). El portal local de desarrollo vuelve a leer los contextos como en `development`.

## Secuencia y cola sin conexión

"Enviar reporte" encola una operación cerrada `FAILED_DELIVERY` (`DELIVERING` → `FAILED_ATTEMPT`, motivo de
transición `DRIVER_DELIVERY_FAILED_REPORTED`, prueba `DELIVERY_PHOTO`) con la foto en `proof_blobs`, en la misma
transacción de IndexedDB. El planificador corre la secuencia de AI-05, cada petición al API con su Idempotency-Key,
reutilizada solo para reintentar el mismo contenido:

| Paso | Petición | Llave | Avance que se guarda |
| --- | --- | --- | --- |
| 1 | `POST /orders/{id}/proof-upload-sessions` (`DELIVERY_PHOTO`) | `drv2-{op}-session-{n}` | `uploadSessionId`, `uploadSessionExpiresAt` |
| 2 | `PUT` a `upload_url` con solo los `required_headers`, sin credenciales | (la URL firmada vive solo en memoria) | `uploadAccepted` |
| 3 | `POST /orders/{id}/proofs` | `drv2-{op}-finalize-{n}` | `proofId`; la foto local se borra |
| 4 | `POST /orders/{id}/incidents` con `type` FAILED_DELIVERY_ATTEMPT, `reason_code`, `occurred_at` y `evidence_proof_ids` [prueba]; nunca `severity`, `description` ni `next_action` | `drv2-{op}-incident` | antes de mandarla, `failedDelivery.incidentRequested`; con la respuesta, `failedDelivery.incidentId` |
| 5 | `POST /orders/{id}/transitions` a FAILED_ATTEMPT con `expected_version`, `metadata.incident_id` y `client_occurred_at` | `drv2-{op}-transition` | `AWAITING_REST_CONFIRMATION` |

- `occurred_at`, `client_occurred_at` y `captured_at` son el mismo instante (el del reporte); el servidor aplica a cada
  uno la regla de 72 horas (`OFFLINE_OPERATION_EXPIRED` saca la operación una vez y avisa, como las demás).
- **El intento que describe.** Mientras no exista su incidencia, un reporte solo se manda si REST muestra la parada en
  DELIVERING en la versión exacta en que se hizo; se revisa antes de la sesión de carga y otra vez antes de la
  incidencia. Si no (despacho registró el intento fallido por su cuenta, o movió la orden, por ejemplo "Reintentar
  hoy"), no se manda nada (ni foto ni incidencia) y el reporte queda retenido (`VERSION_CONFLICT`). Ya abierta la
  incidencia, la transición se repite con su llave sin importar lo que muestre REST: su respuesta guardada es lo que
  dice si el servidor la aplicó.
- **Confirmación por REST.** Un reporte solo se confirma por su propio trabajo: con la transición aceptada
  (`AWAITING_REST_CONFIRMATION`), cualquier versión posterior de la parada, decida lo que decida despacho después; antes,
  FAILED_ATTEMPT en una versión posterior solo si su propia incidencia existe. Un intento fallido que registró despacho
  por su cuenta nunca confirma un reporte que no se mandó: queda retenido con "Despacho ya registró el intento fallido
  de esta entrega, así que tu reporte no se envió. Descártalo." y se puede descartar. La proyección muestra ya
  confirmado un reporte que REST confirma, nunca como conflicto, y la parada vuelve a ofrecer "Confirmar entrega" si
  despacho pidió reintentar. Mientras ese reporte confirmado espera a salir de la cola, la parada puede volver a
  ofrecer "No se pudo entregar" y el teléfono acepta el reporte nuevo: al revisar si ya hay uno guardado ignora los
  que REST ya confirma (`driverOperationConfirmedByStop`), igual que la proyección.
- **Aceptado, esperando a REST.** Una acción que el servidor ya aceptó (`AWAITING_REST_CONFIRMATION`) sigue esperando a
  REST con backoff aunque falle la lectura de confirmación (la de justo después del 2xx o una posterior, por red,
  contrato o error inesperado): nunca vuelve a `RETRY_WAIT`, nunca se manda de nuevo ni queda como conflicto, y a las
  72 horas sale de la cola sin el aviso de "venció… Vuelve a registrarla" (el servidor la tiene). Solo una sesión
  revocada (401/403) vacía la partición, como siempre.
- **Parada que REST no muestra.** Si la respuesta de las paradas deja fuera la parada (por romper el contrato), un
  reporte o un cobro no se manda: queda en espera (`RETRY_WAIT`, `RESOURCE_UNAVAILABLE`) y se vuelve a leer después del
  backoff, también en las revisiones antes de la incidencia y de la transición. No espera para siempre: sin mandarse,
  vence a las 72 horas como cualquier acción; y una acción que el servidor ya aceptó y que REST nunca muestra sale de la
  cola a las 72 horas, sin aviso, porque el servidor ya la tiene.
- Después de una respuesta perdida o de cerrar la app, la operación sigue en el paso siguiente al último guardado con
  las mismas llaves: el servidor repite la sesión, la prueba, la incidencia o la transición, y nunca se abre una
  segunda incidencia. Una transición que el servidor ya aplicó se confirma por REST sin reenviarla.
- La incidencia que regresa el servidor se valida contra lo enviado (orden, motivo, `next_action` null, instante y la
  prueba); si no coincide, la operación queda en atención y no se manda la transición.
- La transición no se manda sin incidencia. La parada sigue en la lista porque la asignación no se cierra (D8).
- Orden por orden: el reporte es la primera acción de la puerta (antes del cobro y la entrega). La pantalla nunca
  guarda un reporte junto a un cobro o una entrega de la misma orden; si aun así coinciden (por ejemplo desde otra
  pestaña), nada más de esa orden se manda mientras el reporte esté pendiente, reintentando o retenido. Una acción que
  el servidor ya aceptó nunca vuelve a quedar bloqueada ni pendiente de mandar.

### Atención

| Resultado | Tratamiento |
| --- | --- |
| Red, timeout o 5xx en cualquier paso | `RETRY_WAIT` con el backoff de siempre; sigue en el mismo paso |
| El intento cambió antes de la incidencia | Retenido sin mandar nada: "Despacho ya registró el intento fallido de esta entrega, así que tu reporte no se envió. Descártalo." o "La entrega cambió desde que guardaste el reporte (por ejemplo, despacho pidió reintentarla), así que el reporte no se envió. Descártalo y, si de nuevo no puedes entregar, vuelve a reportar con una foto nueva." |
| 409 de la incidencia (`INVALID_REQUEST`, `IDEMPOTENCY_CONFLICT`, `ORDER_STATE_NOT_ALLOWED`, `EVIDENCE_NOT_AVAILABLE`, `INCIDENT_STATE_CONFLICT`, `CONFLICT`) | Atención con el código y su mensaje en español; `INVALID_REQUEST` (lo que el servidor contesta a quien trata como despacho) dice que registre el intento desde la orden en Despacho o avise a despacho, nunca habla del reloj |
| 409 de la transición (por ejemplo `VERSION_CONFLICT` o `DISPATCH_DECISION_REQUIRED`) | Atención; como la incidencia ya existe, el aviso dice "Tu reporte ya quedó abierto como incidencia y despacho decide qué sigue. No lo vuelvas a reportar: descarta esta acción." |
| Incidencia pedida sin respuesta y luego retenido | "Puede que tu reporte ya haya quedado abierto como incidencia…": nunca "no se envió" |
| Sesión de carga vencida | Atención con "Crear nueva sesión" (nuevas llaves de sesión y prueba, misma foto e incidencia) |
| 401/403 | Limpia la partición, como siempre |
| 404 | Saca la operación, como siempre |
| Cobro guardado o registrado | Atención `COD_COLLECTED` (ver "Cobro") |

Las acciones de atención de un reporte son descartar, reintentar la misma acción (solo donde todavía aplica y sin
cobro registrado) y, con la sesión vencida, crear una nueva sesión. Un reporte **nunca se rehace para otra versión**
("Crear acción para versión actual" no aparece y el controlador lo rechaza): antes de su incidencia describe un intento
que ya cambió, así que se descarta y, si hace falta, se reporta de nuevo desde la parada con motivo y foto nuevos;
después de su incidencia, esa incidencia es del intento anterior (y despacho pudo haberla usado ya), así que decide
despacho. Reusar el motivo o la foto en otro intento podría mandar a FAILED_ATTEMPT una orden con un reporte de un
intento anterior.

Lo que queda de una parada que despacho movió (FAILED_ATTEMPT, RESCHEDULED, RETURNING) siempre se puede descartar:
al descartar una acción ahí, la primera que queda de la orden pasa a atención; y una acción bloqueada sin nada
retenido antes que ella se muestra como retenida. Reintentar la misma acción también libera las que quedaron
bloqueadas detrás de ella.

Al vencer (72 horas), un reporte cuya incidencia está abierta, o puede estarlo, no dice "ya no se enviará. Vuelve a
registrarla": dice que la incidencia quedó abierta y que despacho decide, y que no lo vuelva a reportar.

### Límites, privacidad, pausa y otra pestaña

- Los de DRV-002: 100 operaciones y 25 MiB de fotos por partición, 10 MiB por foto, solo JPEG y PNG, estimación de
  almacenamiento.
- La operación solo guarda códigos, ids, llaves, el SHA-256 y el tamaño de la foto. Nada de texto, nombre, dirección,
  token, URL firmada ni object key en IndexedDB, logs o consola. La respuesta de roles guardada solo tiene la llave de
  partición, el esquema y los códigos de rol (`organization_roles`).
- Descartar pausa la sincronización: el pase en curso se aborta y se espera, la operación se borra y se recalcula la
  cadena de la orden, y luego se reanuda y se pide una sincronización. Así un cobro que se descarta mientras estaba
  pendiente no se manda ni se vuelve a escribir.
- Si otra pestaña de Paquetenvia (por ejemplo de la versión anterior) tiene la base abierta durante la subida de
  versión, abrir la base falla con un error propio y la pantalla dice "Hay otra pestaña de Paquetenvia abierta con una
  versión anterior. Ciérrala y vuelve a cargar esta página para usar las acciones guardadas en el teléfono."; la
  conexión que el navegador abre después se cierra en el acto. Cada conexión se cierra sola cuando una versión más
  nueva pide la base, así que esta pestaña no bloquea una subida futura.

## Versión del contrato y una app anterior

- El contrato de operación sube al esquema 3 (miembro `failedDelivery`: motivo, llave de la incidencia de esa misma
  operación, incidencia pedida, id de la incidencia) e IndexedDB a la versión 4. La subida reescribe en el mismo lugar
  las operaciones en cola de esquema 2 (versión 3) y de esquema 1 (versión 2) agregando los miembros nuevos en null;
  las instantáneas de esquema 2 se conservan. El parser sigue estricto a un solo esquema. La misma subida crea el store
  `organization_roles` (`ORG-MY-ROLES-2026-10-12`).
- El store `organization_roles` se agregó a la versión 4 en la tercera revisión, sin subir a 5, por la misma razón que
  el punto siguiente: ninguna compilación con la versión 4 llegó a un teléfono real. Un navegador de prueba que ya
  tenga la versión 4 sin ese store sigue funcionando: con conexión usa la respuesta de la API sin guardarla, y al
  arrancar sin conexión no tiene respuesta guardada (el reporte falla cerrado) hasta borrar los datos del sitio.
- El esquema 3 cambió de forma durante la revisión de este PR sin subir de número (se agregó
  `failedDelivery.incidentRequested` y la llave de la incidencia quedó atada al id de su operación). No hace falta
  migrar nada: ninguna compilación con la forma anterior llegó a un teléfono real (el PR no se fusionó ni se desplegó);
  solo existieron en pruebas.
- Se eligió subir la versión de IndexedDB (como lo hizo `UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11`) en lugar de mezclar
  esquemas: el navegador nunca abre una base con una versión menor a la actual, así que una app anterior (la de la
  versión 3) no puede abrirla y no puede leer mal, mandar ni borrar el tipo nuevo; muestra "La cola local no está
  disponible." y no arranca la sincronización. Si dentro de una misma versión aparece un registro con un esquema o un
  tipo que la app no conoce, el parser lo rechaza y toda la cola se reporta no disponible, sin mandar ni borrar nada.
- La caché del Service Worker sube a `paquetenvia-driver-shell-v7` para que el shell anterior (que abriría la versión
  3) no se sirva sin conexión.

## Cobro (respuesta del owner "No, llama a despacho (Recommended)")

- En la pantalla: con un cobro guardado no aparece "No se pudo entregar" y se ofrece descartar el cobro con la
  confirmación de siempre; al descartarlo aparece. Con el cobro registrado se pide llamar a despacho.
- En la sincronización: antes de la sesión de carga, otra vez antes de la incidencia y otra antes de la transición, el
  planificador revisa la cola y REST. Si la orden tiene un cobro guardado (en cualquier estado) o registrado, el reporte
  queda en atención (`COD_COLLECTED`), y lo que sigue de la orden queda bloqueado (salvo lo que el servidor ya aceptó).
  Antes de la incidencia el aviso dice "El cobro de esta entrega ya está registrado, así que el reporte no se envió.
  Llama a despacho: ellos deciden y coordinan la devolución del cobro." (solo "Descartar") o "Hay un cobro guardado en
  el teléfono para esta entrega, así que el reporte no se envió. Si el cobro fue un error, descártalo y el reporte se
  enviará; si no, descarta este reporte." Al descartar el cobro, la cadena se recalcula y el reporte sigue.
- La incidencia nunca se pide con un cobro guardado o registrado. Si el cobro se registra después de abrirla (por
  ejemplo, despacho lo registra entre la incidencia y la transición), la transición no se manda y el aviso dice que el
  reporte ya quedó abierto como incidencia y que llame a despacho: el reporte y el cobro pueden quedar los dos en el
  servidor, y despacho decide.
- Un cobro nunca se registra si REST muestra la parada fuera de DELIVERING (por ejemplo después de un intento
  fallido) ni mientras REST no la muestre: queda en atención o en espera. El servidor sí lo aceptaría en FAILED_ATTEMPT.
- Solo en la PWA: el servidor sigue aceptando DELIVERING → FAILED_ATTEMPT con el cobro registrado (AI-04 no tiene esa
  guarda), así que un reporte del despacho no se bloquea, a propósito, porque despacho decide.

## "Foto del lugar" sin incidencia (respuesta del owner "Después, con ADR (Recommended)")

La foto se finaliza como `DELIVERY_PHOTO` en el paso 3 y solo deja de contar para DELIVERED cuando una incidencia la
cita (guarda del servidor: cualquier `DELIVERY_PHOTO` del intento actual que no sea evidencia de una incidencia). Si el
flujo se detiene entre el paso 3 y el 4 (retenido por cobro o por un intento que cambió, una incidencia rechazada, un
reporte descartado o vencido), queda una foto huérfana que el servidor podría aceptar para DELIVERED en ese intento.
Queda como riesgo residual y pendiente de servidor con ADR (un tipo de foto propio para intento fallido, o que la
entrega cite su foto); no se cambia el servidor. La PWA lo reduce: revisa el intento y el cobro antes de pedir la
sesión de carga (así un reporte viejo o retenido no sube foto) y otra vez antes de la incidencia, no ofrece el reporte
a quien también es despacho (cuya incidencia el servidor rechaza) y no lo ofrece con un cobro guardado o registrado. En
el uso normal la entrega siempre sube su propia foto.

## Pruebas

Vitest (`apps/web`):

- `driver/contracts/failed-delivery.test.ts`: lista cerrada contra el enum de AI-05 y las etiquetas del servidor;
  "No pagó el cobro" solo con cobro.
- `driver/offline/failed-delivery-contract.test.ts`: forma válida, progreso de cada paso (incidencia pedida sin
  respuesta incluida), formas inválidas (llave de otra operación, incidencia sin pedir, incidencia pedida sin foto),
  tipo desconocido, esquema anterior y posterior, migración, sin datos personales.
- `driver/offline/failed-delivery-policy.test.ts`: proyección (pendiente como pendiente, confirmado como "El despacho
  decide", reporte aceptado ya confirmado aunque despacho decidiera después, intento fallido de despacho como
  retenido, acción bloqueada visible, orden en la puerta, cobro fuera del encabezado), cuándo se ofrece (también a quien
  es despacho), retención por cobro y todos los avisos (incidencia abierta o posiblemente abierta, intento cambiado).
- `driver/offline/driver-offline-queue.test.ts` (IndexedDB en memoria, `cache/fake-indexed-db.fixtures.ts`): escritura
  atómica de operación y foto, límites, lease, registro ilegible sin borrar nada, subida de la versión 3 a la 4, una app
  de la versión 3 que no puede abrir la base, una subida que bloquea otra pestaña y la conexión tardía que se cierra.
- `driver/offline/failed-delivery-sync.test.ts`: los cinco pasos con sus llaves; respuesta perdida y red caída en cada
  paso; app cerrada después de cada paso (incidencia pedida sin respuesta incluida); 409 `INVALID_REQUEST`,
  `EVIDENCE_NOT_AVAILABLE`, `DISPATCH_DECISION_REQUIRED` y `VERSION_CONFLICT`; sesión vencida; 401/403;
  `OFFLINE_OPERATION_EXPIRED`; incidencia ajena; intento fallido de despacho antes de mandar (nada se manda); intento
  movido antes de la sesión y antes de la incidencia; transición aplicada con respuesta perdida y despacho que decide
  después; parada fuera de la respuesta (reporte y cobro, antes y después de la foto, vencimiento y acción aceptada sin
  confirmar); cobro guardado, registrado, registrado durante la carga y justo después de la incidencia; cobro fuera de
  la puerta; nada más de la orden después del reporte; pausa.
- `driver/offline/failed-delivery-sync-api.test.ts`: cuerpo exacto de la incidencia y de la transición, llaves,
  lectura estricta de la respuesta y clasificación de errores.
- `driver/state/failed-delivery-controller.test.ts` y `driver/components/driver-failed-delivery.test.ts`: reglas de
  "Enviar reporte" con su motivo real, quien también es despacho (DISPATCHER o PLATFORM_ADMIN en su respuesta de
  roles), roles desconocidos (API anterior con 404, respuesta vacía o 503 sin nada guardado: falla cerrado), un
  reporte confirmado por REST que ya no impide uno nuevo, nunca
  rehacer un reporte, descartar lo que queda de una parada movida, reintentar libera lo bloqueado, avisos de
  vencimiento, pestaña que bloquea, estados de la pantalla, motivos, "Otro…", foto obligatoria, botón deshabilitado,
  vista previa, textos en cola y confirmado, 44 px y sin texto libre. Los roles salen de respuestas de
  `/me/organization-roles` con la forma de la API, por el cliente, el parser y el controlador reales
  (`driver/state/driver-roles.fixtures.ts`), nunca de una bandera inyectada.
- `driver/contracts/organization-roles.test.ts`, `driver/api/organization-roles-api.test.ts`,
  `driver/cache/driver-roles-cache.test.ts` y `driver/state/driver-roles-controller.test.ts`: el objeto cerrado
  `{"roles":[...]}` (vacío, repetido, otro miembro o más de 64 se rechazan), la petición (`X-Organization-Id`,
  `no-store`, credenciales de la sesión) y la clasificación 401/403/404/5xx; el registro guardado (solo llave de
  partición, esquema y códigos; uno ajeno o con otra forma se rechaza; base sin el store); y el controlador: usa lo
  guardado sin conexión, lo reemplaza con cada respuesta correcta, lo borra con 401/403, lo conserva ante red, 5xx, 404
  o respuesta fuera de contrato, se lee como "loading" (nunca "unknown") mientras la primera consulta va en camino,
  vuelve a preguntar tras una falla al regresar la conexión o al volver a verse (no si sigue sin conexión, ni después de
  una respuesta), como máximo 5 veces, nunca después de 401/403, y no escribe ni pregunta nada después de cerrarse (un
  reintento en camino se cancela). `failed-delivery-policy.test.ts` y `driver-failed-delivery.test.ts`: con "loading"
  no se ofrece ni se dice nada; el aviso de rol desconocido nunca pide cerrar sesión.
- `driver/cache/driver-database-upgrade.test.ts` y `driver/components/driver-stop-details-policy.test.ts`: la base
  nueva trae `organization_roles`, y "Cerrar sesión" y 401/403 la vacían con las instantáneas.
- `driver/offline/driver-sync-scheduler.test.ts`: una acción aceptada cuya lectura de confirmación falla (red,
  contrato o error inesperado) sigue esperando a REST, no se vuelve a mandar y a las 72 horas sale sin aviso de
  vencida.
Playwright (`tests/Paqueteria.IntegrationTests/Driver`): `DriverOfflineOperationsPwaPlaywrightTests` agrega el flujo
en línea (orden de llamadas, cuerpo exacto, llaves), sin conexión con recarga y sincronización (la página arranca sin
conexión y reporta con la respuesta de roles guardada, que se revisa: solo llave, esquema y `DRIVER`), cobro guardado y
registrado, reporte retenido por un cobro registrado mientras tanto, reporte retenido sin mandar nada porque despacho
registró el intento fallido (y descartado), quien también es despacho (DRIVER y DISPATCHER en la misma organización,
respuesta guardada incluida), una API anterior sin la consulta (404: no se ofrece el reporte, nada se guarda y la
entrega se sincroniza igual) y la subida de IndexedDB 3 → 4; `DriverStopsPwaPlaywrightTests` agrega la pantalla de
320 px (44 px, sin desbordes) y el intento fallido confirmado. La respuesta de roles la arma el propio registro de la
API (`MyOrganizationRolesResponse` con `MyOrganizationRoles.FromRoles`, `DriverOrganizationRoles.cs`), nunca una
bandera inyectada.

.NET: `MyOrganizationRolesContractTests` (operación y esquema cerrado de AI-05, mismas respuestas que
`getMyDriverPhone`, `OrganizationContext` de vuelta a cuatro miembros, entrada `ORG-MY-ROLES`, forma JSON del registro,
orden y unicidad, endpoint sin escrituras y capacidad antes de leer), `CapabilityMatrixContractTests`
(`own_membership_operations`: todos los roles miembros, sin MFA), `ActiveTenantRuntimeContractTests` (lector en
PostgreSQL real como `paqueteria_app`: solo DRIVER, DRIVER y DISPATCHER, DRIVER y PLATFORM_ADMIN, membresías
suspendidas, revocadas, de otra organización o de una organización suspendida, y nunca los roles de otra persona) y
`PostgreSqlBootstrapTrackingTests` (endpoint real con RLS: roles propios por organización elegida, `no-store`, 403 sin
membresía activa ahí, sin encabezado o con una membresía suspendida o revocada, y 401 sin sesión).

```bash
cd apps/web && pnpm run lint && pnpm run typecheck && pnpm exec vitest run && pnpm run build
dotnet test tests/Paqueteria.IntegrationTests --filter "Category=DriverStopsPwa|Category=DriverOfflineOperationsPwa"
dotnet test tests/Paqueteria.ArchitectureTests tests/Paqueteria.ContractTests --filter "FullyQualifiedName~SyntheticEnvironmentArchitectureTests|FullyQualifiedName~NormativeBaselineTests"
dotnet test tests/Paqueteria.ContractTests --filter "FullyQualifiedName~MyOrganizationRolesContractTests|FullyQualifiedName~CapabilityMatrixContractTests|FullyQualifiedName~OpenApi|FullyQualifiedName~ActiveTenantRuntimeContractTests"
dotnet test tests/Paqueteria.IntegrationTests --filter "FullyQualifiedName~PostgreSqlBootstrapTrackingTests"
python3 docs/normative/v0.6/tools/validate_contracts.py
```

## Riesgos residuales

- Un reporte del despacho no se bloquea con el cobro registrado (a propósito: despacho decide).
- Si el cobro se registra después de abrir la incidencia, la incidencia queda abierta con la orden en DELIVERING y el
  reporte en atención; el reporte y el cobro pueden quedar los dos en el servidor; despacho la ve en Incidencias y
  decide.
- Un cobro registrado entre la última revisión y una petición no se puede impedir desde el teléfono.
- `DriverStop.cod.recorded` es falso para un registro de cobro DISPUTED o REVERSED: el teléfono puede tratar ese cobro
  como pendiente.
- La "Foto del lugar" que queda sin incidencia podría contar para DELIVERED en ese intento (respuesta "Después, con
  ADR"): pendiente de servidor.
- Un reporte que vence (72 horas) después de abrir su incidencia deja esa incidencia abierta para despacho; el aviso lo
  dice y no invita a reportarlo de nuevo, pero el teléfono no impide que el repartidor, después de descartar un reporte
  con incidencia abierta, reporte otra vez en el mismo intento.
- No hay número de despacho en la app; "llama a despacho" depende de que el repartidor lo tenga.
- Los roles se leen al arrancar la sesión en la página (inicio de sesión, cambio de organización o recarga con
  conexión): si a la persona le dan o le quitan DISPATCHER o PLATFORM_ADMIN con la página abierta, o mientras el
  teléfono está sin conexión con una respuesta guardada, el teléfono lo sabe hasta el siguiente arranque con conexión.
  El servidor sigue siendo la barrera: si ya es despacho, rechaza el reporte corto (409 `INVALID_REQUEST`, que dice
  registrar desde Despacho).
- Un repartidor cuyo primer arranque de la sesión fue sin conexión (o contra una API anterior) no tiene respuesta
  guardada y no puede reportar hasta que una consulta responda: la app vuelve a preguntar al regresar la conexión o al
  volver a verse, hasta 5 veces por sesión; después, hasta volver a abrir la app con conexión. Mientras tanto despacho
  registra el intento.
- Una persona con DRIVER y otro rol en la misma organización sin membresía por defecto recibe en `role` de
  `/me/organization-contexts` uno de los dos (como antes de este cambio); solo con DRIVER ahí entra a la app del
  repartidor.
- IndexedDB es almacenamiento del navegador; la foto puede contener información visual sensible (GATE-007).
- Con una entrega guardada en el teléfono (por ejemplo esperando el cobro) no se ofrece el reporte.

## Rollback

No es un revert simple. Un revert pondría `DriverDatabaseVersion` en 3: todo teléfono que ya subió a la versión 4
rechaza abrirla (`VersionError`), así que la cola y las instantáneas quedan no disponibles y nada se sincroniza hasta
borrar los datos del sitio (perdiendo lo que esté en cola). Dejar la versión 4 con el código anterior tampoco sirve:
su parser solo lee operaciones de esquema 2 y la subida ya las dejó en esquema 3, así que la cola falla cerrada.

Hay dos salidas, las dos como una versión nueva de la PWA:

1. **Apagar la entrada (preferida).** Una versión que deja de ofrecer "No se pudo entregar" (la disponibilidad
   regresa siempre "nada") y conserva IndexedDB 4, el esquema 3, el parser y la sincronización de `FAILED_DELIVERY`.
   No se pierde nada: los reportes en cola terminan, se retienen o se descartan como hoy; las demás acciones siguen
   igual.
2. **Quitar el tipo.** Una versión con IndexedDB 5 cuya subida, en la misma transacción, reescribe cada operación en
   cola de esquema 3 que no sea `FAILED_DELIVERY` como esquema 2 (quitando `failedDelivery`, que es null en ellas) y
   borra cada `FAILED_DELIVERY` con su foto; desde las versiones 2 y 3 sigue migrando como antes, y lleva el parser de
   esquema 2 y una caché del Service Worker con nombre nuevo. Las demás acciones en cola se conservan y se sincronizan
   como antes. Los reportes en cola se pierden sin mandarse: los que ya abrieron su incidencia la dejan abierta para
   despacho; los que no, no dejan nada en el servidor. Por eso, antes de publicarla, se pide a los repartidores
   sincronizar y despacho registra desde la orden los intentos que falten.

El servidor, los datos y el esquema no cambian en ningún caso.

La lectura de roles (`ORG-MY-ROLES-2026-10-12`) se revierte aparte, en cualquier orden: revertir la API deja a esta
web con el 404 de una API anterior (cada teléfono conserva su respuesta guardada; sin ella, solo el reporte deja de
ofrecerse), y revertir la web deja una lectura que nadie llama. No hay datos ni esquema que deshacer; el store
`organization_roles` sigue la suerte de la versión 4 descrita arriba.
