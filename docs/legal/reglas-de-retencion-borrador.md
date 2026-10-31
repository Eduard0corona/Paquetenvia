# Reglas de retención y borrado de datos personales (BORRADOR)

> **Borrador sin validez.** Lo redactó Claude por encargo del owner (fila `GATE-007-PRIVACY-DRAFT` del decision log)
> para que lo revise su abogado. **Todos los plazos son propuestas** para que el abogado y el owner los decidan; ninguno
> está aprobado. El borrador no resuelve GATE-007 y no autoriza datos personales reales en el piloto.
>
> La columna "Estado hoy" describe el código de la rama `development` y la configuración del piloto
> (`deploy/azure/pilot`) al 2026-10-19; el borrado temporal de las fotografías se actualizó el 2026-10-20
> (ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20).

Complementa el [aviso de privacidad integral](aviso-de-privacidad-integral-borrador.md).

## 1. Marco legal que orienta los plazos

| Regla | Qué dice | Uso en este documento |
| --- | --- | --- |
| LFPDPPP, artículo 10 | Los datos que dejaron de ser necesarios para las finalidades del aviso se suprimen, previo bloqueo en su caso, al concluir su plazo de conservación | Regla general: conservar solo mientras haga falta |
| LFPDPPP, artículos 2 (fracción III) y 24 | Al cancelar, los datos se bloquean (no se tratan) durante el plazo de prescripción de las acciones de la relación jurídica; después se suprimen y se avisa al titular | Plazo de bloqueo |
| LFPDPPP, artículo 25 | No hay obligación de cancelar, entre otros casos, datos que deben tratarse por disposición legal o que son necesarios para cumplir un contrato | Registros fiscales y contractuales |
| Código Fiscal de la Federación, artículo 30 | La contabilidad y su documentación se conservan cinco años, contados desde la declaración relacionada | Montos, cobros y pagos |
| Código de Comercio, artículos 38 y 46 | El comerciante conserva por al menos diez años los comprobantes de sus operaciones y sus libros, registros y documentos | Registros de órdenes y pagos |
| Código de Comercio, artículo 1047 | La prescripción ordinaria en materia comercial es de diez años | Plazo de bloqueo de evidencias comerciales |
| Ley Federal del Trabajo, artículos 804 y 516 | El patrón conserva contratos durante la relación y un año después, y nóminas y recibos del último año y un año después. Las acciones de trabajo prescriben en un año | Datos de repartidores, si GATE-008 concluye que hay relación laboral |

**[PENDIENTE: el abogado decide qué reglas aplican a cada dato. Por ejemplo: si un registro comercial debe conservar el
nombre y la dirección del destinatario, o si basta con conservar el número de guía, los montos y las fechas. Este
borrador propone lo segundo.]**

## 2. Cómo se puede borrar hoy

### 2.1 El sobre de cifrado (ADP-001)

Lo usan estos datos:

- dirección y referencias, nombre del contacto y teléfono de cada ubicación
  (`locations.locations.address_ciphertext`, `contact_name_ciphertext` y `phone_ciphertext`);
- descripción de incidentes (`incidents.incidents.description_ciphertext`);
- celular del repartidor (`drivers.driver_profiles.phone_ciphertext`).

Cada valor tiene su propia llave de datos (AES-256-GCM). Esa llave se envuelve con una llave RSA de Azure Key Vault y se
guarda dentro del mismo valor cifrado, que queda ligado a la organización, la fila y la columna.

Qué significa "borrado criptográfico" con este diseño:

1. **Por valor (por persona).** Al borrar o sobrescribir el valor cifrado se pierde su llave de datos, porque viaja
   dentro de él. No existe una llave por persona que se pueda destruir aparte. En la práctica, borrar un dato de una
   persona es borrar ese valor de la base de datos.
