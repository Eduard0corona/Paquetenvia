# ADR-NTF-002: avisos al destinatario por WhatsApp y correo

- Estado: aceptado (NTF-RECIPIENT-NOTICES-2026-10-20); **sin implementar y apagado**. GATE-004 y GATE-007 siguen
  abiertas.
- Fecha: 2026-10-20
- Alcance: contratos v0.6 (AI-03, AI-04, AI-05 `x-pilot-contract-deltas`, AI-06 y AI-18 como contrato declarado,
  AI-07, AI-08 NTF-002 a NTF-008, AI-09, AI-12, AI-13 §7.2 y AI-24 `recipient_notices`). El código llega con cada
  ítem de AI-08; la secuencia está en `docs/development/ntf-recipient-notices.md`.

## Contexto

`PILOT-SCOPE-ALL-ENABLED` ("Empezaremos con todo habilitado, todo lo que haga falta hay que desarrollarlo.") y
`GATE-004-CHANNELS` (WhatsApp por Meta Cloud API, correo por Azure Communication Services) piden avisar al
destinatario. Hoy nada convierte un cambio de la orden en un mensaje al cliente: los adaptadores de GATE-004 existen,
apagados, pero no hay productor, fuente de destinatario, consentimiento, bajas ni acuses; `AddressInput` no tiene
correo; `notification_templates` solo admite `IN_APP`; y el parser de tiempo real descarta las filas de WhatsApp y
correo. Preguntas y respuestas literales del project owner (textos copiados del registro de la sesión):

