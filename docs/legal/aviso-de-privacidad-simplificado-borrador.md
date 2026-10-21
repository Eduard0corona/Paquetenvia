# Aviso de privacidad simplificado (BORRADOR)

> **Borrador sin validez legal.** Lo redactó Claude por encargo del owner (fila `GATE-007-PRIVACY-DRAFT` del
> decision log) para que lo revise su abogado. No está aprobado, no resuelve GATE-007 y no se muestra en ninguna
> pantalla. **[PENDIENTE: …]** marca lo que deciden el abogado o el owner.

Cuando los datos se obtienen por medios electrónicos, la Ley pide una versión simplificada del aviso con, al menos,
cuatro elementos (artículo 16, fracción II, que remite al artículo 15, fracciones I a IV):

1. la identidad y el domicilio del responsable;
2. los datos que se tratan, señalando los sensibles;
3. las finalidades, distinguiendo las que requieren consentimiento;
4. las opciones para limitar el uso o la divulgación.

Además, debe decir dónde consultar el aviso integral. Hay tres versiones, una por cada punto donde el sistema recaba
datos. Las tres remiten al [aviso integral](aviso-de-privacidad-integral-borrador.md).

**Hoy ninguna de las tres pantallas muestra un aviso.** Mostrarlo es un cambio de interfaz que no forma parte de este
borrador (ver "Notas de implementación" al final).

---

## A. Asistente de órdenes (personal que captura un envío)

**Dónde:** pantalla "Nueva orden" (`/ops/orders/new`), junto a la casilla "El cliente vio el desglose y aceptó los
términos y el aviso de privacidad vigentes." El personal se lo muestra o se lo lee al cliente antes de marcar la casilla.

> **Aviso de privacidad simplificado**
>
> **[RAZÓN SOCIAL]**, con domicilio en [DOMICILIO], es responsable de los datos personales de este envío.
>
> **Qué datos tratamos:** nombre, teléfono, dirección, referencias y ubicación en el mapa de quien entrega y de quien
> recibe el paquete; el correo electrónico de quien recibe, si lo dio; descripción, peso, medidas y valor del paquete;
> fotografías de la recolección y la entrega; y el registro de su aceptación (versiones, fecha y hora). No pedimos
> datos sensibles.
>
> **Para qué:** cotizar, recoger, entregar o devolver el paquete; dejar evidencia de la entrega; cobrar y facturar el
> servicio; atender incidentes y reclamaciones; y mostrar el estado del envío en la página de seguimiento. Todas son
> necesarias para el servicio. No usamos sus datos para publicidad ni para otros fines.
>
> **Cómo limitar su uso:** escriba a [CORREO ARCO], donde también puede ejercer sus derechos de acceso,
> rectificación, cancelación y oposición.
>
> **Aviso completo:** [URL DEL AVISO]

**[PENDIENTE: decidir si el aviso le pide al remitente que confirme que puede darnos los datos del destinatario, o si
eso va en los términos del servicio.]**

**[PENDIENTE: el abogado decide si al cliente se le debe entregar también una copia, o un enlace por mensaje, porque el
registro de la aceptación es asistido.]**