2. **Por llave de Key Vault.** Destruir la llave RSA deja ilegibles **todos** los valores protegidos con ella, de
   todas las personas. Azure Key Vault no permite borrar versiones individuales de una llave: se borra la llave
   completa. El vault del piloto tiene borrado temporal de 90 días y protección contra purga, así que una llave borrada
   se puede recuperar durante 90 días y nadie, ni Microsoft, puede purgarla antes. Sirve para apagar todo un entorno,
   no para atender a una persona.
3. **Respaldos.** Los respaldos de PostgreSQL (14 días de restauración a un punto en el tiempo) contienen los valores
   cifrados anteriores. Mientras exista la llave, son legibles al restaurar. Un dato borrado desaparece de los
   respaldos solo cuando pasan esos 14 días.
4. **Restricción del esquema.** `locations.locations.address_ciphertext` y `point` no aceptan `NULL` (AI-06). Para
   borrar la dirección y la ubicación hay que cambiar el esquema (AI-06 y una migración) o reemplazarlas por un valor
   neutro: un cifrado de texto vacío y un punto no identificable. **[PENDIENTE: decisión técnica del owner.]**

Las fotografías **no** usan este sobre. Viven en Azure Blob Storage, cifradas en reposo con llaves administradas por
Microsoft (`keySource: Microsoft.Storage`). Borrarlas es borrar el objeto. El almacenamiento del piloto guarda los
objetos borrados, y el estado anterior de uno sobrescrito, durante 30 días (borrado temporal,
ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20); en ese plazo se pueden recuperar. Una foto borrada por fin de plazo o por
una solicitud de la persona desaparece del todo 30 días después.

### 2.2 Purgas ya implementadas

| Purga | Qué borra | Regla | ¿Activa en el piloto? |
| --- | --- | --- | --- |
| OPS-004, outbox de negocio (`security.purge_outbox`) | Eventos internos `PROCESSED` y `DEAD` | `PROCESSED` después de 7 días y `DEAD` después de 30 (no se puede bajar de 1 y 7) | Sí (`OutboxRetention__Enabled=true`, `DryRun=false`) |
| OPS-004, outbox de ubicación (`security.purge_location_outbox`) | Eventos de posición del repartidor | `PROCESSED` después de 1 día y `DEAD` después de 7 (mínimos: 1 hora y 1 día) | Sí |
| OPS-003, llaves de idempotencia (`security.purge_expired_idempotency_keys`) | Respuestas guardadas para reintentos | Al vencer, y nunca antes de 72 horas | Sí |
| OPS-003, sesiones de carga de fotos (`security.expire_proof_upload_sessions`) | Nada: solo marca las sesiones vencidas como `EXPIRED` | Al vencer | Sí; no borra objetos |
| Sesiones de inicio de sesión (`security.purge_bff_sessions`) | Sesiones revocadas o vencidas y marcas de cierre de sesión vencidas | Revisa cada 5 minutos (valor por omisión). El ticket cifrado se borra en el momento de revocar la sesión | Sí (`OperationalCleanup__BffSessions__Enabled=true`) |

### 2.3 Retención de la infraestructura del piloto

| Componente | Retención | Fuente |
| --- | --- | --- |
| Log Analytics (registros técnicos) | 30 días | `deploy/azure/pilot/platform.bicep`, GATE-012-PILOT-SCOPE |
| Application Insights (trazas y métricas) | La del mismo workspace de Log Analytics. Exportación apagada. Enmascaramiento de IP activo | `platform.bicep` (`DisableIpMasking: false`), OBS-003-EXPORT-AFTER-PILOT-WEEK-2026-10-14. **[PENDIENTE: confirmar que ninguna tabla tenga retención propia.]** |
| Respaldos de PostgreSQL | 14 días, sin copia en otra región | `platform.bicep` (`backupRetentionDays: 14`), GATE-012-PILOT-SCOPE |
| Servidores de prueba de restauración | Se borran al terminar, salvo que se use `--keep` | `deploy/azure/pilot/restore-drill.sh` |
| Fotografías borradas o sobrescritas (Blob) | 30 días de borrado temporal, también si se borra el contenedor `proofs` (ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20). Sin versionado y sin política de ciclo de vida | `platform.bicep` |
| Llaves y secretos de Key Vault | 90 días de borrado temporal, con protección contra purga | `deploy/azure/pilot/security.bicep` |