1. E4 (2026-10-19): "Avisos al destinatario: con tu decisión "Empezaremos con todo habilitado" toca construirlos (WhatsApp y correo, apagados hasta cerrar GATE-004). ¿Qué avisos manda el piloto?" → "Los 4 del plan (Recommended)" (descripción: "Enlace de seguimiento al crear la orden, "Va en camino", "No se pudo entregar" y "Entregado", por WhatsApp. Por correo solo si hay correo del destinatario: hoy no se captura, se agrega como campo opcional en la nueva orden."). No elegidas: "Solo enlace y entregado" ("Dos avisos por WhatsApp: el enlace al crear la orden y "Entregado". Menos plantillas que aprobar con Meta."); "Solo el enlace" ("Un aviso por WhatsApp con el enlace de seguimiento al crear la orden.").
2. E4-P1 (2026-10-19): "Avisos por WhatsApp: Meta solo permite escribir a quien dio su número y aceptó recibir mensajes de esa empresa. Aquí el teléfono lo da quien envía, no quien recibe. ¿Cómo obtenemos ese permiso?" → "Casilla en la orden (Recommended)" (descripción: "En la nueva orden: "El destinatario aceptó recibir avisos de su envío por WhatsApp". Si no se marca, no se manda WhatsApp (sí correo si hay). Queda registro de quién la marcó y cuándo."). No elegidas: "Por contrato" ("Los términos obligan a la empresa cliente a tener ese permiso y se manda a todos sin casilla. Más simple, con más riesgo de que Meta limite o bloquee el número."); "El destinatario lo pide" ("No se manda el primer mensaje: quien envía comparte el enlace y el destinatario pide avisos desde la página de seguimiento. Contradice "enlace al crear la orden".").
3. E4-P2 (2026-10-19): "Para mandar avisos, el proceso de fondo (Worker) necesita leer el teléfono y el correo, que hoy solo la API puede abrir; también necesita la llave que arma el enlace de seguimiento. ¿Cómo lo hacemos?" → "Llave propia para avisos (Recommended)" (descripción: "Al cotizar se guarda además una copia cifrada del teléfono y del correo con una llave que solo sirve para avisos. El Worker solo abre esa llave, nunca direcciones, nombres ni descripciones. Una llave más en Key Vault (centavos al mes)."). No elegidas: "La misma llave de hoy" ("Más simple, pero el Worker podría abrir también direcciones y nombres.").
4. E4-P3 (2026-10-19): "¿Cuándo sale el aviso "Va en camino"? En la app del repartidor hay dos momentos: "Iniciar traslado" (sale con el paquete) y "Llegué al destino" (ya está en la puerta)." → "Al iniciar traslado (Recommended)" (descripción: "Avisa con tiempo. En rutas con varias paradas puede llegar horas antes; el texto no promete hora."). No elegidas: "Al llegar al destino" ("El texto sería "Tu repartidor llegó", no "va en camino"."); "En los dos" ("Cinco avisos en total y una plantilla más que aprobar con Meta.").
5. E4-P4 (2026-10-19/20): "Aviso con el enlace "al crear la orden": el asistente crea y confirma en un paso, pero una orden puede quedarse en borrador (si falla la confirmación o en la carga CSV). ¿Cuándo se manda?" → "Al quedar confirmada (Recommended)" (descripción: "Nunca se manda el enlace de una orden que no llegó a confirmarse."). No elegidas: "Al crearla" ("Se manda aunque la orden siga en borrador.").
6. E4-P5 (2026-10-19): "Aviso "No se pudo entregar": el repartidor elige un motivo (no había nadie, dirección no encontrada, rechazó, riesgo de seguridad…) y despacho decide si se reprograma o se devuelve. ¿Qué dice el aviso? (Si falla la recolección no se avisa al destinatario.)" → "Sin motivo, con enlace (Recommended)" (descripción: ""Intentamos entregar y no fue posible; en el seguimiento verás el siguiente paso". No expone motivos delicados."). No elegidas: "Con el motivo" ("Requiere decidir qué motivos se pueden mostrar al público (cambio de contrato)."); "Esperar a despacho" ("No se avisa del intento; se manda "Se reprogramó" o "Se devolverá" cuando despacho decida.").
7. E4-P6 (2026-10-19/20): "Textos de los 4 avisos: borradores neutros, sin marca (GATE-001), categoría "utilidad" de Meta, español de México, con botón "Ver seguimiento". Ejemplo: "Tu envío con número de guía {{1}} ya va en camino a la dirección de entrega. Síguelo con el botón Ver seguimiento." ¿Cómo los quieres?" → "De "tú", sin nombres (Recommended)" (descripción: "Como el ejemplo: sin nombre de la empresa que envía ni de nadie. Los 4 textos completos quedan en el PR para que los revises antes de mandarlos a Meta."). No elegidas: "Con la empresa que envía" (""Farmacia X te envió…": da más confianza y reduce la sospecha de fraude, pero ese nombre lo escribe el cliente y puede ser el de una persona."); "De "usted"" ("Mismo contenido, en trato formal, como la llamada enmascarada.").
8. E4-P7 (2026-10-19/20): "Meta muestra un nombre junto al número de WhatsApp y suele rechazar nombres genéricos como "Seguimiento de envío". Mientras la marca (GATE-001) siga abierta, ¿qué nombre usamos?" → "La razón social (Recommended)" (descripción: "Es el responsable legal del aviso de privacidad y no es la marca comercial."). No elegidas: ""Paquetenvia"" ("Choca con la decisión de marca neutra en lo que ve el destinatario (GATE-001)."); "Esperar a la marca" ("No se da de alta el número hasta cerrar GATE-001; retrasa los avisos.").
9. E4-P8 (2026-10-19/20): "Meta exige respetar a quien pide no recibir más mensajes. ¿Cómo deja de recibir avisos el destinatario?" → "Las tres vías (Recommended)" (descripción: "Responder "BAJA" o tocar "No recibir avisos" en WhatsApp, un enlace en cada correo, y un botón "Detener avisos" en la orden para cuando lo pida por teléfono. Aplica a todas las empresas que le envíen, porque el número de WhatsApp es uno solo."). No elegidas: "Igual, solo esa empresa" ("Las mismas tres vías, pero la baja solo aplica a la empresa que envió ese paquete."); "Solo botón en la orden" ("Despacho la registra a mano. Mínimo, y frágil frente a la política de Meta.").
10. E4-P9 (2026-10-20): "Si el destinatario responde al WhatsApp de avisos (nadie lee ese número), ¿qué pasa?" → "Respuesta automática (Recommended)" (descripción: ""Este número solo envía avisos. Para dudas sobre tu envío contacta a quien te lo envió." Se cobra como mensaje de servicio."). No elegidas: "No responder" ("Solo se procesa "BAJA"; lo demás se ignora."); "Reenviar a despacho" ("Se reenvía a un número de atención de despacho; necesita ese número y alguien que conteste.").
11. E4-P10 (2026-10-20): "Horario sin mensajes (hora de Mazatlán): ¿se limitan los avisos a cierto horario?" → "8:00 a 21:00 (Recommended)" (descripción: "Lo que pase fuera de ese horario se manda a las 8:00, salvo que ya no aplique (por ejemplo, "Va en camino" si ya se entregó)."). No elegidas: "Sin horario" ("Se manda en cuanto pasa, a cualquier hora."); "8:00 a 21:00, descartar" ("Lo que pase fuera de ese horario no se manda.").
12. E4-P11 (2026-10-20): "Cuando el destinatario tiene correo, ¿se le avisa por WhatsApp y correo, o el correo solo como respaldo?" → "Por los dos siempre (Recommended)" (descripción: "Más simple y no depende de los avisos de entrega de Meta; el correo cuesta casi nada."). No elegidas: "Correo como respaldo" ("Correo solo si WhatsApp falla de forma definitiva (sin WhatsApp, bloqueado o dado de baja). Necesita los avisos de entrega de Meta y puede tardar minutos."); "Nunca correo si hubo WhatsApp" ("El correo solo sale cuando no hay WhatsApp que mandar.").
13. E4-P12 (2026-10-20): "Correo de avisos: ¿desde qué dirección y con qué nombre?" → "avisos@paquetenvia.com (Recommended)" (descripción: "Nombre visible "Seguimiento de envío", sin buzón de respuestas. Coherente con la marca neutra; el dominio ya es el del enlace."). No elegidas: "Con buzón que alguien lea" ("seguimiento@paquetenvia.com con un buzón que alguien revise; más trabajo de operación."); "Nombre = razón social" ("La dirección de avisos con el nombre de la razón social, igual que en WhatsApp.").
14. E4-P13 (2026-10-20): "¿La empresa que envía (el remitente) también recibe los avisos de sus envíos?" → "Fuera de este trabajo (Recommended)" (descripción: "El despachador ya ve los estados en la plataforma; se decide después, con el portal de negocios (BUS-001)."). No elegidas: "Incluirlos ahora" ("Las mismas 4 plantillas para el remitente; más permisos que registrar y más costo.").
15. E4-P14 (2026-10-20, ronda F sobre los borradores de este cambio): "Textos de avisos (#288): el de «No se pudo entregar» dice «Hoy intentamos entregar…», pero un intento después de las 21:00 se manda a las 8:00 del día siguiente. Y el correo, que no tiene botón, repite «con el botón Ver seguimiento» antes del enlace. ¿Los corregimos?" → "Corregir los dos (Recommended)" (descripción: "WhatsApp: «Intentamos entregar tu envío con número de guía {{1}} y no fue posible. Con el botón Ver seguimiento verás el siguiente paso.» (como tu respuesta E4-P5). Correo: «con el botón Ver seguimiento» pasa a «en el enlace de abajo» en los 4 avisos."). No elegidas: "Dejar como están" ("Los textos quedan igual. Un aviso que se manda a la mañana siguiente diría «Hoy» aunque el intento fue ayer.").
16. E4-P15 (2026-10-20, ronda F sobre los borradores de este cambio): "Cuando el destinatario escribe BAJA por WhatsApp, hoy no se le contesta nada. ¿Le confirmamos la baja?" → "Sí, una línea (Recommended)" (descripción: "«Listo. Ya no recibirás avisos en este número.» Una sola vez, por la misma vía que la respuesta automática (mensaje de servicio). En el correo la página de baja ya lo confirma en pantalla."). No elegidas: "No contestar" ("Se registra la baja sin mensaje. Más simple; la persona no sabe si funcionó.").
17. E4-P16 (2026-10-20, ronda F sobre los borradores de este cambio): "Una baja aplica a todas las empresas y hoy es definitiva: ni una casilla nueva ni despacho la deshacen. ¿Cómo vuelve a recibir avisos quien se dio de baja?" → "No en el piloto (Recommended)" (descripción: "La baja es definitiva durante el piloto. Si alguien la pidió por error, no hay forma de reactivarla; se revisa al terminar el piloto. Sin trabajo extra."). No elegidas: "Escribir ALTA lo reactiva" ("El destinatario escribe ALTA al WhatsApp (o usa un enlace en el correo) y vuelve a recibir avisos. Solo él puede hacerlo, nunca despacho. Más trabajo en el PR de entrada de mensajes (NTF-006).").

