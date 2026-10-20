# Changelog

## Repartidor: fotos de evidencia reducidas y sin metadatos (DRV-PROOF-PHOTO-REDUCE-2026-10-20) — 2026-10-20

- Respuesta literal del project owner (2026-10-20, pregunta G2), registrada en `decision-log.md` con su contexto y las
  opciones no elegidas: "Fotos de entrega: hoy se suben tal como las toma el teléfono (2 a 5 MB, con metadatos que pueden incluir GPS). Sin señal caben solo de 5 a 12 fotos en la cola. ¿Las procesamos en el teléfono antes de guardarlas?" Respuesta: "Reducir y quitar metadatos (Recommended)" ("JPEG de máximo 1600 px por lado y calidad 80 % (unos 300-500 KB), sin metadatos. La evidencia guardada es esa foto reducida (su huella se calcula después). Caben unas 50 fotos sin señal. Se ve menos detalle al hacer zoom.").
- La app del repartidor reduce cada foto de evidencia en el teléfono antes de calcular su huella y guardarla en la
  cola: la de recolección, la de entrega y la "Foto del lugar" de "No se pudo entregar". Decodifica la foto con su
  orientación visual (`createImageBitmap` con `imageOrientation: "from-image"`, o un `<img>` donde falta esa opción) y
  la vuelve a codificar en un canvas como JPEG de calidad 0.8 con el lado mayor de 1600 px como máximo, sin agrandarla
  y sin cambiar su proporción. Un PNG también se guarda como JPEG; lo transparente queda blanco.
- Sin metadatos: la foto guardada solo conserva el encabezado JFIF y los datos de la imagen. Se quitan EXIF (con GPS,
  modelo del teléfono y hora), XMP, perfiles ICC, comentarios y todo lo que vaya después del fin de la imagen, también
  lo que escribe el propio codificador del navegador (Chromium agrega un perfil ICC).
- La evidencia guardada es esa foto reducida: la vista previa la muestra, y esos mismos bytes son los que se hashean
  (SHA-256), se guardan en IndexedDB, se declaran (`image/jpeg`), se suben y valida el servidor (bytes mágicos y
  SHA-256), sin cambios en el servidor. El archivo que eligió el repartidor no se guarda, no se muestra ni se envía.
- Si el teléfono no puede procesar la foto (formato, archivo dañado o memoria), el paso dice «No se pudo procesar la
  foto en el teléfono. Tómala de nuevo.», no guarda nada y nunca usa el archivo original en su lugar. Mientras la
  reduce dice «Preparando la foto…».
- Límites sin cambio: 10 MiB por foto, 25 MiB de fotos y 100 operaciones por partición. Tamaño típico: unos
  300-500 KB por foto de una cámara de 12 MP (estimación del owner), unas 50 fotos sin señal. Medido en Chromium con
  imágenes sintéticas: una de 4000x3000 y 3.78 MB queda en 1600x1200 y 258 KB; 12 MP de ruido puro (peor caso), de
  10.5 MB a 661 KB.
- Una foto que una versión anterior de la app ya había guardado en la cola se envía como se guardó.
- AI-07 `driver_stop.proof_photo` (nuevo) y `failed_delivery.photo`; AI-08 DRV-002: criterio DECIDED y prueba
  requerida. Pruebas: vitest con una foto sintética con EXIF y GPS inventados (coordenadas imposibles) y Playwright en
  Chromium real (lo guardado, hasheado, declarado y subido es un solo JPEG de 800x1600, derecho y sin metadatos; una
  foto que no se puede decodificar no deja nada). Notas: `docs/development/drv-002-offline-events-pod.md`; borradores
  legales actualizados. Sin cambios de API, SQL, migraciones, roles, esquema de IndexedDB, Service Worker ni paquetes.

## Rutas y asignaciones como flujo atómico 6, con catálogo de escrituras entre módulos (RTE-001-ROUTING-ASSIGNMENT-FLOW6-2026-10-20) — 2026-10-20

- Respuesta literal del project owner (2026-10-20, pregunta G1), registrada en `decision-log.md` con su contexto y la
  opción no elegida: "Rutas: al agregar o quitar una parada, el módulo de Rutas escribe directo en las asignaciones de Despacho, en la misma transacción. Lo exige RTE-001, pero no está entre los 5 flujos permitidos (AI-13 §4) y no hay prueba que detecte escrituras nuevas entre módulos. ¿Qué hacemos?" Respuesta: "Registrarlo como flujo 6 (Recommended)" ("Fila de decisión y ADR que lo agregan a la lista, más la prueba que cataloga cada escritura entre módulos y falla con cualquiera nueva. Sin cambio de comportamiento.").
- AI-13 §4 agrega el flujo 6, route stop ↔ assignment.route_id (RTE-001): lo coordina Routes y, en una sola
  transacción, `addRouteStop` fija y `removeRouteStop` limpia `dispatch.assignments.route_id` junto con la parada, la
  versión de la ruta, la auditoría, el outbox `routes.route-changed` y la idempotencia. Los otros cinco flujos no
  cambian. ADR: `docs/adr/ADR-RTE-001-ROUTE-ASSIGNMENT-FLOW6.md`.
- La cuenta sube a seis en AI-13 §15, AI-03 §25.9 y `CLAUDE.md`; AI-04 `transaction_coordinator_allowlist` agrega
  `route_stop_to_assignment_route_id`; AI-01 §4.18 y AI-11 prohíben ahora un séptimo flujo sin ADR y pruebas. AI-08
  RTE-001: un criterio DECIDED y la prueba exigida.
- Nueva prueba de arquitectura `CrossSchemaWriteCatalogTests`: cataloga cada escritura SQL de runtime en el esquema de
  otro módulo (hoy ocho: flujos 1, 2, 5 y 6, y ADR-031 para el provisioning) y falla ante una nueva, una retirada o una
  sin flujo, con un mensaje que pide el ADR. Nota: `docs/development/rte-001-routing-assignment-flow6.md`.
- Sin cambios de código de `src/`, SQL, migraciones, roles, permisos, RLS, outbox ni paquetes; el comportamiento no
  cambia.

## Avisos al destinatario: contratos (NTF-RECIPIENT-NOTICES-2026-10-20) — 2026-10-20

- Respuestas literales del project owner (2026-10-19 y 2026-10-20), registradas en `decision-log.md` con su
  contexto, las preguntas y las opciones no elegidas: E4 "Los 4 del plan (Recommended)" ("Enlace de seguimiento al crear la orden, "Va en camino", "No se pudo entregar" y "Entregado", por WhatsApp. Por correo solo si hay correo del destinatario: hoy no se captura, se agrega como campo opcional en la nueva orden."); E4-P1 "Casilla en la orden (Recommended)" ("En la nueva orden: "El destinatario aceptó recibir avisos de su envío por WhatsApp". Si no se marca, no se manda WhatsApp (sí correo si hay). Queda registro de quién la marcó y cuándo."); E4-P2 "Llave propia para avisos (Recommended)" ("Al cotizar se guarda además una copia cifrada del teléfono y del correo con una llave que solo sirve para avisos. El Worker solo abre esa llave, nunca direcciones, nombres ni descripciones. Una llave más en Key Vault (centavos al mes)."); E4-P3 "Al iniciar traslado (Recommended)" ("Avisa con tiempo. En rutas con varias paradas puede llegar horas antes; el texto no promete hora."); E4-P4 "Al quedar confirmada (Recommended)" ("Nunca se manda el enlace de una orden que no llegó a confirmarse."); E4-P5 "Sin motivo, con enlace (Recommended)" (""Intentamos entregar y no fue posible; en el seguimiento verás el siguiente paso". No expone motivos delicados."); E4-P6 "De "tú", sin nombres (Recommended)" ("Como el ejemplo: sin nombre de la empresa que envía ni de nadie. Los 4 textos completos quedan en el PR para que los revises antes de mandarlos a Meta."); E4-P7 "La razón social (Recommended)" ("Es el responsable legal del aviso de privacidad y no es la marca comercial."); E4-P8 "Las tres vías (Recommended)" ("Responder "BAJA" o tocar "No recibir avisos" en WhatsApp, un enlace en cada correo, y un botón "Detener avisos" en la orden para cuando lo pida por teléfono. Aplica a todas las empresas que le envíen, porque el número de WhatsApp es uno solo."); E4-P9 "Respuesta automática (Recommended)" (""Este número solo envía avisos. Para dudas sobre tu envío contacta a quien te lo envió." Se cobra como mensaje de servicio."); E4-P10 "8:00 a 21:00 (Recommended)" ("Lo que pase fuera de ese horario se manda a las 8:00, salvo que ya no aplique (por ejemplo, "Va en camino" si ya se entregó)."); E4-P11 "Por los dos siempre (Recommended)" ("Más simple y no depende de los avisos de entrega de Meta; el correo cuesta casi nada."); E4-P12 "avisos@paquetenvia.com (Recommended)" ("Nombre visible "Seguimiento de envío", sin buzón de respuestas. Coherente con la marca neutra; el dominio ya es el del enlace."); E4-P13 "Fuera de este trabajo (Recommended)" ("El despachador ya ve los estados en la plataforma; se decide después, con el portal de negocios (BUS-001).").
- Ronda F (2026-10-20, sobre los borradores de este cambio; también en la fila con su contexto, preguntas y opciones no elegidas): E4-P14 "Corregir los dos (Recommended)" ("WhatsApp: «Intentamos entregar tu envío con número de guía {{1}} y no fue posible. Con el botón Ver seguimiento verás el siguiente paso.» (como tu respuesta E4-P5). Correo: «con el botón Ver seguimiento» pasa a «en el enlace de abajo» en los 4 avisos."); E4-P15 "Sí, una línea (Recommended)" ("«Listo. Ya no recibirás avisos en este número.» Una sola vez, por la misma vía que la respuesta automática (mensaje de servicio). En el correo la página de baja ya lo confirma en pantalla."); E4-P16 "No en el piloto (Recommended)" ("La baja es definitiva durante el piloto. Si alguien la pidió por error, no hay forma de reactivarla; se revisa al terminar el piloto. Sin trabajo extra."). En AI-24: "No se pudo entregar" ya no empieza con "Hoy"; los cuatro cuerpos de correo quedan escritos con "en el enlace de abajo"; una baja por WhatsApp recibe una sola confirmación ("Listo. Ya no recibirás avisos en este número.") por la vía de la respuesta automática y fuera de su límite de 24 h; la baja es definitiva durante el piloto (riesgo residual: una baja por error no se deshace). Lo reflejan AI-05, AI-03, AI-06 (comentario del almacén de respuestas automáticas) y AI-08 NTF-006.
- E4-P17 (2026-10-20, tras el hallazgo MAJOR de la revisión automática sobre las dos copias del correo; también en la fila): "Dos copias, como el teléfono (Recommended)" ("La API conserva el correo con la llave de datos personales, igual que el teléfono, y el Worker solo abre la copia de avisos. Ninguna se muestra, registra ni publica, y al borrar la ubicación se borran las dos. Queda como hoy en el PR."). El contrato no cambia: el correo se guarda con la llave PII, como el teléfono, y con una copia bajo `recipient-notice-kek` para el Worker. AI-24 usa ahora el mismo nombre de parámetro que AI-05 en la baja por correo (`{unsubscribeToken}`).
- Solo contratos (PR-0): nada se construye, configura ni enciende; GATE-004 y GATE-007 siguen abiertas. ADR
  `docs/adr/ADR-NTF-002-RECIPIENT-NOTICES.md` y nota `docs/development/ntf-recipient-notices.md` (arquitectura,
  secuencia PR-1 a PR-8, acciones del owner y reversión).
- AI-24 gana la sección `recipient_notices`: cuatro avisos al destinatario en cinco transiciones de ORD-002 (enlace al
  confirmar, "Va en camino" al iniciar traslado, "No se pudo entregar" sin motivo y nunca por una recolección fallida,
  "Entregado"), revisados otra vez al enviarse; WhatsApp solo con la casilla de permiso y correo solo si hay correo,
  los dos cuando aplican; de 8:00 a 21:00 hora de Mazatlán; llave propia de avisos que el Worker solo desenvuelve;
  textos neutros para revisión del owner antes de Meta; bajas globales por tres vías guardadas como hashes con llave;
  respuesta automática; acuses firmados; interruptores apagados. `tracking_token.derivation.key` también la lee el
  Worker.
- AI-05: entrada `DECIDED` `NTF-RECIPIENT-NOTICES` de `x-pilot-contract-deltas` (correo opcional solo en el destino,
  permiso de WhatsApp en `createOrder` y en la carga CSV, webhooks de Meta y de Event Grid, baja por correo, lista y
  "Detener avisos" del despachador y su capacidad). La superficie HTTP no cambia hasta que cada ítem publique su parte
  con código y pruebas; los conteos de rutas, esquemas y referencias no cambian.
- AI-12: `decided_amendment` de `NotificationStatusChanged` (payload v2 con `order_id` y `notice`, canales WhatsApp y
  correo, estados `DELIVERED` y `CANCELLED`). AI-06 y AI-18: contrato declarado en comentarios (columnas de Locations,
  cambios del carril Notifications, almacén de bajas, respuestas automáticas, tabla de permisos, funciones y el rol
  `paqueteria_notification_receipt_executor`); su DDL entra con cada paso de carril, así que solo se vuelven a fijar
  sus hashes. AI-04: correo como dato sensible e invariantes de privacidad y outbox, sin estados nuevos. AI-07: campo
  de correo, casillas de permiso, panel "Avisos al destinatario" y página `/avisos/baja/:token`. AI-08: ítems NTF-002
  a NTF-008, criterio en TRK-002 y REL-001 depende de NTF-005. AI-09, AI-03 §15/§16 y AI-13 §7.2.
- Sin cambios de comportamiento, migraciones, roles, permisos, Bicep ni paquetes: solo se vuelven a fijar los hashes de
  AI-05, AI-06 y AI-18 (constantes de la línea base, manifiesto del migrador, flujos de CI, documentación y pruebas) y el
  conteo de ítems de AI-08 en las pruebas.

## Alertas de CPU, memoria, disco y créditos de CPU de PostgreSQL en el piloto (SCL-007-POSTGRES-RESOURCE-ALERTS-2026-10-20) — 2026-10-20

- Respuestas literales del project owner (2026-10-20, preguntas D2, D2-a, D2-b, D2-c y D2-d), registradas en
  `decision-log.md` con su contexto y las opciones no elegidas:
  - "La base de datos del piloto solo tiene las 2 alertas de conexiones. ¿Agregamos alertas para ver un problema antes de que lo reporten los usuarios?" Respuesta: "CPU, memoria y disco (Recommended)" ("3 alertas de métrica más al mismo correo de alertas. Con las 2 de conexiones son 5 de las 10 gratis por recurso; si no, unos 0.10 USD por serie al mes.");
  - "Alertas de la base (D2): elegiste 3 alertas (CPU, memoria y disco). Las de conexiones tienen dos niveles (aviso y crítico). ¿Las nuevas tienen uno o dos niveles?" Respuesta: "Un nivel, de aviso (Recommended)" ("3 reglas, tal como respondiste. Con las 2 de conexiones son 5 de las 10 series gratis.");
  - "¿Con qué umbrales? Los contratos solo fijan CPU (70 % aviso, 85 % crítico, en 30 min). El disco de 32 GB crece solo al 80 % (y duplica su costo) y la base se pone en solo lectura al quedar menos de 5 GB libres (84 %)." Respuesta: "CPU 70, memoria 80, disco 70 (Recommended)" ("CPU: promedio de 30 min ≥ 70 % (contrato). Memoria: máximo en 5 min ≥ 80 %, como conexiones. Disco ≥ 70 % (22 GB): avisa antes de que crezca y antes del solo lectura. Si eliges dos niveles: CPU 70/85, memoria 80/95, disco 70/80.");
  - "La base del piloto es de tipo económico (Burstable). Cuando se le acaban los créditos de CPU se frena mucho, y Microsoft recomienda una alerta de «créditos de CPU restantes». ¿La agregamos como cuarta alerta?" Respuesta: "Sí, agregarla (Recommended)" ("Una regla más (avisa con pocos créditos). Sigue dentro de las 10 series gratis. Es la falla más probable de este tipo de servidor, y la alerta de CPU sola podría no mostrarla.");
  - "Alerta de créditos de CPU: el servidor del piloto (B1ms) guarda hasta 288 créditos y gana 12 por hora. A CPU llena gasta cerca de 0.8 por minuto; con 0 se frena al 20 %. ¿Con cuántos créditos avisa?" Respuesta: "Menos de 72 (25 %) (Recommended)" ("Avisa con 72 créditos o menos (mínimo en 5 min, cada minuto). A CPU llena da cerca de hora y media antes del freno. Las cifras de 288 y 12 por hora son las de las máquinas B1ms; la página de PostgreSQL no las repite, así que se revisan en el primer despliegue.").
- `deploy/azure/pilot/observability.bicep` (etapa 5 del flujo del piloto, sin cambios en el flujo) agrega cuatro alertas
  de métrica de Azure Monitor sobre el servidor `pg-pv-pilot-<suffix>`, de un solo nivel (aviso, severidad 2), evaluadas
  cada minuto, con estado (un correo al dispararse y otro al resolverse) y al grupo de acciones `ag-pv-pilot-ops`:
  `mar-pv-pilot-pg-cpu-warning` (`cpu_percent`, promedio de 30 minutos ≥ 70, el `db_cpu_utilization` de AI-15),
  `mar-pv-pilot-pg-memory-warning` (`memory_percent`, máximo en 5 minutos ≥ 80), `mar-pv-pilot-pg-storage-warning`
  (`storage_percent`, máximo en 5 minutos ≥ 70, 22.4 de 32 GiB) y `mar-pv-pilot-pg-cpu-credits-warning`
  (`cpu_credits_remaining`, mínimo en 5 minutos ≤ 72, el 25 % de los 288 créditos del B1ms). Las dos alertas de
  conexiones no cambian. Métricas, unidades, agregaciones y dimensiones verificadas en learn.microsoft.com (*Supported
  metrics for Microsoft.DBforPostgreSQL/flexibleServers*). El disco no tenía ventana en la respuesta: usa el máximo en
  5 minutos, como las de conexiones. Los créditos siguen el texto de la opción ("72 créditos o menos"), no la etiqueta
  "Menos de 72".
- Costo: con las 2 de conexiones son 6 series, dentro de las 10 gratis por recurso; si no, unos 0.10 USD por serie al
  mes. La opción de D2 contaba "5 de las 10" para tres reglas; la cuarta (D2-c) sigue gratis. Los totales del piloto no
  cambian.
- Guardas del piloto: P21 exige exactamente las seis alertas de métrica (métrica, agregación, operador, umbral entero,
  ventana, frecuencia, severidad, servidor, grupo de acciones, activas y con estado, sin dimensión ni correo), rechaza
  cualquier otra (un segundo nivel incluido), exige el SKU Standard_B1ms y el disco de 32 GiB en la plantilla y suma
  las seis series al tope de 5 USD de las alertas. Las pruebas prueban que cada desviación de cada regla falla cerrada.
- AI-08 SCL-007: un criterio DECIDED y la prueba de la guarda. AI-15 no lista reglas de alerta y no cambia. Sin cambios
  de código, esquema, roles, permisos, paquetes ni topología de CI. Revisar en el primer despliegue: las cifras de
  créditos del B1ms (máximo 288, 12 por hora) en el servidor real, y que los porcentajes del disco suponen 32 GiB
  (después de un crecimiento automático son de un disco mayor). Notas: `docs/operations/env-001-pilot/README.md` §10.3 y
  la guía del owner §6.2.

## Organización de plataforma y primer administrador (TEN-003-PLATFORM-BOOTSTRAP-2026-10-19) — 2026-10-19

- Respuesta literal del project owner (2026-10-19, decisión E1), registrada en `decision-log.md` con su contexto y la
  opción no elegida: "Comando único auditado (Recommended)" ("Un comando del migrador que corres una sola vez, antes de tu primer inicio de sesión: crea la organización de plataforma y te hace administrador con tu cuenta de AuthCenter, deja registro en auditoría y se niega a correr una segunda vez."). No elegida: "SQL manual con runbook" ("Un procedimiento paso a paso que ejecutas tú como administrador de PostgreSQL. Sin código nuevo, pero sin auditoría automática y con más riesgo de error.").
- Seguimiento E1b del project owner (2026-10-20, en el PR #283), registrado en `decision-log.md` con su contexto y
  la opción no elegida: "Restaurar y repetir (Recommended)" ("Se mantiene la negativa, sin código nuevo. Si pasa, se restaura la base a un punto antes de tu inicio de sesión (la base del piloto aún está vacía) y corres el comando. La guía lo explica paso a paso. Coincide con E1: 'antes de tu primer inicio de sesión'."). No elegida: "Tomar ese usuario si está solo" ("El comando acepta tu usuario ya creado solo si es el único de la base y no tiene ninguna membresía. Evita la restauración, pero agrega código y pruebas al PR y una ruta más que revisar.").
  Sin código nuevo: el comando sigue negándose (`SUBJECT_ALREADY_SIGNED_IN`, código 11, sin escribir nada). La salida
  es restaurar la base del piloto a un punto anterior a ese primer inicio de sesión (README de ENV-001 §6.4) y correr
  el comando otra vez. Solo sirve mientras nadie haya usado el piloto, y pierde lo escrito después de ese punto, que
  entonces es solo el usuario del owner y su sesión. La guía del owner §7.5 lo explica paso a paso.
- Nuevo comando del migrador `platform-bootstrap --connection-env NAME --identity-subject <sub de AuthCenter>
  --legal-name TEXT --display-name TEXT`. El owner lo corre una sola vez, antes de su primer inicio de sesión, en una
  ejecución del job de migración que solo cambia los argumentos (README de ENV-001 §6.1 paso 10; guía del owner
  §7.5). Primero verifica la base y todas las lanes de módulo.
- En una transacción, con el aprovisionador de TEN-003 y un autorizador ligado a ese único `sub`, crea:
  - la organización PLATFORM (ACTIVE);
  - el usuario de ese `sub` (el id de usuario de AuthCenter, un UUID en minúsculas);
  - su membresía PLATFORM_ADMIN (ACTIVE, predeterminada);
  - una fila de auditoría `INITIAL_ORGANIZATION_PROVISIONED`, escrita con el escritor append-only compartido.
- La transacción toma una llave de advisory lock y revisa, como el login de despliegue, si ya hay datos. Con
  `set_config(..., true)` después de `BEGIN` preautoriza solo los UUID nuevos y escribe como `paqueteria_migrator`
  (NOBYPASSRLS) bajo FORCE RLS, igual que `master-data-gate`. No usa `paqueteria_app`: el login de despliegue de
  Azure no puede asumirlo, y ese rol no ve otros tenants para negarse a una segunda corrida.
- Se niega sin escribir nada (código 11) si ya existe cualquier organización PLATFORM, cualquier PLATFORM_ADMIN o un
  usuario con ese `sub`, y también con un login que no salta RLS. Su salida no muestra el `sub`, los nombres ni
  ningún id, y un error de argumentos no repite el valor.
- El aprovisionador de TEN-003 ahora recibe la interfaz existente `ITenantTransactionRunner<OrganizationsDbContext>`.
  La API lo sigue armando con `TenantTransactionContext` (`paqueteria_app`) y el autorizador que niega.
  `Paqueteria.Infrastructure` expone sus internos al migrador.
- Sin cambios en AI-04, AI-05, AI-06 ni AI-18, ni migraciones, roles, permisos, funciones, paquetes o flujos entre
  módulos. AI-08 TEN-003 registra el comando, su criterio y su prueba. Pruebas: validación de argumentos y contrato en
  PostgreSQL real. Nota: `docs/development/ten-003-platform-bootstrap.md`.

## Remediación de seguridad NuGet: Microsoft.OpenApi y Microsoft.OpenApi.YamlReader a 2.12.0 (SEC-2026-13-NUGET-REMEDIATION-2026-10-19) — 2026-10-19

- Respuesta literal del project owner (2026-10-19), registrada en `decision-log.md` con su contexto y las opciones no
  elegidas ("Subir a 2.12.2" y "Registrar y esperar"): "Remediar con la tabla (Recommended)".
- Un aviso del 2026-10-14, corregido en 2.12.0: `GHSA-7pxr-59rr-hqj2` (CVE-2026-72923, alta), denegación de servicio
  por expansión de alias YAML en `Microsoft.OpenApi.YamlReader` `>=2.0.0-preview.11 <2.12.0`. Sólo ContractTests usa
  ese paquete. El tracking es el Issue #280 y la ruta es la de SEC-2026-12 (PR de remediación a `main`, back-sync a
  `development` y retiro).
- `Directory.Packages.props`: `Microsoft.OpenApi` y `Microsoft.OpenApi.YamlReader` pasan de 2.7.5 a 2.12.0, en su
  lugar. No se agrega ningún `PackageVersion` y el fijado transitivo sigue como lo dejó SEC-2026-12.
- Los locks se regeneran con `dotnet restore --force-evaluate` y se mueve exactamente la tabla del owner:
  - `Microsoft.OpenApi` en los locks de la API y de ContractTests (`Direct`) y en los de ArchitectureTests e
    IntegrationTests (`CentralTransitive`), sin cambiar de tipo; en esos dos últimos, el nodo `Project`
    `paqueteria.api` pide `[2.12.0, )` en vez de `[2.7.5, )`;
  - `Microsoft.OpenApi.YamlReader` sólo en el lock de ContractTests, con su arista a `Microsoft.OpenApi`.

  `SharpYaml` sigue en 2.1.4, byte a byte. Ningún otro lock cambia, tampoco el del Worker. El documento OpenAPI que
  genera la API es idéntico byte a byte y las pruebas de la línea base de AI-05 pasan sin cambios.
- REL-000 agrega:
  - la autorización `SEC-2026-13-OPENAPI-YAMLREADER`;
  - el validador de grafo exacto de SEC-2026-12, ahora compartido (`validate_nuget_security_remediation_diff`):
    ediciones del manifiesto en su lugar, los 2 movimientos con versiones y hashes exactos, y el SHA-256 de cada lock;
  - dos extensiones exactas de ese validador, simétricas al deshacer: un nodo que ya es `CentralTransitive` con
    `requested` igual a `[from, )` se mueve a `[to, )` y conserva su tipo, y la petición de un nodo `Project` por un
    paquete movido sigue al movimiento sólo si es exactamente `[from, )`.
- El grafo permanente MVP-0 (`test_k`) también acepta exactamente esos movimientos (`nuget_remediations`). La evidencia
  de liberación no lo usa, así que el modo normal y el código de admisión de dependencias no cambian. Nota:
  `docs/development/rel-000-internal-release.md`.
- Sin cambios en código de producción, migraciones, AI-04, AI-05, AI-06, AI-18, roles ni permisos.
- Fusionada en `main` por #282 (b949dd8), sincronizada a `development` por #286 (be6e84c) y retirada a
  `historical_remediations` por este PR.

## Repartidor: sesión vencida y recarga sin señal (DRV-SESSION-EXPIRY-OFFLINE-2026-10-19) — 2026-10-19

- Respuestas literales del project owner (2026-10-19), registradas en `decision-log.md` con su contexto y las opciones
  no elegidas: E2 "Conservar y pedir entrar (Recommended)" ("La app guarda lo pendiente, avisa \"Tu sesión venció, entra de nuevo\" con un botón para iniciar sesión, y al volver a entrar el mismo repartidor en la misma empresa se envía todo. A otra persona nunca se le muestra esa cola.") y E3 "Sí, y envía al validar (Recommended)" ("El teléfono recuerda qué repartidor y empresa abrieron la sesión (sin contraseña ni token), muestra las paradas guardadas y guarda las acciones; se envían cuando vuelve la señal y el servidor confirma la sesión.").
- Un 401 (la sesión BFF terminó: venció a las 8 horas, AuthCenter la cerró u otro inicio de sesión la reemplazó) ya no
  borra nada de la partición: la instantánea, los roles y el teléfono de despacho guardados y cada acción en cola, con
  su foto y su avance, se quedan; la cola deja de enviar con esa sesión y la pantalla dice «Tu sesión venció, entra de
  nuevo» con «Iniciar sesión». Al volver a entrar el mismo repartidor en la misma empresa (mismo `sessionNamespace`,
  misma organización, misma partición) la cola se envía sola con las mismas llaves; otra persona, u otra empresa,
  abre su propia partición y nunca ve, envía ni borra la anterior.
- Un 403 y una sesión sin credencial usable siguen borrando la partición, como antes: el código muestra que el
  vencimiento de la sesión nunca llega como 403 ni como token CSRF faltante. La regla de 72 horas
  (OPS-003-OFFLINE-72H) sigue contando desde la hora de cada acción mientras espera el nuevo inicio de sesión.
- El teléfono recuerda en localStorage (`paquetenvia:driver-session-marker`) qué repartidor y empresa abrieron la
  última sesión confirmada: sólo el `sessionNamespace` y el id de la organización, lo que forma la llave de la
  partición; nunca token, cookie, CSRF, contraseña, nombre, correo ni teléfono. Sin señal (o sin respuesta del
  servidor) las páginas del repartidor abren esa partición en modo local («Sesión sin confirmar»): muestran las paradas
  y la cola guardadas y guardan acciones nuevas sin pedir nada a la API. Al volver la señal (`online`), al mostrarse la
  app, con «Sincronizar ahora» o «Reintentar» y cada 30 s se vuelve a consultar `GET /auth/session`; sólo la misma
  persona en la misma organización envía la cola.
- AI-07: `/driver/stops` y `/driver/stops/:id` citan la decisión; `driver_stop` cambia `offline_cache`,
  `details.retention`, `failed_delivery.role_unknown` y `dispatch_contact` (un 401 conserva, un 403 borra) y agrega
  `session_expiry` y `offline_reload`. Sin cambios de API, SQL, migraciones, roles, esquema de IndexedDB, Service
  Worker ni paquetes. Nota: `docs/development/drv-002-offline-events-pod.md`.

## Fotos de entrega: 30 días de borrado suave (ADP-001-PHOTO-SOFT-DELETE-30D-2026-10-20) — 2026-10-20

- Respuesta literal del project owner (2026-10-20, pregunta D3), registrada en `decision-log.md` con su contexto y las
  opciones no elegidas: "Fotos de entrega (evidencia legal): hoy el almacenamiento guarda lo borrado solo 7 días, sin versiones, y restaurar la base de datos no recupera fotos. ¿Cómo las protegemos?" Respuesta: "Borrado suave más largo (Recommended)" ("Subir a 30 días la recuperación de fotos borradas o sobrescritas. Cambio de una línea en el despliegue y costo casi nulo; no protege si se borra la cuenta de almacenamiento completa.").