### 2.4 Lo que no existe

- Ningún proceso borra o anonimiza datos personales al terminar un envío. Las purgas de la sección 2.2 no tocan los
  datos de destinatarios, remitentes ni repartidores; la única que borra datos de contacto es la de sesiones (nombre y
  correo del usuario dentro del ticket cifrado).
- `orders.orders.archived_at` no se usa: siempre se escribe `NULL` y nada lo cambia.
- No hay herramientas para buscar, exportar, rectificar o borrar los datos de una persona (ARCO).
- No hay política de ciclo de vida para las fotografías. Las rechazadas o nunca validadas se quedan en cuarentena sin
  límite.
- No se purgan: `locations.locations`, `drivers.driver_positions`, `organizations.pending_memberships` (las
  invitaciones vencidas se quedan inertes), `drivers.recipient_call_requests`, `identity.users` ni los registros de
  órdenes.
- No hay forma de dar de baja una cuenta ni a un repartidor. El repartidor sí puede borrar su celular.

## 3. Reglas propuestas por categoría

**Fin del envío** es el primer momento en que la orden llega a `CLOSED`, `RETURNED` o `CANCELLED` y no tiene
reclamación ni incidente abiertos. **[PENDIENTE: confirmar la definición.]**

### 3.1 Destinatarios y contactos de recolección o devolución

| Dato | Dónde | Plazo propuesto | Justificación | Cómo se borraría | Estado hoy |
| --- | --- | --- | --- | --- | --- |
| Nombre, teléfono, dirección y referencias | `locations.locations.contact_name_ciphertext`, `phone_ciphertext` y `address_ciphertext` | **90 días** después del fin del envío | Operativa: ventana de reclamación de 72 horas (`Orders:ClaimWindowHours`), aclaraciones con el cliente y reintentos de entrega. Para la defensa legal no hace falta el contacto | Borrado criptográfico por valor: nombre y teléfono a `NULL`; dirección con un valor neutro (sección 2.1, punto 4). Bloqueo previo si hay una reclamación en curso | No implementado |
| Correo del destinatario (opcional) **[PENDIENTE: GATE-007]** | `locations.locations.email_ciphertext` (llave de datos personales) | **90 días** después del fin del envío, como el teléfono | Igual que el teléfono: sirve para los avisos al destinatario mientras el envío está en curso (NTF-RECIPIENT-NOTICES-2026-10-20). La retención de las notificaciones debe cubrir el tiempo en que el enlace de baja del correo siga funcionando | Borrado criptográfico por valor: la columna a `NULL`, junto con sus copias de avisos | No implementado |
| Copias de teléfono y correo para avisos **[PENDIENTE: GATE-007]** | `locations.locations.notice_phone_ciphertext`, `notice_email_ciphertext` y `notice_key_version` (llave de avisos `recipient-notice-kek`) | Igual que el correo y el teléfono | Las únicas copias que el proceso de avisos puede abrir (E4-P2). Hoy no se escriben: la llave de avisos no está configurada (`Locations__NoticeKeyProtector=Disabled`) | Las tres columnas a `NULL` al borrar el contacto; un borrado criptográfico de la ubicación cubre todas las copias | No implementado; hoy vacías |
| Ubicación del domicilio | `locations.locations.point` | **90 días** después del fin del envío | Igual. Además está sin cifrar | Reemplazo por un punto no identificable, por ejemplo el centro de la zona; la columna no acepta `NULL` | No implementado |
| Resumen de dirección | `locations.locations.address_summary` | Igual que la dirección | Las ubicaciones de la cotización guardan un texto fijo sin datos personales; solo `createLocation` guarda texto del cliente | Sobrescritura | No implementado |
| Descripción de incidentes | `incidents.incidents.description_ciphertext` | **90 días** después del cierre del incidente y del fin del envío | Igual | Borrado criptográfico por valor | No implementado |
| Teléfono del destinatario en Twilio | Registros de llamadas de Twilio | **[PENDIENTE]** | Twilio conserva sus registros (VOICE-001-PROVIDER-TWILIO-2026-10-11) | Configuración o borrado en Twilio | Puente apagado; nada enviado |
| Datos de contacto en la app del repartidor | IndexedDB del teléfono | Mientras la parada esté asignada | Trabajo sin conexión | El navegador los borra al dejar de estar asignada la parada, al cerrar sesión o ante 401/403 | **Implementado** (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11) |