Es un cambio de alcance legal y de privacidad (AI-01 §7): nuevos encargados que reciben datos del destinatario
(Meta y Microsoft), una llave nueva que abre el Worker, un almacén de bajas con hashes con llave y un rol de base de
datos nuevo. Restricciones: sin PII en logs, outbox, eventos ni auditoría (AI-01 regla 9); el seguimiento público solo
expone el mapa de AI-04 (regla 11); solo los cinco flujos de AI-13 §4 (regla 18); outbox con valores completos, sin
`RETURNING` y con `lease_token` (reglas 7 a 9); API y Worker `NOBYPASSRLS` (regla 3); ningún dato real antes de
GATE-007.

## Decisión

1. **Cuatro avisos, cinco transiciones, sin estados nuevos.** "Enlace de seguimiento" en `DRAFT→CONFIRMED` (E4-P4),
   "Va en camino" en `PICKED_UP→IN_TRANSIT` ("Iniciar traslado", E4-P3), "No se pudo entregar" en
   `IN_TRANSIT→FAILED_ATTEMPT` y `DELIVERING→FAILED_ATTEMPT` (nunca si falla la recolección) sin motivo (E4-P5) y
   "Entregado" en `DELIVERING→DELIVERED`. Cada aviso se revisa al enviarse; si ya no aplica termina `CANCELLED`
   (`NOTICE_SUPERSEDED`) sin llamar al proveedor.