- `deploy/azure/pilot/platform.bicep`: el borrado suave de blobs del almacenamiento de fotos pasa de 7 a 30 días y el
  versionado sigue apagado (requisito de ADP-001). Sin versionado, sobrescribir un blob deja una instantánea con borrado
  suave del estado anterior, así que los 30 días cubren fotos borradas o sobrescritas (learn.microsoft.com, *Soft delete
  for blobs*). El borrado suave del contenedor también pasa de 7 a 30 días: el de blobs no protege si se borra el
  contenedor `proofs`, y la opción solo dejaba fuera el borrado de la cuenta completa.
- Guarda P12: un solo servicio de blobs, versionado apagado y las dos políticas activas por exactamente 30 días, sin
  borrado permanente; las pruebas prueban que cada desviación falla cerrada.
- Costo: lo borrado se cobra como dato guardado (Hot LRS 0.02288 USD por GB al mes en mexicocentral, 2026-10-20). Con
  ~0.2 GB de fotos al mes y la copia de cuarentena que borra el Worker, unos 0.005 USD al mes.
- AI-08 ADP-001: criterio y prueba de la guarda. Guía de restauración en `docs/operations/env-001-pilot/README.md` §6.4;
  `docs/development/adp-001-production-adapters.md` y el borrador de reglas de retención actualizados. Sin cambios de
  código, SQL, roles, permisos, flujo de despliegue ni paquetes. No protege si se borra la cuenta de almacenamiento o el
  grupo de recursos.

## Repartidor: lecturas viejas de la parada (DRV-STALE-STOP-READ-2026-10-14) — 2026-10-14

- Respuestas literales del project owner (2026-10-14), registradas en `decision-log.md` con su contexto y las opciones
  no elegidas: Q6 "Sí, en un PR aparte (Recommended)" ("La app descarta una lectura de la parada más vieja que la última aceptada (por versión). No cambia lo que ve el repartidor salvo evitar ese parpadeo. Con prueba de regresión."), Q6b "Se aplica, como hoy (Recommended)" ("La app solo descarta una lectura que ya iba en camino cuando aceptó una versión más nueva de esa parada; una lectura pedida después siempre se aplica. Evita igual el parpadeo. Tras una restauración, el repartidor ve lo que dice el servidor desde la siguiente lectura."); en la revisión del PR
  #271, Q6c "Solo paradas ya vistas (Recommended)" ("Una lectura en camino ignora la parada solo si la lectura que la quitó salió cuando la app ya tenía esa parada. Corrige el caso del revisor (una parada quitada ya no regresa con datos viejos) y una parada recién asignada nunca queda oculta más que hoy. Queda un hueco raro: con tres lecturas cruzadas, la parada quitada podría reaparecer una vez, hasta la siguiente lectura.") y, tras el comentario de revisión 4267824654, Q6d "Corregir en este PR (Recommended)" ("Si una lectura que salió antes llega después de una más nueva, el contador se queda con el valor de la más nueva. Mismo criterio que las paradas: una lectura pedida después siempre se aplica. Con prueba de regresión; el PR tarda un poco más.").