### 3.2 Envíos, evidencias y dinero

| Dato | Dónde | Plazo propuesto | Justificación | Cómo se borraría | Estado hoy |
| --- | --- | --- | --- | --- | --- |
| Registro de la orden: guía, estados, fechas, montos, organización, ubicaciones referenciadas | `orders.orders`, `orders.package_items`, `pricing.quotes` | **10 años** desde el fin del envío, ya sin datos de contacto | Código de Comercio, artículos 38, 46 y 1047; CFF, artículo 30 (5 años) como mínimo fiscal | Se conserva; los datos de contacto ya se borraron según la sección 3.1 | Se conserva sin límite |
| Descripción del paquete | `pricing.quotes.package_snapshot`, `orders.orders.package_snapshot`, `orders.package_items.description` y la respuesta guardada en `platform.idempotency_keys` | Igual que la orden | Describe la mercancía. No debería contener datos personales | Si los contiene y procede una cancelación: sobrescritura por el migrador (estas tablas no son de solo inserción) | Texto en claro, con filtro automático de correos, teléfonos y direcciones |
| Huella SHA-256 de la solicitud de cotización (incluye nombre, teléfono y dirección) | `pricing.quotes.input_hash`, `platform.idempotency_keys.request_hash` | Igual que la orden | Detecta reintentos | **[PENDIENTE: técnico. Es una huella sin llave; con los datos casi completos se podría verificar una conjetura. Opciones: aceptarla como dato seudonimizado o cambiarla a HMAC con llave.]** | En `pricing.quotes` sin límite; en idempotencia, purgada (sección 2.2) |
| Historial de la orden | `orders.order_events` (solo inserción) | Igual que la orden | Evidencia de cada cambio de estado | No se borra. No tiene datos de contacto; el motivo escrito pasa por el filtro automático. La persona que hizo el cambio queda seudonimizada con la regla de la sección 3.4 | Se conserva sin límite |
| Aceptación de términos y aviso | `orders.order_acceptances` (solo inserción) | Igual que la orden | Evidencia legal de la aceptación | No se borra. Contiene versiones, fechas, canal, usuario que la registró y huella; no guarda IP ni datos del cliente | Se conserva sin límite |
| Enlace de seguimiento | `orders.public_tracking_tokens` (solo la huella del token) | Igual que la orden | El enlace deja de funcionar 24 horas después del estado final | No requiere borrado: la huella no identifica a nadie | Implementado el fin del enlace |
| Fotografías de recolección, entrega e intento fallido | Azure Blob `proofs/{organización}/{orden}/{sesión}` | **12 meses** después del fin del envío, o hasta resolver la reclamación o el incidente, lo que ocurra después | Evidencia ante reclamaciones. El plazo de prescripción comercial (10 años) es mucho mayor; el abogado decide si las fotos deben vivir tanto | Borrado del objeto (30 días de borrado temporal). La fila de `custody.proofs` se conserva (sección 4) | No implementado; sin política de ciclo de vida |
| Fotografías rechazadas o huérfanas en cuarentena | Azure Blob `quarantine/…` | **30 días** | No sirven como evidencia | Borrado del objeto | No implementado: se quedan sin límite (riesgo residual de ADP-001) |
| Metadatos de las fotos | `custody.proofs` (solo inserción) y `custody.proof_upload_sessions` | Igual que la orden | Liga la evidencia con la orden | Ver sección 4 | Se conservan sin límite |
| Cobros contra entrega | `finance.cod_transactions` | **10 años** | CFF, artículo 30; Código de Comercio, artículos 38 y 46 | Se conservan; no tienen datos de contacto | Se conservan sin límite |