2. **Productor en ORD-002, sin sexto flujo.** Orders escribe `orders.recipient-notice-requested`
   (`recipient-notice-requested-v1`, solo ids) en la misma transacción de la transición, como el lane DISPATCH de D8.
   El lane NOTIFICATIONS lo expande a una notificación por canal; expansión, envío, acuses y bajas escriben solo
   filas de Notifications más el outbox y la auditoría compartidos.
3. **Canales.** WhatsApp solo si la orden registró la casilla "El destinatario aceptó recibir avisos de su envío por
   WhatsApp" (E4-P1, versión `NTF-RECIPIENT-WHATSAPP-CONSENT-V1`, tabla append-only con quién y cuándo). Correo solo
   si el destino tiene correo, campo opcional nuevo en "Nueva orden" (E4). Si aplican los dos, se mandan los dos,
   independientes (E4-P11).
4. **Llave propia para avisos (E4-P2).** Al cotizar, la API guarda el teléfono y el correo del destino con la llave
   PII (como hoy el teléfono) y además una copia con `recipient-notice-kek`. La API solo envuelve con esa llave; el
   Worker solo la desenvuelve y nunca alcanza la llave PII, así que no puede abrir direcciones, nombres ni
   descripciones. El Worker descifra en memoria, sin transacción abierta, y solo tras revisar permiso, vigencia y
   baja. También lee la llave del enlace de seguimiento: deriva el token en memoria y lo compara con el hash
   guardado antes de usarlo.
5. **Textos (E4-P6, E4-P12).** De "tú", sin marca (GATE-001) ni nombre de nadie, categoría UTILITY, `es_MX`, botón
   "Ver seguimiento" con el token como sufijo y respuesta rápida "No recibir avisos". Los cuatro textos y los
   asuntos de correo son los borradores aprobados y quedan en AI-24 `recipient_notices.templates` para que el owner
   los revise antes de enviarlos a Meta. El correo sale de `avisos@paquetenvia.com` con el nombre "Seguimiento de
   envío" y sin buzón de respuestas; el número de WhatsApp muestra la razón social (E4-P7). Correcciones de E4-P14:
   "No se pudo entregar" empieza con "Intentamos entregar" (sin "Hoy", porque un intento después de las 21:00 sale a
   las 8:00 del día siguiente), y el correo, que no tiene botones, dice "en el enlace de abajo" donde WhatsApp dice
   "con el botón Ver seguimiento"; los cuatro cuerpos de correo quedan escritos en AI-24.
6. **Horario (E4-P10).** Solo de 8:00 a 21:00 hora de `America/Mazatlan`; lo que pase fuera espera a las 8:00 y se
   manda solo si sigue aplicando. Un aviso de más de 12 h (sin contar esa espera) no se manda, y hay topes de 8 por
   orden y canal y de 500 por canal al día.
7. **Bajas globales (E4-P8).** Responder BAJA (o ALTO, STOP, CANCELAR) o tocar "No recibir avisos", el enlace de cada
   correo (con `List-Unsubscribe` de un clic) y "Detener avisos" en la orden. La baja aplica a todas las
   organizaciones y se guarda solo como HMAC-SHA256 con llave del teléfono o correo normalizado. Una baja por WhatsApp
   recibe una sola confirmación, "Listo. Ya no recibirás avisos en este número.", por la vía de la respuesta
   automática, solo cuando ese mensaje registra la baja y fuera del límite de una respuesta automática cada 24 h
   (E4-P15); la página de baja del correo la confirma en pantalla y "Detener avisos" no manda nada. La baja es
   definitiva durante el piloto: ni una casilla, ni despacho, ni un mensaje del destinatario la reactivan; se revisa
   al terminar el piloto (E4-P16).
