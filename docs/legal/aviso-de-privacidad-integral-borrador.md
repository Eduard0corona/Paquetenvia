# Aviso de privacidad integral (BORRADOR)

> **Borrador sin validez legal.** Lo redactó Claude por encargo del owner (fila `GATE-007-PRIVACY-DRAFT` del
> decision log, 2026-09-27) para que lo revise su abogado. No está aprobado, no resuelve GATE-007 y no se publica ni se
> muestra a nadie. Ningún dato personal real entra al piloto hasta que el owner registre su aprobación.
>
> - **[PENDIENTE: …]** marca lo que deben decidir el abogado o el owner.
> - Los recuadros **"Nota para revisión"** dicen de dónde sale cada afirmación en el sistema. Se borran del texto final.
> - Las afirmaciones sobre qué guarda o envía el sistema describen el código de la rama `development` al 2026-10-19.
>   Si el código cambia antes de publicar el aviso, hay que revisarlas de nuevo.

**Versión del aviso:** [VERSIÓN] · **Última actualización:** [FECHA]

> **Nota para revisión.** La versión será el valor de `privacy_version` que el sistema guarda con cada orden
> (`PAQUETERIA_PRIVACY_VERSION` del contenedor web; formato `^[A-Za-z0-9._-]{1,64}$`, por ejemplo `2026-11-01`). Hoy vale
> `OWNER_DECISION_REQUIRED` y el despliegue del piloto se detiene mientras no cambie
> (`docs/operations/env-001-pilot/README.md`).

**[PENDIENTE: tratamiento de "usted" o "tú". Las pantallas del sistema tutean ("Consulta el estado de tu envío").]**

---

## 1. Identidad y domicilio del responsable

**[RAZÓN SOCIAL]**, con domicilio en **[DOMICILIO]**, es responsable del tratamiento de sus datos personales en la
plataforma de envíos [NOMBRE DEL SERVICIO]. En este aviso la llamamos "la plataforma".

Para cualquier asunto sobre sus datos personales escriba a **[CORREO ARCO]**. Ahí atiende la persona o el departamento
de datos personales que designamos ([PENDIENTE: nombre o área]).

**[PENDIENTE: razón social, domicilio y correo. No se inventan.]**

**[PENDIENTE: nombre comercial. GATE-001 sigue abierto: el nombre "Paquetenvia" no está validado ante el IMPI. Por la
decisión GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10, la página pública de seguimiento y los mensajes al destinatario no
muestran marca y dicen "Seguimiento de envío". La ley exige identificar al responsable. El abogado y el owner deciden
cómo hacerlo en la página pública, sobre todo si la razón social contiene la marca.]**

**[PENDIENTE, CENTRAL: papel de cada organización. La plataforma es multiempresa. Cada orden pertenece a una
organización dueña (una empresa cliente o la propia plataforma) y la puede operar otra organización (un aliado de
reparto). La empresa cliente captura los datos del destinatario. El abogado decide si la plataforma es responsable de
esos datos, si es encargada de la empresa cliente, o si hay responsables distintos para cada tramo. Este borrador
supone que la plataforma es la responsable en todos los casos. Si la conclusión es otra, cambian los apartados 1, 5
y 6 y hacen falta contratos de encargo con las empresas.]**

> **Nota para revisión.** Tipos de organización: `PLATFORM`, `BUSINESS` y `ALLY` (`organizations.organizations` en
> `docs/normative/v0.6/database/AI-06_SCHEMA.sql`). Cada orden tiene `owner_org_id` y, opcionalmente, un
> `operator_org_id` distinto (`orders.orders` en AI-06).

## 2. A quién aplica este aviso

| Persona titular | Relación con la plataforma |
| --- | --- |
| **Destinatario** | Persona que recibe un envío. Sus datos nos los da quien solicita el envío; no nos los da usted directamente. |
| **Contacto de recolección o devolución (remitente)** | Persona que entrega el paquete al repartidor o a quien se le regresa. Puede ser la misma empresa cliente o alguien en su nombre. |
| **Usuario de una organización** | Personal de empresas clientes, aliados de reparto o de la propia plataforma que entra al sistema: administración, despacho, finanzas, consulta. |
| **Repartidor** | Persona que hace recolecciones y entregas: propio, externo o de un aliado. |
| **Visitante de la página de seguimiento** | Cualquier persona que abre un enlace de seguimiento de envío. |

**[PENDIENTE: si una empresa cliente es persona física con actividad empresarial, su nombre o razón social es dato
personal. Decidir si se le aplica la sección de usuarios o una propia.]**

## 3. Datos personales que tratamos

No pedimos datos personales sensibles. La sección 3.6 explica en qué casos podrían aparecer sin que los pidamos.

### 3.1 Destinatarios y contactos de recolección o devolución