### 3.3 Repartidores

| Dato | Dónde | Plazo propuesto | Justificación | Cómo se borraría | Estado hoy |
| --- | --- | --- | --- | --- | --- |
| Perfil (organización, ciudad, tipo, vehículo, estado) | `drivers.driver_profiles`, `drivers.driver_service_areas` | Durante la relación y **1 año** después | LFT, artículos 804 y 516, si hay relación laboral (GATE-008); si no, el plazo contractual que diga el abogado | El perfil queda `INACTIVE`. La fila no se borra porque la referencian asignaciones, posiciones y solicitudes de llamada | No implementado (no hay baja) |
| Celular del repartidor | `drivers.driver_profiles.phone_ciphertext` | Mientras esté registrado | Consentimiento `VOICE-001-CONSENT-V1` | Borrado por el propio repartidor (a `NULL`). También debería borrarse al terminar la relación | **Implementado** el borrado por el repartidor; la baja administrativa no |
| Posiciones del dispositivo | `drivers.driver_positions` | **30 días** | Operativa: tablero de despacho e investigación de incidentes. Proporcionalidad del monitoreo laboral | Borrado de filas (no es tabla de solo inserción; el outbox de ubicación no tiene llave foránea hacia ella) | No implementado (SCL-006 lo pide). Hoy la app no envía posiciones |
| Eventos de posición | `platform.location_outbox_events` | 1 día procesados, 7 días fallidos | Ya decidido (OPS-004) | Purga | **Implementado** |
| Documentos | `drivers.driver_documents` (solo metadatos) | Vigencia del documento y **1 año** después de terminar la relación | Igual que el perfil | Borrado del archivo y de la fila | No aplica: no se cargan documentos |
| Solicitudes de llamada (sin números) | `drivers.recipient_call_requests` | **12 meses** | Aclaraciones de costo y abuso del puente | Borrado de filas | No implementado; el puente está apagado |
| Asignaciones, rutas y paradas | `dispatch.assignments`, `routes.routes`, `routes.route_stops` | Igual que la orden | Liga trabajo y pagos | Se conservan | Se conservan sin límite |
| Pagos (liquidaciones) | `finance.settlements`, `finance.settlement_lines` | **10 años** (CFF, artículo 30: 5 años como mínimo) | Obligaciones fiscales y comerciales | Se conservan | Se conservan sin límite |

### 3.4 Usuarios y organizaciones

| Dato | Dónde | Plazo propuesto | Justificación | Cómo se borraría | Estado hoy |
| --- | --- | --- | --- | --- | --- |
| Identificador de la cuenta de AuthCenter | `identity.users.identity_subject` | Mientras la cuenta esté activa. Al darla de baja, seudonimizar | Otras tablas guardan el `id` interno del usuario (auditoría, historial, fotos) | Reemplazar `identity_subject` por un valor aleatorio sin relación con AuthCenter y conservar el `id` interno. Requiere una función nueva | No implementado (no hay baja) |
| Nombre y correo | Solo en la sesión cifrada (`identity.bff_sessions.ticket_ciphertext`) | Lo que dura la sesión: 8 horas por omisión y 24 como máximo | Inicio de sesión | Se borra al revocar o vencer la sesión | **Implementado** |
| Membresías y roles | `organizations.organization_memberships` | Mientras exista la organización | Evidencia de permisos | Se conservan con estado `REVOKED` | Se conservan sin límite |
| Invitaciones por correo (solo HMAC) | `organizations.pending_memberships` | **30 días** después de vencer, revocarse o aceptarse | La invitación ya no sirve | Borrado de filas | No implementado: las vencidas se quedan inertes |
| Nombre legal y comercial de la organización | `organizations.organizations` | Mientras exista la organización y **10 años** después | Código de Comercio | Se conservan | Se conservan sin límite |
| Datos de la cuenta en AuthCenter | AuthCenter (fuera de este repositorio) | **[PENDIENTE]** | — | — | Sin verificar |