8. **Respuesta automática (E4-P9).** A cualquier otro mensaje entrante se responde una vez, como mensaje de texto
   libre dentro de la ventana de atención de 24 h: "Este número solo envía avisos. Para dudas sobre tu envío contacta
   a quien te lo envió." El webhook no llama al proveedor: guarda el número sellado con la llave de avisos y el
   Worker lo manda y lo borra; como máximo una respuesta por contacto cada 24 h (la confirmación de la baja va
   aparte).
9. **Acuses y vista del despachador.** Webhooks firmados de Meta y de Event Grid (ACS) marcan `DELIVERED` o
   `FAILED` solo hacia adelante, mediante una función `SECURITY DEFINER` de un rol nuevo
   `paqueteria_notification_receipt_executor` (NOLOGIN, BYPASSRLS porque un webhook no trae tenant, solo EXECUTE para
   la API). El despachador ve "Avisos al destinatario" en la orden (estado, canal, intentos, motivo), nunca el
   destinatario, con `NotificationStatusChanged` v2 en tiempo real.
10. **Apagado por capas.** `Notifications:RecipientNotices:Enabled=false`, proveedores `Disabled`, webhooks inertes
    (404) y proveedores reales rechazados sin las filas que resuelven GATE-004 y GATE-007. Solo el cambio que
    registra esas filas los enciende.
11. **Remitente fuera (E4-P13).** No hay avisos a la empresa que envía hasta BUS-001.

## Cómo quedan los contratos en este cambio

Este cambio solo escribe contratos. AI-05 registra la API como una entrada `DECIDED` de `x-pilot-contract-deltas`
(la superficie HTTP no cambia hasta que cada ítem publica su parte con código y pruebas, porque las pruebas de
contrato comparan los esquemas con los DTO). AI-12 registra el payload v2 como `decided_amendment`. AI-06 y AI-18
declaran en comentarios las columnas, tablas, funciones y el rol nuevos; su DDL entra con el paso de carril que los
instala y con las aserciones de la línea base, como en OBS-003-OUTBOX-TRACE-CONTEXT-2026-10-13, para que una
instalación nueva y una actualizada no diverjan.

## Alternativas descartadas

Las opciones no elegidas de cada pregunta están citadas arriba. Además:

- La misma llave PII para el Worker: rompe la regla de ADP-001 "The Worker needs no access to the PII key" (E4-P2).
- Guardar el teléfono o el correo en la notificación, el outbox o el payload: viola AI-01 regla 9; la notificación
  guarda solo la orden y el destino se lee al enviar.
- Un coordinador nuevo para el aviso: la fila adicional de outbox en ORD-002 basta, como D8.
- Correo como respaldo de WhatsApp: el owner eligió los dos siempre (E4-P11); necesitaría los acuses de Meta.
- Llamar a Meta desde el webhook para la respuesta automática: daría a la API la credencial de envío y haría el
  webhook lento ante Meta; el Worker ya hace todos los envíos.

## Consecuencias

- Meta (WhatsApp Cloud API, fuera de México) y Microsoft ACS reciben el teléfono o el correo y el contenido del
  aviso. Antes de usar datos reales hace falta resolver GATE-007 con el aviso de privacidad que los nombre, la
  transferencia, la base legal y el registro del permiso (puntos en la nota de desarrollo).
- Una llave más en Key Vault y un rol nuevo; el Worker gana la llave del enlace de seguimiento, con el mismo alcance
  que su acceso de lectura a la base.
- Costo estimado del plan (2026-10-19): unos USD 17 a 27 al mes con 20 entregas al día (WhatsApp utilidad México
  USD 0.0085 por mensaje entregado; correo USD 0.00025).
- Las órdenes confirmadas antes de encender los avisos no reciben el enlace; las canceladas o devueltas después de
  recibirlo no reciben aviso (fuera de los cuatro).
- Una baja por error (BAJA, el enlace del correo o "Detener avisos") no se puede deshacer durante el piloto (E4-P16);
  volver a darse de alta se revisa al terminar el piloto.

## Reversión

Apagar `Notifications:RecipientNotices:Enabled` o poner un proveedor en `Disabled`, sin migración: lo pendiente
termina `FAILED` con `MESSAGING_CHANNEL_DISABLED` y el despachador lo ve. Cada `Down` de carril se niega mientras haya
filas que necesiten la forma nueva. Este cambio de contratos se revierte con un revert del commit.