| Dato | Cómo lo obtenemos | Cómo lo protegemos |
| --- | --- | --- |
| Nombre del contacto | Lo captura el personal de la organización al cotizar el envío | Cifrado, con una llave distinta por dato |
| Teléfono (10 dígitos de México) | Igual | Cifrado |
| Dirección y referencias para llegar | Igual | Cifrado |
| Ubicación del domicilio en el mapa (latitud y longitud) | Igual: el personal marca el punto | **Sin cifrar.** Se usa para saber si el domicilio está dentro de la zona de servicio |
| Datos del envío: número de guía, estado, fechas y horas de cada cambio, ventana de entrega, monto a cobrar contra entrega y si se cobró | Los genera la plataforma | Acceso restringido a la organización de la orden |
| Fotografías de recolección y de entrega, y la foto del lugar cuando no se pudo entregar | Las toma el repartidor | El teléfono reduce la foto y le quita los metadatos (ubicación GPS, modelo del teléfono, hora) antes de guardarla. Almacenamiento privado; cada foto pasa un análisis de malware antes de aceptarse |
| Motivo de un intento fallido (por ejemplo "No había nadie") y la descripción que escribe el personal de despacho | Repartidor y despacho | La descripción se guarda cifrada |

Hoy **no** recabamos del destinatario: correo electrónico, firma, identificación oficial, nombre de quien recibe
(el sistema lo rechaza), dirección IP ni datos de su dispositivo.

**[PENDIENTE: una fotografía de entrega puede mostrar a la persona que recibe, su domicilio o placas de vehículos.]**
Los metadatos de la imagen (EXIF), que según el teléfono pueden llevar la ubicación GPS y la hora de la toma, se quitan
en el teléfono antes de guardar la foto (decisión del owner DRV-PROOF-PHOTO-REDUCE-2026-10-20).

**[PENDIENTE: avisos al destinatario por WhatsApp y correo (GATE-004). Los conectores existen pero están apagados y
nada los usa. Hoy no se recaba el correo del destinatario: el formulario de cotización no tiene ese campo. Si se
habilitan antes de publicar el aviso, se agregan: el teléfono para WhatsApp, el correo si se empieza a pedir, y el
contenido de los mensajes (número de guía y enlace de seguimiento).]**

> **Nota para revisión.**
> - Captura: `AddressInput` de `createQuote` (AI-05) pide `address_text`, `contact_name` y `phone`; `lat`, `lng` y
>   `references` también entran. La cotización crea dos filas `locations.locations` (origen y destino).
> - Cifrado: `address_ciphertext` (dirección y referencias juntas), `contact_name_ciphertext` y `phone_ciphertext`, con
>   el sobre de ADP-001: llave de datos AES-256-GCM propia de cada valor, envuelta con una llave RSA de Key Vault y
>   ligada a organización, fila y columna (`docs/development/adp-001-production-adapters.md`). El piloto usa
>   `Locations__PiiProtector=AzureKeyVault` (`deploy/azure/pilot/apps.bicep`).
> - `point` (coordenadas) va en claro. `address_summary` va en claro, pero las ubicaciones que crea la cotización
>   guardan un texto fijo ("Synthetic quote origin/destination"). Solo `createLocation`, que la web no usa, guarda un
>   resumen escrito por el cliente.
> - Quién ve los datos descifrados: solo la app del repartidor asignado mientras la parada está asignada (nombre corto
>   en entregas, nombre completo en recolección y devolución, dirección, referencias y coordenadas) y el puente de
>   llamadas (VOICE-001). Lectores: `PostgreSqlDriverStopsQuery` y `VoicePhoneProtection`.
> - El rol VIEWER ve las coordenadas redondeadas a unos 1.1 km (D5-VIEWER-LOCATION-PRECISION-2026-09-27).
> - `FinalizeProofRequest.recipient_name` se rechaza con `PII_PROTECTION_UNAVAILABLE`. La app del repartidor no manda
>   `lat`/`lng` al finalizar la foto; la API sí los acepta y los guardaría en claro en `custody.proofs.captured_point`.
> - La app reduce la foto en el teléfono antes de calcular su huella y guardarla: un JPEG de máximo 1600 px por lado,
>   calidad 80 %, sin EXIF, GPS, XMP, perfil ICC ni comentarios. La evidencia guardada es esa foto reducida; el archivo
>   original no se guarda ni se envía (`apps/web/src/driver/proofs/proof-photo-reduce.ts`,
>   DRV-PROOF-PHOTO-REDUCE-2026-10-20).
> - La aceptación guarda el `actor_id` del usuario que la registró; IP y user-agent están prohibidos mientras GATE-007
>   siga abierto (AI-04 `OrderAcceptance`).

### 3.2 Remitentes y empresas clientes

Además de los datos de contacto de la sección 3.1:

| Dato | Cómo lo obtenemos | Cómo lo protegemos |
| --- | --- | --- |
| Descripción, peso, medidas y valor declarado de cada paquete | Los captura el personal | **Sin cifrar.** Antes de guardar, el sistema sustituye por `[REDACTED]` los valores que parecen correo, teléfono o dirección. No detecta nombres ni datos de salud |
| Quién paga el envío (remitente, destinatario o cuenta empresarial) y montos | Los captura el personal y los calcula la plataforma | Acceso restringido a la organización |
| Aceptación de términos y aviso de privacidad: versiones aceptadas, fecha y hora, canal (asistido por el personal) y quién la registró | La registra el personal al crear la orden | Registro inalterable; incluye una huella SHA-256 de esos datos |
| Confirmación de que el envío no lleva artículos prohibidos | La registra el personal | Registro inalterable |
| Nombre legal y nombre comercial de la organización | Los da quien registra la organización | Acceso restringido a la organización |