### 3.5 Registros de la plataforma

| Dato | Dónde | Plazo propuesto | Justificación | Cómo se borraría | Estado hoy |
| --- | --- | --- | --- | --- | --- |
| Auditoría: quién hizo qué y cuándo | `platform.audit_logs` (solo inserción) | **10 años**, igual que los registros comerciales | Seguridad, aclaraciones y defensa | No se borra. No guarda datos de contacto (el filtro los reemplaza por `[REDACTED]`). La persona queda seudonimizada con la regla de `identity.users` | Se conserva sin límite |
| Eventos internos | `platform.outbox_events` | 7 días procesados, 30 fallidos | Ya decidido (OPS-004) | Purga | **Implementado** |
| Respuestas para reintentos | `platform.idempotency_keys` | Al vencer, nunca antes de 72 horas | Ya decidido (OPS-003) | Purga | **Implementado** |
| Registros técnicos | Log Analytics | 30 días | GATE-012-PILOT-SCOPE | Lo hace Azure | **Implementado** |
| Respaldos | PostgreSQL | 14 días | GATE-012-PILOT-SCOPE | Lo hace Azure. Ver sección 5, paso 6 | **Implementado** |

## 4. Tablas de solo inserción

AI-01 §4 (regla 13) y AI-06 declaran de solo inserción cuatro tablas. Un trigger (`platform.reject_runtime_mutation`)
rechaza cualquier `UPDATE` o `DELETE` que no venga del rol `paqueteria_migrator`. Para los registros de evidencia y
de auditoría, AI-04 (`privacy_invariants`) dice que las acciones de privacidad usan **restricción de acceso,
seudonimización o borrado criptográfico bajo una política aprobada**, no el borrado de filas.

| Tabla | Datos personales que contiene | Cómo se atiende una cancelación |
| --- | --- | --- |
| `orders.order_acceptances` | `actor_id` del usuario que registró la aceptación. Ningún dato del cliente, ni IP ni navegador | Seudonimizar a la persona en `identity.users`. La fila no cambia |
| `orders.order_events` | `actor_id` y el motivo escrito del cambio, filtrado automáticamente | Igual. **[PENDIENTE: el filtro no detecta nombres. Si un motivo contiene un nombre, solo el migrador puede corregirlo, con un procedimiento aprobado.]** |
| `custody.proofs` | `object_key` (ruta de la foto), `created_by`, `captured_at`; `captured_point` (coordenadas en claro, si la API las recibe); `recipient_name_ciphertext` (siempre `NULL`: la API rechaza el nombre) | Borrar el **objeto** de la foto en Blob, porque el contenido personal vive ahí. La fila se conserva como constancia de que hubo evidencia, con su huella SHA-256. **[PENDIENTE: la app del repartidor no envía coordenadas, pero la API las acepta y quedarían en claro en una fila inalterable. Propuesta técnica: dejar de aceptarlas o cifrarlas.]** |
| `platform.audit_logs` | `actor_id`, `request_id` (identificador de traza) y una carga filtrada sin datos de contacto | Seudonimizar a la persona en `identity.users` |

Borrar o corregir una fila de estas tablas como migrador cambia una regla obligatoria del sistema (AI-01 §4, regla 13).
Requiere un ADR, una decisión del owner y pruebas; no es una tarea operativa.

## 5. Procedimiento propuesto para una solicitud de cancelación

**[PENDIENTE: nada de esto existe hoy. Es una propuesta para el abogado y para un trabajo técnico posterior.]**

1. Recibir la solicitud en [CORREO ARCO], verificar la identidad y registrarla con su fecha (plazo de 20 días hábiles,
   artículo 31).