**[PENDIENTE: agregar estas finalidades solo si están habilitadas al publicar: "conectar una llamada enmascarada con el
repartidor (Twilio)", "enviarle avisos por WhatsApp o correo" y "ubicar la dirección en el mapa (Google)".]**

**[PENDIENTE: GATE-007. El correo de quien recibe es opcional y hoy solo se guarda cifrado; no se le envía nada hasta
habilitar los avisos (NTF-RECIPIENT-NOTICES-2026-10-20). El abogado decide si el aviso lo menciona desde ahora o solo
cuando los avisos se habiliten.]**

---

## B. Página de seguimiento (destinatario y cualquier visitante)

**Dónde:** al pie de la página pública de seguimiento (`/track/…`), en todos sus estados.

**Restricción:** por la decisión GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10 la página no muestra marca. El texto usa la
razón social, no el nombre comercial.

> **Aviso de privacidad simplificado**
>
> **[RAZÓN SOCIAL]**, con domicilio en [DOMICILIO], es responsable de los datos personales de este envío. Quien
> solicitó el envío nos dio su nombre, teléfono y dirección para entregárselo.
>
> **Qué datos tratamos:** su nombre, teléfono, dirección, referencias y ubicación en el mapa; las fotografías de la
> entrega; y el estado del envío. En esta página solo vemos el enlace que abrió y, sin guardarla, su dirección IP para
> evitar abusos. No pedimos datos sensibles y esta página no usa cookies.
>
> **Para qué:** entregarle su paquete, dejar evidencia de la entrega, atender incidentes y mostrarle aquí el estado de
> su envío. Todas son necesarias para el servicio. No usamos sus datos para publicidad.
>
> **Cómo limitar su uso:** escriba a [CORREO ARCO], donde también puede ejercer sus derechos de acceso,
> rectificación, cancelación y oposición.
>
> **Aviso completo:** [URL DEL AVISO]

**[PENDIENTE: si la razón social contiene la marca, el owner decide cómo cumplir con GATE-001 y con el artículo 15,
fracción I, de la Ley a la vez.]**

**[PENDIENTE: el enlace al aviso integral no puede llevar el token de seguimiento ni otros datos del envío. Hoy la página
manda Referrer-Policy no-referrer; basta un enlace simple.]**

**[PENDIENTE: el mismo texto, o uno más corto con enlace, iría en los mensajes de WhatsApp y correo cuando GATE-004 se
habilite. Para WhatsApp hay que aprobar la plantilla en Meta.]**

---

## C. Alta del repartidor (app del repartidor)

**Dónde:** la primera vez que el repartidor abre la app con su cuenta, antes de mostrarle sus paradas.

> **Aviso de privacidad simplificado**
>
> **[RAZÓN SOCIAL]**, con domicilio en [DOMICILIO], es responsable de sus datos personales como repartidor.
>
> **Qué datos tratamos:** su cuenta de acceso (identificador, nombre y correo); su perfil (organización, ciudad,
> tipo de repartidor, vehículo y estado); su trabajo (asignaciones, paradas, entregas, intentos fallidos, fotografías
> que toma y cobros que registra); sus pagos; y, solo si usted lo registra, su número de celular. No pedimos datos
> sensibles.
>
> **Para qué:** darle acceso a la app, asignarle entregas, mostrarle sus paradas, registrar su trabajo, calcular sus
> pagos y, si registró su celular, conectar sus llamadas con los destinatarios sin que vean su número. Todas son
> necesarias para su trabajo. No usamos sus datos para publicidad.
>
> **En su teléfono:** la app guarda los datos de sus paradas para trabajar sin conexión y los borra cuando dejan de
> estarle asignadas o cuando cierra sesión.
>
> **Cómo limitar su uso:** puede borrar su celular en "Cuenta" cuando quiera. Para lo demás, escriba a
> [CORREO ARCO], donde también puede ejercer sus derechos de acceso, rectificación, cancelación y oposición.
>
> **Aviso completo:** [URL DEL AVISO]

**[PENDIENTE: si se habilita la ubicación del repartidor (DRV-003), agregarla aquí con su propio consentimiento expreso
y separado, que diga cuándo se recaba, quién la ve y por cuánto tiempo. Ver aviso integral, sección 3.4.]**

**[PENDIENTE: si se piden documentos (identificación, licencia, tarjeta de circulación, seguro, antecedentes),
agregarlos aquí.]**

**[PENDIENTE: el consentimiento expreso para el número de celular ya existe y no cambia. Es el texto versionado
VOICE-001-CONSENT-V1 de "Cuenta": "Acepto que este número se use solo para conectar mis llamadas con los destinatarios
de mis entregas. El destinatario nunca verá mi número y no se usará para nada más. Puedo cambiarlo o borrarlo cuando
quiera." Confirmar que basta.]**

**[PENDIENTE: si GATE-008 concluye que hay relación laboral (trabajo en plataformas digitales), revisar este texto con
el contrato y la política de gestión algorítmica (Ley Federal del Trabajo, artículos 291-G, 291-H y 291-J).]**

---

## Otros puntos de captura que este borrador no cubre

**[PENDIENTE: el owner decide si también requieren versión simplificada:]**

- Registro de una organización nueva (`/onboarding`): nombre legal y nombre comercial de la empresa o aliado.
- Inicio de sesión y registro de cuenta en AuthCenter: correo, nombre y contraseña o llave de acceso. AuthCenter es un
  sistema aparte y su aviso no vive en este repositorio.
- Invitación de una persona por correo: la captura un administrador, no la persona invitada.

## Notas de implementación (no forman parte del aviso)

Ninguna pantalla muestra hoy estos textos. Mostrarlos requiere, una vez aprobado el aviso:

- **A.** Agregar el texto o un enlace en `/ops/orders/new`. Contrato de pantalla en AI-07.
- **B.** Agregar el texto y un enlace en `public_tracking`. AI-07 regula esa pantalla con la decisión
  GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10 y con encabezados de privacidad estrictos.
- **C.** Hoy no existe una pantalla de bienvenida o alta en la app del repartidor. Habría que crearla.
- Publicar el aviso integral en [URL DEL AVISO] y poner la versión aprobada en `webPrivacyVersion`
  (`deploy/azure/pilot/web.parameters.json`) mediante el proceso del owner.

Son cambios de interfaz con su propio PR, sus pruebas y, en B, una decisión del owner (MAJOR según AI-01 §7).