- La PWA del repartidor ya no toma una parada de una lectura que iba en camino cuando la app aceptó una versión más
  nueva de esa parada: la parada que tiene se queda y una parada que ya salió de la lista no vuelve. Las respuestas se
  toman de una en una, en el orden en que llegan, así que la pantalla y la instantánea se escriben en ese orden.
  Corrige el riesgo residual de `docs/development/driver-photo-selection-refresh.md` (PR #268): una lectura completa
  respondida antes del check-in que llegaba después de su confirmación regresaba la parada y perdía la foto en vista
  previa.
- Una parada quitada (Q6c): si una lectura que salió cuando la app ya tenía la parada deja de traerla, cada otra
  lectura en camino la anota en la versión que la app tenía + 1. No la regresa con esa versión ni con una menor, y la
  toma con esa + 1 o una mayor (una asignación nueva). Una parada puede salir de la lista sin subir la versión de la
  orden: D8 cierra la asignación tras FAILED_ATTEMPT→RESCHEDULED sin tocar la orden.
- El contador "Hoy · N paradas · K hechas" (Q6d): cada lectura de paradas, completa o de confirmación, recibe un
  número de orden al salir y la app recuerda el de la lectura cuyo contador muestra. Una lectura que salió antes que
  esa no cambia el contador, ni en pantalla ni en la instantánea, sea el suyo menor o mayor; cualquier otra lectura
  trae el suyo, así que una lectura pedida después se toma siempre, aunque traiga un número menor (un día nuevo). Al
  abrir no hay ninguna recordada: la primera lectura que se toma trae su contador. La hora de sincronización sigue
  cada lectura como antes.
- Sin cambio: versión igual de una parada que la app tiene, una parada que la lectura no trae (sale de la lista,
  DRV-001), una lectura pedida después (también tras restaurar la base), la cola y sus reglas (DRV-002), API, SQL,
  migraciones e IndexedDB. Pruebas de regresión en Vitest y en `DriverOfflineOperationsPwa`; la parada quitada y el
  contador sólo en Vitest.

## Esperas encadenadas del Worker y del tiempo real aceptadas (SCL-007-WORKER-CHAINED-WAIT-2026-10-14) — 2026-10-14

- Respuesta literal del project owner (2026-10-14), registrada en `decision-log.md` con su contexto y las opciones no
  elegidas: "Dejarlo así (Recommended)" ("Se acepta hasta unos 2 s en el peor caso para esos avisos tras un rato sin
  trabajo. Con trabajo no hay espera. La pantalla de quien asigna se actualiza al momento con su propia respuesta; el
  retraso es para las otras pantallas y la app del repartidor.").
- Los avisos en pantalla de cambio de asignación y de estado de notificación pasan por la cola del Worker y luego por la
  de tiempo real. Tras un rato sin trabajo, las dos esperas pueden sumar unos 2 s en el peor caso (antes unos 0.5 s). El
  owner lo acepta; las dos colas del Worker conservan su espera máxima de 1 s.
- Sin cambios de código, configuración, SQL, permisos, migraciones, Bicep ni paquetes. AI-08 SCL-007 registra la
  aceptación junto al criterio de la espera del Worker. Nota: `docs/development/scl-007-adaptive-outbox-wait.md`.

## Traza y seudónimo en el Worker, y momento de la exportación (OBS-003-WORKER-AUDIT-TRACE-2026-10-14, OBS-003-WORKER-PSEUDONYM-2026-10-14 y OBS-003-EXPORT-AFTER-PILOT-WEEK-2026-10-14, seguimiento de la fase 4) — 2026-10-14

- Respuestas literales del project owner (2026-10-14), registradas en `decision-log.md` con su contexto, la corrección
  que se le dio antes de QA–QC y las opciones no elegidas:
  - auditoría del Worker (Q1): "Sí, en un PR aparte (Recommended)" ("Desde ese PR, esas filas guardan el trace id del
    mensaje. Las filas que ya existen no cambian (la auditoría es de solo agregar). Cambia el contenido de la evidencia
    de auditoría desde ese momento."); validación de fotos (QA): "Se quedan vacías (Recommended)";
  - logs del Worker (Q2): "Sí, en un PR aparte (Recommended)" ("Mismo seudónimo HMAC de la fase 4, nunca el id real, y
    nunca como dimensión de métricas o alertas. Permite filtrar en Log Analytics los mensajes de una organización.");
    tipo de actor (QB): "SYSTEM (Recommended)"; validación de fotos (QC): "Sí, si los metadatos son válidos
    (Recommended)";
  - exportación (Q5): "Tras una semana de piloto (Recommended)" ("Primero se despliega sin exportar y se mide una semana
    cuánto ocupan los logs del tope diario. Con ese dato se enciende en un PR. Así no arriesgamos los logs de las
    alertas en los primeros días.").
- La fila `ASSIGNMENT_CLOSED` que escribe el Worker guarda en `platform.audit_logs.request_id` el trace id del mensaje
  que procesa (el de la petición o tarea que lo escribió, 32 hexadecimales en minúscula, igual que la API). Un mensaje
  sin contexto de traza guardado deja NULL, nunca la traza de la vuelta del Worker. La validación de fotos y el cierre
  automático de órdenes siguen con NULL. Las filas existentes no cambian; sin cambios de base de datos, acciones ni
  actores.
- Cada línea que el Worker escribe al procesar un mensaje de notificaciones o del ciclo de vida de asignaciones lleva el
  seudónimo HMAC de la organización dueña del mensaje (`owner_org_id`, que las funciones de claim ya devuelven) y el tipo
  de actor `SYSTEM`. La línea por foto de la validación lleva lo mismo cuando los metadatos firmados son válidos. Un
  dueño desconocido o metadatos inválidos no llevan organización. Nunca el id real, ni etiqueta de métrica, ni dimensión
  de alerta, libro de trabajo o `TelemetryDimensions`. Los lanes de tiempo real de la API no cambian.
- La exportación a Application Insights sigue apagada (`TelemetryExport__Enabled='false'`). Se enciende en un PR
  posterior tras una semana de piloto, cuando se haya medido el volumen diario de logs contra el tope de 0.3 GB. Sin
  cambio de código.
- AI-08 OBS-003: los dos criterios PENDING pasan a DECIDED y se agrega el criterio de Q5. AI-05: la descripción de
  `Problem.traceId` dice la regla actual de las filas del Worker y la entrada `OBS-003-WORKER-AUDIT-TRACE` de
  `x-pilot-contract-deltas` registra el cambio de solo descripción. AI-03 §18 no cambia. Notas:
  `docs/development/obs-003-worker-correlation.md`.

## Alerta de conexiones de PostgreSQL en el piloto (SCL-007-POSTGRES-CONNECTION-ALERT-2026-10-14) — 2026-10-14

- Respuestas literales del project owner (2026-10-14), registradas en `decision-log.md` con su contexto y las opciones
  no elegidas:
  - "¿Creamos la alerta de Azure Monitor sobre conexiones de PostgreSQL? Es el respaldo si alguien desbalancea los pools a mano." Respuesta: "Sí, 80 % y 95 % (Recommended)" ("Dos reglas sobre las 35 conexiones de usuario: aviso en 28 y crítico en 33, como pide AI-15. Son 2 series, dentro de las 10 gratis por recurso; si no, unos 0.10 USD por serie al mes. Avisa al correo de alertas que pongas al desplegar.");
  - "Alerta de conexiones de PostgreSQL (aviso en 28, crítico en 33): ¿con qué cálculo y cada cuánto la evaluamos? Es la primera alerta de métrica del piloto; las de OBS-002 son de logs cada 15 min, y ese ritmo se eligió por costo. Esta alerta cuesta lo mismo con cualquier frecuencia, porque se cobra por serie y no por evaluación." Respuesta: "Máximo en 5 min, cada minuto (Recommended)".
- `deploy/azure/pilot/observability.bicep` (etapa 5 del flujo del piloto, sin cambios en el flujo) declara dos alertas de
  métrica de Azure Monitor sobre `active_connections` del servidor `pg-pv-pilot-<suffix>`: `mar-pv-pilot-pg-connections-warning`
  (28 o más, severidad 2) y `mar-pv-pilot-pg-connections-critical` (33 o más, severidad 1). Son el 80 % y el 95 % de las
  35 conexiones de usuario del B1ms, el `connection_pool_utilization` 0.8 y 0.95 de AI-15. Cada una toma el máximo de las
  muestras de un minuto de los últimos 5 minutos y se evalúa cada minuto; avisan al grupo de acciones `ag-pv-pilot-ops`
  (un correo al dispararse y otro al resolverse). El correo sigue siendo el parámetro `alertEmailAddress`
  (OWNER_DECISION_REQUIRED); no se escribe ninguna dirección.
- Costo: 2 series, dentro de las 10 gratis por recurso; si no, unos 0.10 USD por serie al mes
  (azure.microsoft.com/en-us/pricing/details/monitor). El precio no depende de la frecuencia.
- Guardas del piloto: P01 admite `Microsoft.Insights/metricAlerts` solo en `observability.bicep`; P21 exige exactamente
  estas dos reglas (servidor, métrica, agregación, ventana, frecuencia, umbral, severidad, grupo de acciones), rechaza
  cualquier otra alerta de métrica y cualquier correo escrito en la plantilla, exige el SKU Standard_B1ms y suma las dos
  series al tope de 5 USD de las alertas. Las pruebas prueban que cada desviación falla cerrada.
- AI-08 SCL-007: el criterio PENDING pasa a DECIDED y se agrega la prueba de la guarda. AI-15 no lista reglas de alerta y
  no cambia. Sin cambios de código, esquema, roles, permisos, paquetes ni topología de CI. Riesgo residual: Microsoft no
  documenta si `active_connections` cuenta las conexiones propias de Azure (`azure_superuser`); el primer despliegue lo
  compara con `pg_stat_activity` por usuario. Notas: `docs/operations/env-001-pilot/README.md` §10 y
  `docs/development/scl-007-pool-per-role.md`.

## Espera adaptativa en las colas del Worker (SCL-007-WORKER-ADAPTIVE-WAIT-2026-10-14, seguimiento de la fase 4) — 2026-10-14

- Respuesta literal del project owner (2026-10-14), registrada en `decision-log.md` con su contexto y las opciones no
  elegidas: "1 segundo en ambas (Recommended)" ("Sin trabajo bajan a unas 2 consultas por segundo. La primera
  notificación o evento después de un rato sin trabajo tarda hasta 1 s más. Igual que lo que elegiste para los avisos
  en tiempo real.").
- Las colas de notificaciones y del ciclo de vida de asignaciones del Worker usan la misma espera adaptativa que los
  despachadores de tiempo real (`OutboxIdleWait`, que pasa a `Paqueteria.Application.Scaling` para que los cuatro lanes
  usen una sola implementación). Cuando una consulta trae trabajo, lleno o parcial (en notificaciones, propio o sin
  dueño), vuelven a consultar de inmediato y la espera empieza de nuevo; tras una consulta vacía esperan 250 ms, 500 ms
  y luego 1 s. Cada lane tiene su propia espera.
- Sin trabajo pasan de unas 8 a unas 2 consultas por segundo entre las dos colas. Cada consulta de notificaciones son
  dos claims (propios y sin dueño), así que en transacciones de claim pasan de unas 12 a unas 3 por segundo.
- Configuración validada al arrancar: `Notifications:MaximumIdleWaitMilliseconds` y
  `Dispatch:AssignmentLifecycle:MaximumIdleWaitMilliseconds`, 1000 por defecto. Un valor menor que el intervalo de poll
  del lane, mayor que 1000 ms o que no sea un número impide arrancar el Worker, con los mismos límites que los lanes de
  tiempo real.
- Se mantienen el latido por minuto de `OutboxLaneSummary` (OBS-002) y el aviso `oldest_outbox_age_seconds` de AI-15
  (60 s). `MaxClaimAgeMs` incluye la espera. La primera notificación o reacción después de un rato sin trabajo espera
  hasta 1 s (antes 250 ms).
- Solo se usan las funciones existentes de claim, settle y requeue con `lease_token`. La validación de fotos y las tareas
  programadas no cambian.
- AI-08 SCL-007 registra la decisión y sus límites. Sin cambios en AI-04, AI-05, AI-06, AI-12, AI-13, AI-15, AI-18,
  AI-24, SQL, permisos, migraciones, secretos, Bicep ni paquetes. Nota: `docs/development/scl-007-adaptive-outbox-wait.md`.

## Pruebas de punta a punta de la plataforma (OPS-005-JOURNEYS-2026-10-13, OPS-005-E2E-LOCKFILE-REL000-2026-10-14 y OPS-005-SUITE-IN-INTEGRATIONTESTS-2026-10-14, fase 4 PR 10) — 2026-10-14

- Respuesta literal del project owner (2026-10-13), registrada en `decision-log.md` con su contexto y las opciones no
  elegidas: "Recorrido principal (Recommended)" ("Cotizar, crear y confirmar la orden, asignar repartidor, recoger,
  entregar con foto y ver 'Entregada' en el seguimiento público. Un recorrido completo; suma unos minutos al job de CI
  existente."). 'Entregada' era la forma de nombrar el estado entregado en el seguimiento público; la suite comprueba
  la etiqueta pública vigente de DELIVERED, "Entregado" (`apps/web/src/tracking/contracts/public-tracking-labels.ts`,
  `DELIVERED: "Entregado"`), y ninguna etiqueta cambia.
- La suite vive en `tests/Paqueteria.IntegrationTests/PlatformEndToEnd` (categoría `PlatformEndToEnd`) y arma la API,
  el Worker y la web de producción contra PostgreSQL/PostGIS y el almacenamiento S3 de prueba, con el arnés de ese
  proyecto y solo datos sintéticos. El recorrido corre por las pantallas reales: "Nueva orden" cotiza, crea y confirma;
  el detalle libera para recolección y asigna al repartidor; la PWA del repartidor recoge y entrega con sus dos fotos
  por carga firmada; el seguimiento público muestra "Entregado".
- En la misma corrida mide las comprobaciones de AI-08 OPS-005: una traza por acción del navegador (logs y auditoría;
  el `traceId` del 429), el 429 `problem+json` con `Retry-After` y el texto aprobado en el asistente, las conexiones de
  PostgreSQL por rol dentro del presupuesto de SCL-007, un aviso tras 3 s sin trabajo en menos de 2 s y nada que salga
  del proceso con la exportación apagada.
- Corre dentro del job `driver-stops-pwa` (categoría `PlatformEndToEnd` del build de IntegrationTests que el job ya
  restaura y compila; excluida de los jobs `dotnet`); su TRX entra en la evidencia REL-000 de ese job bajo el proyecto
  IntegrationTests. Ningún job nuevo: Foundation sigue con 13.
- El archivo de paquetes de la suite (`OPS-005-E2E-LOCKFILE-REL000-2026-10-14`). Dos respuestas literales del project
  owner (2026-10-14), registradas en `decision-log.md` con su contexto y las opciones no elegidas; la segunda reemplazó
  a la primera:
  - Primera pregunta: "PR 10: el control de dependencias de REL-000 rechaza los paquetes del proyecto de pruebas de punta a punta, aunque son exactamente los mismos que ya usan las pruebas de integración (tabla de arriba). ¿Cómo lo resolvemos?" Respuesta: "Archivo vacío = nuevo (Recommended)" ("En este PR, REL-000 trata un archivo de paquetes que en la base está vacío igual que uno nuevo: solo acepta paquetes que ya están en el repositorio, idénticos (nombre, versión y hash). Cualquier otro cambio sigue rechazado. Aplica a los 3 proyectos que hoy tienen el archivo vacío (EndToEndTests, Contracts y Domain). Con pruebas en REL-000.").
  - Segunda pregunta: "Corrección: la opción \"Archivo vacío = nuevo\" falla la prueba permanente de REL-000, salvo que también se cambie cómo esa prueba deshace la remediación de seguridad SEC-2026-12. ¿Cómo seguimos con el archivo de paquetes de la suite de punta a punta?" Respuesta: "Solo este proyecto (Recommended)" ("REL-000 acepta solo el archivo de EndToEndTests, solo si en la base está vacío y solo si cada paquete es idéntico (nombre, versión y hash) al de las pruebas de integración del mismo commit, que REL-000 ya audita. Cualquier otro cambio sigue rechazado. Pasa la evidencia de release y la prueba permanente, ya probado. Deja en REL-000 un caso especial para una ruta.").
- Dónde vive la suite (`OPS-005-SUITE-IN-INTEGRATIONTESTS-2026-10-14`). Tercera respuesta literal del project owner
  (2026-10-14), registrada en `decision-log.md` con su contexto y las opciones no elegidas; reemplaza a la segunda antes
  del merge, así que la regla de REL-000 no se aplicó y ningún cambio de REL-000 llegó a `development`:
  - Pregunta: "PR 10: el PR Gate rechaza el archivo de paquetes de la suite de punta a punta, porque ninguna dependencia entra por development. ¿Cómo seguimos?" Respuesta: "Suite en integración (Recommended)" ("La suite se mueve al proyecto de pruebas de integración, con su propia categoría, en el mismo job de CI. No cambia ningún archivo de paquetes y no se toca ningún control: se retira de este PR el cambio a REL-000 que habías aprobado. Se actualiza AI-08 OPS-005, que hoy dice EndToEndTests, con su fila en el decision-log. El proyecto EndToEndTests se queda vacío.").
- La suite se movió con `git mv` de `tests/Paqueteria.EndToEndTests` a
  `tests/Paqueteria.IntegrationTests/PlatformEndToEnd`. `tests/Paqueteria.EndToEndTests` queda reservado y vacío,
  idéntico a `development`. No cambia ningún archivo de paquetes (el csproj y el lock de IntegrationTests quedan
  iguales) ni ningún control: `tools/rel-000`, la nota de REL-000, `tools/ci/change-domains.json`, el clasificador y el
  PR Gate quedan idénticos a `development`.
- AI-08 OPS-005 registra el recorrido decidido y, en `expected_outputs`, el proyecto IntegrationTests con
  `Category=PlatformEndToEnd`. Sin cambios en AI-03, AI-04, AI-05, AI-06, AI-07, AI-12, AI-13, AI-15, AI-18, roles,
  permisos ni migraciones; ningún paquete nuevo ni versión nueva. Nota: `docs/development/ops-005-end-to-end-suite.md`.

## Remediación de seguridad NuGet: SignalR, Cryptography.Pkcs y Cryptography.Xml a 10.0.13 (SEC-2026-12-NUGET-REMEDIATION-2026-10-13) — 2026-10-13

- Respuestas literales del project owner (2026-10-13), registradas en `decision-log.md` con su contexto y las opciones
  no elegidas:
  - aviso de SignalR: "Remediar con la tabla (Recommended)";
  - ampliación a Pkcs y Xml: "Los 4 en un PR (Recommended)";
  - cómo subir Xml y Pkcs: "Fijado transitivo (Recommended)";
  - dos avisos más de Xml: "Incluir los 6 (Recommended)".
- Seis avisos de Microsoft del 2026-10-13, todos corregidos en 10.0.13. El tracking es el Issue #260 y la ruta es la de
  SEC-2026-11 (PR de remediación a `main`, back-sync a `development` y retiro):
  - `GHSA-334f-vvp4-7g6p` (SignalR.Common);
  - `GHSA-846f-f764-76mx` y `GHSA-2fmv-rv2c-59x7` (Pkcs);
  - `GHSA-r955-8q5j-3h83`, `GHSA-7hqp-7c5m-4vq6` y `GHSA-c399-9853-6jmj` (Xml).
- `Directory.Packages.props`:
  - `Microsoft.AspNetCore.SignalR.Client` pasa a 10.0.13;
  - `CentralPackageTransitivePinningEnabled` queda activo en todo el repositorio;
  - `System.Security.Cryptography.Pkcs` y `.Xml` se fijan en 10.0.13.
- Los locks se regeneran con `dotnet restore --force-evaluate` y se mueven exactamente 22 nodos:
  - IntegrationTests: los 14 paquetes SignalR más Xml y Pkcs;
  - ContractTests, ArchitectureTests y DatabaseMigrator: Xml y Pkcs, que pasan a `CentralTransitive`.

  Ningún otro lock cambia, tampoco los de la API y el Worker.
- REL-000 agrega:
  - la autorización `SEC-2026-12-SIGNALR-CRYPTOGRAPHY`;
  - un validador de grafo exacto: ediciones del manifiesto en su lugar, los 16 movimientos con versiones y hashes
    exactos, y el SHA-256 de cada lock.
- El grafo permanente MVP-0 (`test_k`) también acepta exactamente esos movimientos (`nuget_remediations`). La evidencia
  de liberación no lo usa, así que el modo normal y el código de admisión de dependencias no cambian. El fijado
  transitivo de todo el repositorio sí acota lo que una admisión futura puede admitir: un `PackageVersion` nuevo para
  un paquete que ya es transitivo en algún proyecto vuelve ese nodo `CentralTransitive` y falla con
  `DEPENDENCY_ADMISSION_BASELINE_PACKAGE_CHANGED`, y un consumidor nuevo de Xml o Pkcs también falla. Ambos fallan
  cerrado. Nota: `docs/development/rel-000-internal-release.md`.
- Sin cambios en código de producción, migraciones, AI-04, AI-05, AI-06, AI-18, roles ni permisos.
- Fusionada en `main` por #261 (a29ab4a), sincronizada a `development` por #262 (b5a1fc9) y retirada a
  `historical_remediations` por este PR.

## Contexto de traza a través del outbox (OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13, fase 4 PR 7) — 2026-10-13

- Respuesta literal del project owner (2026-10-13), registrada en `decision-log.md` con su contexto y las opciones no
  elegidas: "Columna nueva (Recommended)" ("Una columna con la traza en las dos tablas del outbox (migración con ida y
  vuelta probadas). Los logs del Worker llevan la traza de la petición original, así que una sola búsqueda en Log
  Analytics muestra API y Worker juntos, aunque haya muestreo. No toca el contenido del evento ni lo que reciben los
  clientes. Cumple AI-03 §18 tal como está.").
- AI-06: columna `trace_parent` (`text`, nula) con CHECK de forma exacta (`00-{trace id}-{span id}-{00|01}`) en
  `platform.outbox_events` y `platform.location_outbox_events`. Nunca `tracestate`, baggage, empresa, usuario ni datos
  de la petición, y nunca dentro del `payload`.
- Cada productor la escribe en su propia transacción con el contexto de su actividad actual (`ValueGeneratedNever`, sin
  `RETURNING`); la función del operador (DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03) conserva su firma y la lee de
  `app.trace_parent`, local a la transacción; las dos funciones de Notifications copian la del evento de origen.
- AI-18: el ejecutor del operador recibe `INSERT (trace_parent)` sobre `platform.outbox_events`, propiedad del paso del
  lane de Dispatch; la sentencia publicada no cambia y la aserción 30 lo documenta.
- Migraciones: `20261013000100_AddOutboxTraceContext` (Dispatch) y `20261013000200_CarryOutboxTraceContext`
  (Notifications), con ida, vuelta e ida probadas en PostgreSQL y el catálogo idéntico; la vuelta va primero en
  Notifications y Dispatch la rechaza en otro orden. Ninguna fila se reescribe.
- Las protecciones de contenido de AI-06 (`platform.protect_outbox_content()` y
  `platform.protect_location_outbox_content()`) también comparan `trace_parent`: ningún UPDATE la cambia. El paso de
  Dispatch las instala y su vuelta restaura las publicadas antes de quitar la columna.
- Las funciones de claim devuelven la columna sin cambios (devuelven la fila completa). El Worker y los lanes de tiempo
  real de la API procesan cada mensaje en su propia actividad, hija del contexto guardado: sus logs llevan la traza de
  la petición original. Los ciclos de despacho siguen siendo su propia traza y nunca se exportan; con la exportación
  encendida, el mensaje se exporta solo si la traza de origen se muestreó, sin atributos, junto con sus llamadas
  HTTP salientes (la del proveedor de una notificación) bajo la lista permitida de PR 6.
- AI-03 §18 no cambia para `trace_id` y `correlation_id`. Quedan pendientes del owner (AI-08 OBS-003): el seudónimo de
  empresa y el tipo de rol en las líneas por mensaje del Worker, y el trace id en sus filas de auditoría.
- Sin cambios en AI-03, AI-04, AI-05, AI-12, AI-13 ni AI-24, en el contenido de los eventos, en los mensajes de SignalR
  ni en las notificaciones. AI-08 OBS-003 registra la decisión. Nota: `docs/development/obs-003-outbox-trace-context.md`.

## Exportación de trazas y métricas a Application Insights (OBS-003-APPINSIGHTS-EXPORT-2026-10-13, fase 4 PR 6) — 2026-10-13

- Aplica `OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12` ("Application Insights (Recommended)") y
  `OBS-003-TENANT-PSEUDONYM-2026-10-12` ("Seudónimo de empresa (Recommended)"), con dos respuestas literales del
  project owner (2026-10-13), registradas en `decision-log.md` con su contexto y las opciones no elegidas:
  - Q-PR6-1, qué ciclos se exportan: "Solo tareas programadas (Recommended)".
  - Q-PR6-2, cómo se autentica el envío: "Llave en la conexión (Recommended)".
- La API y el Worker exportan trazas y métricas de OpenTelemetry (nunca logs) con el exportador oficial de Azure
  Monitor, sin paquetes nuevos. Apagado por omisión: la sección `TelemetryExport` se valida al arrancar y falla
  cerrado; apagado, no se registra ningún exportador y nada sale del proceso.
- Muestreo de 10 % por traza, fijo en código (`ParentBasedSampler(TraceIdRatioBasedSampler(0.10))`). Solo se exportan
  las peticiones de la API (no `/health/*`) y los ciclos de las tareas programadas de ADR-034, con sus llamadas HTTP
  salientes. Nunca las vueltas del outbox, la validación de fotos ni spans de base de datos.
- Lista permitida de atributos de span y de etiquetas de métricas (la de OBS-002, con las de
  `aspnetcore.rate_limiting.requests`). La propagación sigue sin hacer nada, también con la exportación encendida.
- Seudónimo de empresa `v{versión}-<32 hex>` (HMAC con el secreto `paquetenvia-telemetry-pseudonym-key-1` que escribe
  el owner) y tipo de rol del actor en los logs y la traza de las peticiones con empresa seleccionada; nunca en
  métricas, eventos de OBS-002, alertas ni el libro. Llave sintética en Development y Testing; fuera de ellos la API y
  el Worker no arrancan sin la llave.
- Piloto: componente `appi-pv-pilot-<suffix>` sobre el mismo Log Analytics (mismo tope diario de 0.3 GB), con
  `TelemetryExport__Enabled='false'` literal en `apps.bicep` (se enciende con un PR revisado); guardas P01, P06, P17,
  P21 y P23 y la comprobación del secreto en el workflow. Riesgo anotado: quien obtenga la cadena de conexión puede
  mandar telemetría falsa, no leerla, y esa telemetría cuenta contra el tope diario. Ninguna alerta nueva.
- AI-08 OBS-003 registra lo implementado. Sin cambios en AI-04, AI-05, AI-06, AI-18, roles, permisos, migraciones ni
  paquetes. Nota: `docs/development/obs-003-appinsights-export.md`.

## Espera adaptativa de los avisos en tiempo real (SCL-007-ADAPTIVE-OUTBOX-WAIT-2026-10-13, fase 4 PR 9) — 2026-10-13

- Respuesta literal del project owner (2026-10-13), registrada en `decision-log.md` con su contexto y las opciones no
  elegidas: "1 segundo en ambos (Recommended)" ("Negocio y ubicación esperan como máximo 1 s. Sin trabajo bajan de unas
  6 a 2 consultas por segundo; el primer aviso después de un rato sin trabajo tarda hasta 1 s más, dentro de los 2 s de
  AI-15.").
- Los despachadores de tiempo real de la API (lanes de negocio y de ubicación) vuelven a reclamar de inmediato cuando un
  claim trae trabajo, lleno o parcial, y vuelven a empezar. Tras un claim vacío esperan su intervalo de poll y duplican
  la espera con cada claim vacío siguiente, hasta 1 s: negocio 250 ms, 500 ms y 1 s; ubicación 500 ms y 1 s. Sin
  trabajo pasan de unas 6 a unas 2 consultas por segundo. Cada lane tiene su propia espera.
- Configuración validada al arrancar: `Realtime:OutboxDispatcher:Business:MaximumIdleWaitMilliseconds` y
  `Realtime:OutboxDispatcher:Location:MaximumIdleWaitMilliseconds`, 1000 por defecto. Un valor menor que el intervalo de
  poll del lane, mayor que 1000 ms (la mitad del aviso `event_delivery_p95_seconds` de AI-15, 2 s) o que no sea un
  número impide arrancar la API.
- Se mantienen el latido por minuto de `OutboxLaneSummary` (OBS-002), el aviso `oldest_outbox_age_seconds` de AI-15
  (60 s) y el de `event_delivery_p95_seconds` (2 s). `MaxClaimAgeMs` ya incluye la espera: se mide al volver el claim.
- Solo se usan las funciones existentes de claim, settle y requeue con `lease_token`. Los pollers del Worker no cambian.
- AI-08 SCL-007 registra la decisión, los límites y la prueba de entrega tras un rato sin trabajo. Sin cambios en
  AI-04, AI-05, AI-06, AI-12, AI-13, AI-15, AI-18, AI-24, SQL, permisos, migraciones, secretos, Bicep ni paquetes. Nota:
  `docs/development/scl-007-adaptive-outbox-wait.md`.

## Un pool de conexiones por rol (SCL-007-POOL-PER-ROLE-2026-10-13, fase 4 PR 8) — 2026-10-13

- Aplica la fase 4 aprobada; no hay pregunta nueva al owner. Respuesta literal del owner
  (`PHASE4-PLATFORM-APPROVAL-2026-10-12`): "Sí, avanza con la fase 4 (Recommended)"; la pregunta nombraba "un pool de
  conexiones por rol".
- Un `NpgsqlDataSource` por rol de base de datos en cada proceso, compartido por todos los módulos: `paqueteria_app`
  (`ConnectionStrings:Paqueteria`) y `paqueteria_realtime` (`ConnectionStrings:PaqueteriaRealtime`, los despachadores de
  tiempo real) en la API, y `paqueteria_worker` (`ConnectionStrings:PaqueteriaWorker`) en el Worker. Antes, 22 lugares
  del código armaban su propio pool: 5 pools en la API y 9 en el Worker.
- Presupuesto validado al arrancar (`DatabasePools`): 10 + 4 + 10 = 24 conexiones como máximo; cada tamaño entre 1 y 24
  y la suma hasta 24 = piso((50 − 15 reservadas por Azure) × 0.70) de las conexiones de usuario del B1ms (AZR-001-INV-014).
  Quedan 11 para el migrador, el mantenimiento y la administración. El `Maximum Pool Size` de la cadena de conexión se
  reemplaza, y cada pool se arma siempre con `Pooling=true`, `Multiplexing=false`, `Include Error Detail=false` y
  `Max Auto Prepare=0`, diga lo que diga la cadena; un valor inválido impide arrancar la API o el Worker.
- Sigue igual: transacción explícita con `set_config(..., true)` y `SET LOCAL ROLE` después de `BEGIN`; cada pool se
  autentica con un solo login `NOBYPASSRLS` de su rol; las comprobaciones de readiness siguen fallando ante un rol mal
  formado; el `pooling_contract` de AI-15 no cambia y PgBouncer sigue detrás de GATE-014. Pruebas en PostgreSQL real:
  ningún estado de sesión sobrevive entre préstamos, también con `No Reset On Close=true`.
- La validación de pruebas del Worker lee `ConnectionStrings:PaqueteriaWorker` (el piloto ya le da el mismo secreto con
  los dos nombres). `DataProtection:ConnectionStringName` solo acepta `Paqueteria` con `DataProtection:RuntimeRole`
  `paqueteria_app` y `PaqueteriaWorker` con `paqueteria_worker`.
- Pendiente del owner: la alerta de métrica de Azure Monitor sobre conexiones de PostgreSQL y su correo.
- AI-08 SCL-007 registra el rol de tiempo real, el presupuesto y las credenciales que quedan fuera. Sin cambios en AI-04,
  AI-05, AI-06, AI-13, AI-15, AI-18, AI-24, roles, permisos, migraciones, secretos, Bicep ni paquetes. Nota:
  `docs/development/scl-007-pool-per-role.md`.

## Límites de tasa en las pantallas del panel: mensaje con cuenta regresiva y recargas espaciadas (SEC-004-PANEL-SCREENS-2026-10-13, fase 4 PR 3, parte del panel) — 2026-10-13

- Respuestas literales del project owner (2026-10-13), registradas en `decision-log.md` con su contexto y las opciones
  no elegidas:
  - Q11, pantallas que se actualizan solas: "Reintenta sola (Recommended)".
  - Q12, un segundo: ""1 segundo" (Recommended)".
  - Q13, "Buscar guía" y "Agregar entrega": "Mensaje común (Recommended)".
  - Q14, botones de las pantallas que se actualizan solas: "Se pausan (Recommended)".
  - Q15, selector de repartidor: "La pide sola (Recommended)".
  - Q16, solo los conteos: "Toda la pantalla (Recommended)".
- Aplica también Q6 "Mensaje común con espera (Recommended)" (`SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12`) y Q9
  "Espaciar recargas (Recommended)" y Q10 "Dejarlo en 60/min (Recommended)" (`SEC-004-RELOADS-AND-BULK-2026-10-13`).
- AI-07 agrega `panel_rate_limit` y una entrada `rate_limit` en cada pantalla limitada: "Demasiadas solicitudes. Espera
  N segundos e intenta de nuevo." con N del `Retry-After` contando cada segundo ("1 segundo" al final; 60 si el
  encabezado falta o no es un entero positivo), anunciado una vez por espera.
- Pantallas que envían (CSV, exportar liquidación, "Calcular precio", "Buscar guía", "Agregar entrega"): el botón se
  desactiva durante la espera y se reactiva solo; lo capturado se conserva y nada se reenvía solo; al volver a pulsar
  se envía la misma `Idempotency-Key`.
- Pantallas que se actualizan solas (tablero, bandeja, detalle de la orden, Órdenes, Cobros pendientes): se pausan las
  recargas automáticas, "Actualizar" y "Cargar más"; un cambio de filtro se guarda; al llegar a 0 se recargan una vez
  (Órdenes no, si ya se cargaron más páginas). Una espera de los conteos pausa toda la pantalla.
- Toda pantalla que recarga con avisos en tiempo real (tablero, bandeja, detalle de la orden, Rutas) recarga como
  máximo cada 3 segundos por pestaña, sin perder avisos.
- Ningún límite cambia. Sin cambios en AI-04, AI-05, AI-06, AI-18, roles, permisos, migraciones ni paquetes. Nota:
  `docs/development/sec-004-panel-limits.md`.

## Recargas del panel y asignación en lote ante los límites (SEC-004-RELOADS-AND-BULK-2026-10-13, fase 4 PR 3) — 2026-10-13

- Respuestas literales del project owner (2026-10-13), registradas en `decision-log.md` con su contexto: la premisa
  de Q3 ("se refrescan cada 30 segundos") estaba incompleta, porque el tablero y la bandeja también recargan con cada
  aviso en tiempo real y cada recarga lee los conteos.
  - Q9, tablero y conteos: "Espaciar recargas (Recommended)" ("El panel junta los avisos y recarga como máximo cada 3
    segundos por pestaña. Los límites de 60/min no cambian. Los cambios se ven hasta 3 segundos después. Va en el PR de
    pantallas del panel.").
  - Q10, repartidores asignables: "Dejarlo en 60/min (Recommended)" ("En el piloto, tres lotes de 20 en un minuto es
    raro. Si pasa, el panel pide esperar unos segundos. Se revisa con las métricas en 2 semanas.").
- Ningún número cambia: tablero, conteos y repartidores asignables siguen en 60 por minuto por usuario.
- El espaciado de 3 segundos por pestaña sale en el PR de pantallas del panel (la parte web del PR 3). Hasta que se
  fusione, una ráfaga de avisos (una asignación en lote, la confirmación de un CSV) todavía puede gastar esos cupos.
- La asignación en lote sigue leyendo los repartidores de cada orden por separado: un lote de 20 órdenes usa al menos
  20 de los 60 por minuto. Se revisa con el evento 4604 en 2 semanas.
- Sin cambios en AI-04, AI-05, AI-06, AI-07, AI-18, roles, permisos, migraciones ni paquetes. Nota:
  `docs/development/sec-004-heavy-endpoint-limits.md` (riesgos residuales).

## Límites de tasa en los endpoints pesados: servidor, contratos y app del repartidor (SEC-004, fase 4 PR 3) — 2026-10-13

- Respuestas literales del project owner (2026-10-12 y 2026-10-13), registradas en `decision-log.md`:
  - `SEC-004-HEAVY-ENDPOINT-MAPPING-2026-10-12`: CSV "Compartido, 1 a la vez (Recommended)"; fotos "60 cada paso
    (Recommended)"; tablero y conteos "Uno cada uno (Recommended)"; inicio de sesión "Solo /auth/login (Recommended)";
    texto ante un 429 "Mensaje común con espera (Recommended)".
  - `OBS-001-DASHBOARD-429-2026-10-12`: "Enmendar ADR-OBS-001 (Recommended)".
  - `SEC-004-RETRY-AFTER-2026-10-13`: "Sí, agregarlo (Recommended)".
  - `SEC-004-REJECTION-SUMMARY-2026-10-13`: "Resumen en los logs (Recommended)".
- Límites por usuario (hash del sujeto; la red del cliente sin sujeto), ventana fija sin cola: vista previa y
  confirmación del CSV 10/min juntas y una confirmación a la vez por usuario; exportar liquidación 10/min; crear
  dirección 30/min; pedir subida de foto 60/min y confirmarla 60/min, cada uno con su presupuesto; tablero 60/min y
  conteos 60/min, cada uno con el suyo; lista de órdenes 120/min; cotizar 60/min; repartidores asignables 60/min.
  Inicio de sesión: 20/min por red, solo `GET /auth/login`; `/signin-authcenter` no se limita. Los números son la
  sección `RateLimits`, validada al arrancar; un valor inválido detiene la API.
- AI-05: 429 `TooManyRequests` en `previewOrderCsv`, `commitOrderCsv`, `exportSettlementCsv`, `createLocation`,
  `createProofUploadSession`, `finalizeProof`, `getOperationsQueueCounts`, `listOrders`, `createQuote` y
  `listAssignableDrivers`; `startAuthCenterSignIn` declara su 429 (página HTML simple para una navegación del
  navegador, con el texto aprobado, o el problema). `TooManyRequests` declara el encabezado `Retry-After` (entero,
  mínimo 1) que ya manda todo 429. Entrada `SEC-004-HEAVY-ENDPOINT-LIMITS` en `x-pilot-contract-deltas`. Rutas y
  esquemas siguen en 69 y 125; las referencias internas pasan de 651 a 662.
- El tablero, fuera de AI-05, registra su 429 en una enmienda de ADR-OBS-001; `HttpSurfaceOpenApiCoverageTests` ya no
  lo omite y revisa sus respuestas de error contra esa enmienda.
- OBS-002: evento 4604 `RateLimitSummary`, una línea por límite y minuto con rechazos (`Policy`, `Rejected`,
  `WindowSeconds`); `Policy` y `Rejected` entran a `TelemetryDimensions.Allowed`. Ninguna alerta ni libro de trabajo
  lo lee.
- App del repartidor (respuestas Q2 y Q6): un 429 al pedir o confirmar una foto ya no se toma por evidencia rechazada.
  La acción conserva sus llaves y su avance, la cola no manda nada hasta que pasa el `Retry-After` del servidor (ni con
  "Sincronizar ahora") y sigue sola, sin mensaje. AI-07 lo registra en `driver_stop.rate_limit` y la página de
  `/auth/login` en la ruta `/login`.
- AI-08 SEC-004: las tres decisiones pendientes del PR 3 quedan decididas. El mensaje y la espera del botón en cada
  pantalla del panel (AI-07) salen en la parte web del PR 3. Sin cambios en AI-04, AI-06, AI-18, roles, permisos,
  migraciones ni paquetes. Nota: `docs/development/sec-004-heavy-endpoint-limits.md`.

## Correlación W3C sin paquetes (OBS-003-W3C-CORRELATION-2026-10-12, fase 4 PR 1) — 2026-10-12

- Aplica la fase 4 aprobada; no hay pregunta nueva al owner. Respuesta literal del owner
  (`PHASE4-PLATFORM-APPROVAL-2026-10-12`): "Sí, avanza con la fase 4 (Recommended)" ("Empiezo por OpenTelemetry y
  correlación, y por los límites de tasa en endpoints pesados, que son lo que más ayuda a operar el piloto.").
- W3C Trace Context con `System.Diagnostics.Activity` y un `ActivitySource` propio (`Paqueteria`), sin paquetes
  nuevos. Un listener que solo propaga (`PropagationData`) garantiza una actividad W3C por petición y por ciclo, sin
  etiquetas, eventos ni baggage.
- Entrada: se ignoran `traceparent`, `tracestate`, `baggage`, `Request-Id` y `Correlation-Context`; cada petición a la
  API empieza una traza nueva. Salida: ninguna petición lleva esos encabezados (Google Maps, mensajes, voz, AuthCenter y
  Azure). Azure.Core agrega `traceparent` aunque `IsDistributedTracingEnabled` sea `false`, así que cada cliente de
  Azure envía por un transporte que quita esos encabezados.
- Un solo identificador: el trace id W3C (32 hexadecimales en minúscula) va en `HttpContext.TraceIdentifier`, en el
  `traceId` de todo `application/problem+json`, en `State.TraceId` de cada línea de log de la petición y en el
  `request_id` de cada fila de `platform.audit_logs` que se escribe al atender una petición a la API, también en los
  webhooks de voz de Twilio (estado de la llamada y teclas del repartidor y del destinatario), que ahora lo pasan en sus
  comandos. Una excepción: `PENDING_MEMBERSHIP_ACCEPTED`, que `security.apply_pending_memberships` escribe al iniciar
  sesión, sigue con `request_id` NULL porque la función no recibe el id. Las filas que escribe el Worker (validación
  de pruebas y ciclo de vida de asignaciones) no pertenecen a ninguna petición y siguen en NULL hasta el PR 7. AI-05
  dice exactamente eso en `Problem.traceId` y en la entrada `OBS-003-W3C-CORRELATION`. El seguimiento público conserva
  sus cuerpos uniformes sin `traceId`.
- Worker: cada ciclo de job y cada ciclo de despacho del outbox (también los despachadores de tiempo real en la API)
  corre en su propia traza raíz.
- AI-05, cambio aditivo: `Problem` documenta `traceId` opcional (`^[0-9a-f]{32}$`) y `x-pilot-contract-deltas` agrega
  `OBS-003-W3C-CORRELATION`; este cambio no altera rutas, esquemas ni referencias internas (69, 125 y 651 en la base fusionada). Sin cambios en AI-04,
  AI-06, AI-18, roles, permisos, migraciones ni paquetes. Nota: `docs/development/obs-003-w3c-correlation.md`.

## OBS-003: admisión explícita de los paquetes de telemetría (OBS-003-DEPENDENCY-ADMISSION-2026-10-13) — 2026-10-13

- Preguntas y respuestas literales del project owner (2026-10-13), registradas en `decision-log.md`:
  - "¿Admites explícitamente esta lista exacta de paquetes (12 nuevos y 6 subidas, con las versiones y hashes de la
    tabla) para la telemetría OBS-003?" → "Sí, admito la lista (Recommended)".
  - Como esa pregunta se hizo antes de mostrar la tabla, se mostró la tabla (id, versión o subida y los primeros 16
    caracteres del hash SHA-512 de cada paquete) y se volvió a preguntar: "Te pedí la admisión antes de mostrarte la
    tabla. Con la tabla de arriba a la vista (12 nuevos y 6 subidas, con esas versiones y hashes), ¿confirmas que tu
    admisión es exactamente esta lista?" → "Sí, confirmo esta tabla (Recommended)".
- Quedan admitidos exactamente los paquetes de la entrada `OBS-003-OPENTELEMETRY` de
  `tools/rel-000/security-remediation-policy.json`: 2 directos, 10 transitivos (5 `Microsoft.Extensions.*` solo en el
  lock de ArchitectureTests) y 6 subidas de paquetes que ya admitió ADP-001-AZURE (Azure.Core 1.61.0 → 1.63.0 y las
  cinco que arrastra).
- Ningún paquete, lock file ni código cambia con esta fila: entran solo por el PR de dependencias desde
  `deps/obs-003-opentelemetry` hacia `main` (GOV-DEPENDENCY-ROUTE-001, fase 4 PR 5).

## API: `created_at` en la `Order` y columna "Creada" de "Órdenes" (ORD-ORDER-CREATED-AT-2026-10-12) — 2026-10-12

- Segundo paso de la respuesta literal del project owner registrada en `UI-PHASE3-ORDERS-LIST-2026-10-12`: "Pantalla
  Órdenes: el API de la lista no trae la fecha de creación de cada orden, así que la columna "Creada" no se puede
  mostrar (los filtros por fecha sí funcionan). Agregarla cambia el API y la web actual rechaza campos que no conoce.
  ¿Qué hacemos?" → "En dos pasos (Recommended)" ("Este PR deja la web lista para aceptar la fecha; en un PR siguiente
  el API la agrega y se muestra la columna. Así ningún orden de despliegue rompe nada."); no elegida: "Sin columna de
  fecha" ("La lista queda con guía, estado y total, de la más reciente a la más antigua; la fecha se ve al abrir la
  orden."). El primer paso salió en #234. Fila `ORD-ORDER-CREATED-AT-2026-10-12` en `decision-log.md` (MAJOR de
  AI-01 §7, su registro ADR).
- AI-05: el esquema `Order` agrega `created_at` (`string`, `format: date-time`, instante UTC) y lo pone en
  `required`, porque el servidor siempre lo tiene (`orders.orders.created_at` es NOT NULL y nunca cambia). Lo mandan
  `createOrder`, `listOrders`, `getOrder` (`OrderDetail`) y `transitionOrder`, también al repetir una solicitud
  idempotente. Entrada `ORD-ORDER-CREATED-AT` en `x-pilot-contract-deltas`. Rutas, esquemas y referencias internas
  siguen en 69, 125 y 651.
- Servidor: `OrderResult.CreatedAt` lleva el valor de la fila; `createOrder` responde el instante de creación
  truncado a microsegundos, igual al que guarda PostgreSQL. Una respuesta idempotente guardada antes de este cambio no
  trae `created_at`; al repetirla se lee de la fila de la orden en la misma transacción tenant (si no hay fila, falla
  cerrado). La respuesta va en UTC: System.Text.Json escribe `+00:00`, que la regla única de la web
  (`src/lib/utc-instant.ts`) acepta.
- Privacidad: `created_at` no es dato personal, financiero ni secreto. El rastreo público, los eventos, el outbox,
  SignalR, la auditoría y los logs no cambian. FINANCE lo recibe en su lista de cobros pendientes como cualquier otro
  campo de la `Order`.
- AI-07 `order_list`: enmienda `ORD-ORDER-CREATED-AT-2026-10-12`; la columna "Creada" dice que el API ya manda la fecha
  y se quita de `not_supported`. La web no cambia: la columna aparece sola y los lectores siguen aceptando que falte.
- Sin cambios de parámetros, capacidades, RLS, tablas, índices, roles, permisos, migraciones ni flujos atómicos;
  AI-04, AI-06 y AI-18 iguales. Despliegue y reversión en cualquier orden (revertir el commit; no hay datos que
  deshacer). Nota: `docs/development/ord-order-created-at.md`.

## REL-000: subidas encadenadas entre admisiones de dependencias (GOV-REL000-CHAINED-UPGRADES-2026-10-12, OBS-003-TEST-EXTENSIONS-ADMISSION-2026-10-12) — 2026-10-12

- Preguntas y respuestas literales del project owner (2026-10-12), registradas en `decision-log.md`:
  - `GOV-REL000-CHAINED-UPGRADES-2026-10-12`: "Fase 4, paquetes de telemetría: el exportador de Azure que elegiste
    (1.10.0) sube Azure.Core 1.61→1.63, y eso arrastra 5 subidas más (System.ClientModel, Microsoft.Identity.Client y
    su extensión Msal, Microsoft.Bcl.AsyncInterfaces y System.Memory.Data). El control de dependencias (REL-000) hoy
    no puede registrar la subida de un paquete que ya admitió otra entrada (ADP-001). ¿Cómo seguimos?" → "Ajustar
    REL-000 primero (Recommended)" ("Un PR de la herramienta, como el que necesitó ADP-001: una subida exacta puede
    encadenarse sobre la de otra entrada solo si su versión de partida coincide, y las declaraciones que se encimen se
    rechazan. Endurece el control, no lo relaja. Después va la admisión con el exportador 1.10.0 y Azure.Core 1.63,
    tal como lo decidiste."); no elegidas: "Exportador 1.8.3" ("Funciona hoy sin tocar REL-000 ni Azure.Core (se queda
    en 1.61): 12 paquetes nuevos y ninguna subida. Pero el exportador queda dos versiones atrás, contradice "sube
    Azure.Core 1.61→1.63", y subirlo después choca con el mismo hueco.") y "Detener la exportación" ("Sigo con la
    correlación (PR 1) y los límites de tasa, sin paquetes nuevos por ahora. Retomamos Application Insights
    después.").
  - `OBS-003-TEST-EXTENSIONS-ADMISSION-2026-10-12`: "Además, el proyecto de pruebas de arquitectura necesita 5
    paquetes Microsoft.Extensions.* 10.0.0 (Configuration, Configuration.Binder, Configuration.EnvironmentVariables,
    Logging.Configuration y Options.ConfigurationExtensions). Solo cambian el lock file de pruebas; nada de eso se
    despliega. ¿Los admitimos?" → "Sí, admitirlos (Recommended)" ("Se suman a la lista que admites explícitamente al
    aprobar el PR de admisión. Solo afectan pruebas."); no elegida: "No, usar el framework" ("El proyecto de pruebas
    referencia el framework de ASP.NET. Eso también poda paquetes de la línea base y exige declarar esas bajas en la
    admisión. Más cambios en REL-000.").
- REL-000 (`tools/rel-000/rel000.py`), sólo más estricto:
  - Las `baseline_package_upgrades` de un mismo nodo (archivo de lock, framework e `id`) declaradas por admisiones
    distintas forman una cadena exacta en el orden de la política: cada paso empieza donde termina el anterior (versión
    y `content_hash`) y no vuelve a una versión ni a un `content_hash` visitados. Cualquier otro encime de subidas o
    bajas entre admisiones se rechaza al cargar la política con el código nuevo
    `DEPENDENCY_ADMISSION_AMENDMENT_CONFLICT`; antes la última declaración reemplazaba en silencio a la anterior. La
    misma versión adicional declarada dos veces sigue fallando antes con `DEPENDENCY_ADMISSION_POLICY_INVALID`.
  - Un nodo se acepta sólo si se movió por pasos declarados consecutivos. Contra el baseline MVP-0 (`test_k`) es la
    cadena declarada hasta donde esté hoy el nodo (6.0.0 → 10.0.9, y 6.0.0 → 10.0.9 → 10.0.10 cuando se entregue el
    paso siguiente). Contra la base probada es un paso, porque cada paso se acredita a su admisión y un PR sólo
    introduce los paquetes de una admisión, desde su rama (`DEPENDENCY_ADMISSION_BRANCH_NOT_AUTHORIZED` si no).
  - Una subida que aterriza en una versión de `additional_package_versions` de la misma admisión entrega ese
    `id@version` en ese lock; sin esto, la subida de un nodo ya admitido (Azure.Core 1.61.0 de ADP-001) quedaba
    siempre incompleta.
  - La clave de uso de cada paso es `upgrade:<lock>:<framework>:<id>@<to_version>` y se acredita a la admisión que
    lo declara. AUTH-001-OIDC y ADP-001-AZURE validan igual que antes.
  - Límite conocido, sin cambio en este PR y como seguimiento: un paso que baja de versión se acepta si está declarado
    exactamente; REL-000 no compara versiones con el orden de NuGet.
- Pruebas nuevas en `DependencyAdmissionAmendmentTests` con datos sintéticos: cadena aceptada contra la base probada
  y contra el baseline, paso con otra versión o hash de partida, encimes entre admisiones (también un hash visitado
  con otra escritura de versión), la misma versión adicional dos veces, subida a una versión no declarada, saltos
  sobre pasos de dos admisiones, rama y entrega completa, y las admisiones registradas como cadenas de un paso.
- Sin cambios en contratos normativos AI-xx, AI-08, `security-remediation-policy.json`, paquetes, lock files,
  workflows ni código de la aplicación. La entrada de admisión de OBS-003 (fase 4, PR 4: exportador 1.10.0 y
  Azure.Core 1.63) va después; el owner admite su lista exacta por paquete al aprobarla.

## Credencial propia y mínima para el tiempo real de la API (RTM-API-REALTIME-DISPATCHER-ROLE-2026-10-12) — 2026-10-12

- BLOCKER de seguridad y privilegio mínimo (AI-01 §7); el cambio de operación del piloto es MAJOR y lo registra
  `docs/adr/ADR-RTM-API-REALTIME-DISPATCHER-ROLE.md`. Pregunta y respuesta literales del project owner (2026-10-12):
  "Bloqueo del primer despliegue: la API del piloto envía los avisos en tiempo real (tablero, repartidor) leyendo la cola con la credencial del Worker, que hoy no tiene, así que no arrancaría. ¿Cómo lo resolvemos?" → "Rol propio y mínimo (Recommended)" ("Un rol de base de datos nuevo que solo puede tomar y cerrar mensajes de tiempo real, con su propio secreto en Key Vault para la API (ADR + AI-18 + migración). La API no gana ningún otro permiso."); no elegidas: "Darle a la API la del Worker" ("La API lee también el secreto del Worker. Es rápido, pero la API queda con todos los permisos del Worker (otras colas y mantenimiento).") y "Sin tiempo real en el piloto" ("Se apaga el despachador en la API; las pantallas se actualizan por consulta cada 30 s y el repartidor no recibe avisos al instante.").
- AI-18: rol `paqueteria_realtime NOLOGIN NOBYPASSRLS`, creado sin privilegios, revocado de `paqueteria_app`,
  `paqueteria_worker` y `paqueteria_bootstrap`, y sin poder heredar roles privilegiados (aserciones 35 a 37).
- Lane Notifications `20261012000100_AddRealtimeDispatcherRole`: EXECUTE en las seis funciones con `lease_token` de
  REALTIME y ubicación (`claim_realtime_outbox`, `requeue_stale_realtime_outbox`, `settle_outbox`,
  `claim_location_outbox`, `settle_location_outbox`, `requeue_stale_location_outbox`), USAGE en siete esquemas y
  SELECT en las 53 columnas que leen las lecturas de evidencia, todas también legibles por `paqueteria_app`. Sin
  escrituras, sin tablas de outbox, sin otras colas, sin purgas ni mantenimiento. El Down revoca todo y lo verifica.
- AI-24 `realtime_dispatcher_role`: `ConnectionStrings:PaqueteriaRealtime` validada al arrancar (login distinto del de
  la API y del Worker), `SET LOCAL ROLE paqueteria_realtime`, readiness y rollback.
- Piloto: secretos `pg-realtime-runtime-connection` y `pg-realtime-login-verifier`, login `pv_pilot_realtime`, la
  identidad de la API lee solo ese secreto nuevo; guardas P06 y P23. Sin cambios en AI-04, AI-05, AI-06, AI-12 ni en
  los flujos atómicos de AI-13 §4.
- Correcciones de la revisión de seguridad (misma decisión): la readiness exige que el login tenga la forma
  aprovisionada (`LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS`) y no sea miembro,
  directo o por cadena, de ningún rol fuera de `paqueteria_realtime`. AI-18 dice que las dos funciones de reclamo
  devuelven filas completas de outbox REALTIME y de ubicación de todos los tenants (`payload` incluido), revoca cada
  ejecutor también de `paqueteria_realtime` y precisa la aserción 37; AI-24 lista los pasos en el orden del código
  (`BEGIN`, `SET LOCAL ROLE`, `set_config`).

## Repartidor: "Llamar a despacho" con el teléfono de despacho de la empresa (DRV-DISPATCH-CONTACT-2026-10-12) — 2026-10-12

- Pregunta y respuesta literales del project owner (2026-10-12), registradas en `decision-log.md` como cambio MAJOR
  del contrato de la API (AI-01 §7): "En la app del repartidor, "Llama a despacho" (cuando ya registró un cobro y no
  puede entregar) no muestra ningún número porque la app no tiene uno. ¿Agregamos un teléfono de despacho?" → "Sí,
  configurable (Recommended)" ("Un número de despacho por empresa en la configuración del piloto; la app lo muestra
  como botón "Llamar a despacho". Tú me das el número cuando lo tengas."); no elegida: "No, ya lo saben" ("Los
  repartidores ya tienen el número de despacho; la app solo dice que llamen."). El owner todavía no da el número: no
  hay ninguno configurado y las pruebas usan solo números sintéticos.
- AI-05: nueva operación `getMyDispatchContact` (`GET /driver/me/dispatch-contact`, etiqueta Driver, con
  `X-Organization-Id`) y esquema `DriverDispatchContact`: el objeto cerrado `{"phone_e164": "+52…"}` o
  `{"phone_e164": null}` cuando la organización elegida no tiene número; 200, 401, 403 y 503, como
  `getMyDriverPhone`; `Cache-Control: no-store`. `x-capability-matrix` agrega `dispatch_contact_operations` (solo
  DRIVER, sin MFA, como `listMyStops`) y `x-pilot-contract-deltas` la entrada `DRV-DISPATCH-CONTACT`.
- Servidor: `Drivers:DispatchContacts` (lista de `{OrganizationId, PhoneE164}`), vacía por omisión. Al arrancar se
  valida (falla y no arranca): `OrganizationId` como UUID canónico en minúsculas y nunca el nulo; `PhoneE164` como
  número mexicano E.164 (`+52` y diez dígitos ASCII, el primero de 2 a 9, los que llama `VoicePhoneNumber`; se eligió
  México y no E.164 general porque el piloto y todas las reglas de teléfono del sistema son mexicanas); cada
  organización una sola vez (un mismo número puede servir a varias). El mensaje de error no lleva valores y
  `ToString` oculta el número. `DriverDispatchContactEndpoints` revisa la sesión, el contexto tenant (403) y la
  capacidad antes de leer; lee solo la configuración (sin base de datos, sin transacción tenant, sin escribir, sin
  logs) y nunca contesta el número de otra organización. Sin PII, secreto, tabla, índice, rol, permiso, migración ni
  flujo atómico nuevo; AI-04, AI-06 y AI-18 sin cambios.
- Piloto: el número es un valor de negocio del owner en `deploy/azure/pilot/apps.settings.json` (`businessSettings`,
  que `apps.bicep` agrega a los ajustes de la API y del Worker): `Drivers__DispatchContacts__0__OrganizationId` y
  `Drivers__DispatchContacts__0__PhoneE164`. Hoy no hay ninguna entrada. `env001_pilot_guards.py settings-check`
  (guarda P17) aplica las mismas reglas antes de desplegar y nunca repite un número.
- PWA del repartidor: `DriverDispatchContactController` pide el número al instalar la sesión del repartidor y en
  cada sesión nueva (otro inicio de sesión u otra organización); usa al instante la respuesta guardada para la
  partición, la reemplaza con cada respuesta correcta (número o null), la borra ante 401 o 403 y la conserva ante
  cualquier otra falla, preguntando de nuevo como la lectura de roles (al volver la conexión o la app, hasta 5 veces
  por sesión). Se guarda en el IndexedDB particionado de siempre, store nuevo `dispatch_contacts` (esquema 1: llave
  de partición, esquema y número o null), que necesita la versión 5 porque la 4 ya llegó a main; la subida desde la
  4 solo crea el store (acciones en cola, fotos, leases, instantáneas y roles guardados quedan igual) y el shell del
  Service Worker sube a `paquetenvia-driver-shell-v8`. Sale del teléfono con la partición ante 401 o 403 y con todas
  las instantáneas al cerrar sesión.
- "Llamar a despacho" es un enlace `tel:` de al menos 44 px que aparece solo junto a los textos que piden llamar a
  despacho por el cobro: el de la parada con el cobro registrado, el rechazo de "Enviar reporte" con el cobro
  registrado (formulario y resumen de sincronización), el reporte retenido antes de su incidencia por el cobro
  registrado y el reporte con incidencia abierta (o posiblemente abierta) retenido por el cobro registrado o todavía
  guardado. Sin número conocido los textos quedan exactamente igual y no se muestra nada; el número nunca se escribe
  en pantalla.
- AI-07 `driver_stop`: nuevo `dispatch_contact`; `failed_delivery.cash` y `offline` lo citan; `offline_cache` y
  `details.retention` agregan la respuesta guardada.
- Despliegue: no hace falta ningún orden (la web anterior nunca llama la lectura; esta web con una API anterior
  recibe 404 y no muestra el enlace). Rollback: la parte del API se revierte sola; la de la web no es un revert
  simple, porque una compilación de la versión 4 no abre una base en la versión 5: es una versión que conserva
  IndexedDB 5 con el store y deja de mostrar el enlace.

## Despacho: "Órdenes" y filtro por fecha de creación en `listOrders` (UI-PHASE3-ORDERS-LIST-2026-10-12, ORD-LIST-DATE-FILTER-2026-10-12) — 2026-10-12

- Preguntas y respuestas literales del project owner (2026-10-12), registradas en `decision-log.md` como dos cambios
  MAJOR (AI-01 §7). Primera: "El menú aprobado de Despacho incluye "Órdenes", pero esa pantalla no existe (hoy se llega
  a una orden desde la bandeja, el tablero o "Buscar guía"). ¿La hacemos?" → "Sí, lista de órdenes (Recommended)"
  ("Lista de todas las órdenes con búsqueda por guía y filtros por estado y fecha, usando el API que ya existe; abre el
  detalle."); no elegidas: "No, quitar del menú" y "Después del piloto". Segunda: "Corrección mía: para "Órdenes" dije
  "filtros por estado y fecha con el API que ya existe", pero la lista de órdenes del API no filtra por fecha (solo
  estado, dueña, cobro pendiente y guía). ¿Cómo la hacemos?" → "Agregar filtro de fecha al API (Recommended)" ("Cambio
  pequeño y aditivo (con ADR): la lista de órdenes acepta "desde" y "hasta" (hora de Mazatlán). La pantalla filtra por
  guía, estado y fecha."); no elegida: "Sin filtro de fecha". ADR: `docs/adr/ADR-ORD-LIST-DATE-FILTER.md`.
- Preguntas y respuestas literales del project owner durante la revisión (2026-10-12), añadidas a la fila
  `UI-PHASE3-ORDERS-LIST-2026-10-12`. Primera: "Pantalla Órdenes: el API de la lista no trae la fecha de creación de
  cada orden, así que la columna "Creada" no se puede mostrar (los filtros por fecha sí funcionan). Agregarla cambia el
  API y la web actual rechaza campos que no conoce. ¿Qué hacemos?" → "En dos pasos (Recommended)" ("Este PR deja la web
  lista para aceptar la fecha; en un PR siguiente el API la agrega y se muestra la columna. Así ningún orden de
  despliegue rompe nada."); no elegida: "Sin columna de fecha" ("La lista queda con guía, estado y total, de la más
  reciente a la más antigua; la fecha se ve al abrir la orden."). Segunda: "El menú aprobado de Finanzas incluye
  "Órdenes (lectura)", pero hoy Finanzas solo puede listar órdenes con cobro pendiente de conciliar. ¿Ampliamos su
  acceso?" → "No por ahora (Recommended)" ("Finanzas sigue viendo solo los cobros pendientes en su pantalla; no aparece
  "Órdenes" en su menú. Ampliar lectura es un cambio de permisos (ADR)."); no elegida: "Sí, solo lectura".
- AI-05 `listOrders`, cambio aditivo: parámetros opcionales `created_from` y `created_to` con las cotas del tablero
  (instantes RFC3339 estrictos en UTC, `yyyy-MM-ddTHH:mm:ss` con 0 a 7 decimales y `Z`; ambos inclusivos, cada uno
  solo o los dos con 31 días como máximo); una cota mal formada o repetida o un rango fuera de las reglas es
  `409 INVALID_REQUEST` antes de la capacidad (nueva respuesta `409`,
  `x-authorization-precedence: shape-validation-then-capability-before-persisted-state`). Cada `next_cursor` nuevo queda
  ligado a la organización y a los filtros; usado con otros filtros es `409 INVALID_REQUEST` después de la capacidad,
  donde siempre se leyó el cursor. Todo lo demás responde como antes: un cursor del formato anterior sigue paginando y
  cualquier otro texto (vacío, largo o ilegible), un estado desconocido, un `public_id` mal formado, otra dueña o un
  valor de COD que no es `true`/`false` siguen dando la página vacía. Capacidad, forma `Order`, RLS y orden sin cambios;
  FINANCE solo puede acotar con fechas su lista de cobros pendientes. Entrada `ORD-LIST-DATE-FILTER` en
  `x-pilot-contract-deltas`; rutas y esquemas siguen en 68 y 124, referencias internas 646.
- Servidor: la lectura de Orders agrega `created_at >= created_from` y `created_at <= created_to` en la misma
  transacción tenant con RLS; los índices existentes `orders_owner_status_idx` y `orders_operator_status_idx` toman el
  rango como condición de índice (EXPLAIN ANALYZE en PostgreSQL 18 con 200 000 órdenes sintéticas, en el ADR). Sin
  migración, tabla, índice, rol ni permiso; AI-04, AI-06 y AI-18 sin cambios; ningún flujo atómico nuevo.
- AI-07: ruta `/ops/orders` (DISPATCHER, PLATFORM_ADMIN y VIEWER) y contrato `order_list`; `work_inbox.not_supported` ya
  no menciona la pantalla faltante. Web: "Órdenes" después de "Bandeja" y antes de "Tablero"; búsqueda por guía exacta
  (`ORD_…`, misma validación que "Buscar guía"), un estado o todos, "Creada desde" y "Creada hasta" en días de Mazatlán
  (primer instante y último microsegundo del día), filas con guía, estado y total en centavos enteros, "Cargar más" con
  el cursor de la misma consulta; la fila abre el detalle existente para DISPATCHER y PLATFORM_ADMIN y no es enlace para
  VIEWER. El detalle `/ops/orders/:id` sigue bajo "Bandeja" para quien la tiene. Sin almacenamiento del navegador ni
  consola; sin IDs internos. VIEWER entra a "Órdenes" después del login y al cambiar a una organización donde su página
  no aplica. La fecha escrita queda como borrador hasta ser un día completo desde 2000-01-01 (mínimo del campo) o hasta
  vaciar el campo; una fecha incompleta dice "Escribe una fecha completa (día, mes y año, desde 2000).". El foco nunca
  cae al cuerpo de la página ("Buscar guía", "Quitar búsqueda", último "Cargar más").
- "En dos pasos": todo lector web de la `Order` de AI-05 (esta lista, "Buscar guía", "Agregar entrega" de Rutas,
  "Cobro contra entrega", "Nueva orden", el detalle y las transiciones fuera de línea del repartidor) acepta un
  `created_at` opcional, estricto en UTC cuando viene; "Órdenes" muestra la columna "Creada" (hora de Mazatlán) solo
  cuando el API lo manda. El API no cambia aquí; lo agrega el PR siguiente. FINANCE sigue sin "Órdenes".
- Despliegue: el API antes que la web o juntos (un API anterior ignoraría las fechas); una web anterior sigue
  funcionando con el API nuevo, cursores incluidos. Rollback: revertir el commit, sin datos ni esquema que deshacer.

## Fase 4 de UI-001, plataforma: decisiones y backlog (PHASE4-PLATFORM-APPROVAL-2026-10-12) — 2026-10-12

- Preguntas y respuestas literales del project owner (2026-10-12), registradas en `decision-log.md`:
  - `PHASE4-PLATFORM-APPROVAL-2026-10-12`: "La fase 3 quedó completa y en main. ¿Sigo con la fase 4 de la propuesta
    (plataforma: plomería compartida, un pool de conexiones por rol, espera adaptativa del outbox, OpenTelemetry,
    límites de tasa y pruebas de punta a punta)?" → "Sí, avanza con la fase 4 (Recommended)" ("Empiezo por
    OpenTelemetry y correlación, y por los límites de tasa en endpoints pesados, que son lo que más ayuda a operar el
    piloto."); no elegidas: "Primero preparar el despliegue" ("Antes de más funciones, preparo contigo el despliegue
    del piloto en Azure con lo que ya está en main.") y "Esperar" ("Me detengo aquí; tú decides después.").
  - `OBS-003-TELEMETRY-APPINSIGHTS-2026-10-12`: "Fase 4, telemetría: ¿a dónde mandamos métricas y trazas? (Hoy solo
    hay logs en Log Analytics.)" → "Application Insights (Recommended)" ("OpenTelemetry con el exportador oficial de
    Azure, en México Central, sobre el mismo Log Analytics con muestreo de 10 %. Aprox. 0–5 USD/mes; el peor caso del
    presupuesto no sube por el tope diario. Admite 7 paquetes nuevos y sube Azure.Core 1.61→1.63 (admisión
    explícita)."); no elegidas: "Agente OTLP de Container Apps" ("Menos paquetes (sin tocar Azure.Core), mismo costo,
    pero hay que verificar que acepte métricas.") y "Solo logs" ("Sin paquetes ni costo: identificador de traza en
    cada log y resúmenes periódicos, pero sin vista de trazas."). La lista exacta de paquetes todavía necesita su
    admisión explícita por paquete (GOV-DEPENDENCY-ADMISSION-001) cuando se prepare la entrada de admisión.
  - `SEC-004-RATE-LIMITS-2026-10-12` (cambio MAJOR de AI-01 §7 al aplicarse; la fila es su registro ADR): "Fase 4,
    límites de tasa: ¿qué endpoints limitamos? (Hoy solo seguimiento público, tiempo real y GPS. Es cambio de contrato:
    cada uno declara 429 en el API.)" → "Lista corta y generosa (Recommended)" ("Por usuario: CSV 10/min (1 importación
    a la vez), exportar liquidación 10/min, crear dirección (cuesta Google) 30/min, fotos 30–60/min, tablero y conteos
    60/min, lista de órdenes 120/min, cotizar 60/min, repartidores asignables 60/min; inicio de sesión 20/min por red.
    Se ajusta tras 2 semanas con métricas."); no elegidas: "Solo caros o anónimos" ("CSV, exportar, crear dirección e
    inicio de sesión.") y "Medir primero" ("Dos semanas solo midiendo (sin nuevos 429) y luego fijamos números.").
  - `OBS-003-TENANT-PSEUDONYM-2026-10-12`: "Fase 4, privacidad en trazas: la arquitectura pide marcar cada traza con la
    empresa y el actor, pero la observabilidad del piloto lo prohíbe. ¿Qué hacemos?" → "Seudónimo de empresa
    (Recommended)" ("Un código HMAC con llave en Key Vault (no el id real) solo en trazas y logs, y el actor solo como
    tipo de rol; nunca en métricas ni alertas."); no elegidas: "Id real de la empresa" ("El id de la organización en
    las trazas (no en métricas).") y "Sin empresa" ("Las trazas no llevan empresa; se corrige la arquitectura con un
    ADR.").
- AI-08: ítems nuevos OBS-003 (trazas W3C y correlación, exportación a Application Insights apagada por defecto con
  muestreo de 10 %, lista permitida de atributos de span, seudónimo de organización y tipo de rol del actor), SEC-004
  (plomería compartida de límites de tasa, 429 `application/problem+json` con `Retry-After`, la lista decidida y la
  declaración 429 en AI-05), SCL-007 (un pool de conexiones por rol de base de datos y espera adaptativa del outbox) y
  OPS-005 (pruebas de punta a punta de la plataforma); todos MVP-1, P1, compuerta `owner_technology_operations`.
  `item_count` pasa de 66 a 70; el conjunto MVP-0/P0 no cambia. Quedan como decisiones pendientes del owner en sus
  criterios: la espera máxima del outbox, el contexto de traza a través del outbox (columna, payload o ninguno), una
  alerta de métrica sobre conexiones de PostgreSQL y el correo de alertas que el owner fija al desplegar, el mapeo
  operación por operación de la lista de límites, cómo registra su 429 el tablero (`GET /operations/dashboard`, fuera
  de AI-05 por ADR-OBS-001), el texto de cada pantalla ante un 429 y los recorridos de negocio de la suite de punta a
  punta.
- AI-03 §18: el tenant va como seudónimo HMAC de la organización (llave en Key Vault, nunca el id real) y el actor
  técnico solo como tipo de rol, únicamente en trazas y logs y con contexto de organización; nunca como etiqueta de
  métrica, dimensión de alerta o de libro de trabajo ni dimensión de telemetría. La lista permitida de OBS-002
  (`TelemetryDimensions.Allowed`) no los incluye.
- AI-13 §10: enmienda mínima de "enrichment tenant/correlation sin PII" con la misma regla, porque se leía como el id
  real de la organización.
- Secuencia de diez PR en `docs/development/phase4-platform-plan.md`. Sin cambios en AI-02, AI-04, AI-05, AI-06, AI-07,
  AI-10, AI-12, AI-15, AI-18, AI-24, código, paquetes, plantillas, roles, migraciones ni flujos cross-module.

## Rutas: el repartidor (DRV-) en cada fila de la lista (UI-PHASE3-ROUTES-ROW-DRIVER-2026-10-12) — 2026-10-12

- Pregunta y respuesta literales del project owner (2026-10-12), registradas en `decision-log.md` como cambio MAJOR
  (AI-01 §7): "En la lista de Rutas, varias rutas del mismo día se ven iguales ("Ruta del 12 de octubre de 2026") sin
  decir de qué repartidor son. ¿Agrego el DRV- a cada fila?" → "Sí, DRV- en cada fila (Recommended)" ("Cada ruta de la
  lista muestra "Ruta del 12 de octubre de 2026 · DRV-1a2b3c4d". Solo pantalla, sin cambiar el API."); no elegida: "No
  por ahora" ("La lista queda como está; el repartidor se ve al abrir la ruta.").
- AI-07 `route_planner.list` (`decision_amendment`, `display` y `driver`), `sources.drivers` y `decision_amendments`;
  la ruta `/ops/routes` suma la decisión. Cada fila dice "Ruta del 12 de octubre de 2026 · DRV-1a2b3c4d" con la
  referencia de la misma lectura de `listOrganizationDrivers` que ya hacen "Crear ruta" y el encabezado del detalle
  (cualquier estado; ninguna lectura más). Un repartidor que esa lectura no trae, o una lectura fallida, es "Repartidor
  no disponible", como en el detalle; mientras se lee, la fila muestra solo el nombre por fecha; nunca el id. Se
  conservan el orden del servidor, los nombres por fecha, el estado, las paradas y el costo total; el nombre accesible
  de cada fila sigue siendo su texto.
- Sin cambios en AI-04, AI-05, AI-06, AI-18, backend, roles, capacidades, migraciones ni flujos cross-module.

## Roles propios en la organización activa: `GET /me/organization-roles` (ORG-MY-ROLES-2026-10-12) — 2026-10-12

- Preguntas y respuestas literales del project owner (2026-10-12), registradas en `decision-log.md` como cambio MAJOR
  del contrato de la API (AI-01 §7). Primera: ""Registrar desde Despacho" no se puede cumplir sin tocar el servidor: el
  API solo dice un rol por empresa, así que la app del repartidor no sabe que esa persona también es despacho. ¿Qué
  hacemos?" → "Agregar los roles al API (Recommended)" ("Cambio pequeño y aditivo: el contexto de cada empresa trae
  también la lista de roles de la persona (con ADR). La app no le ofrece el reporte desde el inicio y no se sube ninguna
  foto."); no elegida: "Aprender tras el primer rechazo". Segunda, tras revisar esa primera versión (un miembro `roles`
  en `OrganizationContext`, nunca fusionado ni desplegado): "Agregar "roles" dentro del contexto de cada empresa rompe
  el inicio de sesión de la web actual (la de hoy rechaza campos que no conoce) si el API se publica antes que la web, y
  deja fallando las pestañas ya abiertas hasta recargar. ¿Cómo lo hacemos?" → "Endpoint nuevo de roles (Recommended)"
  ("Los roles salen de una consulta nueva del API (solo los de la persona en su empresa activa). La web actual nunca la
  llama, así que nada se rompe en ningún orden de despliegue; mismo resultado en la app del repartidor."); no elegida:
  "Campo en el contexto y orden de despliegue".
- AI-05: `OrganizationContext` y `listOrganizationContexts` quedan como estaban (cuatro miembros; el parser estricto de
  la web no cambia). Nueva operación `getMyOrganizationRoles` (`GET /me/organization-roles`, etiqueta Identity, con
  `X-Organization-Id`) y esquema `MyOrganizationRoles`: el objeto cerrado `{"roles":[...]}` con los roles propios de
  quien pregunta en la organización elegida (membresía ACTIVE de una organización ACTIVE; el mismo enum de roles, sin
  repetidos, en orden ordinal, minItems 1); 200, 401, 403 (encabezado ausente, mal formado o ajeno, o sin esa membresía)
  y 503, como `getMyDriverPhone`; `Cache-Control: no-store`. `x-capability-matrix` agrega `own_membership_operations`
  (todos los roles miembros, sin MFA: no dice nada más allá de las propias membresías) y `x-pilot-contract-deltas` la
  entrada `ORG-MY-ROLES`.
- Servidor: `OrganizationEndpoints` la mapea con `ActiveOrganizationMember` y el contexto tenant (403); la capacidad
  `getMyOrganizationRoles` se revisa antes de leer; `PostgreSqlMyOrganizationRolesReader` lee solo las membresías
  propias dentro de una transacción tenant explícita con RLS como `paqueteria_app` y nunca escribe. Nada de otras
  personas, sin PII, sin migración, tabla, rol, permiso ni flujo atómico nuevo; AI-04, AI-06 y AI-18 sin cambios.
- PWA del repartidor: pide la consulta al instalar la sesión y en cada sesión nueva (otro inicio de sesión u otra
  organización); usa al instante la respuesta guardada para la partición y la reemplaza con cada respuesta correcta.
  La respuesta se guarda en el IndexedDB particionado existente (store `organization_roles` de la versión 4, que
  ningún teléfono ha abierto todavía, así que no sube de versión): la llave de partición, el esquema y los códigos de
  rol, nada más. Sale del teléfono con la partición ante 401 o 403 y con todas las instantáneas al cerrar sesión;
  cualquier otra falla (sin conexión, 5xx, 404 de una API anterior, respuesta fuera de contrato) conserva la guardada
  y la consulta se repite al volver la conexión o al volver a verse la app (en línea, una a la vez, hasta 5 veces por
  sesión, nunca después de 401 o 403). Las reglas no cambian: con DISPATCHER o PLATFORM_ADMIN la parada dice que se
  registre desde Despacho; DRIVER sin ellos reporta, también sin conexión; mientras la primera consulta va en camino
  el reporte todavía no se ofrece y no se dice nada del rol; sin respuesta conocida ni consulta en camino el reporte
  no se ofrece (falla cerrado) con "No pudimos confirmar tu rol. Vuelve a abrir la app con conexión para reportar."
  Ningún aviso de rol pide cerrar sesión, que borraría lo que el teléfono guarda sin conexión.
- AI-07 `driver_stop`: `failed_delivery.dispatch_role`, `role_loading` y `role_unknown` citan esta consulta;
  `offline_cache` y `details.retention` agregan la respuesta guardada.
- Despliegue: no hace falta ningún orden. La web anterior nunca llama la consulta nueva, y esta web con una API anterior
  (404) solo deja de ofrecer "No se pudo entregar"; el inicio de sesión, las paradas y todo lo demás siguen igual.
  Rollback: revertir la web o la API, en cualquier orden, sin datos ni esquema que deshacer.

## Repartidor: "No se pudo entregar" con motivo y foto, también sin conexión (UI-PHASE3-DRIVER-FAILED-DELIVERY-2026-10-12) — 2026-10-12

- Respuestas literales del project owner, registradas con sus preguntas en `decision-log.md` como cambio MAJOR
  (AI-01 §7): fase 3 aprobada con "avanza con la fase 3" y la pantalla de entrega del diseño ("Cómo llegar",
  "Llamar", "Llegué al destino", "Foto de entrega", "Cobro $…", "Confirmar entrega", "Vista previa de la foto ·
  Repetir", "Usar esta foto", "No se pudo entregar"; "¿Qué pasó?" con "El despacho decidirá si se reprograma o
  regresa", "No había nadie", "Dirección incorrecta", "Rechazó el paquete", "No pagó el cobro", "Otro…", "Foto del
  lugar (obligatoria)" y "Enviar reporte"); el 2026-10-11 "Despacho decide (Recommended)", "Lista cerrada
  (Recommended)" y "Según el motivo (Recommended)" (INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11); el 2026-10-12
  "Solo si hay cobro (Recommended)" (""No pagó el cobro" aparece solo cuando la parada tiene un cobro COD; en las demás
  se ven los otros motivos y "Otro…".") y "No, llama a despacho (Recommended)" ("Con un cobro guardado en el teléfono o
  ya registrado, "No se pudo entregar" no aparece. Si fue un error, descarta el cobro guardado; si ya se registró, la
  app le pide llamar a despacho, que decide y coordina la devolución del dinero."); y, tras la revisión del PR,
  "Registrar desde Despacho (Recommended)" ("La app no le ofrece "No se pudo entregar" y le explica que registre el
  intento desde la orden en Despacho, donde elige motivo y siguiente paso. Sin cambiar el servidor.") y "Después, con
  ADR (Recommended)" ("Lo registro como riesgo y pendiente (un tipo de foto propio para intento fallido, o que la
  entrega cite su foto). En el uso normal de la app la entrega siempre sube su propia foto, así que no bloquea el
  piloto.").
- AI-07 `/driver/stops/:id` agrega la decisión y `screen_contracts.driver_stop.failed_delivery`: en una parada de
  entrega en DELIVERING (confirmado o por sincronizar), "No se pudo entregar" abre "¿Qué pasó?" (renglones de 44 px;
  "No pagó el cobro" solo si `DriverStop.cod` no es null; "Otro…" muestra "Acceso restringido", "Paquete dañado" y
  "Riesgo de seguridad"), "Foto del lugar (obligatoria)" con la misma vista previa de la entrega y "Enviar reporte",
  deshabilitado hasta tener motivo y foto, sin texto libre ni confirmación. `cod_collection.not_paid` apunta a este
  flujo y `cod_collection.door_only` dice que un cobro solo se registra con la parada en DELIVERING.
- PWA: una operación cerrada FAILED_DELIVERY corre la secuencia de AI-05 (sesión DELIVERY_PHOTO, PUT, prueba,
  `openIncident` sin severity, description ni next_action, `transitionOrder` a FAILED_ATTEMPT con
  `metadata.incident_id`), con una Idempotency-Key por petición al API; guarda la foto, las llaves y su avance, así
  que un reintento o una pestaña nueva siguen en el paso correcto y nunca abren otra incidencia. Operaciones esquema 3,
  IndexedDB versión 4 (las operaciones en cola se migran sin perderse; una app anterior no puede abrir la base) y caché
  del Service Worker v7. Pendiente se ve como pendiente ("Reporte guardado en el teléfono. Se enviará cuando haya
  conexión."); confirmado se ve "Intento fallido · El despacho decide".
- Cobro: con un cobro guardado en el teléfono el reporte no aparece y la parada pide descartarlo primero (con la
  confirmación de siempre); con el cobro registrado pide llamar a despacho, sin mostrar teléfono. La sincronización
  retiene el reporte si aparece un cobro de la orden (antes de la sesión de carga, antes de la incidencia y antes de la
  transición) y nunca registra un cobro fuera de la puerta: la incidencia nunca se pide con un cobro guardado o
  registrado; si el cobro se registra después de abrirla, la transición no se manda y la incidencia queda para
  despacho. Solo en la PWA: el servidor sigue aceptando el reporte del despacho con cobro, a propósito, porque despacho
  decide.
- Revisión del PR: antes de su incidencia, un reporte solo se manda si REST muestra la parada en DELIVERING en la
  versión exacta en que se hizo (antes de la sesión y antes de la incidencia); un intento fallido que registró despacho
  nunca lo confirma ("Despacho ya registró el intento fallido de esta entrega, así que tu reporte no se envió.
  Descártalo."); REST confirma un reporte solo por su transición aceptada (cualquier versión posterior) o por su propia
  incidencia; sin la parada en la respuesta de REST no se manda ni reporte ni cobro, y lo aceptado que REST nunca
  muestra sale a las 72 horas; un reporte nunca se rehace para otra versión; lo que queda de una parada que despacho
  movió siempre se puede descartar; un reporte con incidencia abierta (o posiblemente abierta) lo dice y nunca invita a
  reportarlo de nuevo, también al vencer; "Enviar reporte" muestra el motivo real de un rechazo; quien también es
  despacho ve el aviso de registrar desde Despacho y el 409 `INVALID_REQUEST` de la incidencia ya no habla del reloj;
  una actualización de la base que bloquea otra pestaña pide cerrarla y cierra la conexión tardía.
- Riesgos residuales: la "Foto del lugar" que queda sin incidencia podría contar para DELIVERED (pendiente de servidor
  con ADR); `DriverStop.cod.recorded` es falso con un cobro DISPUTED o REVERSED; un cobro registrado entre la última
  revisión y una petición no se puede impedir desde el teléfono.
- Segunda y tercera revisión: el teléfono sabe que la persona también es despacho por su propia consulta de roles
  (`GET /me/organization-roles`, ORG-MY-ROLES-2026-10-12, arriba), guardada por partición para que funcione sin
  conexión; sin respuesta conocida el reporte no se ofrece (falla cerrado) y la parada lo explica; un reporte que REST
  ya confirma ya no impide un reporte nuevo de la misma parada; y una acción que el servidor ya aceptó sigue esperando a
  REST cuando falla la lectura de confirmación, así que nunca vence como no enviada.
- Sin cambios en AI-04, AI-06, AI-18, roles, permisos ni migraciones, y sin un flujo atómico nuevo; el cambio de AI-05
  y del servidor que necesita la respuesta 3 es ORG-MY-ROLES-2026-10-12, sin orden de despliegue.
  Rollback: no es un revert simple (la app anterior abre IndexedDB 3 y el teléfono actualizado la rechaza); es una
  versión que deja de ofrecer "No se pudo entregar" con IndexedDB 4, o una con IndexedDB 5 que reescribe la cola al
  esquema 2 y descarta los reportes en cola (ver la nota de desarrollo).

## Tablero y bandeja: la otra parte de la orden sin nombre en lugar de 503 (OBS-001-OPERATED-OWNER-NAME-2026-10-12) — 2026-10-12

- Instrucción literal del project owner (2026-10-11), registrada en `decision-log.md` como cambio MAJOR (AI-01 §7):
  "Pick the minimal option consistent with `owner_org_id` ≠ `operator_org_id` semantics (CLAUDE.md invariant 2), and
  document the choice.", entre "Return no owner name, or a neutral label, for orders the reader only operates." y "Add
  a narrow SECURITY DEFINER lookup that returns only the display name of owners that have an operator relationship
  with the caller." Se eligió la primera, para las dos partes de la orden.
- Defecto preexistente (riesgo residual del PR #221): AI-06 `organizations_tenant` oculta al operador la fila de la
  organización dueña, el read model OBS-001 leía su nombre `NULL` y respondía 503 para toda la página (cada cola de la
  bandeja y el Tablero), aunque `getOperationsQueueCounts` contaba esas órdenes. La dueña que lee una orden que opera
  otra organización fallaba igual con el nombre del operador.
- Read model `GET /api/v1/operations/dashboard` (ADR-OBS-001, fuera de AI-05): `owner` y `operator` conservan
  `{organization_id, display_name}`; `display_name` es el nombre propio de la organización seleccionada y `null` para
  la otra parte. Falla cerrado (503) una fila que no posee ni opera, su propio nombre ausente o un nombre que RLS debía
  ocultar. Cliente, zona y demás filas de la dueña siguen ausentes para el operador; misma transacción RLS, mismos dos
  comandos, mismos lectores y mismo 403.
- PWA: el parser acepta `display_name: null` y exige una parte con nombre por orden; la tarjeta del Tablero y el
  detalle muestran "Otra organización" (nunca el id); los filtros Dueño/Opera solo ofrecen organizaciones con nombre.
- AI-07 `dispatch_dashboard`: `decision_amendments` y `organization_names`.
- Sin cambios en AI-04, AI-05, AI-06, AI-18, migraciones, roles, grants, funciones SECURITY DEFINER, capacidades ni
  flujos cross-module.

## Rutas: errores por motivo con "Verificar identidad", fechas legibles, todas las páginas, "Agregar entrega" por guía y detalle sin IDs (UI-PHASE3-ROUTES-NO-IDS-2026-10-12) — 2026-10-12

- Pregunta y respuesta literales del project owner (2026-10-12), registradas en `decision-log.md` como cambio MAJOR
  (AI-01 §7): "En la pantalla Rutas quedaron detalles fuera de lo que pedí: si falla "Crear ruta" dice "La ruta cambió
  mientras la editabas…" aunque el motivo sea otro, no ofrece verificar identidad (MFA), la lista muestra fechas como
  2026-10-12 y solo la primera página, "Agregar entrega" pide pegar el ID de la orden y el detalle muestra IDs cortos.
  ¿Los corrijo?" → "Sí, todo (Recommended)" ("Mensaje por motivo y verificar identidad, fechas legibles y todas las
  páginas, "Agregar entrega" por número de guía (PQ-…), y el detalle muestra fecha, DRV- y guía en lugar de IDs.").
  "PQ-…" venía del mockup: la guía real es el `public_id` de la orden (`ORD_` y 22 caracteres, el patrón de
  `listOrders.public_id`), el mismo valor de "Buscar guía (ORD_…)" de la barra superior.
- AI-07 `route_planner`:
  - `errors`: cada cambio (`createRoute`, `addRouteStop`, `removeRouteStop`, `reorderRouteStops`) dice su motivo por el
    código del problema; solo VERSION_CONFLICT dice que la ruta cambió y la vuelve a leer; sesión, rol y MFA con los
    textos compartidos y "Verificar identidad" de regreso a `/ops/routes` con la ruta mostrada; sin respuesta válida
    nunca dice que se guardó. La Idempotency-Key de cada cambio sigue siendo nueva.
  - `list`: todas las páginas de `listRoutes` en el orden del servidor (máximo 10, con aviso; un cursor repetido hace
    fallar la lectura) y cada ruta como "Ruta del 12 de octubre de 2026" o "Ruta sin fecha".
  - `add_order`: "Guía (ORD_…)" con la validación y el cliente de `listOrders?public_id=` de la barra superior (ninguna:
    "No encontramos esa guía"; más de una u otra guía fallan cerrado) y luego `addRouteStop`; `?orderId=` llena la guía
    leída con `getOrder` o deja el campo vacío.
  - `detail`: nombre por fecha, "Repartidor DRV-…" de `listOrganizationDrivers` ("Repartidor no disponible" si no
    aparece), estado y paradas; cada parada con su guía de `getOrder` (máximo 100, cancelable, en memoria) o "Guía no
    disponible"; las etiquetas accesibles usan la guía o la posición.
  - Fuentes nuevas `order_lookup` (`listOrders`) y `order_guides` (`getOrder`); `not_supported`: terminar o descartar
    rutas.
- Sin cambios en AI-04, AI-05, AI-06, AI-18, backend, roles, capacidades, migraciones ni flujos cross-module.

## Bandeja: "Crear ruta nueva" por día (UI-PHASE3-ROUTES-DAILY-2026-10-12) — 2026-10-12

- Pregunta y respuesta literales del project owner (2026-10-12), registradas en `decision-log.md` como cambio MAJOR
  (AI-01 §7): "Rutas: el API no tiene forma de terminar ni cancelar una ruta, así que toda ruta se queda en borrador para
  siempre. Por eso "Crear ruta nueva" en la bandeja (solo aparece si el repartidor no tiene ninguna ruta en borrador)
  dejaría de aparecer desde el segundo día. ¿Qué hacemos?" → "Ruta por día (Recommended)" ("Sin cambiar el API: el paso
  ofrece "Crear ruta nueva" cuando el repartidor no tiene ruta en borrador con fecha de hoy (Mazatlán), y las rutas de
  hoy salen primero. Terminar o descartar rutas viejas queda para después del piloto."). No se eligieron "Ruta por día +
  cerrar rutas" ("Lo anterior, más un ADR con operaciones nuevas para "Terminar ruta" (sin paradas pendientes) y
  "Descartar ruta" (borrador sin paradas), para que las viejas salgan de las listas.") ni "Dejar como está".
- AI-07 `work_inbox.bulk_add_to_route` (`route_order`, `no_route`) y `create_route` (`offered_when`, `outcome`,
  `found`): "Crear ruta nueva" se ofrece cuando el repartidor no tiene una ruta en borrador con `scheduled_for` de hoy en
  America/Mazatlan (una ruta sin fecha no cuenta), también debajo de sus rutas anteriores, que se pueden seguir eligiendo;
  las rutas de hoy van primero y llevan "(hoy)"; tras una falla ambigua solo cuenta como encontrada una ruta de hoy. Las
  demás reglas de UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11 no cambian. Rutas conserva el orden del servidor.
  `work_inbox.not_supported` y `route_planner.not_supported`: terminar o descartar rutas viejas, después del piloto.
- Sin cambios en AI-04, AI-05, AI-06, AI-18, backend, roles, capacidades, migraciones ni flujos cross-module.

## Repartidor: datos de la parada, "Cómo llegar" y cobro en la puerta (UI-PHASE3-DRIVER-STOP-DETAILS-2026-10-11) — 2026-10-11

- Respuestas literales del project owner: "avanza con la fase 3" (2026-10-09) y, el 2026-10-11, "Sí, como el diseño
  (Recommended)" ("Solo de sus paradas asignadas. Se borra del teléfono cuando la parada deja de estar asignada o al
  cerrar sesión. Con datos de prueba hasta cerrar GATE-007."), "Google Maps (Recommended)", "Solo confirma el monto
  (Recommended)" ("Toca "Recibí $1,250.00" y el sistema pone la referencia (guía y hora). Si no le pagan, reporta "No
  pagó el cobro"."), "Completo en recolección (Recommended)" ("En recolección se ve el nombre completo del contacto,
  como en tu diseño; en entrega, el nombre corto ("M. López")."), "Completo, como recolección (Recommended)" ("El
  remitente es el mismo contacto de la recolección, así que se ve igual: "Farmacia Las Quintas".") y "Solo el número
  (Recommended)" ("El servidor manda cuántas paradas terminó hoy (hora de Mazatlán), sin datos de esas paradas.");
  registradas, con sus preguntas, en `decision-log.md` como cambio MAJOR (AI-01 §7).
- AI-05 listMyStops responde `DriverStopList` (`stops` y `completed_today`). Cada `DriverStop` agrega
  `details_available`, `contact_short_name`, `contact_full_name`, `zone_name`, `service_window`, `cod` (`expected` y
  `recorded`) y `navigation` (latitud, longitud, dirección y referencias), solo para las asignaciones ACCEPTED o ACTIVE
  del repartidor que consulta. El servidor descifra la dirección y el contacto con el sobre ADP-001, en un lote, después
  de cerrar la transacción del tenant y a través del puerto compartido `ILocationPiiReader` (Paqueteria.Application),
  que Locations implementa con su mismo modo de `Locations:PiiProtector`; Dispatch no referencia el módulo Locations.
  Si los datos de una parada no se pueden leer, la parada llega con `details_available` en false y sin nombre ni
  navegación; la respuesta nunca falla ni es parcial por eso. `details=false` devuelve las mismas paradas sin leer
  ningún dato protegido (otro valor, otro parámetro o uno repetido: 409 INVALID_REQUEST). Toda respuesta lleva
  Cache-Control no-store.
  Contacto: en recolección y devolución, el nombre completo (`contact_full_name`); en entrega, solo el nombre corto
  (`contact_short_name`: inicial de la primera palabra, punto y última palabra, "M. Pérez"). Los caracteres de formato
  cuentan como espacio, un texto vacío es null y `zone_name` se recorta a 200 caracteres. `cod.recorded` es la misma
  condición de la guarda de DELIVERED. `completed_today` cuenta los cambios a PICKED_UP, DELIVERED o RETURNED que hizo
  el repartidor que consulta en órdenes asignadas a él, durante el día de hoy en America/Mazatlan; solo el número.
- AI-07 `/driver/stops/:id` agrega las acciones `navigate` y `cod_collection`; `screen_contracts.driver_stop` agrega
  `details` (lista y detalle del diseño con el encabezado "Hoy · N paradas · K hechas", ventana en hora de Mazatlán,
  "Datos de entrega no disponibles por ahora" cuando no hay datos, lecturas de confirmación sin datos protegidos que no
  borran los guardados; retención: solo la instantánea vigente, cerrar sesión borra las instantáneas del teléfono y
  401/403 la de la sesión), `navigation` (Google Maps por coordenadas en una pestaña nueva, sin solicitud al servidor ni
  cálculo de rutas, GATE-003) y `cod_collection` ("Recibí $X" con confirmación; referencia con la guía y la hora local
  del teléfono; una Idempotency-Key por cobro; la cola sigue el orden de dependencia, así que el cobro siempre va antes
  de la entrega; una entrega sin cobro guardado espera el cobro y "Recibí" se ofrece de nuevo). `offline_cache` dice
  que el teléfono guarda el nombre del contacto y la dirección de las paradas asignadas en este momento.
- PWA: IndexedDB versión 3 (se descartan las instantáneas anteriores y las operaciones en cola se migran sin perderse) y
  caché del Service Worker v6. Una parada que no cumple el contrato se deja fuera (o sin sus datos) en lugar de
  rechazar la lista; cerrar sesión durante una sincronización nunca descarta una acción en cola. Un rollback debe
  conservar la versión 3 o superior de IndexedDB.
- Pendientes: "No pagó el cobro" (intento fallido). Datos de prueba hasta cerrar GATE-007. Sin cambios en AI-04, AI-06,
  AI-18, roles, permisos ni migraciones, y sin un flujo atómico nuevo.

## Bandeja y Rutas: "Crear ruta nueva" dentro del paso y selectores en "Crear ruta" (UI-PHASE3-INBOX-CREATE-ROUTE-2026-10-11) — 2026-10-11

- Pregunta y respuesta literales del project owner (2026-10-11), registradas en `decision-log.md` como cambio MAJOR
  (AI-01 §7): "Bandeja, "Agregar estas órdenes a una ruta…": si el repartidor no tiene una ruta en borrador, hoy hay
  que ir a Rutas a crearla. ¿Agregamos "Crear ruta nueva" dentro de ese mismo paso?" → "Sí, en el paso (Recommended)"
  ("Crea la ruta en borrador para ese repartidor, con la ciudad de las órdenes y la fecha de hoy (hora de Mazatlán), y
  sigue a la vista previa."). La otra opción, "Solo en Rutas" ("El paso sigue mandando a Rutas para crearla; ahí cambio
  los IDs por selectores de repartidor y ciudad."), no se eligió. Después se le dijo al owner que el mismo cambio
  sustituye también los campos de ID internos del formulario "Crear ruta" de Rutas por selectores, y no objetó; la
  propuesta UX aprobada (UI-001) nombra "Las rutas piden el UUID del repartidor" como problema por corregir. La fase 3
  se aprobó con "avanza con la fase 3".
- AI-07 `work_inbox.bulk_add_to_route.create_route`: "Crear ruta nueva" aparece solo donde el paso dice que el
  repartidor no tiene rutas en borrador. Cada orden del lote se lee con `getOrder`, una tras otra, para su `city_id`:
  una sola ciudad ofrece el botón (nombrada con `listCities`, sin su id); órdenes de ciudades distintas no lo ofrecen y
  el paso explica que una ruta cubre una sola ciudad y que se crean en Rutas; una orden que no se puede leer deja el
  mensaje de no disponible. `createRoute` va con el repartidor del lote, la ciudad de las órdenes,
  `service_area_id` nulo y la fecha de hoy en America/Mazatlan, con una Idempotency-Key que solo se reutiliza para
  reenviar la misma solicitud tras una falla de red o del servidor; el botón espera mientras se envía. La ruta creada
  queda elegida y el paso sigue a la misma vista previa, confirmación y `addRouteStop`. Las negativas (sesión, rol,
  MFA con "Verificar identidad", 404 uniforme, DRIVER_INELIGIBLE, INVALID_REQUEST) dicen en español que no se creó la
  ruta; ante una falla que no confirma si se creó, primero se vuelven a listar las rutas en borrador para encontrarla
  en vez de duplicarla. `work_inbox.not_supported` deja solo los lotes con órdenes de ciudades distintas.
- AI-07 `route_planner.create_form` (`/ops/routes`): "Crear ruta" ya no pide pegar IDs. Repartidor es un selector de
  `listOrganizationDrivers` (todas las páginas, solo ACTIVE, `DRV-` con vehículo y estado), Ciudad uno de
  `listCities` (nombre y estado) y Área de servicio uno de `listServiceAreas` (solo ACTIVE, con "Toda la ciudad");
  cada lista tiene estados de carga, vacío y error, y el envío espera repartidor y ciudad. El resto de Rutas no cambia.
- Sin cambios en AI-04, AI-05, AI-06, AI-18, backend, roles, capacidades, migraciones ni flujos cross-module.

## Bandeja: "Agregar estas órdenes a una ruta…" al terminar una asignación en lote (UI-PHASE3-INBOX-BULK-ADD-TO-ROUTE-2026-10-11) — 2026-10-11

- Respuesta literal del project owner, registrada con su pregunta en `decision-log.md` como cambio MAJOR (AI-01 §7):
  "Al terminar el lote (Recommended)" ("Después de "Asignar seleccionadas…" aparece "Agregar estas órdenes a una
  ruta…" con las mismas órdenes y el mismo repartidor, con vista previa."). La fase 3 se aprobó con "avanza con la
  fase 3".
- AI-07 `work_inbox.bulk_add_to_route`: cuando un lote de "Asignar seleccionadas…" termina con al menos una orden
  asignada, el panel ofrece "Agregar estas órdenes a una ruta…" solo con las órdenes que ese lote asignó y con su
  repartidor. Solo se eligen rutas en borrador de ese repartidor (`listRoutes` con `status=DRAFT` y `driver_id`),
  nombradas por fecha y paradas actuales, sin identificadores ni montos. La vista previa muestra la ruta, el
  repartidor (`DRV-`) y las órdenes en el orden en que se agregan; la confirmación nombra la ruta, el repartidor y el
  número de órdenes.
- `addRouteStop` se envía una orden tras otra, cada una con su Idempotency-Key reutilizada al reenviar, con el
  `expected_version` recién leído y después el de cada respuesta. Un VERSION_CONFLICT detiene el envío, vuelve a leer
  la ruta y pide confirmar de nuevo. DRIVER_INELIGIBLE e INVALID_REQUEST responden solo por su orden. Un CONFLICT
  genérico o el 404 también, pero el servidor los usa además para una ruta que dejó el borrador, así que antes de la
  siguiente orden `getRoute` debe mostrar la misma ruta en borrador y la misma versión; si no, el envío se detiene como
  con VERSION_CONFLICT. Un 403 MFA_REQUIRED ofrece "Verificar identidad". Resultado por fila con mensaje en español,
  resumen sin ocultar fallas parciales y nueva lectura de la cola y los conteos. Es una secuencia de una operación
  existente, no un flujo atómico nuevo de AI-13 §4.
- La bandeja no crea rutas: si el repartidor no tiene una en borrador, la pantalla indica crearla en Rutas. Sin
  cambios en AI-04, AI-05, AI-06, AI-18, backend, roles, capacidades ni migraciones.

## Llamadas enmascaradas: confirmar con una tecla (VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11) — 2026-10-11

- Pregunta y respuesta literales del project owner: "Llamadas enmascaradas: si contesta un buzón de voz, el saludo del
  buzón puede decir el número del repartidor o del destinatario, y eso rompe el enmascarado. ¿Cómo lo evitamos?" →
  "Confirmar con una tecla (Recommended)" ("Al repartidor le dice "Presiona 1 para llamar al destinatario". Al
  destinatario: "Tiene una llamada sobre su envío; presione 1 para contestar" (texto neutro, sin marca). Si nadie
  presiona, se cuelga sin enlazar. Gratis y no falla con buzones."); registrada como
  `VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11` en `decision-log.md` y en `docs/adr/ADR-VOICE-001-MASKED-CALL-BRIDGE.md`.
- AI-05: webhooks firmados `confirmTwilioDriverKeypress`, `screenTwilioRecipient` y `confirmTwilioRecipientKeypress`
  (esquemas `TwilioKeypressForm` y `TwilioRecipientKeypressForm`); `RecipientCallAvailability.retry_after_seconds`;
  descripciones de `requestRecipientCall` (tecla en ambos tramos, cualquier 5xx es ambiguo, reserva propia tras un
  commit, solicitudes abandonadas), `receiveTwilioCallStatus` (nunca promueve una solicitud `FAILED`) y del celular del
  repartidor (consultar y borrar con cualquier estado del perfil; registrar solo con perfil `ACTIVE`); entrada
  `VOICE-001-VOICEMAIL-KEYPRESS` en `x-pilot-contract-deltas`.
- AI-07: `driver_stop.recipient_call` (botón deshabilitado sin conexión solo tras confirmar la disponibilidad,
  respuestas tardías descartadas, una nueva consulta al terminar la espera) y `driver_account` (borrar el celular con
  cualquier estado del perfil).
- AI-08: VOICE-001 agrega la decisión, criterios de aceptación (tecla, 5xx ambiguo, solicitudes abandonadas, ARCO) y
  pruebas de las teclas.
- AI-24 `voice_bridge`: `call`, `key_presses`, `resilience`, `request`, `webhooks`, `audit` y `erasure`.
- Sin cambios en AI-06, AI-18 ni AI-12; la columna `bridge_stage` entra en la migración del lane Drivers
  `20261011000100_AddDriverVoiceBridge`, que aún no se fusiona. No es un sexto flujo de AI-13 §4.

## Llamadas enmascaradas repartidor–destinatario con Twilio (VOICE-001) — 2026-10-11

- Preguntas y respuestas literales del project owner: "App del repartidor: hoy no ve el teléfono del destinatario.
  ¿Debe poder llamarle desde la app?" → "Sí, con número enmascarado"; "Número enmascarado: … ¿Cómo seguimos?" →
  "Investiga y propón (Recommended)"; "Número enmascarado: comparé proveedores. Azure Communication Services queda
  descartado: no vende números mexicanos para llamar y Microsoft lo va a retirar. ¿Con cuál seguimos?" → "Twilio
  (Recommended)". Registradas como `VOICE-001-MASKED-CALLS-2026-10-11` y `VOICE-001-PROVIDER-TWILIO-2026-10-11` (ADR:
  Twilio como nuevo encargado de datos personales) en `decision-log.md` y en
  `docs/adr/ADR-VOICE-001-MASKED-CALL-BRIDGE.md`.
- AI-05: `getMyDriverPhone`, `registerMyDriverPhone`, `removeMyDriverPhone` (`/driver/me/phone`),
  `getRecipientCallAvailability` y `requestRecipientCall` (`/driver/me/stops/{orderId}/recipient-call`, solo DRIVER,
  `x-capability-matrix.voice_call_operations`, 404 uniforme, Idempotency-Key, 429, sin números en petición ni
  respuesta) y los webhooks firmados `receiveTwilioCallStatus` y `answerTwilioInboundCall` (etiqueta Voice); entrada
  `VOICE-001-MASKED-CALLS` en `x-pilot-contract-deltas`.
- AI-07: `driver_stop.recipient_call` ("Llamar al destinatario" solo si el servidor lo permite; acción solo en línea,
  nunca en la cola offline; "masked phone or relay") y `driver_account` (celular propio con consentimiento explícito).
- AI-08: ítem VOICE-001 (MVP-1, P1, compuerta GATE-007; el piloto no usa números reales antes de resolver GATE-007).
- AI-24 `voice_bridge`; AI-03 §15 añade `IVoiceBridgeProvider`; AI-04 `privacy_invariants` añade la regla de los
  teléfonos de la llamada puente.
- Sin cambios en AI-06, AI-18 ni AI-12: el lane Drivers `20261011000100_AddDriverVoiceBridge` agrega las columnas del
  celular del repartidor y la tabla `drivers.recipient_call_requests`. No es un sexto flujo de AI-13 §4.

## Repartidores del despacho y selector de repartidor en liquidaciones (UI-PHASE3-DRIVERS-2026-10-11) — 2026-10-11

- Respuestas literales del project owner: "avanza con la fase 3" (2026-10-09) y, el 2026-10-11, "Lista de consulta
  (Recommended)" y "Sí, con referencia DRV- (Recommended)"; registradas con sus preguntas y el texto de cada opción en
  `decision-log.md` como cambio MAJOR (AI-01 §7), cuyo ADR es esa fila.
- AI-05 `listOrganizationDrivers` (`GET /drivers`, esquemas `OrganizationDriver` y `OrganizationDriverPage`): lista por
  cursor los repartidores OWN de la organización activa que no están INACTIVE, con la referencia `DRV-`, vehículo,
  estado, paradas activas (las mismas que el repartidor ve en su app, sin filtro por día) y la hora de su última
  ubicación, nunca la ubicación. Solo DISPATCHER y PLATFORM_ADMIN con MFA, los roles del selector de asignación.
- AI-05 `listSettlementPayees` (`GET /settlement-payees`, esquemas `SettlementPayee` y `SettlementPayeePage`): lista por
  cursor todos los repartidores de la organización que `createSettlement` acepta (cualquier tipo y estado, para pagar
  también a uno suspendido o dado de baja), solo con `DRV-` y vehículo. Solo FINANCE y PLATFORM_ADMIN con MFA, los
  roles de `createSettlement`; Finanzas no ve estado, paradas ni ubicaciones.
- Ambas operaciones en `x-capability-matrix.driver_list_operations`; capacidad antes de leer, 403 uniforme, sin 404,
  falla cerrada con 503 y no leen nombre, correo, teléfono ni documentos. Solo lectura, sin migraciones, tablas,
  índices, roles, grants ni flujos nuevos; AI-06 y AI-18 sin cambios.
- AI-07: ruta `/ops/drivers` con el contrato `driver_roster` ("Repartidores" después de "Importar CSV", sin acciones) y
  contrato `settlement_desk` de `/finance/settlements`: el repartidor se elige de la lista, el periodo es "Semana
  pasada", "Quincena pasada" (hora de Mazatlán) o "Fechas a mano" con las fechas explicadas ("1–7 sep 2026"), y cada
  liquidación nombra a su repartidor por `DRV-`.
- Libreta de direcciones de "Nueva orden" (UI-PHASE3-ADDRESS-BOOK-MVP2-2026-10-11): respuesta literal "Después del
  piloto (Recommended)" ("Se queda en MVP-2, como dice el backlog."); sigue en BUS-001 (MVP-2) y el asistente del piloto
  sigue capturando las direcciones. Sin cambios de contrato ni de código.

## Bandeja: "Precio por revisar" solo con órdenes activas y "Asignar seleccionadas…" con costo por orden (UI-PHASE3-INBOX-PRICE-REVIEW-BULK-2026-10-11) — 2026-10-11

- Respuestas literales del project owner, registradas con sus preguntas en `decision-log.md` como cambio MAJOR
  (AI-01 §7): "Solo órdenes activas (Recommended)" ("Lista y conteo solo de órdenes que no han terminado; al terminar,
  salen solas. Nadie tiene que marcar nada.") y "Costo por orden (Recommended)" ("La vista previa muestra cada orden
  con su costo para capturar; cuando exista el tabulador, se llena solo."). La fase 3 se aprobó con "avanza con la
  fase 3".
- AI-05 `getOperationsQueueCounts`: `queues.price_review` cuenta solo las órdenes con `cost_warning` que no han
  terminado, es decir, en cualquier estado salvo DELIVERED, RETURNED, CLAIM_RESOLVED, CLOSED y CANCELLED (el grupo
  "Terminadas" de UI-STATUS-GROUPS-2026-10-05). La terminalidad de AI-04, el plazo de reclamación y LIF-001 no cambian;
  una orden en CLAIM_OPEN vuelve a estar activa.
- El read model OBS-001 (`GET /api/v1/operations/dashboard`, ADR-OBS-001, fuera de AI-05) agrega el filtro booleano
  `price_review` con la misma regla, dentro de la misma consulta con RLS y con el mismo cursor. La pestaña "Precio por
  revisar" de la bandeja es una lista del servidor y ya no lleva la nota de "solo conteo".
- AI-07 `work_inbox.bulk_assignment` ("Asignar seleccionadas…"): casillas solo en las filas con `unassigned_alert`,
  "Seleccionar todas" y un máximo de 20 órdenes por lote; solo se ofrecen los repartidores elegibles para todas las
  órdenes, y los demás aparecen con las órdenes que los bloquean. La vista previa pide el costo de cada orden en
  pesos, vacío y obligatorio, y lo convierte a centavos enteros; nada se llena solo mientras no exista el tabulador.
  La confirmación nombra al repartidor, el número de órdenes y la suma de los costos. `assignDriver` se envía una
  orden tras otra, cada una con su Idempotency-Key, con resultado por fila y nueva lectura de la cola y los conteos.
  Es una secuencia de operaciones existentes, no un flujo atómico nuevo de AI-13 §4.
- Pendiente: "Agregar a ruta…" en lote (las rutas solo aceptan órdenes ya asignadas al repartidor de la ruta y ninguna
  cola de la bandeja las lista). Sin cambios en AI-04, AI-06, AI-18, roles, capacidades ni migraciones.

## El despacho decide el siguiente paso de un intento fallido reportado por el repartidor (INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11) — 2026-10-11

- Respuestas literales del project owner: "avanza con la fase 3" (2026-10-09) y, el 2026-10-11, "Despacho decide
  (Recommended)", "Lista cerrada (Recommended)" y "Según el motivo (Recommended)"; registradas con sus preguntas y el
  texto de cada opción en `decision-log.md` como cambio MAJOR (AI-01 §7), cuyo ADR es esa fila.
- AI-05 `openIncident` y `OpenIncidentRequest` (`x-reporter-shape`): el despacho (DISPATCHER, o PLATFORM_ADMIN con MFA)
  reporta como hoy, con severidad, descripción y siguiente acción. El repartidor manda solo tipo, motivo, hora del
  intento y evidencias; si manda severidad, descripción o siguiente acción recibe 409 `INVALID_REQUEST`. El servidor
  fija la severidad por motivo (alta para "Riesgo de seguridad" y "Paquete dañado", media para los demás), guarda
  cifrada una descripción fija armada con el motivo y deja `next_action` en null: decide el despacho. `Incident`
  publica `next_action` null en ese caso.
- Migración del carril Incidents `20261011000100_AllowDispatchDecidedNextAction`: `incidents.incidents.next_action`
  admite NULL. Su reversa restaura NOT NULL y se niega (`INC_DISPATCH_DECISION_DOWNGRADE_BLOCKED`) mientras exista una
  incidencia que deja la decisión al despacho. AI-06 y AI-18 no cambian.
- AI-04: guarda nueva `failed_attempt_dispatch_decides`. Mientras la incidencia del intento fallido no tenga siguiente
  acción, solo DISPATCHER o PLATFORM_ADMIN con MFA sacan la orden de FAILED_ATTEMPT hacia RESCHEDULED ("Reprogramar"),
  DELIVERING ("Reintentar hoy") o RETURNING ("Regresar"); el repartidor recibe 409 `DISPATCH_DECISION_REQUIRED`. Las
  demás guardas (custodia, asignación válida) siguen igual y los reportes del despacho se comportan como antes.
- getOrder `allowed_transitions` lista desde FAILED_ATTEMPT solo las salidas que esa decisión y la custodia permiten.
- AI-07: el detalle de la orden muestra el motivo del intento fallido, si falta la decisión del despacho y el plazo de
  atención; "Siguiente paso" ofrece "Reprogramar", "Reintentar hoy" y "Regresar" solo cuando el servidor los permite;
  la bandeja de incidencias muestra "Lo decide despacho". AI-08 INC-001 precisa el criterio de siguiente acción.
- Pendiente para otra entrega: la pantalla "No se pudo entregar" de la PWA del repartidor y su cola sin conexión. No se
  agrega un sexto flujo atómico, ni roles, permisos, RLS u outbox.

## Correo del piloto: se mantiene Azure Communication Services (NTF-EMAIL-ACS-PILOT-2026-10-11) — 2026-10-11

- Respuesta literal del project owner a "Correo: Microsoft anunció el 23 de septiembre de 2026 que retira Azure
  Communication Services el 30 de septiembre de 2028. [...] ¿Qué hacemos con el correo?": "Mantener ACS en el piloto
  (Recommended)" ("El código ya está listo. Creas el recurso pronto, porque Microsoft puede cerrar las altas nuevas en
  cualquier momento, y cambiamos de proveedor antes de pasar a producción."); registrada en `decision-log.md`.
- El piloto sigue con el correo de GATE-004-CHANNELS por ACS; `AzureCommunicationEmailProvider` no cambia y el canal de
  correo sigue `Disabled` hasta que cierre GATE-004.
- Fuente (Microsoft Learn, guía de retiro de ACS, actualizada el 2026-10-08): desde el 2026-10-23 Microsoft empieza a
  limitar las altas de clientes sin un recurso de ACS creado antes de esa fecha. La guía del owner (sección A §8)
  explica cómo crear antes de esa fecha los recursos Communication Services y Email Communication Services en la
  suscripción del piloto, sin cambiar plantillas, workflow ni código.
- AI-10 `release_gates.PRODUCTION` agrega la condición de dejar ACS para el correo antes de producción. El proveedor
  que lo reemplace es una decisión pendiente del owner.
- Sin cambios en API, AI-04, AI-05, AI-06, AI-18, roles, permisos ni migraciones. GATE-004 sigue abierto.

## Sin enlace de soporte en el seguimiento público (GATE-001-TRACKING-SUPPORT-LINK-2026-10-11) — 2026-10-11

- Respuesta literal del project owner: "Quitarlo por ahora (Recommended)"; registrada, con su pregunta, en
  `decision-log.md`. GATE-001 sigue abierto.
- Mientras GATE-001 siga abierto, el seguimiento público no muestra enlace de soporte: el workflow del piloto ya no
  usa el host del piloto como valor por omisión de `PILOT_TRACKING_SUPPORT_URL`, un
  `NEXT_PUBLIC_TRACKING_SUPPORT_URL` vacío significa sin enlace (la página indica responder por el mismo canal del
  enlace) y `next build` rechaza un enlace que nombre la marca en cualquier variante.
- AI-07 `public_tracking.branding` agrega la regla del canal de soporte. Sin cambios en API, AI-04, AI-05, AI-06,
  AI-18, roles ni migraciones.

## Nombre neutro en el seguimiento público mientras GATE-001 sigue abierto (GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10) — 2026-10-10

- Respuestas literales del project owner: "Interno sí, público neutro (Recommended)" y "Seguimiento de envío
  (Recommended)"; registradas, con sus preguntas, en `decision-log.md`. GATE-001 sigue abierto: solo fija el texto
  público provisional mientras el nombre comercial no esté validado ante el IMPI.
- AI-07 `public_tracking.branding`: todos los estados del seguimiento abren con "Seguimiento de envío" y ninguno nombra
  la marca; el título de la página es "Seguimiento de envío", `/track` no enlaza el manifest de la PWA del repartidor y
  el 404 al que lleva un enlace mal formado se titula "Página no encontrada". En `show`, `brand` pasa a `heading`.
- `NEXT_PUBLIC_TRACKING_BRAND_NAME` se conserva sin definir (sin marca): el workflow del piloto ya no pasa
  "Paquetenvia" y `next build` rechaza ese nombre en cualquier variante.
- Los mensajes al destinatario que llevan el enlace (TRK-002, plantillas GATE-004) usan el mismo texto neutro; hoy el
  repositorio no contiene textos para destinatarios.
- Pantallas internas, login, onboarding y PWA del repartidor conservan "Paquetenvia". Sin cambios en API, AI-04, AI-05,
  AI-06, AI-18, roles ni migraciones; el dominio `paquetenvia.com` (PILOT-DOMAIN-PRODUCTION) no cambia.

## Asistente de nueva orden en 4 pasos que crea y confirma (UI-PHASE3-ORDER-WIZARD-2026-10-10) — 2026-10-10

- Respuestas literales del project owner: "avanza con la fase 3" (2026-10-09) y, a la pregunta "Asistente de nueva
  orden: al terminar los 4 pasos, ¿cómo debe quedar la orden?", "Confirmada (Recommended)" ("El asistente la crea y la
  confirma con las casillas del paso 4 (términos y artículos prohibidos). Queda lista para preparar."); registradas en
  `decision-log.md` como cambio MAJOR de operación (AI-01 §7).
- AI-07 `/ops/orders/new` y `create_order.wizard`: "1. Dónde", "2. Qué se envía", "3. Servicio y precio" y
  "4. Confirmar" (solo quién paga y las dos casillas), con stepper, validación por paso junto a cada campo y un resumen
  fijo (apilado en teléfonos) con el desglose: neto, IVA, total "IVA incluido" y la regla en palabras, antes de
  confirmar. "Autorizar envío de bajo monto" sigue en el paso 3 con motivo obligatorio.
- Al terminar: `createOrder` y después `transitionOrder` DRAFT → CONFIRMED con el motivo "Confirmada al crear la orden",
  `restricted_goods_acknowledged` de la casilla del paso 4 y la versión de la orden creada. Si la confirmación se
  rechaza, la orden queda en borrador, la pantalla lo dice con el mensaje de ORD-002-GUARD-CODES (o la verificación MFA)
  y enlaza al detalle, donde "Siguiente paso" la confirma. Los reintentos usan la misma Idempotency-Key: nunca se crea
  una segunda orden.
- Pendientes: libreta de direcciones (BUS-001, MVP-2) y selector de cuenta cliente (no hay operación que las liste).
  La importación CSV y la API siguen creando órdenes en borrador. Sin cambios en API, AI-04, AI-05, AI-06, AI-18, roles
  ni migraciones.

## Total de la orden en la bandeja de trabajo (UI-PHASE3-INBOX-TOTAL-2026-10-10) — 2026-10-10

- Completa la columna Total de la tabla aprobada en la fase 3 (respuesta literal del project owner: "avanza con la
  fase 3"); registrada en `decision-log.md`.
- El read model OBS-001 `GET /operations/dashboard` (ADR-OBS-001, fuera de AI-05) agrega `total` a cada orden: el
  `total_cents` guardado de la orden, con IVA incluido, en la forma `Money` de AI-05 `Order.total` (`currency` MXN,
  `amount_cents` en centavos int64). Un valor negativo o en otra moneda falla cerrado (503).
- Mismos lectores que hoy (DISPATCHER y PLATFORM_ADMIN con MFA), que ya leen ese total en `listOrders`/`getOrder` y
  `getOrderFinancials`; FINANCE, VIEWER y los demás roles siguen con el 403 uniforme. Sin tarifa, desglose, costo de
  asignación, margen ni cobro contra entrega.
- AI-07 `work_inbox`: columna "Total (IVA incluido)" después de Repartidor, desde centavos enteros con el formato MXN
  compartido; se retira de `not_supported` y la regla de privacidad permite solo ese monto.
- Sin cambios en AI-04, AI-05, AI-06, AI-18, roles, capacidades, migraciones ni flujos.

## Cierre automático de órdenes entregadas (ORD-AUTO-CLOSE-2026-10-10) — 2026-10-10

- Pregunta y respuesta literales del project owner: "Cierre de órdenes: hoy una orden entregada se cierra a mano con "Cerrar orden". El servidor solo lo permite si no tiene incidencias abiertas y el cobro contra entrega ya está conciliado. Aun cerrada, se puede abrir una reclamación dentro del plazo. ¿Quieres que se cierre sola cuando cumpla esas reglas?" → "Sí, que se cierre sola" (descripción de la opción: "Agrego en la fase 3 un proceso en el servidor que la cierra en cuanto cumple las reglas. Requiere ADR, porque cambia el comportamiento.");
  también dijo "avanza con la fase 3". Cambio MAJOR (AI-01 §7) registrado como ADR en `decision-log.md` y en
  `docs/adr/ADR-ORD-AUTO-CLOSE.md`.
- AI-04 `order_state_machine.automatic_close`: el sistema cierra (DELIVERED -> CLOSED) una orden entregada en cuanto
  cumple todas las guardas de CLOSED, con la misma transición ORD-002 que el cierre manual y solo para la organización
  dueña; escribe el mismo evento, outbox y auditoría, sin actor (`actor_id` nulo) y con el motivo "Cierre automático".
  La reclamación dentro del plazo, la finalización LIF-001 y el cierre manual no cambian.
- AI-24 `order_auto_close`: job `orders.auto-close` del Worker (apagado por defecto; intervalo 60 s, lotes de 100,
  10 lotes por ciclo), descubrimiento de organizaciones dueñas, lectura de candidatas por tenant, bloqueo de fila y
  versión optimista, manejo de fallos, telemetría sin identificadores y rollback.
- AI-18: `paqueteria_auto_close_executor NOLOGIN BYPASSRLS` con `USAGE` en `orders` y `SELECT (owner_org_id,status)`
  sobre `orders.orders`; aserciones 32-34. La función `security.list_auto_close_owner_organizations(uuid,integer)` la
  instala el lane Orders (`20261010000100_AddOrderAutoCloseDiscovery`), con `EXECUTE` solo para `paqueteria_worker`.
- AI-03 §25.2 y §25.8, AI-08 LIF-001 (salida y criterio de aceptación) y `tools/validate_contracts.py` (contrato
  exacto del ejecutor).
- Sin cambios en AI-05, AI-06 ni AI-12; no es un sexto flujo de AI-13 §4 (cada cierre es la transición ORD-002 de una
  orden).

## Bandeja de trabajo de despacho (UI-PHASE3-INBOX-2026-10-10) — 2026-10-10

- Respuesta literal del project owner: "avanza con la fase 3"; registrada en `decision-log.md`.
- AI-07 `/ops/inbox` (contrato `work_inbox`): "Bandeja de trabajo" para DISPATCHER y PLATFORM_ADMIN con MFA, su
  página de inicio y primer elemento del menú (Bandeja · Tablero · + Nueva orden · Rutas · Incidencias · Importar CSV).
  VIEWER y los demás roles quedan fuera, igual que en el tablero y los conteos.
- Colas con los conteos reales de `getOperationsQueueCounts`: Sin asignar, Requiere atención, Precio por revisar (solo
  conteo con nota), Entregadas sin cerrar y En ruta. Cada cola se lee con los filtros existentes de
  `GET /operations/dashboard`; las de varios estados se combinan en el orden del servidor sin saltar ni reordenar filas.
- Tabla (Guía, Estado, Destino por zona, Ventana en hora de Mazatlán, Repartidor) con chips que filtran en el servidor,
  "Cargar más" y las acciones "Abrir" y "Asignar" (selector de repartidor del detalle). La cola y los chips viven en la
  URL; el detalle abierto desde la bandeja ofrece "Volver a la bandeja".
- `/ops/dashboard` sigue disponible como "Tablero", con su vista de posiciones ("Mapa de posiciones" desde la bandeja).
- Pendientes: columna Total (el tablero no trae el total de la orden), lista de Precio por revisar (sin filtro del
  servidor), acciones masivas con vista previa y las entradas Órdenes y Repartidores. Sin cambios en API, AI-04, AI-05,
  AI-06, AI-18, roles ni migraciones.

## Reprogramar, devolver y reclamaciones en "Siguiente paso" (UI-NEXT-STEP-RETURNS-CLAIMS-2026-10-09) — 2026-10-09

- Respuesta literal del project owner: "haz los opcionales mientras decido lo de la fase 3"; registrada en
  `decision-log.md`.
- AI-07 `/ops/orders/:id` next_step: además de confirmar, liberar, cerrar y cancelar, el detalle ofrece "Reprogramar
  entrega", "Iniciar devolución", "Marcar como devuelta", "Abrir reclamación" y "Resolver reclamación", solo cuando
  `allowed_transitions` los incluye, con motivo obligatorio, confirmación y los mensajes de ORD-002-GUARD-CODES.
- Siguen fuera de esta pantalla ASSIGNED (selector de repartidor), FAILED_ATTEMPT (incidencia) y los pasos del
  repartidor. AI-04, AI-05, reglas del servidor y roles sin cambios.

## Conteos reales de la bandeja de operaciones (UI-PHASE2-QUEUE-COUNTS-2026-10-05) — 2026-10-05

- Respuesta literal del project owner: "Sí a los 5 grupos de estado, avanza con la fase 2"; registrada en
  `decision-log.md`. Fase 2: "Conteos para la bandeja" (conteo real del servidor; hoy los indicadores cuentan solo lo
  cargado).
- AI-05: `getOperationsQueueCounts` (`GET /operations/queue-counts`, esquemas `OperationsQueueCounts`,
  `OperationsQueues` y `OperationsCount`, módulo Reporting). Solo conteos enteros sobre las órdenes que la organización
  activa puede leer como dueña u operadora (RLS): `total`, `by_status` con los 17 estados AI-04 (incluye ceros) y
  `queues` (`unassigned`, `needs_attention`, `price_review`, `delivered_not_closed`, `en_route`), cada una con una regla
  que el tablero ya aplica. Sin parámetros ni filtros (cualquier query es 400). Mismos roles que el tablero:
  DISPATCHER y PLATFORM_ADMIN con MFA (`x-capability-matrix.operations_queue_operations`); VIEWER recibe 403.
- AI-07 `/ops/dashboard` `queue_counts`: indicadores "Sin asignar", "Requiere atención", "Revisar precio",
  "Entregadas sin cerrar" y "En ruta", y "N en total" por grupo de estado, recargados junto con la lista.
- Sin migraciones, tablas, índices, roles, grants ni flujos nuevos; AI-04, AI-06 y AI-18 sin cambios.

## Código de la regla incumplida al cambiar el estado (ORD-002-GUARD-CODES-2026-10-05) — 2026-10-05

- Respuesta literal del project owner: "Sí a los 5 grupos de estado, avanza con la fase 2"; registrada en
  `decision-log.md`; ADR `docs/adr/ADR-ORD-002-TRANSITION-REJECTION-CODES.md`.
- AI-05 `TransitionConflictProblem.code` (409 de `transitionOrder`): enum cerrado con los códigos de versión y matriz
  AI-04 (VERSION_CONFLICT, TRANSITION_NOT_ALLOWED, ORDER_TERMINAL, ORDER_FINALIZED, CLAIM_WINDOW_CLOSED) y un código por
  cada guarda AI-04 existente (`x-ord-002-guard-codes`), además de OFFLINE_OPERATION_EXPIRED. Sin guardas ni estados
  nuevos.
- Solo se devuelve para una orden de la organización seleccionada (dueña) y a quien tiene la capacidad de
  `transitionOrder` sobre ella (DISPATCHER, PLATFORM_ADMIN con MFA, DRIVER con la asignación ACCEPTED/ACTIVE de esa
  orden). Orden inexistente, ajena u operada, forma inválida, idempotencia, concurrencia y quien no tiene la capacidad
  siguen recibiendo el mismo 409 uniforme sin código. Sin datos personales, identificadores ni montos.
- AI-07 "Siguiente paso": mensaje es-MX por código desde un solo helper; código ausente o desconocido usa el mensaje
  genérico.
- Sin migraciones, tablas, roles, grants ni flujos nuevos; AI-06 y AI-18 sin cambios.
- AI-04 `guards` documenta `CLAIM_RESOLVED: [claim_resolution_reason_present]`, guarda que el código ya aplicaba;
  el mapa de códigos y AI-04 se verifican ahora en ambas direcciones.

## Búsqueda por guía y acciones válidas de la orden (UI-PHASE2-SEARCH-TRANSITIONS-2026-10-05) — 2026-10-05

- Respuesta literal del project owner: "Sí a los 5 grupos de estado, avanza con la fase 2"; registrada en
  `decision-log.md`.
- AI-05 `listOrders`: parámetro opcional `public_id`, coincidencia exacta del número de guía (`ORD_` y 22 caracteres
  Base64URL); un valor con otro formato no coincide con nada; orden ajena, inexistente o mal formada devuelven la
  misma página vacía (RLS). Solo se busca por guía, nunca por datos personales. Sin capacidad nueva.
- AI-05 `OrderDetail.allowed_transitions` (esquema `OrderAllowedTransition`): transiciones ORD-002 que quien consulta
  podría pedir ahora, calculadas por el servidor con la matriz AI-04, la regla de solo dueño, la versión y las reglas de
  rol de `transitionOrder`; `required_metadata` indica `restricted_goods_acknowledged` o `incident_id`. No evalúa ni
  expone guardas; es orientativa y `transitionOrder` vuelve a validar todo.
- AI-07: "Buscar guía" en la barra superior y "Siguiente paso" en `/ops/orders/:id` (solo acciones válidas, motivo
  obligatorio, confirmación, Idempotency-Key, recarga REST).
- Sin migraciones, tablas, índices, roles, grants ni flujos nuevos; AI-04, AI-06 y AI-18 sin cambios.

## Asignar repartidor desde el detalle de la orden (UI-PHASE2-DRIVER-PICKER-2026-10-05) — 2026-10-05

- Respuesta literal del project owner: "Sí a los 5 grupos de estado, avanza con la fase 2"; registrada en
  `decision-log.md`.
- AI-05: `listAssignableDrivers` (`GET /orders/{orderId}/assignable-drivers`, esquemas `AssignableDriver` y
  `AssignableDriverPage`). Lista por cursor los repartidores OWN de la organización activa (no INACTIVE) con
  `driver_id`, la referencia `DRV-xxxxxxxx` que ya muestra el tablero (no hay nombre de repartidor en AI-06; no se lee
  ni devuelve nombre, correo, teléfono, documentos ni ubicación), vehículo, si `assignDriver` lo aceptaría ahora para
  esa orden con la misma política DSP-001 y los códigos estables cuando no, y sus asignaciones ACCEPTED/ACTIVE.
  Mismos roles que `assignDriver` (`x-capability-matrix.assignable_driver_operations`); 404 uniforme; 409 CONFLICT si la
  orden no admite asignación; solo lectura.
- AI-07 `/ops/orders/:id`: panel "Asignar repartidor" (lista, costo en MXN a centavos enteros, confirmación, misma
  Idempotency-Key en reintentos, recarga REST); "Publicar oferta externa desde el tablero" sigue como alternativa.
- Sin migraciones, tablas, índices, roles, grants ni flujos nuevos; AI-06 y AI-18 sin cambios.

## Cinco grupos de estado en la interfaz (UI-STATUS-GROUPS-2026-10-05) — 2026-10-05

- Respuesta literal del project owner: "Sí a los 5 grupos de estado, avanza con la fase 2"; registrada en
  `decision-log.md` con el mapeo aprobado.
- Solo presentación en `apps/web` (UI-001): el tablero agrupa las órdenes en Por preparar, En recolección, En ruta,
  Requiere atención y Terminadas, sin ocultar el estado exacto de AI-04. Máquina de estados, terminalidad, ventana de
  reclamo, API, tracking público y PWA del repartidor sin cambios.

## El repartidor del operador recibe los cambios de estado del dueño (ORD-002-OPERATOR-DRIVER-EVENTS-2026-10-03) — 2026-10-03

- Respuesta literal del project owner: "Solo avisar a su repartidor"; registrada en `decision-log.md`.
- ORD-002 sigue siendo solo del dueño. Si la orden tiene operador distinto del dueño y la asignación vigente
  (OWN/EXTERNAL, ACCEPTED o ACTIVE) es de ese operador, el `orders.status-changed` (etiquetado con el dueño) nombra a
  su repartidor; Realtime solo lo entrega tras verificar en el contexto del propio operador la asignación, la orden y
  el repartidor exactos, que el operador de la asignación siga siendo el de la orden y que perfil, usuario y membresía
  DRIVER estén activos. Ningún repartidor de un tercero y ninguna otra audiencia cambian.
- AI-04 `outbox_invariants` registra la regla. Sin migración, rol, grant ni función nuevos; AI-06, AI-12 y AI-18 sin
  cambios.

## Outbox y auditoría a nombre del dueño cuando asigna el operador (DSP-OPERATOR-OWNER-OUTBOX-DEFINER-2026-10-03) — 2026-10-03

- Respuestas literales del project owner: "Función segura a nombre del dueño" y "Sí, el dueño lo ve"; registradas en
  `decision-log.md`.
- Corrige un fallo confirmado: cuando la organización operadora de un pedido ajeno asignaba a su repartidor (DSP-002),
  RLS rechazaba las filas de outbox y auditoría etiquetadas con el dueño (42501) y la asignación se revertía.
- AI-18: nuevo rol `paqueteria_operator_outbox_executor NOLOGIN BYPASSRLS` con `SELECT`/`INSERT` por columna exactos
  (sin UPDATE/DELETE, sin CREATE, sin membresía runtime) y aserciones 29-31. AI-06: solo una nota junto a
  `audit_logs_tenant`/`outbox_tenant` (políticas sin cambio). AI-03 §25.2 y AI-04 `outbox_invariants` registran la regla.
- Lane Dispatch `20261003000100_AddOperatorOwnerOutboxExecutor`: `security.append_operator_order_outbox` y
  `security.append_operator_order_audit` (SECURITY DEFINER, `search_path=pg_catalog, pg_temp`, EXECUTE solo para
  `paqueteria_app`); escriben una fila cada una solo si el contexto es exactamente el operador de la orden, el actor es
  despachador o admin activo del operador y la fila corresponde a la asignación recién hecha (tema/acción, audiencia,
  payload y versión en lista permitida, sin repetidos); si no, 42501. Rollback: solo elimina las dos funciones.
- DSP-002 usa esas funciones únicamente cuando actúa el operador; el dueño conserva los inserts directos. Realtime
  autoriza como audiencia de conductor al repartidor del operador de esa asignación exacta; no se amplía otra audiencia.
- ORD-002 sin cambios (solo el dueño transiciona); extenderlo a operadores queda como decisión aparte.

## AI-02 y AI-15 alineados con la excepción WhatsApp de leases vencidos (DOC-AI02-AI15-STALE-LEASE-SYNC-2026-10-03) — 2026-10-03

- Respuesta literal del project owner: "Sí, actualizarlo"; registrada en `decision-log.md`
  (`DOC-AI02-AI15-STALE-LEASE-SYNC-2026-10-03`).
- AI-02 `outbox_lifecycle.recovery` y AI-15 `outbox_operations.lease_recovery`: el PROCESSING vencido se sigue
  reencolando, salvo el `notifications.send-requested` de WhatsApp, que queda DEAD con su Notification FAILED
  (`AMBIGUOUS_TIMEOUT`) y un `notifications.status-changed` que avisa al despachador, como ya dice AI-04
  `outbox_invariants` (NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03, PR #186).
- Solo redacción: sin cambio de comportamiento ni migración; AI-04, AI-05, AI-06, AI-12 y AI-18 sin cambios.

## WhatsApp con lease vencido: fallido sin reencolar y aviso al despachador (NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03) — 2026-10-03

- Respuesta literal del project owner (GATE-004): "Sí, marcar fallido y avisar"; registrada en `decision-log.md`
  (`NTF-WHATSAPP-STALE-LEASE-FAILS-2026-10-03`).
- Si el proceso que envía un WhatsApp se cae a mitad del envío, la recuperación de leases vencidos ya no lo reencola:
  el `notifications.send-requested` queda DEAD (`AMBIGUOUS_TIMEOUT`, bajo su propio `lease_token`), la Notification
  FAILED con `AMBIGUOUS_TIMEOUT` y el mismo `notifications.status-changed` (audiencia operations, sin PII) se escribe
  en la misma transacción. Una fila bloqueada por un settle concurrente se omite y nunca se reencola.
- Lane Notifications `20261003000100_FailStaleWhatsAppNotificationLeases`: reemplaza solo el cuerpo de
  `security.recover_stale_notifications_outbox` (misma firma, dueño, search_path y permisos); rollback restaura el
  cuerpo de NTF-001. IN_APP y email conservan la recuperación de NTF-001.
- AI-04 `outbox_invariants`: la excepción WhatsApp en la recuperación de PROCESSING vencido. AI-06, AI-12 y AI-18 sin
  cambios.

## Prefijo +52 en teléfonos y 10 dígitos en ubicaciones (ORD-PHONE-PLUS52-LOCATIONS-2026-10-03) — 2026-10-03

- Respuestas literales del project owner: "Aceptar +52 y quitarlo" y "Exigir 10 dígitos en ubicaciones"; registradas
  en `decision-log.md`.
- AI-05: `AddressInput.phone` (`createQuote`) acepta un prefijo `+52` inicial opcional, que se quita junto con espacios
  y guiones; deben quedar exactamente 10 dígitos ASCII (otro prefijo, `52` sin `+` u otro carácter sigue siendo 422).
  `CreateLocationRequest.phone` (`createLocation`, GEO-001) aplica la misma regla: sigue siendo opcional (`null`) y
  otro valor es el 400 uniforme; se valida solo al escribir, las ubicaciones ya guardadas siguen legibles. Solo los 10
  dígitos normalizados se hashean, protegen y guardan. Nueva entrada `ORD-PHONE-PLUS52-LOCATIONS` en
  `x-pilot-contract-deltas`.
- AI-07: `create_order.phone_and_restricted_goods` menciona el `+52` opcional.
- Sin migración; AI-04, AI-06 y AI-18 sin cambios.

## Límites de la ventana de servicio confirmados (ORD-SERVICE-WINDOW-LIMITS-CONFIRMED-2026-10-03) — 2026-10-03

- Respuestas literales del project owner: "Sí, 12 h y 30 días" (duración máxima de la ventana y anticipación máxima);
  "No por ahora" (columnas de ventana en el CSV de pedidos); "No en el piloto" (validar la ventana contra el horario
  de la zona). Registrado en `decision-log.md`.
- Sin cambios de comportamiento: la API ya aplicaba 12 horas de duración máxima y 30 días de anticipación; el CSV-001
  sigue sin columnas de ventana y la ventana no se compara contra ningún horario de zona en el piloto (las zonas no
  guardan horario). La entrada ORD-SERVICE-WINDOW-OPTIONAL de `x-pilot-contract-deltas` lo documenta.

## Ventana de servicio opcional en la orden (ORD-SERVICE-WINDOW-OPTIONAL-2026-10-02) — 2026-10-02

- Respuesta literal del project owner a "Ventana de servicio (horario de entrega): la pantalla la pide pero la API no
  la guarda. ¿La agrego a la API?": "Sí, opcional". Registrado en `decision-log.md`.
- AI-05: `CreateOrderRequest.service_window` opcional (`from`, `to` en RFC 3339 con zona explícita, segundos enteros,
  normalizados a UTC; `from < to`, máximo 12 horas, `from >= ahora - 5 min`, `to > ahora`, `from <= ahora + 30 días`;
  cualquier otra forma es 409). `Order.service_window` (null si no hay ventana: aplica el horario de la zona). Entra
  en el hash de idempotencia solo si está presente. Nueva entrada ORD-SERVICE-WINDOW-OPTIONAL en
  `x-pilot-contract-deltas`.
- AI-06: `orders.orders` agrega `service_window_from` y `service_window_to` (timestamptz, nulos) con
  `orders_service_window_check`. Lane Orders `20261002000100_AddOrderServiceWindow` adopta la forma de AI-06 y su
  rollback falla cerrado. AI-18 sin cambios.
- AI-04: regla de `Order.service_window`. AI-07: `create_order.service_window` (hora de Mazatlán).
- El CSV de pedidos no cambia: su plantilla no tiene columna de ventana (confirmado por el owner el 2026-10-03,
  "No por ahora").

## Autorización manual de envíos de bajo monto en la cotización (LOW-PRICE-MANUAL-AUTH-2026-10-02) — 2026-10-02

- Respuestas literales del project owner: "Sí, con autorización"; "En la cotización". Registrado en `decision-log.md`
  (`LOW-PRICE-MANUAL-AUTH-2026-10-02`); implementa PRC-002 (flujo de financial override) acotado a esa regla.
- AI-05: `CreateQuoteRequest.low_price_authorization` opcional (`reason` recortado, 1 a 200 caracteres, sin datos
  personales). Solo DISPATCHER o PLATFORM_ADMIN con MFA (`x-capability-matrix.low_price_authorization`); otro rol recibe
  403 (`MFA_REQUIRED` si solo falta el segundo factor) antes de leer estado persistido. Si el precio la necesita (sin ruta
  consolidada y tarifa 52/45 o total de 52 MXN o menos con IVA) la cotización guarda `financial_override` = {actor_id,
  reason, valid_until = expires_at} y la orden lo copia; si no la necesita, 409 uniforme. createQuote declara 409.
  `Quote.low_price_authorization` muestra `valid_until` a todo lector y `actor_id`/`reason` solo a quien tiene
  getOrderFinancials.
- Auditoría append-only `QUOTE_LOW_PRICE_AUTHORIZED` (actor, motivo, cotización, total en centavos) en la misma
  transacción de createQuote; la autorización forma parte de la huella de idempotencia.
- AI-07: `create_order.low_price_guard` deja pasar una cotización autorizada y `create_order.low_price_authorization`
  describe el campo "Autorizar envío de bajo monto". AI-02 y AI-08 (PRC-002) registran la regla.
- Sin migración: `financial_override` y los CHECK de tarifa/ruta y piso ya existen en AI-06. AI-06 y AI-18 sin cambios.

## Reglas del piloto para Google Maps (GATE-003-MAPS-PILOT-RULES-2026-10-02) — 2026-10-02

- Respuestas literales del project owner: "Sí, las 4" (key restringida a la Geocoding API, sin rutas ni ETAs en el
  piloto, reemplazar el pin solo con coincidencia exacta, búsquedas restringidas a México) y, sobre qué es "exacta",
  "Solo ROOFTOP".
- Adaptador Google Maps: el pin del cliente se reemplaza solo con un único resultado, sin `partial_match` y
  `location_type` `ROOFTOP` cuyo `address_components` tenga el país `MX`; todo lo demás conserva el pin manual.
  `components=country:MX` se envía siempre desde una constante y `Locations:GoogleMaps:ComponentsCountry` solo
  admite `MX` (otro valor, o vacío, falla la validación al arrancar).
- Prueba de arquitectura: ningún endpoint de Directions, Routes o Distance Matrix ni puerto de ruteo/ETA en `src`.
- GATE-003 sigue abierto: tope de gasto, cuotas diarias, alertas de facturación y la key restringida en Key Vault
  (`google-maps-api-key`) quedan pendientes del owner; el piloto mantiene `Locations__GeocodingProvider=Manual`.
- Sin cambios en AI-05, AI-06, AI-18 ni migraciones.

## Versiones de política de asignación y de elegibilidad por organización (POLICY-VERSIONS-PER-ORG-2026-10-02) — 2026-10-02

- Respuesta literal del project owner: "Por empresa, piloto-2026-10-v1"; registrada en `decision-log.md`.
- AI-06: `organizations.organizations.assignment_policy_version` y `driver_eligibility_policy_version`, `text NOT NULL
  DEFAULT 'piloto-2026-10-v1'` con el formato `^[A-Za-z0-9._-]{1,64}$`. Toda organización, existente o futura,
  empieza en `piloto-2026-10-v1`.
- AI-04 `Organization`: ambas versiones son de la organización; se aplica la de la organización del repartidor
  asignado o evaluado (la dueña de la orden si asigna a su repartidor, la operadora si lo hace la operadora). No
  existe versión global.
- Se eliminan `Dispatch:AssignmentPolicyVersion` y `Drivers:Eligibility:PolicyVersion`: la API no arranca si siguen
  configuradas y ya no bloquean el despliegue del piloto (el guard las rechaza en `apps.settings.json`). Las reglas
  de elegibilidad (documentos y capacidad por vehículo) siguen siendo configuración compartida.
- Lane Organizations `20261002000100_VersionDispatchPoliciesPerOrganization`: agrega o adopta ambas columnas sin
  reescribir filas; el rollback se niega si alguna organización tiene otra versión. AI-18 sin cambios.

## Confirmación de artículos prohibidos y teléfonos de México (ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02) — 2026-10-02

- Respuesta literal del project owner: "Sí, ambas"; registrada en `decision-log.md`.
- AI-05: `CreateOrderRequest.restricted_goods_acknowledged` obligatorio (solo `true`) y el campo multipart
  `restricted_goods_acknowledged=true` obligatorio en `CsvImportCommitRequest`; cualquier otro valor es 409 uniforme.
  La confirmación entra al hash de idempotencia de ORD-001 y queda en el evento append-only `ORDER_CREATED` y en su
  auditoría. `order_acceptances` y `OrderAcceptanceCanonicalForm v1` no cambian. `AddressInput.phone` (`createQuote`)
  es un número de México de 10 dígitos tras quitar espacios y guiones (sin `+52`); otro valor es 422. Nueva entrada
  `ORD-PROHIBITED-GOODS-PHONE-MX` en `x-pilot-contract-deltas`.
- AI-07: `create_order.phone_and_restricted_goods` y la casilla de confirmación del commit de `csv_order_import`.
- Sin migración; AI-04, AI-06 y AI-18 sin cambios.

## WhatsApp ambiguo: fallido sin reintento y aviso al despachador (NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02) — 2026-10-02

- Respuesta literal del project owner (GATE-004): "Marcar fallido y avisar"; registrada en `decision-log.md`
  (`NTF-WHATSAPP-AMBIGUOUS-FAILS-2026-10-02`).
- Un envío WhatsApp con resultado ambiguo (timeout o respuesta sin `wamid`: Meta pudo aceptarlo) es terminal: la
  Notification queda FAILED con el motivo `AMBIGUOUS_TIMEOUT`, el `notifications.send-requested` queda DEAD y nunca se
  reintenta ni se reencola, así el cliente nunca lo recibe dos veces.
- Aviso: el `notifications.status-changed` que el mismo settle ya escribe (`NotificationStatusChanged.v1`, audiencia
  operations, sin destinatario ni PII). Sin evento, flujo cross-module, estado ni tabla nuevos.
- Lane Notifications `20261002000100_FailAmbiguousWhatsAppNotifications`: reemplaza solo el cuerpo de
  `security.apply_notification_outcome` (misma firma, dueño, permisos y verificación de `lease_token`); rollback
  restaura el cuerpo de NTF-001. IN_APP y email conservan el reintento AMBIGUOUS. AI-06, AI-12 y AI-18 sin cambios.

## Zona horaria del piloto solo America/Mazatlan en el cargador MDM-001 (MDM-001-TZ-MAZATLAN-ONLY-2026-10-02) — 2026-10-02

- Respuesta literal del project owner: "Solo America/Mazatlan"; registrada en `decision-log.md`
  (`MDM-001-TZ-MAZATLAN-ONLY-2026-10-02`).
- Lane Pricing `20261002000100_RequireMazatlanTimeZoneInMasterDataLoader`: `security.load_master_data` rechaza una entrada de
  ciudad con zona distinta de `America/Mazatlan` (`MDM001_CITY_TIMEZONE_NOT_IN_PILOT`, también en dry run); el validador del
  job repite la regla. Una zona fuera de la lista mexicana sigue siendo `MDM001_CITY_TIMEZONE_NOT_ALLOWED`.
- Ninguna fila guardada se reescribe. Sin permisos, tablas ni roles nuevos; rollback restaura la función anterior. AI-06 y AI-18
  sin cambios.

## Alertas OBS-002 del piloto cada 15 minutos (OBS-002-ALERTS-15MIN-COST-2026-10-02) — 2026-10-02

- Respuesta literal del project owner: "Sí, revisar cada 15 min"; registrada en `decision-log.md`
  (`OBS-002-ALERTS-15MIN-COST-2026-10-02`).
- `deploy/azure/pilot/observability.bicep`: la alerta de disponibilidad `sqr-pv-pilot-readiness` pasa de cada 5 minutos
  con ventana de 10 a cada 15 minutos con ventana de 15 (Azure exige ventana ≥ frecuencia). Las cinco reglas cuestan
  ≈ 2.75 USD/mes; el piloto queda en ≈ 101 USD típico y ≈ 111 USD con el tope diario de logs.
- El correo de destino `alertEmailAddress` sigue siendo parámetro obligatorio del owner, sin valor por defecto, pendiente.
- Sin cambios de contrato, esquema, roles ni migraciones.

## Finanzas ve la lista de cobros pendientes de conciliar (FIN-PENDING-COD-LIST-FINANCE-2026-10-02) — 2026-10-02

- Respuesta literal del project owner: "Tope COD 20,000 pesos, finanzas sí ve la lista". Esta entrada cubre solo
  "finanzas sí ve la lista"; registrado en `decision-log.md` (`FIN-PENDING-COD-LIST-FINANCE-2026-10-02`).
- AI-05: FINANCE con MFA puede llamar `listOrders` únicamente con `cod_pending_reconciliation=true` (sin MFA,
  `403 MFA_REQUIRED`); cualquier otra llamada de FINANCE a `listOrders` sigue en 403. Se documentan la descripción
  de `listOrders`, su `x-authorization-precedence` y `x-capability-matrix.cod_pending_reconciliation_filter`; la
  fila `operations.listOrders` no cambia. La lista vuelve a verificar los roles de `getOrderFinancials` dentro de
  la transacción tenant antes de leer órdenes. FINANCE recibe la misma representación `Order` que los demás
  lectores, sin datos personales ni `cod_expected_cents`, y sigue sin crear ni modificar órdenes.
- AI-07 `cod_control.pending_list`: agrega FINANCE con MFA.
- Sin migraciones, esquemas, roles de base de datos ni flujos nuevos.

## Tope de COD declarado por orden: 20,000 MXN (COD-CAP-20000-2026-10-02) — 2026-10-02

- Literal del project owner: "Tope COD 20,000 pesos, finanzas sí ve la lista". Esta entrada cubre solo el tope;
  registrado en `decision-log.md` (`COD-CAP-20000-2026-10-02`).
- AI-05: `CreateOrderRequest.cod_expected_cents` y `CsvImportRowPreview.cod_expected_cents` agregan
  `maximum: 2000000` (20,000.00 MXN, inclusivo). Un monto mayor en `POST /orders` es el mismo 409 uniforme que
  cualquier literal COD inválido; en el CSV es el error de fila `COD_EXPECTED_CENTS_INVALID` (sin código nuevo) y
  bloquea la confirmación. La entrada D6-COD-EXPECTED de `x-pilot-contract-deltas` documenta el tope.
- AI-07: `create_order.cod_expected` y `csv_order_import` muestran el tope.
- Sin migración ni CHECK nuevo: AI-06 conserva `CHECK (cod_expected_cents >= 0)`; el tope vive en
  `OrderInputPolicy` y en el guard de dominio de `Order.Create`. AI-06 y AI-18 sin cambios.

## COD declarado por el despachador en la orden y en el CSV (D6-COD-EXPECTED) — 2026-09-29

- Implementa `D6-COD-PILOT` ("el despachador declara el monto COD al crear la orden (y en el CSV)"), reaprobado para
  el CSV de pedidos el 2026-09-29; registrado en `decision-log.md` (`D6-COD-EXPECTED-IMPLEMENTED-2026-09-29`).
- AI-05: `CreateOrderRequest.cod_expected_cents` opcional (int64 en centavos MXN, mínimo 0; ausente o 0 es sin COD; cualquier literal que no sea un entero simple es 409). `CsvImportPreviewRequest.file` documenta la columna
  opcional `cod_expected_cents` al final del encabezado; `CsvImportRowError` agrega `COD_EXPECTED_CENTS_INVALID` y
  `CsvImportRowPreview` devuelve `cod_expected_cents` en cada fila válida. La entrada D6-COD-EXPECTED de `x-pilot-contract-deltas` queda
  marcada como implementada. `Order` no expone el monto: VIEWER lee órdenes y no tiene lectura financiera.
- AI-07: `create_order.cod_expected` y una validación de `csv_order_import` para mostrar el COD por fila.
- Sin migración: `orders.cod_expected_cents` ya existe en AI-06 con `CHECK >= 0`. AI-06 y AI-18 sin cambios.

## Lectura de incidencias y pruebas; roles de openIncident (API-INC-LIST-PROOFS-2026-09-29) — 2026-09-29

- Respuesta literal del project owner: "El punto I apruebo todos los puntos listados."
- AI-05: `listIncidents` (`GET /incidents`, paginación por cursor, filtros `status` y `order_id`),
  `getIncident` (`GET /incidents/{incidentId}`) y `listOrderProofs` (`GET /orders/{orderId}/proofs`). Devuelven
  las representaciones existentes `Incident` y `Proof` (esquemas nuevos `IncidentPage` y `ProofPage`); nunca la
  descripción, el motivo de resolución, bytes, llaves de almacenamiento, URLs firmadas, punto de captura ni nombre
  del destinatario. Solo DISPATCHER y PLATFORM_ADMIN con MFA, como `resolveIncident`; capacidad antes de leer
  cualquier orden, incidencia o prueba; 404 uniforme para lo ajeno o inexistente; solo lectura.
- `x-capability-matrix.incident_operations` publica quién abre incidencias: DISPATCHER, PLATFORM_ADMIN con MFA y
  DRIVER solo para una orden con asignación ACCEPTED o ACTIVE; es la regla que el servidor ya aplicaba.
- AI-07 `incident_desk`: la pantalla lista y elige incidencias y evidencias en lugar de pedir UUID escritos.
- Sin migraciones, estados, eventos ni roles de base de datos nuevos.

## Presentación de impuestos con IVA incluido (GATE-011-VAT-INCLUDED-2026-09-29) — 2026-09-29

- Respuestas literales del project owner: "Presentación de impuestos, IVA incluido"; "52 con IVA incluido";
  "Igual para todas". AI-10 mueve GATE-011 a `resolved_decisions`.
- Todas las organizaciones cotizan con IVA incluido: la tarifa es el total; `subtotal = floor((100 * total + 58) / 116)`
  (redondeo half-up al centavo, aritmética entera, IVA 16 %), `tax = total - subtotal`, `discount = 0`. El piso
  congelado `minimum_total_cents_snapshot` es el total con IVA. `PLUS_VAT` conserva su aritmética probada y `EXEMPT`
  se conserva en el vocabulario, pero ninguno es seleccionable: una regla seleccionada así falla cerrado.
- El umbral de 52 MXN de AI-07 (`low_price_guard`) compara el total con IVA incluido, no el neto; se conserva el
  límite no estricto (52.00 exactos se bloquean salvo ruta consolidada).
- Lane Pricing `20260929000200_RequireVatIncludedTariffsInMasterDataLoader`: `security.load_master_data` no crea reglas
  `PLUS_VAT` ni `EXEMPT` (`MDM001_TARIFF_TAX_MODE_NOT_ALLOWED`); una regla guardada se puede cerrar. Sin permisos,
  tablas ni roles nuevos; rollback restaura la función anterior. AI-06 y AI-18 sin cambios.
- AI-04, AI-07 y AI-19 registran la resolución. Las cotizaciones y órdenes existentes conservan sus montos congelados.

## Enlaces de tracking sin revocación (TRK-002-NO-REVOCATION) — 2026-09-29

- Decisión literal del project owner registrada en `decision-log.md` (`TRK-002-NO-REVOCATION`): "La liga la puede
  ver el despachador, y el cliente al que le llegará el pedido, la cual se le enviará por WhatsApp y correo
  electronico."; PLATFORM_ADMIN "Sí, con MFA"; quién la anula: "Nadie la anula".
- AI-05: se elimina `revokeTrackingLink` (`POST /orders/{orderId}/tracking-link/revoke`) y su fila en
  `x-capability-matrix.tracking_link_operations`; la decisión de esa sección deja de estar pendiente de
  confirmación (DISPATCHER sin MFA, PLATFORM_ADMIN con MFA). `issueTrackingLink` y `PublicTrackingLink` explican
  que nadie revoca el enlace y que `generation` solo crece al sustituir un token previo a la derivación o de una
  versión de llave ya no configurada.
- Vigencia sin cambios: mientras la orden avanza y 24 horas después de su primer estado público final.
- AI-04, AI-08 y AI-24 registran la ausencia de revocación. AI-06 y AI-18 sin cambios y sin migración: no existía
  función ni grant exclusivos de la revocación; `revoked_at` y `generation` se conservan.
- El envío del enlace por WhatsApp y correo sigue bloqueado por GATE-004, GATE-007 y las plantillas del owner.

## Enlaces de tracking automáticos y derivados (TRK-002-AUTO-LINK) — 2026-09-29

- Decisión literal del project owner registrada en `decision-log.md` (`TRK-002-AUTO-LINK`); sustituye el emitir y
  rotar de `TRK-002-ISSUE-ENDPOINT`.
- Cada orden recibe su enlace dentro de la transacción de `createOrder` (flujo 1 de AI-13 §4; escritura interna de
  Orders, no un sexto flujo), auditado `TRACKING_TOKEN_ISSUED`. El token es Base64URL sin padding de HMAC-SHA256 con
  una llave de Key Vault (`public-tracking-link-key`) sobre `paquetenvia-trk-v1|key_version|order_id|generation`;
  solo se guarda su SHA-256 (regla 6 de AI-01 sin cambio), con `generation` y `key_version`.
- AI-05: `issueTrackingLink` pasa a obtener-o-crear: 200 `no-store` con `url`
  (`{PublicTracking:PublicBaseUrl}/track/{token}`), `generation` y `valid_until`; el mismo enlace para cualquier
  reintento o Idempotency-Key, sin rotar. 409 `TRACKING_LINK_ORDER_FINISHED` para una orden en estado público final
  sin enlace vigente (`TrackingLinkConflict`, `TrackingLinkConflictProblem`). `revokeTrackingLink` retira la
  generación actual; la siguiente lectura deriva la generación siguiente, nunca una revocada.
- Vigencia: mientras la orden avanza (RESCHEDULED incluido) y 24 horas después de su primer evento público
  DELIVERED, RETURNED o CANCELLED, verificado en `security.get_public_tracking_projection`; después, 404 uniforme.
  Se elimina `PublicTracking:TokenLifetimeHours`.
- AI-06: `orders.public_tracking_tokens` gana `generation` y `key_version` y dos índices únicos parciales; la
  proyección pública aplica la vigencia. Lane Orders `20260929000100_AddTrackingLinkGenerations` y lane
  PlatformEvolution `20260929000200_BoundTrackingLinksToOrderLifecycle`, ambas con rollback que falla cerrado.
  AI-18 sin cambios.
- AI-04, AI-08, AI-13 y AI-24 registran la derivación, la vigencia y las pruebas.
- El envío del enlace a clientes sigue bloqueado por GATE-004 y GATE-007.

## Pantallas UI-001 de importación CSV, incidencias y COD (UI-001-SCREENS-CSV-INC-COD) — 2026-09-28

- Respuesta literal del project owner: "Sí, agrégalas (Recommended)".
- AI-07 agrega las rutas `/ops/orders/import`, `/ops/incidents` y `/finance/cod` y sus contratos de pantalla
  `csv_order_import`, `incident_desk` y `cod_control`: roles, fuentes, estados y validaciones, limitados a las
  operaciones AI-05 existentes (`previewOrderCsv`, `commitOrderCsv`, `openIncident`, `resolveIncident`,
  `getOrderFinancials`, `recordCodCollection`, `reconcileCod`). D5-CAPABILITY-MATRIX y el step-up MFA de
  PLATFORM_ADMIN y FINANCE siguen aplicando; el backend sigue siendo la barrera de autorización.
- Sin API, estados, eventos ni migraciones nuevos. Cada contrato declara en `not_supported` lo que AI-05 no
  permite hoy (leer o listar incidencias y pruebas, leer registros COD, filtrar órdenes con COD pendiente).

## Emisión y revocación de enlaces de tracking (TRK-002) — 2026-09-28

- Implementa `TRK-002-ISSUE-ENDPOINT` (respuesta literal del project owner: "Sí, con botón en UI
  (Recommended)").
- AI-05: `issueTrackingLink` (`POST /orders/{orderId}/tracking-link`, 201 con `Cache-Control:
  no-store` y el esquema `PublicTrackingLink`) y `revokeTrackingLink`
  (`POST /orders/{orderId}/tracking-link/revoke`, 204). El token en claro se devuelve una sola vez
  y nunca se persiste ni se registra; solo se guarda su SHA-256. Emitir cuando ya hay un enlace
  lo rota y revoca los anteriores. Auditoría `TRACKING_TOKEN_ISSUED`, `TRACKING_TOKEN_ROTATED` y
  `TRACKING_TOKEN_REVOKED`. 404 uniforme para una orden ajena o inexistente.
- `x-capability-matrix.tracking_link_operations`: DISPATCHER sin MFA y PLATFORM_ADMIN con un reto
  MFA satisfecho (403 `MFA_REQUIRED` cuando solo falta el segundo factor), como `assignDriver`,
  `createRoute` y `createExternalOffer`: emitir o revocar un enlace crea o retira una credencial
  pública al portador. D5 no cubría estas operaciones; es el valor por defecto más seguro según
  AI-01 §7 y queda pendiente de confirmación del owner.
- El envío del enlace a clientes por WhatsApp o correo sigue bloqueado por GATE-004 y GATE-007.

## Endurecimiento del cargador de datos maestros (MDM-001-LOADER-HARDENING) — 2026-09-28

- Seguimiento de tres hallazgos MINOR de la revisión de MDM-001, diferidos del PR de MDM-001; sin API,
  estados, eventos ni UI nuevos.
- X1: `security.load_master_data` rechaza en cada llamada una sesión cuyo login sea miembro de
  `paqueteria_migrator` (`MDM001_DEPLOYMENT_PRINCIPAL_REFUSED`): un miembro con sólo `ADMIN` sobre
  `paqueteria_master_data_loader` podía concederse `SET` y llamar la función directamente.
- X2: `operator_ref` deja de ser un SHA-256 sin sal de un login adivinable y pasa a ser un UUID aleatorio
  por login, en `platform.master_data_operator_refs` (sólo plataforma: FORCE RLS con una única política para
  `paqueteria_migrator`, sin permisos de runtime; el ejecutor sólo `SELECT`/`INSERT` de dos columnas). Sin
  secreto nuevo. Las filas de auditoría anteriores (append-only) conservan el formato SHA-256.
- X3: `master-data-gate` toma `pg_advisory_xact_lock(2026092803)` antes de leer la marca de despliegue, para
  que dos primeras ejecuciones concurrentes no puedan dejar `SYNTHETIC` sobre un `REAL`.
- Lane de Pricing `20260928000400_HardenMasterDataLoaderOperatorBoundary` (dueña de la tabla y de los cuatro
  grants, como el paso de `policy_version`); AI-18 lo documenta en comentarios y en las aserciones 27 y 28.

## Versión de política de precios por organización (PRC-POLICY-VERSION-PER-ORG) — 2026-09-28

- Respuesta literal del project owner: "Versión por organización" ("Cada organización tiene su
  propia versión de política, que se sube cuando cambia sus tarifas, y esa versión se congela en
  cada cotización.").
- AI-06: `pricing.tariff_rules.policy_version text NOT NULL CHECK (policy_version ~
  '^[A-Za-z0-9._-]{1,64}$')`. La cotización congela en `pricing_policy_version` la versión de la
  regla de tarifa seleccionada y la orden la copia sin cambios. Se elimina la configuración global
  `Pricing:PricingPolicyVersion`.
- Lane de Pricing (`20260928000200_VersionPricingPolicyPerOrganization`): adopta la columna en
  instalaciones nuevas y la crea en las existentes sin reescribir filas (NOT NULL si ninguna regla
  carece de versión; si no, CHECK NOT VALID para filas nuevas o actualizadas). El rollback se niega
  mientras alguna regla tenga versión.
- AI-02 (`contract_hardening.quote_to_order.pricing_policy_version`), AI-04 (`TariffRule` y reglas
  de `Quote`/`Order`), AI-05 (descripción de `pricing_policy_version`), AI-08 (PRC-001) y AI-13 §4.
- Integración con MDM-001 (lane de Pricing `20260928000300_StoreTariffPolicyVersionInMasterDataLoader`):
  `security.load_master_data` guarda el `policy_version` de cada regla y rechaza cambiar el de una regla
  guardada (`MDM001_TARIFF_POLICY_VERSION_IMMUTABLE`); el ejecutor recibe `SELECT` e `INSERT` sobre esa
  columna (nunca `UPDATE`). Esos dos grants los posee la lane, no los `GRANT` de AI-18, porque el paso
  MDM-001 publicado verifica exactamente los 116 grants de AI-18 antes; AI-18 lo documenta (aserción 27).

## Carga de datos maestros del piloto (MDM-001) — 2026-09-28

- Decisión del project owner `MDM-001-OPERATOR-LOADER`, respuesta literal: "Herramienta de operador
  (Recommended)". Traducción en `MDM-001-CONTRACT-TRANSLATION`.
- AI-06: `platform.master_data_deployment_gate`, marca global de despliegue (`SYNTHETIC` o `REAL`,
  `gate_007_closed`) con FORCE RLS, escrita sólo por el migrador (cada cambio auditado; nunca de `REAL` a
  `SYNTHETIC`); sin fila vale `REAL` con GATE-007 abierto. AI-18 le da una única política, para
  `paqueteria_migrator`.
- AI-18: `paqueteria_master_data_executor NOLOGIN BYPASSRLS`, con grants exactos por columna sobre las
  seis tablas maestras, lecturas mínimas de usuario, organización (incluido su tipo), membresía y la marca
  de despliegue e `INSERT` en `platform.audit_logs` (sin `DELETE`), y `paqueteria_master_data_loader
  NOLOGIN NOBYPASSRLS`, con sólo `USAGE` sobre `security`: una capacidad de operador de plataforma, no una
  frontera de tenant, que sólo reciben logins de operador con nombre que no sean principales de despliegue.
  Ninguno se concede a
  `paqueteria_app` ni a `paqueteria_worker`; los roles persisten tras el rollback de la lane. Aserciones de
  despliegue 26 a 28; `validate_contracts.py` verifica los grants exactos.
- La lane de Pricing (`20260928000100_AddMasterDataLoader`) instala
  `security.load_master_data(uuid,uuid,json,bytea,boolean)`, SECURITY DEFINER con
  `search_path=pg_catalog, pg_temp`, con `EXECUTE` sólo para el beneficiario del operador. Aplica
  GATE-007 y la clasificación del archivo según la marca de despliegue, sólo deja crear ciudades a una
  organización `PLATFORM` (siempre `ACTIVE`, zonas IANA de México), rechaza vigencias de tarifa
  traslapadas, valida todo el documento (incluidos los mismos límites y tokens de centavos que el job)
  antes de escribir, es idempotente por llave natural, exige el tenant exacto en `app.current_org_ids`,
  escribe una fila de auditoría por carga con un seudónimo del operador (`operator_ref`) y no escribe nada
  en dry-run. La lane elimina la sobrecarga `jsonb` de la primera versión publicada.
- AI-06 no cambia.

## Unirse a una organización existente por correo (REG-002) — 2026-09-27

- Respuestas literales del project owner: "El admin la agrega por correo (Recomendado)",
  "Tabla de invitaciones + HMAC (Recomendado)", "PLATFORM_ADMIN + admins de la org con MFA
  (Recomendado)", "Los de su tipo, incluido admin (Recomendado)", "Cualquier rol; PLATFORM_ADMIN
  sólo en la org de plataforma (Recomendado)", "Sí; el perfil se completa después (Recomendado)",
  "Sólo renueva la vigencia (Recomendado)", "7 días (Recomendado)" y "Acepta todas (Recomendado)".
- AI-06: `organizations.pending_memberships` (HMAC con llave del correo normalizado y versión de
  la llave, nunca el correo; estados PENDING, ACCEPTED y REVOKED; vence a los 7 días), FORCE RLS
  con política de tenant.
- AI-18: `paqueteria_app` sólo lee la tabla bajo RLS y `paqueteria_worker` no tiene privilegios;
  `paqueteria_registration_executor` recibe grants exactos por columna (verificados por
  `validate_contracts.py`) y la aserción 25. Sus cuatro funciones SECURITY DEFINER
  (`add_pending_membership`, `renew_pending_membership`, `revoke_pending_membership`,
  `apply_pending_memberships`) se instalan en la lane de Organizations
  (`20260927000500_AddPendingMemberships`), con `EXECUTE` sólo para `paqueteria_app`.
- AI-05: `addPendingMembership` (202 idéntica exista o no la cuenta), `listPendingMemberships`,
  `renewPendingMembership` y `revokePendingMembership` (409 `IDEMPOTENCY_CONFLICT` o
  `PENDING_MEMBERSHIP_NOT_PENDING`); `x-capability-matrix.membership_operations` con MFA.
- AI-03 §17.1, AI-04 (`PendingMembership`), AI-24 `bff_session` (`pending_memberships`) y
  AI-08 (REG-002).

## Almacén PostgreSQL de sesiones BFF — 2026-09-27

- `BFF-SESSION-STORE-IMPLEMENTATION` (implementa `BFF-SESSION-STORE-POSTGRESQL` y
  `BFF-SESSION-TABLE-SHAPE`): AI-06 agrega `identity.bff_sessions`, previa al
  tenant, con FORCE RLS y sin política; guarda el SHA-256 de la clave opaca, el
  `sub`, el `sid` de AuthCenter y el ticket cifrado con Data Protection, que la
  revocación borra.
- AI-18 agrega `paqueteria_session_executor NOLOGIN BYPASSRLS` con grants por
  columna (sin `DELETE`), revoca todo privilegio de `paqueteria_app` y
  `paqueteria_worker` sobre la tabla y registra las cinco funciones que instala
  el lane de Identity (`20260927000400_AddBffSessionStore`):
  `security.create_bff_session`, `security.resolve_bff_session(bytea)` y
  `security.revoke_bff_session` por clave, por `authcenter_sid` y por `sub`
  anterior a un momento, con `EXECUTE` solo para `paqueteria_app`. Aserciones de
  despliegue 20 a 24.
- La purga `security.purge_bff_sessions(integer)` se suma a
  `paqueteria_cleanup_executor` mediante el lane de Custody
  (`20260927000400_AddBffSessionPurge`) y un job del Worker desactivado por
  defecto. `validate_contracts.py` fija los grants exactos del nuevo rol y que la
  tabla no tenga grants de runtime ni política.
- AI-03 §17.1 y §25.2, AI-24 `bff_session` y AI-08 (OPS-003) describen el
  almacén.
- `BFF-LOGOUT-JTI-PERSISTENCE` (respuesta literal del project owner: "Sí, a
  PostgreSQL (Recomendado)"): AI-06 agrega `identity.bff_logout_jtis` (SHA-256
  del `jti`, retención `exp` + 5 min con tope de un día), previa al tenant, con
  FORCE RLS, sin política ni grants de runtime; AI-18 agrega
  `security.register_bff_logout_jti(bytea,timestamptz)` de
  `paqueteria_session_executor` (`INSERT ... ON CONFLICT DO NOTHING`, verdadero
  solo en el primer registro de cualquier réplica, `EXECUTE` solo para
  `paqueteria_app`). La API registra el `jti` y revoca las sesiones en una sola
  transacción; `security.purge_bff_sessions(integer)` también purga los `jti`
  vencidos. `validate_contracts.py` fija el grant exacto y la ausencia de grants
  de runtime sobre la nueva tabla.
## Membresía por defecto liberada al crear (REG-001) — 2026-09-27

- Respuesta literal del project owner: "Liberarla al crear (Recomendado)"
  (`REG-DEFAULT-MEMBERSHIP-RELEASE`). `create_self_service_organization` pone
  `is_default=false` en las membresías por defecto del usuario cuya organización ya no está
  ACTIVE (por ejemplo, un ALLY rechazado) y después hace por defecto la nueva membresía si no
  queda ninguna por defecto en una organización ACTIVE. Nunca toca una por defecto en una
  organización ACTIVE.
- AI-18: `GRANT UPDATE (is_default) ON organizations.organization_memberships TO
  paqueteria_registration_executor`; `validate_contracts.py` lo agrega a los grants exactos y a
  la lista de grants `UPDATE` por columna.

## Registro abierto y onboarding de organizaciones (REG-001) — 2026-09-27

- Respuestas literales del project owner: "El login y el registro de cuentas no será por
  invitación, cualquiera puede registrarse en Paquetenvia" (`AUTH-OPEN-REGISTRATION`, que
  reemplaza a `AUTH-FIRST-LOGIN-INVITATION`), "Crea su propia organización", "BUSINESS, activa;
  ALLY con aprobación (Recomendado)", "Sí, obligatorio (Recomendado)" (correo verificado),
  "Sólo activar la org (Recomendado)", "Lista + aprobar/rechazar (Recomendado)", "Queda
  cerrada; puede volver a solicitar (Recomendado)", "Endpoint propio de solicitudes
  (Recomendado)" y "solo una organización por persona". Unirse a una organización existente
  ("El admin la agrega por correo (Recomendado)") queda para un PR aparte.
- AI-06: `organizations.status` admite `PENDING_APPROVAL`; nueva columna
  `organizations.self_service_creator_user_id` e índice único parcial
  `organizations_one_open_self_service_uq` (una organización no CLOSED creada por persona).
- AI-18: rol `paqueteria_registration_executor NOLOGIN BYPASSRLS` con grants exactos por
  columna (verificados por `validate_contracts.py`) y aserciones de despliegue 17–19. Sus cinco
  funciones SECURITY DEFINER (`register_identity_subject`, `create_self_service_organization`,
  `list_own_organization_applications`, `list_pending_ally_organizations`,
  `decide_ally_organization`) se instalan en la lane de Organizations
  (`20260927000400_AddSelfServiceRegistration`), con `EXECUTE` sólo para `paqueteria_app`.
- AI-05: `createOnboardingOrganization` (POST /onboarding/organizations, 201; 409
  `IDEMPOTENCY_CONFLICT` u `ORGANIZATION_LIMIT_REACHED`), `listMyOrganizationApplications`,
  `listPendingAllyOrganizations` y `decideAllyOrganization` (409 `ALLY_DECISION_CONFLICT`);
  `x-capability-matrix.platform_operations`; el callback redirige a
  `/login?error=email_not_verified` sin `email_verified == true`.
- AI-03 §17.1/§24, AI-04 (estados de Organization), AI-07 (`/login`, `/onboarding`), AI-24
  `bff_session` (`first_access`, `email_verified`, `onboarding`) y AI-08 (REG-001).
- Límite anti-abuso decidido; sin preguntas abiertas.

## Tope de 72 h para la edad máxima de `openIncident` — 2026-09-27

- Respuesta literal del project owner: "Tope en 72 h (Recomendado)"
  (`OPS-003-INCIDENT-AGE-CAP-72H-2026-09-27`). `Incidents:MaximumOccurrenceAgeHours`
  sólo se configura hacia abajo (1–72 h, por defecto 72); un valor mayor impide el
  arranque. El tope coincide con el piso de 72 h de la purga de llaves de
  idempotencia, así que un replay cuya llave pudo purgarse siempre se rechaza.
- AI-05 `x-offline-operation-age`: `openIncident.maximum_age_range` pasa a
  `PT1H..PT72H`, se agrega la decisión y una regla; la descripción de
  `openIncident` indica el tope. AI-08 (OPS-003) registra la decisión.

## `openIncident` unificado con `OFFLINE_OPERATION_EXPIRED`, límites configurables — 2026-09-27

- Respuesta literal del project owner: "Unificar pero configurable"
  (`OPS-003-INCIDENT-72H-UNIFICATION-CONFIGURABLE-2026-09-27`), que implementa
  `OPS-003-INCIDENT-72H-UNIFICATION`.
- AI-05: `openIncident` describe la regla; `IncidentConflictProblem` agrega
  `OFFLINE_OPERATION_EXPIRED` (sólo `openIncident` lo emite);
  `x-offline-operation-age` agrega las dos decisiones y la entrada
  `openIncident` con `maximum_age_configurable: true`, sus ajustes
  (`Incidents:MaximumOccurrenceAgeHours`, `Incidents:MaximumOccurrenceSkewMinutes`),
  valores por defecto (PT72H, PT5M) y rangos (PT1H..PT720H, luego PT1H..PT72H por el tope; PT0S..PT60M); el reloj
  adelantado y la marca ausente siguen siendo 409 `INVALID_REQUEST`. Las otras
  tres operaciones conservan su política fija.
- AI-08 (OPS-003) registra la decisión y la regla de `openIncident`.

## Matriz de capacidades D5 implementada — 2026-09-27

- AI-05 `x-capability-matrix` queda `IMPLEMENTED` (PR 108): cada operación de la
  matriz decide la capacidad en el servidor antes de leer estado persistido y
  los roles no listados reciben el 403 uniforme; un 403 cuyo único requisito
  pendiente es MFA lleva `MFA_REQUIRED`.
- `listLocations` admite VIEWER con lat/lng redondeadas en el servidor a 2
  decimales (~1.1 km, mitad lejos de cero); DISPATCHER y PLATFORM_ADMIN reciben
  coordenadas exactas (`D5-VIEWER-LOCATION-PRECISION-2026-09-27`,
  `x-viewer-coordinate-precision`, `x-capability-matrix.viewer_location_precision`).
- `D7-SETTLEMENT-MFA` queda implementado: aprobar y pagar liquidaciones exige MFA
  para todo rol permitido, FINANCE incluido.

## Deltas del piloto publicados con su implementación — 2026-09-27

- AI-05 publica, junto con su código y sus pruebas: `listSettlements`
  (`GET /settlements`, filtros `payee_id`, `status`, `period_from` y
  `period_to`, schema `SettlementPage`); `Cache-Control: no-store` en
  `exportSettlementCsv`; `CreateLocationRequest` sin `pii_key_version`; los
  límites aprobados de aceptación (versiones de 1 a 64 caracteres de
  `^[A-Za-z0-9._-]+$`, `accepted_at` entre -72 h y +5 min del servidor) y de
  paquetes (1 a 20); los 503/409/429 que ya se emitían y los 400 de Locations
  (componente `BadRequest`).
- `reconcileCod`, `getOrderFinancials` y `getRouteFinancials` admiten FINANCE con
  MFA (`FINANCE-COD-RECONCILIATION`, `FINANCE-COD-MFA-2026-09-27`); DISPATCHER
  sigue sin MFA. Las entradas correspondientes de `x-pilot-contract-deltas` y
  `finance_operations_status` quedan como implementadas.
- AI-06/AI-18: `resolve_identity_context` exige organización `ACTIVE`, el
  timeline público se ordena por `(occurred_at, aggregate_version)` con su grant
  de bootstrap, y se agregan los índices parciales de purga y los cuatro
  operativos (`AI06-PILOT-INDEXES`). Las instalaciones existentes los reciben
  por la lane `PlatformEvolution`.
- `validate_contracts.py` acepta la matriz DSP-002 con `503`.

## AUTH-001 logout, back-channel y step-up — 2026-09-27

- AI-05: `POST /auth/logout` responde 200 con `BffLogoutResult`
  (`endSessionUrl`, `AUTH-001-RP-INITIATED-LOGOUT`); nuevo
  `POST /auth/backchannel-logout` (anónimo, `logout_token` form-urlencoded, 200 o
  400 `BackchannelLogoutError`, `AUTH-001-BACKCHANNEL-LOGOUT`); `GET /auth/login`
  acepta `mfa=required` (`AUTH-001-MFA-STEP-UP`); el callback documenta
  `access_denied` → `/login?error=access_denied`
  (`AUTH-001-ACCESS-DENIED-MESSAGE`); la respuesta `Forbidden` usa
  `ForbiddenProblem` con el código opcional `MFA_REQUIRED`.
- AI-03 §17.1 y AI-24 `bff_session`: cierre de sesión RP-initiated (única excepción
  a tokens en el navegador: `id_token_hint` en `endSessionUrl`), back-channel logout
  detrás de una interfaz de terminación de sesiones, step-up MFA, reemplazo de sesión
  en cada inicio y mensaje propio para `access_denied`.
- AI-07 `/login`: mensaje de `access_denied`, oferta "Verificar identidad" y regreso
  desde el end-session de AuthCenter.
- Registro: `AUTH-001-RP-INITIATED-LOGOUT`, `AUTH-001-BACKCHANNEL-LOGOUT`,
  `AUTH-001-MFA-STEP-UP` y `AUTH-001-ACCESS-DENIED-MESSAGE` en `decision-log.md`.

## AUTH-001 BFF — 2026-09-27

- AI-05 documenta la superficie BFF fuera de `/api/v1` (`servers: /` por ruta):
  `GET /auth/login`, `GET /signin-authcenter`, `GET /auth/session` y
  `POST /auth/logout`, con el esquema `bffSession` (cookie
  `__Host-Paquetenvia.Session`), el parámetro `X-AuthCenter-CSRF` y el esquema
  `BffSession` (`GATE-002-BFF-001`). La respuesta de autorización exige `iss`
  (RFC 9207).
- AI-07: `/login` es anónima (punto de entrada de sesión), sin aprovisionar
  (`AUTH-FIRST-LOGIN-INVITATION`).
- AI-03 §17.1 y AI-24 `bff_session` registran el patrón BFF, el almacén de
  sesiones en PostgreSQL (`BFF-SESSION-STORE-POSTGRESQL`, cambio AI-06/AI-18
  pendiente; en memoria y una sola réplica hasta entonces) y el enrutamiento del
  mismo origen del piloto (`PILOT-SAME-ORIGIN-ROUTING`).
- Registro: `AUTH-001-BFF-CONTRACT-TRANSLATION` en `decision-log.md`.

## Finance en AI-05: regla vigente y delta pendiente — 2026-09-27

- Las descripciones de `reconcileCod`, `getOrderFinancials` y
  `getRouteFinancials` vuelven a la regla implementada (DISPATCHER y
  PLATFORM_ADMIN con MFA). FINANCE con MFA (`FINANCE-COD-RECONCILIATION`,
  `FINANCE-COD-MFA-2026-09-27`) queda como delta pendiente en
  `x-pilot-contract-deltas` y en `x-capability-matrix`; lo publica
  `feature/pilot-contract-deltas` junto con el código. Corrige la deriva señalada
  en la revisión del PR #100. Paths y schemas no cambian.

## Decisiones del owner sobre los contratos del piloto — 2026-09-27

- Respuesta literal del project owner: "Bloquean el MVP 1 — Apruebo 2, 3, 4, 5,
  6". Quedan decididos `D8-OUTBOX-LANE-DISPATCH`,
  `AI12-ASSIGNMENT-TERMINAL-STATES`, `BFF-SESSION-TABLE-SHAPE`,
  `OPS-003-CLEANUP-ROLE` y `OPS-003-SERVER-72H-REJECTION`.
- AI-12, AI-13 §7.1, la adenda de ADR-004, AI-08 (OPS-003 y AUTH-001) y AI-10
  pasan de PROPOSED a DECIDED; la traducción a AI-06/AI-18 llega con su migración en el PR de implementación.
- Se elimina de AI-04 el bloque `proposed_amendments`: sus enmiendas de
  intento, custodia e incidencia se decidieron el mismo día y el PR #91 las
  escribe en AI-04 y en el decision-log.
- Respuesta literal del project owner: "Apruebo Contrato de la API (AI-05)".
  Quedan decididos `AI05-LIST-SETTLEMENTS`, `AI05-REMOVE-PII-KEY-VERSION`,
  `AI05-INPUT-LIMITS`, `AI05-DECLARE-EMITTED-ERRORS`, `AI05-TIMELINE-ORDER`
  (incluido el grant de bootstrap sobre `orders.order_events.aggregate_version`)
  y `AI05-EXPORT-NO-STORE`. La superficie de AI-05 cambia con su
  implementación, porque las `*OpenApiImplementationTests` comparan AI-05 con el
  código; este PR no modifica paths ni schemas.
- Respuestas literales del project owner: "Acepto todas las sugerencias para
  Base de datos y pagos contra entrega" y "Aprobado todo". Quedan decididos
  `AI06-PILOT-INDEXES`, `IDENTITY-ORG-ACTIVE-REQUIRED`,
  `FINANCE-COD-RECONCILIATION`, `QUOTE-NO-COD` y `AI08-PILOT-ITEMS`. Los siete
  ítems nuevos de AI-08 quedan aprobados y REL-001 pasa a depender también de
  AUTH-001, ENV-001 y UI-001. El inventario MVP-0/P0 sigue en 29.
- El owner eligió "Declararlos en AI-05 (Recomendado)" para las respuestas 400
  de Locations y del header de contexto tenant (`AI05-DECLARE-400-ERRORS`): se
  declaran sin cambiar el comportamiento, en el PR de implementación
  `feature/pilot-contract-deltas`. `PILOT-CONTRACTS-2026-09` pasa a
  `resolved_decisions` en AI-10; no queda ningún punto abierto.

## Contratos del piloto — 2026-09-26 (propuesta y decisiones)

- AI-05: `x-capability-matrix` (DECIDED D5) y `x-pilot-contract-deltas`, con
  entradas DECIDED (D6, D7) y PROPOSED (`listSettlements`, `pii_key_version`,
  límites de aceptación, códigos no declarados, orden del timeline). La
  superficie HTTP no cambia hasta que la tarea de implementación publique
  cada delta junto con su código y sus pruebas.
- AI-08: 63 ítems. Se agregan AUTH-001, ENV-001, ADP-001, UI-001, OBS-002,
  MDM-001 y TRK-002, marcados `PROPOSED`. Se incorporan criterios DECIDED en
  OPS-003 (72 h), CSV-001 y FIN-001 (D6) y SET-001 (D7).
- AI-04 y AI-12: enmiendas `PROPOSED` tomadas de los PRs #91 y #90. Las
  guards y los eventos vigentes no cambian.
- AI-04 `decided_pending_translation`, AI-13 §7.1 y AI-08 AUTH-001/ENV-001
  recogen las decisiones del 2026-09-26/27: reasignación con asignación nueva,
  primer ingreso por invitación, sesiones BFF en PostgreSQL, enrutamiento del
  mismo origen, claim `amr` y dominio `authcenter.info`. La forma exacta de la
  tabla de sesiones BFF en AI-06/AI-18 se propone en el PR.
- D7: `Cache-Control: no-store` pasa a PROPOSED porque no forma parte del texto
  decidido.
- AI-13 §7.1 y ADR-004: adenda D8 (dirección DECIDED; mecanismo PROPOSED).
- AI-10: decisión abierta `PILOT-CONTRACTS-2026-09`.
- AI-06 y AI-18 no cambian. Sus propuestas SQL se describen en el PR y
  requieren una migración antes de entrar a los archivos canónicos.

## ORD-002: reglas del intento en la máquina de estados — 2026-09-27

- AI-04 registra las reglas de ORD-002 (PR 91) aprobadas por el project owner
  ("Aprobar las 5"): la acción siguiente de la incidencia decide la salida de
  `FAILED_ATTEMPT` (guard `failed_attempt_next_action_respected`); la custodia
  solo se deriva del evento `PICKED_UP`; las pruebas valen solo para el intento
  vigente y nunca si son evidencia de una incidencia; una incidencia justifica un
  solo `FAILED_ATTEMPT`; el intento vigente empieza en la última entrada al estado
  de captura (`ORD-002-ATTEMPT-RULES-2026-09-27`, `ORD-002-ATTEMPT-BOUNDARY`).
- Se agrega `order_state_machine.guard_semantics`. Los 17 estados y las
  transiciones no cambian.

## Reconciliación de gobernanza — 2026-09-26

- Registro documental posterior de los ítems fusionados desde el 2026-08-04
  (entradas siguientes y `decision-log.md`). Cada entrada cita el PR y la
  autorización que consta en su cuerpo; no se infieren autorizaciones.
- Los merges de PRs que declaraban no incluir autorización de merge (#42, #43,
  #44, #45, #46, #59, #60, #79, #80, #81, #82) o que no la mencionan (#70,
  #71, #72, #73, #75, #83) se registran como hechos ejecutados por el project
  owner; la autorización formal queda como pregunta abierta
  `GOV-2026-09-MERGE-AUTHORIZATION`. Las fechas de merge están en UTC.
- Decisiones del project owner tomadas en la sesión de trabajo con el owner vía
  Claude Code del 2026-09-26 (texto literal donde consta):
  "Ramas + PR borrador; el owner fusiona" (`GOV-AGENT-INTEGRATION-001`);
  delegación en Claude de la revisión y fusión de PRs en verde desde el
  2026-09-26 (`GOV-AGENT-MERGE-DELEGATION-001`); "Ruta por main" para
  dependencias nuevas, elegida frente a "Relajar PR Gate"
  (`GOV-DEPENDENCY-ROUTE-001`); login AuthCenter con BFF en la API y cookie
  HttpOnly del mismo origen (`GATE-002-BFF-001`); cierre de asignaciones por
  reacción outbox de Dispatch (`D8-DISPATCH-OUTBOX-CLOSURE`); matriz de
  capacidades (`D5-CAPABILITY-MATRIX`); "el despachador declara el monto COD al
  crear la orden (y en el CSV)" (`D6-COD-PILOT`); "Solo se liquidan periodos ya
  cerrados; un total negativo se rechaza; aprobar y pagar requieren MFA; la
  exportación se audita" (`D7-SETTLEMENT-RULES`); "Una operación offline de la
  PWA puede reintentarse hasta 72 horas; después se descarta y se avisa al
  conductor. Las llaves de idempotencia se purgan después de ese plazo."
  (`OPS-003-OFFLINE-72H`).
- Decisiones del 2026-09-26/27 en la misma sesión: reasignación siempre con
  asignación nueva y cierre CANCELLED de la anterior al reprogramar
  (`D8-REASSIGNMENT-NEW-ASSIGNMENT`); primer ingreso por invitación previa
  (`AUTH-FIRST-LOGIN-INVITATION`); sesiones BFF en una tabla PostgreSQL
  (`BFF-SESSION-STORE-POSTGRESQL`); enrutamiento del mismo origen en el ingress
  de Azure (`PILOT-SAME-ORIGIN-ROUTING`); claim `amr` tras MFA en AuthCenter
  (`AUTHCENTER-AMR-MFA`); dominio `authcenter.info` (`AUTHCENTER-DOMAIN`).
- AI-05, AI-04, AI-06, AI-18 y la implementación no cambian con estas
  decisiones; hoy el código no las cumple y su traducción a contratos queda
  pendiente en `governance/pilot-contracts`.
- AI-10 incorpora `REL-000-OWNER-001` y un registro de integración post-MVP-0
  sin cambiar gates abiertos.
- `CANONICAL_SOURCE_OF_TRUTH.md` y `CLAUDE_VALIDATION_HANDOFF.md` citan los
  hashes vigentes de AI-06 y AI-18.
- No cambian AI-02, AI-04, AI-05, AI-06, AI-08, AI-12, AI-18, SQL, roles,
  migraciones ni código de producción.

## OPS-003 limpieza operativa y rechazo de operaciones offline de más de 72 h — 2026-09-27

- `OPS-003-CLEANUP-ROLE`: AI-18 agrega `paqueteria_cleanup_executor NOLOGIN BYPASSRLS`
  con grants exactos por columna (más `DELETE` de tabla sobre
  `platform.idempotency_keys`, que no tiene forma por columna) y las aserciones
  de despliegue 13 a 16. Las funciones
  `security.purge_expired_idempotency_keys(timestamptz,integer,boolean)` (piso
  fijo de 72 h) y `security.expire_proof_upload_sessions(integer)` las instala
  el lane de Custody (`20260927000100_AddOperationalCleanupExecutor`), como
  ADR-034; AI-06 no cambia. `validate_contracts.py` admite exactamente los dos
  grants `UPDATE (...)` por columna y fija los grants del nuevo rol.
- `OPS-003-SERVER-72H-REJECTION`: AI-05 agrega `x-offline-operation-age`,
  `client_occurred_at` opcional en `TransitionRequest` y
  `CreateProofUploadSessionRequest`, y las respuestas `TransitionConflict` y
  `ProofConflict` con el código `OFFLINE_OPERATION_EXPIRED`.
- AI-03 §25.2 y AI-08 (OPS-003) describen el rol, las funciones y los jobs.

## OPS-004 retención acotada del outbox — 2026-09-26 UTC (PR #81)

- Job de Worker sobre las funciones de purga aprobadas de AI-06/AI-18
  (ADR-030), deshabilitado y en dry-run por defecto; sin migración.
- El cuerpo del PR declara "Draft. Do not merge"; merge ejecutado por el
  project owner; autorización formal pendiente.

## LIF-001 finalización de ventanas de reclamación — 2026-09-26 UTC (PR #83)

- ADR-034: `paqueteria_lifecycle_executor NOLOGIN BYPASSRLS` y
  `security.finalize_expired_orders(integer)`; AI-18 y AI-03 actualizados con
  autorización normativa del project owner registrada en el PR.
- El merge no menciona autorización en el cuerpo del PR. El bridge de Azure
  no cubre el nuevo rol; no hay despliegue autorizado.

## SET-001 flujo de liquidaciones y exportación CSV — 2026-09-26 UTC (PR #82)

- AI-05 agrega siete operaciones de liquidación (41 paths, 79 schemas, 371
  refs); no existe `listSettlements`.
- El cuerpo del PR declara que no se autoriza merge ni despliegue; merge
  ejecutado por el project owner; autorización formal pendiente.

## INC-001 resolución de incidencias — 2026-09-25 UTC (PR #80)

- AI-05 agrega `resolveIncident` y `INCIDENT_STATE_CONFLICT` de forma aditiva.
- El cuerpo del PR describe el seguimiento como autorizado por el owner y
  declara "Draft. Do not merge"; autorización formal de merge pendiente.

## SET-001 integridad del ledger — 2026-09-25 UTC (PR #79)

- Primera migración de Finance (`20260925000100`) sobre las tablas canónicas
  de AI-06, sin modificar AI-05, AI-06 ni AI-18.
- El cuerpo del PR declara que merge, auto-merge y despliegue no están
  autorizados; autorización formal de merge pendiente.

## Promoción development → main — 2026-09-24 UTC (PR #75)

- Promueve FIN-001, SCL-001, INC-001 y CSV-001 a `main`.
- El cuerpo del PR no contiene declaración de autorización de merge;
  autorización formal pendiente (`GOV-2026-09-MERGE-AUTHORIZATION`).

## CSV-001 carga CSV de órdenes — 2026-09-24 UTC (PR #70)

- AI-05 agrega `previewOrderCsv` y `commitOrderCsv`. El cuerpo del PR no
  contiene declaración de autorización.

## INC-001 incidencias e intento fallido — 2026-09-24 UTC (PR #71)

- AI-05 alinea `POST /orders/{orderId}/incidents` con autorización explícita
  del owner registrada en el PR; el merge no menciona autorización.

## SCL-001 hosts stateless — 2026-09-24 UTC (PR #73)

- Key ring de Data Protection en PostgreSQL y atribución de claims por
  réplica. Requiere protector externo de claves antes de activación
  productiva. El cuerpo del PR no contiene declaración de autorización.

## FIN-001 COD y economía por orden — 2026-09-24 UTC (PR #72)

- AI-05 agrega `getOrderFinancials` y `getRouteFinancials` y alinea las dos
  operaciones COD; cambio autorizado por el owner y limitado a esas cuatro
  operaciones según el PR. El merge no menciona autorización.

## AZR-001 fase A — 2026-09-21 UTC (PR #61)

- Controles de repositorio para despliegue DEV-SYNTHETIC. Merge controlado
  autorizado por el owner según el PR, condicionado al head `e1ebce8` y a
  Foundation 35568160027 13/13.

## AZR-001 bridge DEV-SYNTHETIC y corrección E-002 — 2026-09-20 UTC (PRs #59 y #60)

- #59 registra implementación autorizada y merge no autorizado; #60 declara
  "Merge is NOT authorized". Ambos fusionados por el project owner;
  autorización formal pendiente. Azure Clean path sin ejercitar.

## SEC-003 política de entorno sintético — 2026-09-15 UTC (PR #56)

- Merge autorizado por el project owner el 2026-09-15 según el PR; no autoriza
  producción ni datos reales.

## RTE-001 rutas manuales — 2026-09-13 UTC (PR #53) y contrato — 2026-08-29 UTC (PR #46)

- #46 agrega a AI-05 las seis operaciones de rutas y alinea AI-07, AI-08 y
  AI-12 (sin autorización de merge en el cuerpo).
- #53 implementa rutas OWN-only con `merge_authorized = true` limitado a
  `415f57a`.

## EXT-001 ofertas externas — 2026-08-28 UTC (PRs #44 y #45)

- #44 agrega `GET /driver/me/external-offers` y `ExternalOffer.expires_at` a
  AI-05 y alinea AI-07/AI-08; #45 implementa las ofertas. Ambos cuerpos
  declaran "No merge authorization is included"; autorización formal pendiente.

## Entorno local de pruebas manuales — 2026-08-27 UTC (PR #43)

- Sin cambios normativos. El cuerpo declara "No merge authorization is
  included"; autorización formal pendiente.

## NTF-001 notificaciones outbox — 2026-08-26 UTC (PR #42)

- Vertical sintética `orders.created` → `OWNER_ORG_DISPATCHERS` → `IN_APP`
  según NTF-001-OWNER-001/002/003; sin cambios normativos. El cuerpo declara
  "No merge authorization is included"; autorización formal pendiente.

## Decisiones funcionales NTF-001 — 2026-08-04

- El Project owner aprobó `orders.created` como trigger inicial,
  `OWNER_ORG_DISPATCHERS` como audiencia lógica inicial e `IN_APP` como canal
  lógico inicial de la primera vertical de NTF-001.
- La vertical es exclusivamente sintética: utiliza un provider fake
  determinista, template neutral, versionado y tenant-scoped, sin fallback
  cross-tenant, conexiones externas, secretos ni PII real.
- `SENT` significa aceptación por el sink sintético, no lectura humana. No se
  autorizan email, SMS, WhatsApp, push, inbox, dashboard, endpoint, read model,
  destinatarios reales ni provider real.
- Esta actualización es únicamente documental y de gobernanza. No modifica
  AI-05, AI-06, AI-08, AI-10, AI-12 o AI-18, ni código, SQL, migrations, roles,
  tests funcionales o composición runtime.
- La implementación todavía no está autorizada; NTF-001 y MVP-1 continúan sin
  iniciar. GATE-001, GATE-004 y GATE-007 permanecen sin resolver.
- El registro prepara una autorización posterior y separada. No autoriza
  deployment, piloto, producción o go-live, y no inicia EXT-001.

## Aprobación REL-000-OWNER-001 — 2026-08-02 (registro posterior)

- El project owner declaró "Apruebo REL-000" (PR #36), limitado a
  `MVP-0_INTERNAL` con datos sintéticos.
- No autoriza piloto, producción, despliegue, go-live, PII real ni el inicio
  automático de EXT-001.
- El valor histórico `rel000_approved: false` de REL-000-DEF-001 (2026-07-31)
  se conserva; AI-10 registra la aprobación posterior como entrada separada.

## Resolución de alcance REL-000-DEF-001 — 2026-07-31

- El project owner aprobó la opción A: FIN-001 pasa completo de MVP-0 a MVP-1.
- FIN-001 conserva prioridad P0, alcance funcional y dependencias DSP-002,
  EXT-001 y RTE-001; no se crea una variante financiera reducida en MVP-0.
- El inventario MVP-0/P0 pasa de 30 a 29 elementos y REL-000 deja de depender
  normativamente de FIN-001.
- EXT-001 continúa dependiendo de REL-000 y no queda autorizado ni iniciado.
- La decisión resuelve REL-000-DEF-001, pero no aprueba REL-000,
  MVP-0_INTERNAL ni la liberación.
- Bundle emitido como `v0.6-full-canonical-sync-7-fin001-mvp1`; no cambian
  contratos funcionales, OpenAPI, SQL, roles ni SignalR.

## Cursores de resincronización RTM-002 — 2026-07-24 (registro posterior)

- Commit `4738861`: AI-05 agrega `order_id` y `aggregate_version` (int64,
  mínimo 1) obligatorios a `DriverStop` y `aggregate_version` obligatorio a la
  proyección pública de tracking.
- AI-06: `security.get_public_tracking_projection` agrega `aggregate_version` (`o.version`)
  al JSON público.
- AI-18: `paqueteria_bootstrap` recibe `SELECT` sobre la columna `version` de
  `orders.orders` (grant de columnas `id,public_id,status,version`).
- Decisiones asociadas en `decision-log.md`:
  `RTM-002-INPROCESS-DISPATCHER-COLOCATION`, `RTM-002-LOCATION-CURSOR-UTC-MS` y
  `RTM-002-STABLE-OUTBOX-ID-AS-EVENT-ID`. El cambio no tenía entrada en este
  changelog; se registra sin alterar los archivos afectados.

## Capability antes de estado persistido DSP-002 — 2026-07-24

- `DSP-002-CAPABILITY-BEFORE-PERSISTED-STATE` separa validación de forma de
  acceso productivo: `INVALID_REQUEST` puede preceder capability sin abrir la
  transacción.
- Todo request válido relee autorización tenant-aware antes del advisory lock,
  fila idempotente, evidencia de replay o recursos de orden/driver.
- Viewer, Driver y `PLATFORM_ADMIN` sin MFA reciben el mismo 403 para key
  inexistente, completada, con hash distinto o incompleta.
- Dispatcher y admin con MFA conservan creación/replay 201 y conflictos 409.
- El hash canónico, replay histórico, schema, migraciones, AI-06 y AI-18 no
  cambian.

## Visibilidad no enumerable DSP-002 — 2026-07-23

- `DSP-002-NON-ENUMERABLE-VISIBILITY` interpreta AI-04/ADR-023 de forma
  capability-first: un actor sin capacidad recibe 403 antes de resource access.
- El actor autorizado ejecuta siempre `order_packages` y
  `driver_profile_documents` antes de decidir el 404 uniforme.
- Orden/driver missing o cross-tenant comparte componente, plan lógico,
  Problem Details y rollback sin efectos.
- No se usan delays, jitter, cronómetros de seguridad, retries ficticios ni
  consultas deliberadamente costosas.
- Replay reautoriza antes de leer evidencia y sigue sin reevaluar elegibilidad.
- AI-06 y AI-18 permanecen byte-identical.

## Remediación contractual DSP-002 — 2026-07-23

- AI-05 declara 409 para conflictos DSP-002 mediante Problem Details con los
  códigos públicos `INVALID_REQUEST`, `CONFLICT`, `DRIVER_INELIGIBLE` y
  `DRIVER_DOCUMENT_EXPIRED`.
- La matriz de `assignDriver` queda 201/401/403/404/409 conforme a AI-04 y
  ADR-023; orden o conductor ausente/cross-tenant usa 404 uniforme.
- `route_id` es nullable, pero DSP-002 solo admite ausencia o `null` hasta
  RTE-001.
- El vocabulario global conserva `OWN`, `EXTERNAL`, `ALLY_CAPACITY`; DSP-002
  habilita solo `OWN`, EXT-001 reserva `EXTERNAL` y ALY-004 reserva
  `ALLY_CAPACITY`.
- AI-06 y AI-18 permanecen intactos. La adopción de Dispatch se endurece para
  rechazar drift de checks, FKs, acciones, índice parcial, RLS y policy.
- La decisión queda registrada como `DSP-002-CONTRACT-REMEDIATION`.

## Resolución de gates — 2026-07-21

- GATE-002 (proveedor de identidad) RESUELTO: se adopta AuthCenter, servidor OIDC propio, con SLA comprometido de 99.5%, hospedaje en Azure Mexico Central y custodia de llave en Azure Key Vault.
- Migración verificada de access tokens a RS256: los consumidores validan contra JWKS público sin poseer material de firma.
- Separación normativa registrada: AuthCenter autentica, Paquetería autoriza la tenencia por organización.
- Pendiente antes de producción: rotación de llaves, rotación de la llave de desarrollo previamente embebida y confirmación de topología para el SLA.
- Actualización únicamente documental: no modifica contratos normativos v0.6, SQL, roles, endpoints ni código.

## Revisión canónica v0.6 sync 3 — 2026-07-21

- ARC-002 pasa de `PARTIAL` a `DONE` después de que la remediación normativa y
  los cinco jobs del PR pasaran.
- `purge_outbox` y `purge_location_outbox` ya no usan `FOR UPDATE SKIP LOCKED`.
- El `DELETE` revalida ID, estado terminal y cutoff sobre la fila objetivo.
- ADR-030 se conserva: maintenance continúa con `SELECT,DELETE`, sin `UPDATE`.
- AI-18 permanece sin cambios y conserva su checksum.
- La ejecución canónica se alinea a PostgreSQL 18/PostGIS 3.6.
- Purga real, límites, idempotencia y concurrencia se validan en Testcontainers.
- Bundle emitido como `v0.6-full-canonical-sync-3-arc002-purge-remediation`.

## Registro de decisiones v0.7 — 2026-07-21

- ADR-032 aceptado como referencia de diseño para v0.7: tracking en vivo, `PICKUP_IN_PROGRESS` y verificación segura de entrega.
- ADR-033 aceptado como referencia de diseño para v0.7: inventario de sellos de integridad y cadena de custodia física.
- Se registran también el contrato conjunto del guard ADR-032↔ADR-033 y su plan de pruebas SQL/Testcontainers.
- Esta actualización es únicamente aditiva y documental: no modifica contratos normativos v0.6, SQL, roles, endpoints, migraciones ni código.
- La implementación continúa bloqueada por GATE-007/GATE-013, el delta normativo coordinado y la ejecución de pruebas en PostgreSQL/PostGIS real.

## v0.6 — 2026-07-20

- `pgcrypto` aislado en `extensions`; PostGIS permanece en `public` protegido.
- Token de tracking con contrato SHA-256 simétrico C#/SQL sobre UTF-8 exacto.
- Outbox business/GPS con `lease_token`, settle, stale requeue, dead-letter y purga.
- Runtime sin SELECT/UPDATE/DELETE directo sobre outbox; inserts sin `RETURNING`.
- Estados públicos normativos y timeline privado por defecto con `public_event_code`.
- `order_acceptances` RLS/append-only con canonicalización legal fija.
- Todo dinero migrado a `bigint`/OpenAPI `int64`.
- Provisioning transaccional de usuarios/organizaciones bajo RLS.
- `external_offers` recupera aceptante, fecha y versión.
- Lane GPS sin FK y con `UNIQUE(driver_position_id)`.
- OPS-004 y ADR-026 a ADR-031.
- Backlog ampliado a 56 tareas.

## v0.5 — 2026-07-20

- Roles runtime NOBYPASSRLS; bootstrap y claim cross-tenant mediante funciones de roles NOLOGIN dedicados.
- Grants append-only y mutabilidad por columna del outbox.
- Contexto tenant transaction-local, retry-safe y preparado para PgBouncer.
- Esquema físico por módulo y FKs cross-schema gobernadas.
- Cotización de uso único, `pricing_tier`, piso congelado y snapshot sin PII.
- Modelo multi-ciudad inicial.
- POD por URL firmada/cuarentena y GPS batch con lane separado.
- Política 401/403/404, COD mínimo, líneas de liquidación y ventana de reclamación.
- Backlog ampliado a 53 tareas y nuevos ADR-016 a ADR-025.

## v0.4 — 2026-07-20

- Corrige contrato Quote→Order con ubicaciones y snapshots persistidos.
- Introduce membresías y roles por organización.
- Endurece RLS con FORCE, roles separados y políticas para tablas sensibles/hijas.
- Corrige máquina de estados de cancelación, custodia y reintentos.
- Añade ingesta REST de posiciones y contrato SignalR consistente.
- Corrige OpenAPI, idempotencia, audit schema y `Order.version`.
- Corrige FK de tarifas, índice de outbox y nombres de tablas de escalabilidad.
- Corrige numeración ADR, plantilla de lectura y referencia a documentos inexistentes.
- Backlog: 48 tareas, incluyendo ARC-002 y DRV-003; OBS-001 asciende a P0.

## v0.3 — 2026-07-19

- Arquitectura y escalabilidad progresiva formalizadas.