2. Localizar los datos con lo que dé el titular (número de guía, organización, referencia "DRV-").
3. Revisar si aplica una excepción del artículo 25 (por ejemplo, una reclamación en curso o una obligación fiscal).
4. Si procede: bloquear (restringir el acceso) y, al terminar el plazo de bloqueo, borrar los valores cifrados, los
   objetos de fotografía y las posiciones, y seudonimizar al usuario, como dicen las secciones 3 y 4.
5. Avisar al titular. Si sus datos se comunicaron a un aliado o a un encargado, avisarles también para que los
   cancelen (artículo 24).
6. Llevar un registro de cancelaciones aplicadas (sin los datos borrados) para volver a aplicarlas si se restaura un
   respaldo de los últimos 14 días.

## 6. Trabajo técnico pendiente

Cada punto necesita su propia tarea y, cuando cambie contratos o invariantes, su decisión del owner:

1. Un job que, al fin del envío más el plazo aprobado, borre los datos de contacto de `locations.locations` e
   `incidents.incidents` (y defina el valor neutro de la dirección y el punto, o cambie AI-06).
2. Usar `orders.orders.archived_at`, o decidir eliminarlo, como marca de que una orden ya no tiene datos de contacto.
3. Una política de ciclo de vida o un job para las fotografías en `proofs/` y `quarantine/`.
4. La purga de `drivers.driver_positions` (SCL-006), si se habilita la ubicación.
5. La purga de `organizations.pending_memberships` y de `drivers.recipient_call_requests`.
6. Baja de usuario y de repartidor, con seudonimización de `identity_subject` y borrado del celular.
7. Herramientas ARCO para buscar, exportar, rectificar y cancelar por titular, con auditoría.
8. Decidir sobre `custody.proofs.captured_point` y sobre la huella sin llave de la cotización.
9. Confirmar o configurar la retención de Twilio, y la de Meta y Google si se habilitan.
10. Confirmar que Azure no registra rutas `/track/…` ni IP de visitantes.

## 7. Decisiones pendientes del abogado y del owner

- Aprobar o cambiar cada plazo propuesto: 90 días para datos de contacto, 12 meses para fotos, 30 días para posiciones,
  10 años para registros comerciales, entre otros.
- Definir el "fin del envío" y qué pasa con las órdenes que nunca terminan, por ejemplo una que se queda en
  `FAILED_ATTEMPT`.
- Plazo de bloqueo según la prescripción aplicable a cada relación: remitente, destinatario y repartidor.
- Si las fotos de entrega se tratan como evidencia de larga duración o como dato operativo de corta duración.
- Retención en los proveedores (Twilio, Meta, Google, Microsoft) y en AuthCenter.

## Fuentes

Consultadas el 2026-10-19:

1. Ley Federal de Protección de Datos Personales en Posesión de los Particulares (nueva ley DOF 20-03-2025; última
   reforma DOF 14-11-2025): <https://www.diputados.gob.mx/LeyesBiblio/pdf/LFPDPPP.pdf>. Artículos 2, 10, 24, 25 y 31.
2. Código Fiscal de la Federación (última reforma DOF 09-04-2026): <https://www.diputados.gob.mx/LeyesBiblio/pdf/CFF.pdf>.
   Artículo 30.
3. Código de Comercio (última reforma DOF 14-11-2025): <https://www.diputados.gob.mx/LeyesBiblio/pdf/CCom.pdf>.
   Artículos 38, 46 y 1047.
4. Ley Federal del Trabajo (última reforma DOF 14-05-2026): <https://www.diputados.gob.mx/LeyesBiblio/pdf/LFT.pdf>.
   Artículos 516 y 804.
5. Microsoft Learn, "Azure Key Vault recovery overview" (borrado temporal y protección contra purga):
   <https://learn.microsoft.com/en-us/azure/key-vault/general/key-vault-recovery>.
6. Microsoft Learn, `KeyClient.StartDeleteKey` ("The delete key operation cannot be used to remove individual
   versions of a key"):
   <https://learn.microsoft.com/en-us/dotnet/api/azure.security.keyvault.keys.keyclient.startdeletekey?view=azure-dotnet>.