**[PENDIENTE: la aceptación la registra el personal de la organización ("El cliente vio el desglose y aceptó los
términos y el aviso de privacidad vigentes"). El abogado decide si ese registro asistido basta como evidencia y cómo se
pone el aviso a disposición del cliente en ese momento (artículo 16 de la Ley).]**

**[PENDIENTE: pedir al personal que no escriba nombres ni datos de salud en la descripción del paquete. Por ejemplo, una
farmacia que escribe "medicamento controlado para Juan Pérez".]**

> **Nota para revisión.** Evidencia de aceptación: `orders.order_acceptances` (AI-06), forma canónica
> `OrderAcceptanceCanonicalForm v1` (AI-04 `legal_acceptance_invariants`). Canal `ASSISTED` fijo en la web
> (`apps/web/src/operations/contracts/create-order.ts`). Descripción del paquete: `pricing.quotes.package_snapshot`,
> `orders.orders.package_snapshot` y `orders.package_items.description`, después de `AuditPayloadRedactor`. La
> cotización también guarda una huella SHA-256 sin llave de toda la solicitud, contacto incluido (`pricing.quotes.input_hash`,
> `platform.idempotency_keys.request_hash`).

### 3.3 Usuarios de las organizaciones

| Dato | Cómo lo obtenemos | Cómo lo protegemos |
| --- | --- | --- |
| Identificador de su cuenta en AuthCenter, nuestro servicio de inicio de sesión | AuthCenter, al iniciar sesión | Se guarda para reconocer su cuenta |
| Nombre y correo electrónico | AuthCenter, al iniciar sesión | **No se guardan en nuestra base de datos.** Viven solo dentro de la sesión cifrada, que dura 8 horas por omisión y nunca más de 24, y se borran al cerrar sesión o al vencer |
| Si su correo está verificado y si usó un segundo factor de autenticación | AuthCenter | Dentro de la sesión cifrada |
| Organizaciones a las que pertenece, rol y estado | Los asigna un administrador o se crean al registrar una organización | Acceso restringido |
| Invitaciones por correo | Las crea un administrador | Solo se guarda una huella con llave secreta (HMAC) del correo, nunca el correo. Vencen a los 7 días |
| Historial de acciones: qué hizo, cuándo, sobre qué orden o registro | Lo genera la plataforma | Registro de auditoría inalterable, sin datos de contacto |

Los datos de su cuenta en AuthCenter (correo, nombre, contraseña o llave de acceso, factores de autenticación) los
trata AuthCenter. **[PENDIENTE: aviso o sección para AuthCenter; ver sección 6.]**

Para limitar abusos, la plataforma usa en memoria una huella de su cuenta o, antes de iniciar sesión, de su dirección
IP. No se guarda.

> **Nota para revisión.** `identity.users` guarda `identity_subject` y `status`. `email_ciphertext` siempre queda en
> `NULL` (`PostgreSqlInitialOrganizationProvisioner`, migración `20260927000400_AddSelfServiceRegistration`). Sesión:
> `identity.bff_sessions.ticket_ciphertext`, protegido con ASP.NET Data Protection; en el piloto, el anillo de llaves
> está envuelto con una llave de Key Vault (`DataProtection__KeyEncryption__Provider=AzureKeyVault`). El ticket contiene
> `sub`, `sid`, nombre, correo, `email_verified`, refresh token e ID token (`docs/development/auth-001-authcenter-bff.md`
> §3-4). Duración: `AuthCenter:SessionLifetimeMinutes`, 480 por omisión y 1440 como máximo. Invitaciones:
> `organizations.pending_memberships.email_hmac` (REG-PENDING-MEMBERSHIP-STORAGE, REG-PENDING-EXPIRY-7D). Cualquiera
> puede registrarse (AUTH-OPEN-REGISTRATION), pero solo con correo verificado (AUTH-EMAIL-VERIFIED-REQUIRED).

### 3.4 Repartidores

| Dato | Cómo lo obtenemos | Cómo lo protegemos |
| --- | --- | --- |
| Cuenta de usuario | Como en la sección 3.3 | Como en la sección 3.3 |
| Perfil de repartidor: organización, ciudad base, tipo (propio, externo o de aliado), tipo de vehículo y estado | Lo carga el personal de la plataforma | Acceso restringido. El sistema lo muestra como una referencia "DRV-", no con su nombre |
| Su número de celular, si decide registrarlo (solo se puede mientras las llamadas enmascaradas estén habilitadas) | Usted, en la app, con su consentimiento expreso | Cifrado. Nunca se muestra a nadie, tampoco a usted después de registrarlo; puede borrarlo cuando quiera |
| Trabajo realizado: asignaciones, rutas, paradas completadas, cambios de estado que hizo, intentos fallidos que reportó, fotos que tomó, cobros contra entrega que registró | Lo genera su uso de la app | Acceso restringido a su organización y a la organización de cada orden |
| Pagos (liquidaciones): periodo, conceptos y montos | Los calcula finanzas | Acceso restringido |
| Ubicación del dispositivo (latitud, longitud, precisión, rumbo, velocidad, hora) | **Hoy no se recaba.** La app no la envía | Ver nota |
| Documentos (identificación, licencia, tarjeta de circulación, seguro, antecedentes) | **Hoy no se recaban.** No existe forma de cargarlos | Ver nota |

No recabamos fotografías de usted. Las fotos de la app son de paquetes y lugares.

En su teléfono, la app guarda los datos de sus paradas asignadas (nombre del contacto, dirección y referencias) para
trabajar sin conexión. Los borra cuando la parada deja de estarle asignada, cuando cierra sesión o cuando pierde el
acceso. También guarda las fotos y acciones pendientes de enviar hasta que se sincronizan o vencen a las 72 horas.

**[PENDIENTE: ubicación del repartidor (DRV-003). El servidor ya puede recibir y guardar posiciones de repartidores
propios, y despacho vería en su tablero la última posición de cada repartidor con una entrega activa. Si se habilita en
la app, decidir antes: base legal, consentimiento expreso, horario (solo en jornada o con una entrega activa), quién la
ve, plazo de conservación y su relación con las reglas de trabajo en plataformas digitales (Ley Federal del Trabajo,
artículos 291-A a 291-U; GATE-008).]**

**[PENDIENTE: documentos del repartidor. La base de datos tiene una tabla para los metadatos (tipo, vigencia, estado,
huella SHA-256 y ubicación del archivo), pero ninguna función los escribe y el piloto no exige ninguno. Si el owner los
quiere, se decide antes qué documentos, dónde se guardan los archivos, quién los revisa y por cuánto tiempo.]**

**[PENDIENTE: si GATE-008 concluye que los repartidores son personas trabajadoras de plataformas digitales, la Ley
Federal del Trabajo (artículo 291-K, fracción IV) exige garantizar también la portabilidad de sus datos, y el
artículo 291-J pide una política de gestión algorítmica. Hoy la asignación la decide una persona de despacho; el
sistema solo filtra a los repartidores elegibles por estado, zona, vehículo y capacidad.]**

> **Nota para revisión.** Perfil: `drivers.driver_profiles` (AI-06), cargado por el job MDM-001; la base rechaza
> perfiles reales mientras GATE-007 esté abierto (`platform.master_data_deployment_gate`). Sin nombre ni teléfono en
> pantallas (UI-PHASE3-DRIVERS-2026-10-11). Celular: `phone_ciphertext`, `phone_consent_version`,
> `phone_consented_at` (migración Drivers `20261011000100_AddDriverVoiceBridge`); texto de consentimiento
> `VOICE-001-CONSENT-V1` en `apps/web/src/driver/contracts/voice.ts`. Ubicación: `POST /driver/me/location-updates`
> solo para perfiles `OWN` (`docs/development/drv-003-driver-location-ingestion.md`), tabla `drivers.driver_positions`
> y evento `DriverLocationUpdated` al tablero (AI-12). En `apps/web/src` no hay `navigator.geolocation` ni llamadas a
> ese endpoint. Documentos: `drivers.driver_documents` se lee en la elegibilidad (DSP-001) y no se escribe en `src` ni
> en `tools`; `Drivers:Eligibility:RequiredDocumentTypesByVehicleType` está vacío. Teléfono: IndexedDB particionado,
> borrado de instantáneas (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11) y regla de 72 h (OPS-003).

### 3.5 Visitantes de la página de seguimiento

La página de seguimiento muestra solo el número de guía, el estado del envío y la fecha y hora de cada evento. Nunca
muestra nombres, direcciones, teléfonos ni datos del repartidor.

Al abrirla tratamos:

- **El enlace que usted abrió.** Identifica un envío, no a una persona. Funciona mientras el envío está en curso y
  hasta 24 horas después de que se entrega, se devuelve o se cancela.
- **Su dirección IP.** Se usa solo en memoria, convertida en una huella, para limitar abusos. No se guarda.

La página no usa cookies, almacenamiento del navegador, analítica ni recursos de terceros.

**[PENDIENTE: confirmar en Azure que la plataforma de hospedaje no guarde registros de acceso con la ruta /track/ o la
IP del visitante. El código de la aplicación no los registra; la plataforma no se verificó.]**

> **Nota para revisión.** `security.get_public_tracking_projection` (AI-06) devuelve `public_id`, `public_status`,
> `aggregate_version`, `estimated_window` (siempre nulo) y `timeline` (código y fecha). Límite: partición por SHA-256
> de la red del cliente, en memoria (`ClientNetworkPartition`). Sin cookies ni almacenamiento: TRK-001
> (`docs/development/trk-001-public-tracking.md`). Vida del enlace: TRK-002-AUTO-LINK y TRK-002-NO-REVOCATION.

### 3.6 Datos sensibles

No pedimos datos sensibles (artículo 2, fracción VI de la Ley). Aun así, podrían aparecer sin que los pidamos:

- en una fotografía (por ejemplo, el empaque de un medicamento o una persona);
- en la descripción de un paquete que escriba el personal.

**[PENDIENTE: decidir si la ubicación del repartidor, en caso de habilitarse, se trata como dato sensible. Si es así,
necesita consentimiento expreso y por escrito (artículo 8).]**

### 3.7 Datos financieros o patrimoniales

Tratamos montos de envíos y de cobros contra entrega, y los pagos a repartidores. No recabamos datos de tarjetas ni
cuentas bancarias.

**[PENDIENTE: los pagos a un repartidor son datos patrimoniales suyos. La Ley pide consentimiento expreso para ellos
(artículo 7), salvo las excepciones del artículo 9, como una relación jurídica entre el titular y el responsable
(fracción IV). El abogado decide si aplica la excepción o si se pide consentimiento.]**

## 4. Para qué usamos sus datos

### 4.1 Finalidades necesarias para el servicio

**Destinatarios y contactos de recolección o devolución:**

1. Cotizar, crear y confirmar el envío; comprobar que el domicilio esté en la zona de servicio.
2. Asignar el envío a un repartidor y darle los datos para recoger, entregar o devolver el paquete.
3. Dejar evidencia de la recolección y la entrega con fotografías, y atender intentos fallidos, incidentes y
   reclamaciones.
4. Registrar y conciliar el cobro contra entrega.
5. Mostrar el estado del envío en la página de seguimiento.
6. Cuando esté habilitado **[PENDIENTE: VOICE-001]**: conectar una llamada entre el repartidor y el destinatario sin que
   ninguno vea el número del otro.
7. Cuando esté habilitado **[PENDIENTE: GATE-004]**: enviar el enlace de seguimiento y avisos del envío por WhatsApp o
   correo.
8. Cuando esté habilitado **[PENDIENTE: GATE-003]**: ubicar el domicilio en el mapa a partir de la dirección escrita.

**Remitentes y empresas clientes:** las finalidades 1 a 5, además de facturar y cobrar el servicio y conservar la
evidencia de aceptación de términos y aviso de privacidad. **[PENDIENTE: facturación electrónica, GATE-006.]**

**Usuarios de las organizaciones:** crear y administrar su cuenta y su acceso; verificar su identidad y su segundo
factor; aplicar los permisos de su rol; registrar quién hizo cada acción, por seguridad y para aclaraciones.

**Repartidores:** administrar su perfil y su acceso; asignarle trabajo y mostrarle sus paradas; registrar sus
recolecciones, entregas, intentos fallidos y cobros; calcular sus pagos; y, solo si usted registra su número, conectar
sus llamadas con los destinatarios.

**Visitantes de la página de seguimiento:** mostrar el estado del envío y proteger la página contra abusos.

**Todos:** mantener la seguridad de la plataforma, investigar fallas y cumplir obligaciones legales y requerimientos
de autoridades.

### 4.2 Finalidades secundarias

Hoy **no** usamos sus datos para finalidades que no sean necesarias para el servicio: ni mercadotecnia, ni
publicidad, ni prospección comercial, ni encuestas. No vendemos datos personales.

**[PENDIENTE: el owner confirma que no habrá finalidades secundarias en el piloto. Si las hay, se listan aquí con su
mecanismo de negativa: casillas sin marcar o un correo, disponible antes del tratamiento; si el aviso no se da en
persona, un plazo de cinco días hábiles para negarse (Lineamientos del Aviso de Privacidad, Vigésimo quinto).]**

### 4.3 Decisiones automatizadas

No tomamos decisiones sobre usted solo con medios automatizados. El sistema calcula precios con las tarifas vigentes y filtra
qué repartidores pueden recibir una entrega; la asignación la decide una persona de despacho. Cuando un repartidor
reporta un intento fallido, el sistema asigna la gravedad según el motivo, y despacho decide qué sigue.

**[PENDIENTE: revisar contra el artículo 26, fracción II, de la Ley (oposición a tratamientos automatizados) y, si
aplica, el artículo 291-J de la Ley Federal del Trabajo. Las ofertas a repartidores externos (EXT-001) también pasan
por un filtro automático.]**

## 5. Fundamento y consentimiento

- **Usuarios, repartidores, remitentes y empresas clientes.** Tratamos sus datos para cumplir la relación jurídica que
  tenemos con usted, así que no se requiere su consentimiento para las finalidades necesarias (artículo 9,
  fracción IV). **[PENDIENTE: confirmar.]**
- **Destinatarios.** Sus datos nos los da quien solicita el envío. Le damos a conocer este aviso en el primer
  contacto: la página de seguimiento y, cuando se habiliten, los mensajes del envío. Si no se opone, su consentimiento
  es tácito (artículo 7). **[PENDIENTE: el abogado define la base para el destinatario, que no tiene relación jurídica
  directa con la plataforma; también si los términos deben pedir al remitente que garantice que puede darnos esos
  datos.]**
- **Número del repartidor para llamadas.** Consentimiento expreso en la app, con el texto versionado
  `VOICE-001-CONSENT-V1`: "Acepto que este número se use solo para conectar mis llamadas con los destinatarios de mis
  entregas. El destinatario nunca verá mi número y no se usará para nada más. Puedo cambiarlo o borrarlo cuando
  quiera."
- **Datos financieros y ubicación del repartidor:** ver las secciones 3.7 y 3.4.

## 6. Con quién compartimos sus datos

### 6.1 Proveedores que tratan datos por nuestra cuenta (encargados)

Estos proveedores tratan datos solo para darnos su servicio. Según el Reglamento de la Ley (artículo 53), comunicar
datos a un encargado no requiere informarlo ni pedir consentimiento. Los nombramos para que usted sepa quiénes son.

| Proveedor | Para qué | Datos que recibe | Dónde | Estado en el piloto |
| --- | --- | --- | --- | --- |
| Microsoft (Azure) **[PENDIENTE: entidad contratante]** | Hospedaje de la aplicación y la base de datos, almacenamiento de fotografías, análisis de malware de cada foto (Microsoft Defender for Storage), custodia de llaves de cifrado (Key Vault) y registros técnicos (Log Analytics) | Todos los que guarda la plataforma. Los datos de contacto se guardan cifrados con llaves que viven en Key Vault | Región Azure México Central | Activo |
| Microsoft (Azure Application Insights) | Trazas y métricas técnicas para diagnosticar fallas | Ningún dato de contacto. La organización aparece con un seudónimo y la persona solo con su tipo de rol | Región Azure México Central | Construido y apagado; el owner decidió encenderlo después de medir la primera semana del piloto |
| Microsoft (Azure Communication Services, correo) | Enviar correos del envío | Correo del destinatario y contenido del mensaje | **[PENDIENTE]** | Construido y apagado; el correo del destinatario no se recaba |
| Meta Platforms (WhatsApp Business Cloud API) | Enviar mensajes de WhatsApp del envío | Teléfono del destinatario y los datos de la plantilla aprobada, por ejemplo número de guía y enlace de seguimiento **[PENDIENTE: plantillas]** | **[PENDIENTE]**, fuera de México | Construido y apagado; nada lo usa |
| Twilio | Conectar la llamada enmascarada entre repartidor y destinatario | Celular del repartidor y teléfono del destinatario, en cada llamada. Twilio conserva sus propios registros de llamadas. No se graban las llamadas | **[PENDIENTE]**, fuera de México | Construido y apagado |
| Google (Maps Platform, Geocoding API) | Ubicar en el mapa la dirección escrita | Solo el texto de la dirección, sin nombre ni teléfono | **[PENDIENTE]**, fuera de México | Construido y apagado; el piloto usa el punto que marca el personal |
| AuthCenter **[PENDIENTE]** | Inicio de sesión | Cuenta, nombre, correo, contraseña o llave de acceso y factores de autenticación | Azure México Central | Activo |

**[PENDIENTE: AuthCenter es el servicio de identidad propio del owner. Si lo opera la misma persona moral que el
responsable, es parte del responsable y no un encargado. Si lo opera otra entidad, es encargado o responsable de sus
propios datos, y necesita su aviso y un contrato.]**

**[PENDIENTE: contratos o cláusulas de encargo con cada proveedor (Reglamento, artículos 50 y 51) y revisión de las
condiciones de servicios en la nube (artículo 52).]**

**[PENDIENTE: "Cómo llegar". En la app del repartidor, ese botón abre Google Maps en su teléfono con las coordenadas del
destino. Es una acción del repartidor con un servicio de Google bajo las condiciones de Google; la plataforma no envía
nada. Decidir si se menciona.]**

> **Nota para revisión.** Región: `PILOT_REGION: mexicocentral` (`.github/workflows/deploy-azure-pilot.yml`) y
> GATE-012-PILOT-SCOPE. Defender: `deploy/azure/pilot/platform.bicep` (malware al subir; descubrimiento de datos
> sensibles apagado). Application Insights: OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12, OBS-003-TENANT-PSEUDONYM-2026-10-12
> y OBS-003-EXPORT-AFTER-PILOT-WEEK-2026-10-14 (`TelemetryExport__Enabled='false'`). Correo: NTF-EMAIL-ACS-PILOT-2026-10-11;
> ACS se retira el 2028-09-30. WhatsApp y correo: `docs/development/gate-004-messaging-adapters.md` ("Not in this
> change": sin productor ni fuente de destinatarios). Twilio: VOICE-001-PROVIDER-TWILIO-2026-10-11 pide que este aviso
> nombre a Twilio como encargado, la transferencia internacional de números y la retención de sus registros;
> `Voice:Provider` está `Disabled`. Google: GATE-003-MAPS-PILOT-RULES-2026-10-02 (`Locations__GeocodingProvider=Manual`);
> el adaptador envía `address_text` sin referencias (`PostgreSqlLocationService`). AuthCenter: GATE-002, "sin
> transferencia internacional".

### 6.2 Transferencias a terceros

Una transferencia es comunicar datos a alguien distinto de usted, de nosotros y de nuestros encargados (artículo 2,
fracción XX). Hacemos estas:

| A quién | Qué y para qué | ¿Requiere su consentimiento? |
| --- | --- | --- |
| La organización dueña de la orden y, si es otra, el aliado de reparto que la opera, con su personal y sus repartidores | Los datos del envío necesarios para recogerlo, entregarlo, devolverlo y atender incidentes | **[PENDIENTE: depende de la conclusión del apartado 1. Si la plataforma es responsable, podría aplicar el artículo 36, fracción IV (contrato en interés del titular).]** |
| Autoridades competentes | Lo que nos requieran con fundamento | No (artículo 36, fracciones I, V, VI y VII) |

**[PENDIENTE: cláusula del artículo 35 ("acepto / no acepto la transferencia"). Solo hace falta si alguna transferencia
requiere consentimiento.]**

### 6.3 Transferencias y remisiones internacionales

Los datos del piloto se guardan en México (Azure México Central). Cuando se habiliten, Twilio, Meta y Google podrán
tratar en el extranjero los datos que se les envían (sección 6.1). **[PENDIENTE: países y garantías de cada uno.]**

## 7. Sus derechos ARCO

Usted puede **acceder** a sus datos, **rectificarlos** si están mal, pedir su **cancelación** u **oponerse** a su
tratamiento (artículos 22 a 26 de la Ley).

**Cómo pedirlo.** Escriba a **[CORREO ARCO]** **[PENDIENTE: ¿también en persona en [DOMICILIO]?]** con:

1. su nombre y un medio para responderle;
2. un documento que acredite su identidad o, si actúa por otra persona, la de su representante y su representación;
3. qué datos y qué derecho quiere ejercer (para el acceso basta decir que quiere conocerlos);
4. cualquier dato que nos ayude a encontrarlos: número de guía, fecha del envío, organización o referencia "DRV-" si
   es repartidor;
5. para rectificar, el cambio que pide y el documento que lo respalda.

**[PENDIENTE: formato o formulario de solicitud y forma de verificar la identidad sin pedir más datos de los
necesarios.]**

**Plazos.** Le respondemos en un máximo de **20 días hábiles** desde que recibimos la solicitud. Si procede, la
cumplimos dentro de los **15 días hábiles** siguientes. Podemos ampliar cada plazo una sola vez por un periodo igual si
el caso lo justifica (artículo 31).

**Costo.** Es gratuito. Solo cobramos el costo de reproducción o envío si usted pide copias físicas (artículo 34).

**Cómo le entregamos sus datos.** Por correo electrónico, en un documento electrónico. **[PENDIENTE: confirmar.]**

**Cuándo podemos negarnos.** En los casos del artículo 33. Por ejemplo, si no se acredita su identidad, si los datos
no están en nuestro poder o si lo impide una ley. La negativa siempre se explica.

**Qué pasa al cancelar.** Primero bloqueamos los datos: dejan de usarse y solo se conservan para responder por
responsabilidades del tratamiento, durante el plazo de prescripción que corresponda. Después los suprimimos y se lo
avisamos (artículo 24). No cancelamos datos que debemos conservar por ley o que son necesarios para cumplir el
contrato (artículo 25). Algunos registros son inalterables por seguridad: evidencia de entrega, historial de la orden,
aceptación de términos y auditoría. En ellos la cancelación se haría borrando las fotografías y los datos de contacto
cifrados que los acompañan y dejando solo identificadores sin datos de contacto. **[PENDIENTE: es una propuesta; no
está implementada. Ver [reglas de retención](reglas-de-retencion-borrador.md), sección 5.]**

**En la app.** Si es repartidor, puede ver si tiene un celular registrado y borrarlo en cualquier momento desde
"Cuenta".

**Si no está conforme** con la respuesta, o no la recibe, puede iniciar un procedimiento de protección de derechos ante
la **Secretaría Anticorrupción y Buen Gobierno** dentro de los 15 días hábiles siguientes a la respuesta (artículo 40).

**[PENDIENTE: hoy no existen herramientas para buscar, exportar, rectificar ni borrar los datos de una persona. Cada
solicitud requeriría trabajo técnico manual. Ver reglas de retención, sección 6.]**

## 8. Revocación del consentimiento

Puede revocar en cualquier momento el consentimiento que nos dio, por el mismo medio de la sección 7. La revocación no
tiene efectos retroactivos (artículo 7). Si es necesaria para el servicio, ya no podremos prestárselo. El repartidor
retira su consentimiento para llamadas borrando su número en "Cuenta", sin pedirlo a nadie.

## 9. Cómo limitar el uso o la divulgación de sus datos

Como no usamos sus datos para publicidad ni para finalidades secundarias, no tenemos una lista de exclusión. Si quiere
limitar algún uso, escriba a [CORREO ARCO]. **[PENDIENTE: confirmar si se crea un listado de exclusión propio.]**

## 10. Cookies y almacenamiento en su dispositivo

No usamos cookies de publicidad ni de analítica, ni herramientas de terceros que recaben datos automáticamente.

| Qué | Dónde | Para qué | Cuánto dura |
| --- | --- | --- | --- |
| Cookie `__Host-Paquetenvia.Session` | Sistema interno y app del repartidor | Mantener su sesión. Solo contiene una clave aleatoria | Hasta cerrar el navegador. La sesión en el servidor vence a las 8 horas o al cerrar sesión |
| Cookies `__Host-Paquetenvia.Correlation.*` y `__Host-Paquetenvia.Nonce.*` | Al iniciar sesión | Proteger el inicio de sesión | Solo durante el inicio de sesión |
| Almacenamiento local (IndexedDB) y caché de la aplicación | App del repartidor | Trabajar sin conexión (sección 3.4) | Ver sección 3.4 |
| Ninguno | Página de seguimiento | — | — |

**[PENDIENTE: el abogado decide si estas cookies técnicas requieren la advertencia del Lineamiento Trigésimo primero.]**

## 11. Cómo protegemos sus datos

- Nombre, teléfono, dirección y descripciones de incidentes se cifran por separado, con llaves en Azure Key Vault.
- Cada organización solo ve los datos de las órdenes que le pertenecen o que opera: la base de datos lo impone con
  seguridad por fila.
- La base de datos no tiene acceso público. Las fotografías se guardan en almacenamiento privado y pasan un análisis de
  malware.
- Los registros técnicos y las métricas no deben llevar nombres, teléfonos, correos, direcciones, coordenadas ni
  tokens.
- La página pública de seguimiento solo muestra el estado del envío.

Si ocurre una vulneración de seguridad que afecte de forma significativa sus derechos patrimoniales o morales, se lo
informaremos de inmediato (artículo 19). **[PENDIENTE: procedimiento de respuesta a incidentes.]**

## 12. Cuánto tiempo conservamos sus datos

Conservamos sus datos solo mientras sean necesarios para las finalidades de este aviso. Después los bloqueamos durante
el plazo de prescripción que corresponda y los suprimimos (artículos 10 y 24). Los plazos están en las
[reglas de retención](reglas-de-retencion-borrador.md). **[PENDIENTE: resumir aquí los plazos aprobados.]**

## 13. Cambios a este aviso

Publicaremos cada versión en [URL DEL AVISO], con su número de versión y su fecha. Si cambian las finalidades, el
responsable o las transferencias, se lo informaremos:

- a usuarios y repartidores, al iniciar sesión;
- a los destinatarios, en la página de seguimiento.

**[PENDIENTE: mecanismo concreto. Hoy el sistema no muestra avisos ni versiones al iniciar sesión, y la página de
seguimiento no tiene enlace al aviso. Ambos requieren desarrollo. Para datos obtenidos indirectamente, el artículo 17
exige dar a conocer el cambio al titular.]**

## 14. Autoridad

La autoridad en protección de datos personales en posesión de particulares es la **Secretaría Anticorrupción y Buen
Gobierno** (artículo 2, fracción XV de la Ley).

## Fuentes

Consultadas el 2026-10-19:

1. Ley Federal de Protección de Datos Personales en Posesión de los Particulares. Nueva ley publicada en el DOF el
   20 de marzo de 2025; texto vigente con última reforma DOF 14-11-2025. Cámara de Diputados:
   <https://www.diputados.gob.mx/LeyesBiblio/pdf/LFPDPPP.pdf>. Artículos 1 a 49 y transitorios. El transitorio
   Segundo abroga la ley del 5 de julio de 2010.
2. La misma ley en el Orden Jurídico Nacional: <https://www.ordenjuridico.gob.mx/Documentos/Federal/html/wo125102.html>.
3. Reglamento de la LFPDPPP (DOF 21-12-2011), que la Cámara de Diputados lista como vigente:
   <https://www.diputados.gob.mx/LeyesBiblio/regley/Reg_LFPDPPP.pdf> y
   <https://www.diputados.gob.mx/LeyesBiblio/regla.htm>. Artículos 14, 24 a 27, 32, 49 a 53.
4. Lineamientos del Aviso de Privacidad (DOF 17-01-2013):
   <https://dof.gob.mx/nota_detalle.php?codigo=5284966&fecha=17/01/2013>. Lineamientos Vigésimo a Cuadragésimo
   primero.
5. Ley Federal del Trabajo, texto vigente con última reforma DOF 14-05-2026:
   <https://www.diputados.gob.mx/LeyesBiblio/pdf/LFT.pdf>. Artículos 291-A a 291-U (trabajo en plataformas
   digitales, adicionados DOF 24-12-2024), 516 y 804.

**[PENDIENTE: el Reglamento de 2011 y los Lineamientos de 2013 se expidieron bajo la ley abrogada. Los transitorios de
la ley de 2025 no dicen si siguen aplicando. La Cámara de Diputados lista el Reglamento como vigente. El abogado
confirma su aplicación.]**
