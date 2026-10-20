# Términos y condiciones del servicio de envíos (BORRADOR)

> **Borrador sin validez legal.** Lo redactó Claude por encargo del owner (decisión D1 del 2026-10-20: "Borrador para
> el abogado (Recommended)") para que lo revise su abogado. No está aprobado, no se publica, no se muestra a nadie y no
> resuelve nada. Hoy el sistema no tiene ningún texto de términos y condiciones, aunque el asistente de nueva orden
> dice que el cliente "aceptó los términos".
>
> - **[PENDIENTE: …]** marca lo que deben decidir el abogado o el owner, en especial todo lo legal o comercial. No se
>   inventaron precios, límites de responsabilidad, seguros ni datos de la empresa.
> - Los recuadros **"Nota para revisión"** dicen de dónde sale cada afirmación sobre el sistema. Se borran del texto
>   final, igual que la sección final "Notas legales para el abogado".
> - Las afirmaciones sobre lo que hace el sistema describen el código de la rama `development` al 2026-10-20. Si el
>   código cambia antes de publicar los términos, hay que revisarlas de nuevo.
> - Si este borrador contradice un contrato de `docs/normative/v0.6/`, manda el contrato.

**Versión de los términos:** [VERSIÓN] · **Última actualización:** [FECHA]

> **Nota para revisión.** La versión es el valor de `terms_version` que el sistema guarda con cada orden. Lo toma de la
> variable `PAQUETERIA_TERMS_VERSION` del contenedor web, que sale de `webTermsVersion` en
> `deploy/azure/pilot/web.parameters.json`. Formato: `^[A-Za-z0-9._-]{1,64}$`, por ejemplo `2026-11-01`. Hoy vale
> `OWNER_DECISION_REQUIRED`: con ese valor la pantalla no deja confirmar órdenes y el despliegue del piloto se detiene
> (verificación P22, `docs/operations/env-001-pilot/README.md`). Ver la sección 16.

**[PENDIENTE: trato. Este borrador usa la tercera persona ("el Cliente"). Las pantallas del sistema tutean.]**

---

## Quién es "el Cliente" (decisión central)

**[PENDIENTE, CENTRAL: definir con quién se celebran estos términos.]** Hechos que el abogado necesita:

1. Hoy el cliente no usa el sistema. Solo el personal con rol de despacho (`DISPATCHER`) o administración de plataforma
   (`PLATFORM_ADMIN`) cotiza y crea órdenes. El cliente "ve el desglose" en la pantalla del personal o se lo dicen, y
   el personal marca que aceptó. El portal para empresas (BUS-001) está planeado para una etapa posterior.
2. Una orden puede llevar una **cuenta cliente**, con una tarifa privada, o no llevarla. Sin cuenta se cobra la
   **tarifa ocasional**.
3. Quien paga puede ser el **remitente**, el **destinatario** o una **cuenta empresarial**.
4. El registro de la aceptación no identifica a la persona del cliente que aceptó. Guarda al usuario del personal que
   la registró y, si la hay, la cuenta cliente de la orden.

Este borrador supone que el Cliente es la empresa que solicita el envío (remitente). Con la tarifa ocasional, el
Cliente puede ser una persona que envía algo para sí misma, es decir, un consumidor. En ese caso aplica la Ley Federal
de Protección al Consumidor y varias cláusulas de este borrador tendrían que cambiar (ver
"Notas legales para el abogado", sección A).

> **Nota para revisión.** Roles: `x-capability-matrix` de AI-05 (`createQuote` y `createOrder`: `DISPATCHER`,
> `PLATFORM_ADMIN`). Un administrador de empresa (`BUSINESS_ADMIN`) no puede dar el rol de despacho (REG-ROLE-CEILING).
> Tarifa ocasional: `PricingTierSelector` (`Pricing.Domain/PricingPolicies.cs`); sin `client_account_id` el nivel es
> `OCCASIONAL`. Quién paga: `SENDER`, `RECIPIENT` y `BUSINESS_ACCOUNT` (`OrderInputPolicy.IsPayerType`). Aceptación:
> `orders.order_acceptances` (AI-06), con `actor_id` del personal. AI-02 lista entre sus actores a `occasional_user`.

---

## 1. Partes y definiciones

**[RAZÓN SOCIAL]**, con domicilio en **[DOMICILIO]** ("la Plataforma"), presta el servicio de envíos
[NOMBRE DEL SERVICIO] a quien lo solicita ("el Cliente") en los términos de este documento.

**[PENDIENTE: razón social, domicilio, RFC si se incluye, y medio de contacto [CORREO DE ATENCIÓN]. No se inventan.]**

**[PENDIENTE: nombre comercial. GATE-001 sigue abierto y la decisión GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10 deja sin
marca lo que ve el destinatario.]**

| Término | Significado |
| --- | --- |
| **Envío** u **orden** | Una solicitud de recolección y entrega de uno o más paquetes, con un solo origen y un solo destino |
| **Número de guía** | El identificador del envío que muestran la orden y la página de seguimiento |
| **Remitente** | Quien entrega el paquete en el origen. Normalmente es el Cliente o alguien en su nombre |
| **Destinatario** | La persona que recibe el paquete en el destino |
| **Repartidor** | La persona que recoge, traslada y entrega el paquete: propio de la Plataforma, externo o de un aliado |
| **Aliado** | Otra empresa de reparto a la que la Plataforma puede encargar la operación de un envío |
| **Cobro contra entrega** | El dinero que el repartidor cobra al destinatario por cuenta del Cliente al entregar |
| **Evidencia de entrega** | Las fotografías que el repartidor toma al recoger, al entregar o al no poder entregar |
| **Enlace de seguimiento** | La dirección web pública donde se consulta el estado del envío |

---

## 2. Aceptación de estos términos

2.1. El Cliente acepta estos términos y el aviso de privacidad vigente **cada vez que solicita un envío**. La
aceptación queda ligada a esa orden.

2.2. Hoy la aceptación es **asistida**: el personal de la Plataforma captura el envío, muestra o dice al Cliente el
desglose del precio y, si el Cliente acepta, marca la casilla "El cliente vio el desglose y aceptó los términos y el
aviso de privacidad vigentes." Sin esa casilla la orden no se crea.

2.3. Con cada orden el sistema guarda un registro que no se puede modificar ni borrar, con:

- la versión de estos términos y la versión del aviso de privacidad vigentes;
- la fecha y hora de la aceptación según el equipo del personal, y la fecha y hora en que el servidor la registró;
- el canal (asistido);
- el usuario del personal que la registró;
- la cotización y la organización de la orden;
- una huella digital SHA-256 de esos datos.

No guarda la dirección IP ni el navegador, ni el texto de los términos: guarda solo su versión. Por eso la Plataforma
debe conservar el texto completo de cada versión (sección 16).

**[PENDIENTE: el abogado decide si la aceptación asistida basta como prueba frente al Cliente. El mensaje de datos lo
genera el personal, no el Cliente (Código de Comercio, artículo 90). Opciones: que el Cliente firme o acepte por
escrito una vez, al abrir su cuenta cliente; enviarle una confirmación con el enlace a esta versión; o que acepte él
mismo cuando exista el portal de empresas.]**

**[PENDIENTE: cómo se pone el texto a disposición del Cliente antes de aceptar. Hoy ninguna pantalla muestra ni enlaza
los términos.]**

**[PENDIENTE: si el Cliente con cuenta cliente firma un contrato marco aparte, decidir si estos términos lo
complementan o lo sustituyen.]**

> **Nota para revisión.**
> - Casillas y textos: `apps/web/src/operations/components/create-order-wizard.tsx` (paso "Confirmar"). La pantalla
>   añade: "La aceptación queda registrada como asistida por ti, con las versiones vigentes de los términos y del aviso
>   de privacidad."
> - El canal es siempre `ASSISTED` y las versiones salen de la configuración del servidor, nunca de lo que escribe el
>   personal (`apps/web/src/operations/contracts/create-order.ts`, `acceptance-versions.ts`). La hora es la del
>   navegador al confirmar.
> - Registro: `orders.order_acceptances` (AI-06), una fila por orden, de solo inserción. Huella:
>   `OrderAcceptanceCanonicalForm v1` sobre `schema_version`, `order_id`, `quote_id`, `owner_org_id`, `actor_id`,
>   `terms_version`, `privacy_version`, `accepted_at_client` y `acceptance_channel` (AI-04
>   `legal_acceptance_invariants`; ADR-029). IP y navegador prohibidos mientras GATE-007 siga abierta (AI-04
>   `OrderAcceptance`).
> - El servidor rechaza una hora de aceptación de más de 72 horas atrás o más de 5 minutos adelante
>   (`OrderAcceptanceInputPolicy`, AI05-INPUT-LIMITS).
> - **Carga por archivo CSV.** El personal también puede crear órdenes con un archivo. Ahí la versión de los términos,
>   la del aviso, la hora y el canal vienen escritos en cada fila del archivo (`docs/development/csv-001-order-csv-import.md`).
>   El servidor solo revisa el formato de la versión, no que sea la vigente. Ver la sección 16.

---

## 3. El servicio

3.1. La Plataforma recoge paquetes en el origen y los entrega en el destino que indica el Cliente, **dentro de la
misma ciudad y en las zonas donde presta servicio** **[PENDIENTE: zonas del piloto en Culiacán, GATE-010]**. El
sistema rechaza la cotización si el origen o el destino quedan fuera de cobertura o en ciudades distintas.

3.2. Tipos de servicio: **Mismo día**, **Urgente** y **Ruta programada**. **[PENDIENTE: qué promete cada uno (horarios,
plazos, nivel de servicio). El sistema no los define; dependen de GATE-010.]**

3.3. El Cliente puede indicar una **ventana de entrega** de hasta 12 horas, en hora de Mazatlán (la del piloto), con un
máximo de 30 días de anticipación. Si no la indica, aplica el horario de la zona. **[PENDIENTE: si la ventana es un
compromiso de la Plataforma o una preferencia, y qué pasa si no se cumple. El sistema la guarda y la muestra, pero no
mide su cumplimiento ni calcula compensaciones.]**

3.4. El sistema no calcula rutas, distancias ni horas estimadas de llegada, y la página de seguimiento no muestra una
hora estimada.

3.5. Cada orden admite de 1 a 20 paquetes. De cada paquete se registra una descripción (hasta 250 caracteres), el peso,
el valor declarado y, si se conocen, las medidas. **[PENDIENTE: pesos y medidas máximos, y requisitos de empaque
(GATE-010). El sistema no los limita; solo revisa la capacidad del vehículo del repartidor al asignar.]**

3.6. La Plataforma puede encargar la recolección y la entrega a repartidores propios, a repartidores externos o a un
aliado. **[PENDIENTE: si el Cliente debe autorizarlo o basta informarlo, y quién responde frente al Cliente (Código de
Comercio, artículo 577).]**

3.7. Cada envío tiene un **enlace de seguimiento** público desde que se crea. Muestra el número de guía, el estado y la
fecha y hora de cada evento; nunca nombres, direcciones, teléfonos ni datos del repartidor. Funciona mientras el envío
está en curso y hasta 24 horas después de que se entrega, se devuelve o se cancela.

**[PENDIENTE: qué comprobante recibe el Cliente de su envío. Hoy el sistema no le envía nada: el personal ve el número
de guía y puede copiar el enlace de seguimiento para dárselo.]**

> **Nota para revisión.** Cobertura: AI-02 (`mission` y `business_decisions.coverage`: "Culiacán y zonas explícitamente
> autorizadas") y `QuoteGeographyPolicy` (misma ciudad); AI-07 `create_order` ("createQuote checks the Culiacán
> coverage"). Tipos de servicio: `SAME_DAY`, `URGENT`, `SCHEDULED_ROUTE` (`operations-formatters.ts`). Ventana:
> `OrderServiceWindowPolicy` (12 horas, 30 días, `America/Mazatlan`). Sin rutas ni ETA:
> `docs/development/prc-001-basic-quote-engine.md` y GATE-003-MAPS-PILOT-RULES-2026-10-02; `estimated_window` siempre
> nulo en el seguimiento (`security.get_public_tracking_projection`). Paquetes: AI05-INPUT-LIMITS (1 a 20) y PRC-001
> (descripción de hasta 250 caracteres). Capacidad: `DriverEligibilityPolicy`. Flota mixta: AI-02 (`fleet_model:
> mixed`), EXT-001 y `operator_org_id` (AI-04). Enlace: TRK-002-AUTO-LINK y TRK-002-NO-REVOCATION.

---

## 4. Cotización y precio

4.1. Antes de crear la orden, la Plataforma calcula el precio con la tarifa vigente para la zona, el tipo de servicio
y el nivel del Cliente: tarifa ocasional o tarifa de su cuenta cliente.

4.2. **Todos los precios están en pesos mexicanos (MXN) e incluyen el IVA.** El desglose muestra el importe sin IVA,
el IVA y el total, y dice "IVA incluido". La regla es la misma para todos los clientes.

4.3. La cotización vale **30 minutos** **[PENDIENTE: confirmar; es un parámetro técnico]**. Si vence, o si cambia
cualquier dato con el que se calculó (direcciones, paquetes, tipo de servicio, ruta consolidada o cuenta cliente), se
calcula de nuevo.

4.4. Al crear la orden, el precio queda fijo. Un cambio posterior de tarifas no cambia el precio de una orden ya
creada.

4.5. **[PENDIENTE: tarifas. No hay tarifas reales aprobadas (GATE-010). Este documento no fija precios.]**

4.6. **[PENDIENTE: forma y plazo de pago del envío. El sistema registra quién paga (remitente, destinatario o cuenta
empresarial), pero no cobra el envío ni registra su pago. No hay pagos en línea (GATE-005).]**

4.7. **[PENDIENTE: cómo se cobra el envío cuando paga el destinatario. El cobro contra entrega de la sección 5 es otro
monto y el sistema no le suma el precio del envío.]**

4.8. **[PENDIENTE: facturación electrónica (CFDI, GATE-006).]**

4.9. **[PENDIENTE: cargos adicionales por reintento, devolución, espera, almacenaje o cancelación. Hoy el sistema no
calcula ningún cargo adicional: el precio de la orden es el de la cotización.]**

4.10. **Cancelación.** El personal puede cancelar una orden antes de que el repartidor recoja el paquete. Una vez
recogido, el paquete en traslado o en entrega no se cancela: se entrega o se devuelve (sección 8). **[PENDIENTE: si el
Cliente paga algo al cancelar. El Código de Comercio, artículo 578, prevé la mitad del porte antes del viaje y el total
después, salvo pacto.]**

> **Nota para revisión.** IVA: GATE-011-VAT-INCLUDED-2026-09-29 (AI-10 `GATE-011`): la tarifa es el total; importe sin
> IVA y IVA salen por aritmética entera al 16 %. Moneda fija MXN (AI-04 `money_invariants`). Vigencia:
> `Pricing:QuoteLifetimeMinutes` = 30 en `src/Paqueteria.Api/appsettings.json`; el piloto no lo cambia
> (`deploy/azure/pilot/apps.bicep`). Precio congelado: PRC-001 (snapshots) y `pricing_policy_version` copiada a la
> orden. Precio mínimo: un total de 52.00 MXN o menos, IVA incluido, solo se confirma en ruta consolidada o con una
> autorización manual del personal, auditada con su motivo (LOW-PRICE-MANUAL-AUTH-2026-10-02); es regla interna y no se
> propone como cláusula. AI-02 trae montos de referencia por nivel; no son tarifas aprobadas (GATE-010). Cobro del
> envío: ningún módulo lo registra; las liquidaciones solo pagan a repartidores (`SettlementPayeeType { Driver }`).
> Cancelación: AI-04 (`CANCELLED` desde `DRAFT`, `CONFIRMED`, `READY_FOR_PICKUP`, `ASSIGNED` y `AT_PICKUP`, este
> último solo sin custodia; motivo obligatorio; solo `DISPATCHER` y `PLATFORM_ADMIN` con MFA). Caso límite: una orden
> reprogramada después de un intento fallido vuelve a `READY_FOR_PICKUP` o `ASSIGNED` y desde ahí se puede cancelar
> aunque la Plataforma ya tenga el paquete. **[PENDIENTE: qué pasa con el paquete en ese caso.]**

---

## 5. Cobro contra entrega

5.1. Al crear la orden, el Cliente puede pedir que el repartidor cobre al destinatario un monto por su cuenta. El
monto máximo es de **20,000.00 MXN por orden**. La cotización no incluye este monto.

5.2. El repartidor cobra **en efectivo** y **el monto exacto** al entregar. El sistema no marca la orden como entregada
hasta que queda registrado el cobro. Si el destinatario no paga, no se entrega el paquete: se registra un intento
fallido con el motivo "No pagó el cobro" y se sigue la sección 8.

5.3. El personal de despacho o de finanzas de la Plataforma concilia cada cobro. El repartidor que cobró no puede
conciliar su propio cobro.

5.4. **[PENDIENTE: plazo, forma y comisión para entregar al Cliente lo cobrado. El sistema no registra esa entrega: las
liquidaciones de hoy solo pagan a repartidores.]**

5.5. **[PENDIENTE: quién responde por el efectivo desde que el repartidor lo cobra hasta que lo recibe el Cliente, y
qué pasa si se pierde o hay un faltante.]**

5.6. **[PENDIENTE: si se admiten pagos parciales, otros medios de pago o cambio. Hoy el sistema solo acepta el monto
exacto.]**

> **Nota para revisión.** Tope: COD-CAP-20000-2026-10-02 (2,000,000 centavos, inclusive), en `OrderInputPolicy` y
> `OrderCodExpectationPolicy`. Declarado por el personal: D6-COD-PILOT; sin cobro en la cotización: QUOTE-NO-COD.
> Efectivo: "Cobra este monto en efectivo al entregar." (`apps/web/src/driver/components/driver-cod-collection.tsx`).
> Monto exacto: `recordCodCollection` (AI-05). Guarda de entrega: `if_cod_expected_then_cod_status_recorded_or_reconciled`
> (AI-04). Motivo `PAYMENT_UNAVAILABLE` ("No pagó el cobro", `docs/development/inc-failed-attempt-dispatch-decides.md`).
> Conciliación: `reconcileCod` (AI-05), FINANCE-COD-RECONCILIATION y FINANCE-COD-MFA-2026-09-27.

---

## 6. Artículos prohibidos

6.1. El Cliente se obliga a no enviar artículos prohibidos. **[PENDIENTE: lista de artículos prohibidos y restringidos
(GATE-010). No existe ninguna lista en el sistema ni en los contratos.]**

6.2. Al confirmar cada envío, el personal marca la casilla "Confirmo que el envío no contiene artículos prohibidos
(queda registrado en la orden)." Sin ella la orden no se crea ni se confirma. El sistema guarda esa confirmación en el
historial de la orden, que no se puede modificar, junto con quién la dio y cuándo. En la carga por archivo, la
confirmación se da una sola vez para todos los envíos del archivo.

**[PENDIENTE: esa confirmación la da el personal, no el Cliente. El abogado decide si basta que el personal le pregunte
al Cliente, o si el Cliente debe declararlo por sí mismo (por ejemplo, con una casilla propia o en el contrato marco).]**

6.3. **[PENDIENTE: consecuencias. Por ejemplo: rechazar el paquete, devolverlo a cargo del Cliente, cobrar los costos,
responder por daños y avisar a la autoridad. Ninguna está decidida.]**

6.4. **[PENDIENTE: si el repartidor puede pedir que se abra el paquete al recogerlo (Código de Comercio, artículos 591,
fracción V, y 601). Hoy la app no tiene un paso para eso, y la lista de motivos para no poder recoger o entregar no
tiene uno para "artículo prohibido".]**

6.5. El Cliente no debe escribir nombres, teléfonos, correos, direcciones ni datos de salud en la descripción del
paquete. El sistema sustituye por "[REDACTED]" lo que parece correo, teléfono o dirección, pero no detecta nombres ni
datos de salud.

> **Nota para revisión.** Casilla: `create-order-wizard.tsx`. Registro: ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02
> (`restricted_goods_acknowledged` solo `true`; evento `ORDER_CREATED` de `orders.order_events`, de solo inserción,
> con `actor_id` y `occurred_at`, y su auditoría). También es metadato obligatorio de la transición
> `DRAFT → CONFIRMED` (guarda `restricted_goods_check`, AI-04). CSV: un solo campo del formulario para todo el archivo
> (`docs/development/csv-001-order-csv-import.md`). La confirmación no forma parte de la evidencia de aceptación
> (`OrderAcceptanceCanonicalForm v1` no cambia). Lista pendiente: AI-10 `GATE-010` ("prohibited items"). Motivos:
> `RECIPIENT_ABSENT`, `ADDRESS_NOT_FOUND`, `RECIPIENT_REFUSED`, `ACCESS_RESTRICTED`, `PAYMENT_UNAVAILABLE`,
> `PACKAGE_DAMAGED`, `SECURITY_RISK` (`docs/development/inc-001-incidents-failed-attempt.md`). Redacción: AUD-001
> (`AuditPayloadRedactor`), igual que en el aviso de privacidad integral, sección 3.2.

---

## 7. Obligaciones del Cliente

El Cliente se obliga a:

1. Dar datos completos y correctos del origen y del destino: dirección, referencias para llegar, nombre del contacto,
   teléfono de 10 dígitos de México y el punto en el mapa. El sistema rechaza teléfonos que no tengan 10 dígitos.
2. Describir el contenido de cada paquete y declarar su peso y su valor con veracidad.
3. Tener el paquete listo y empacado en el origen. **[PENDIENTE: requisitos de empaque.]**
4. Contar con la autorización del destinatario y del contacto de recolección para dar sus datos a la Plataforma
   (sección 13).
5. Cuando estén habilitados los avisos por WhatsApp, marcar que el destinatario aceptó recibirlos solo si de verdad lo
   aceptó (sección 10).
6. Atender las dudas del destinatario sobre su envío (sección 10.6).
7. Pagar el precio del envío **[PENDIENTE: forma y plazo, sección 4]**.

**[PENDIENTE: qué pasa si un dato es incorrecto: por ejemplo, una dirección equivocada que provoca un intento fallido o
una devolución. Hoy no hay cargos adicionales en el sistema (sección 4.9).]**

> **Nota para revisión.** Campos de la cotización: `AddressInput` de `createQuote` (AI-05): `address_text`,
> `contact_name`, `phone`, `lat`, `lng` y `references`. Teléfono: ORD-PROHIBITED-GOODS-PHONE-MX-2026-10-02 y
> ORD-PHONE-PLUS52-LOCATIONS-2026-10-03 (se aceptan espacios, guiones y un `+52` inicial, que se quitan).

---

## 8. Recolección, intentos de entrega, reprogramación y devolución

8.1. **Recolección.** Al recoger, el repartidor toma una fotografía. Sin esa foto el sistema no registra la
recolección.

8.2. **Recolección fallida.** Si no se puede recoger, se registra el motivo con al menos una foto como evidencia. Hoy
la app del repartidor solo tiene ese reporte para las entregas; una recolección fallida la registra el personal de
despacho. Como el paquete nunca salió del origen, la orden solo puede reprogramarse y, después, si se decide,
cancelarse.

8.3. **Entrega fallida.** Si no puede entregar, el repartidor registra el motivo de una lista cerrada ("No había
nadie", "Dirección incorrecta", "Rechazó el paquete", "No pagó el cobro", "Acceso restringido", "Paquete dañado" o
"Riesgo de seguridad") y una foto del lugar. La orden queda en atención del personal de despacho, que decide una de
tres cosas:

| Decisión | Qué pasa |
| --- | --- |
| **Reintentar hoy** | El mismo repartidor vuelve a intentar la entrega |
| **Reprogramar** | La orden se asigna de nuevo, a ese u otro repartidor, y repite recolección, traslado y entrega |
| **Regresar** | El paquete regresa al origen, a quien lo envió |

8.4. El sistema no limita el número de intentos. **[PENDIENTE: número máximo de intentos antes de devolver, plazo
máximo de reprogramación y cargos de cada intento o de la devolución.]**

8.5. **Devolución.** La devolución se entrega en el origen del envío. **[PENDIENTE: qué pasa si nadie recibe la
devolución en el origen, cuánto tiempo la guarda la Plataforma y con qué costo (Código de Comercio, artículos 591,
fracción VIII, 603 y 604, si aplican).]**

**[PENDIENTE: el sistema no exige foto al marcar una devolución como entregada en el origen. Decidir si los términos
prometen evidencia de la devolución; si es así, hace falta un cambio técnico.]**

8.6. Una orden devuelta o cancelada ya no puede tener reclamación en el sistema (sección 12).

> **Nota para revisión.** Fotos obligatorias: guardas `pickup_proof_complete` y `delivery_proof_complete` (AI-04).
> Intento fallido: INC-001 (`docs/development/inc-001-incidents-failed-attempt.md`), con 1 a 10 fotos de evidencia y el
> reporte del repartidor sin texto libre (INC-FAILED-ATTEMPT-DISPATCH-DECIDES-2026-10-11). La pantalla "No se pudo
> entregar" de la app solo aparece en paradas de entrega (`driverFailedDeliveryAvailability`,
> `apps/web/src/driver/offline/failed-delivery-policy.ts`); "No pagó el cobro" solo aparece si la parada tiene cobro
> contra entrega. La API admite la incidencia desde `AT_PICKUP`, `IN_TRANSIT` y `DELIVERING`. Sin custodia no hay
> devolución (`incidents_returning_requires_custody_check`). Reprogramar pasa por una nueva asignación
> (D8-RESCHEDULED-NO-DIRECT-DELIVERY). La devolución va al origen: "El paquete regresará a quien lo envió."
> (`apps/web/src/operations/contracts/order-transitions.ts`) y la parada de devolución usa la ubicación de origen
> (`PostgreSqlDriverStopsQuery`). `RETURNING → RETURNED` no tiene guardas en AI-04 ni en
> `OrderTransitionGuardRegistry`. `RETURNED` y `CANCELLED` son terminales (AI-04).

---

## 9. Evidencia de entrega (fotografías)

9.1. Para registrar la entrega, el repartidor toma una **fotografía** al entregar. El sistema no marca un envío como
entregado sin ella. También hay fotografía al recoger y al no poder entregar. El sistema no registra dónde se tomó la
foto: la app no envía la ubicación del teléfono.

9.2. El sistema **no** recaba firma, identificación ni el nombre de quien recibe: rechaza el nombre si se intenta
enviar. **[PENDIENTE: si una fotografía basta como prueba de entrega frente al Cliente, y si el repartidor puede dejar
el paquete sin entregarlo en mano (por ejemplo, en la puerta o con un vecino). El sistema no distingue esos casos.]**

9.3. Antes de guardarla, el teléfono reduce cada foto (máximo 1600 px por lado) y le quita los metadatos; la evidencia
es esa foto reducida. Cada foto se guarda en almacenamiento privado, con la hora y una huella SHA-256 de la foto
reducida, y pasa un análisis de malware antes de aceptarse. Una foto usada para reportar un intento fallido nunca cuenta como prueba de entrega.

9.4. Hoy ninguna pantalla deja ver ni descargar las fotografías: el personal solo puede consultar que existen, su
tipo, su huella y su hora. **[PENDIENTE: cómo y en qué plazo se entrega al Cliente una copia de la foto en una
reclamación. Hace falta un cambio técnico o un procedimiento manual.]**

9.5. Las fotos pueden mostrar a la persona que recibe, el domicilio o placas de vehículos. No llevan metadatos: el
teléfono quita la ubicación GPS, el modelo del teléfono y la hora que la cámara guarda en la imagen. Se tratan conforme
al aviso de privacidad (sección 13). **[PENDIENTE: plazo de conservación; las reglas de retención proponen 12 meses
después del fin del envío y hoy no se borran.]**

> **Nota para revisión.** Fotos: POD-001 (`docs/development/pod-001-secure-proof-upload.md`): JPEG o PNG de hasta
> 10 MiB ("Foto de evidencia", `apps/web/src/driver/components/driver-proof-capture.tsx`). `recipient_name` se
> rechaza (`FinalizeProofRequest`, aviso integral, sección 3.1). Evidencia de incidente excluida de la entrega (AI-04
> `prohibited`). Lectura: `listOrderProofs` devuelve id, tipo, `sha256` y `captured_at`, nunca bytes ni URL
> (API-INC-LIST-PROOFS-2026-09-29); `IProofDownloadService` existe pero ningún endpoint lo usa. EXIF: la app reduce
> la foto a un JPEG de máximo 1600 px por lado y calidad 80 % sin metadatos antes de calcular la huella
> (`apps/web/src/driver/proofs/proof-photo-reduce.ts`, DRV-PROOF-PHOTO-REDUCE-2026-10-20). Retención:
> [reglas de retención](reglas-de-retencion-borrador.md), sección 3.2.

---

## 10. Avisos al destinatario

**Hoy el sistema no envía ningún aviso al destinatario.** Los conectores de WhatsApp y correo existen, pero están
apagados y nada los usa. El personal puede ver y copiar el enlace de seguimiento de cada orden.

El owner ya decidió cómo funcionarán cuando se construyan y se habiliten **[PENDIENTE: GATE-004 y GATE-007; ninguna de
estas decisiones está construida ni registrada en el decision log]**. Este borrador las recoge para que el abogado
revise las obligaciones del Cliente:

10.1. **Qué avisos.** Por WhatsApp: el enlace de seguimiento cuando la orden queda confirmada, "Va en camino" cuando el
repartidor inicia el traslado, "No se pudo entregar" (sin el motivo; no se avisa si falla la recolección) y
"Entregado". Por correo, los mismos avisos si se captura el correo del destinatario; hoy no se captura y se agregaría
como campo opcional.

10.2. **Permiso del destinatario.** En la nueva orden habrá una casilla: "El destinatario aceptó recibir avisos de su
envío por WhatsApp". Si no se marca, no se envía WhatsApp; sí se envía correo si hay. Queda registro de quién la marcó
y cuándo.

10.3. **Obligación del Cliente.** Al pedir que se marque esa casilla, el Cliente declara que el destinatario aceptó
recibir los avisos por WhatsApp. **[PENDIENTE: redacción de esta declaración, qué prueba debe conservar el Cliente y
qué pasa si resulta falsa. Meta exige ese permiso y puede limitar o bloquear el número si se incumple.]**

10.4. **Baja.** El destinatario puede dejar de recibir avisos respondiendo "BAJA" o tocando "No recibir avisos" en
WhatsApp, con el enlace de cada correo, o pidiéndolo por teléfono para que el personal toque "Detener avisos" en la
orden. La baja vale para todos los envíos que le manden, de cualquier empresa, porque el número de WhatsApp es uno
solo.

10.5. **Contenido.** Los avisos no llevan marca ni el nombre del Cliente ni de nadie, tutean al destinatario y tienen un
botón "Ver seguimiento". En WhatsApp aparece la razón social de la Plataforma como nombre; los correos salen de
avisos@paquetenvia.com con el nombre "Seguimiento de envío" y sin buzón de respuestas. Solo se envían de 8:00 a 21:00,
hora de Mazatlán; lo que ocurra fuera de ese horario sale a las 8:00, salvo que ya no aplique.

10.6. **Dudas del destinatario.** Si el destinatario responde al WhatsApp, recibe una respuesta automática: "Este
número solo envía avisos. Para dudas sobre tu envío contacta a quien te lo envió." **[PENDIENTE: obligación del
Cliente de atender esas dudas, y medio de contacto con la Plataforma para casos que el Cliente no pueda resolver.]**

10.7. El Cliente no recibe avisos automáticos de sus propios envíos. **[PENDIENTE: cómo se entera el Cliente del
estado. Hoy, por el enlace de seguimiento que le pase el personal.]**

> **Nota para revisión.** Conectores apagados: `docs/development/gate-004-messaging-adapters.md` y el aviso integral,
> sección 3.1. Texto de la pantalla del personal: "El envío al cliente por WhatsApp o correo todavía no está
> disponible." (`apps/web/src/operations/components/operations-tracking-link.tsx`). Decisiones del owner del
> 2026-10-19 y 2026-10-20, respuestas literales registradas en la sesión de trabajo: E4 ("Los 4 del plan
> (Recommended)"), E4-P1 ("Casilla en la orden (Recommended)"), E4-P3 ("Al iniciar traslado (Recommended)"), E4-P4
> ("Al quedar confirmada (Recommended)"), E4-P5 ("Sin motivo, con enlace (Recommended)"), E4-P6 ("De \"tú\", sin
> nombres (Recommended)"), E4-P7 ("La razón social (Recommended)"), E4-P8 ("Las tres vías (Recommended)"), E4-P9
> ("Respuesta automática (Recommended)"), E4-P10 ("8:00 a 21:00 (Recommended)"), E4-P11 ("Por los dos siempre
> (Recommended)"), E4-P12 ("avisos@paquetenvia.com (Recommended)") y E4-P13 ("Fuera de este trabajo (Recommended)").
> Canales: GATE-004-CHANNELS. Si la implementación cambia algo de esto, hay que revisar esta sección.

---

## 11. Llamadas enmascaradas

11.1. Cuando esté habilitado **[PENDIENTE: VOICE-001; apagado en el piloto hasta resolver GATE-007]**, el repartidor
que ya llegó al destino podrá llamar al destinatario desde la app. La llamada sale de un número de la Plataforma.
Ninguno ve el número del otro.

11.2. El destinatario escucha "Tiene una llamada sobre su envío; presione 1 para contestar." Si no presiona 1, la
llamada se corta sin conectar. **Las llamadas no se graban.**

11.3. El proveedor de telefonía (Twilio) recibe los dos números en cada llamada y conserva sus propios registros de
llamadas. Ver el aviso de privacidad.

11.4. **[PENDIENTE: si el Cliente debe informar al destinatario que puede recibir esta llamada.]**

> **Nota para revisión.** `docs/development/voice-001-masked-call-bridge.md`; decisiones VOICE-001-MASKED-CALLS-2026-10-11,
> VOICE-001-PROVIDER-TWILIO-2026-10-11 y VOICE-001-VOICEMAIL-KEYPRESS-2026-10-11. Solo en la parada de entrega en
> `DELIVERING` ("Llegué al destino"). Sin grabación: `record="do-not-record"`
> (`src/BuildingBlocks/Paqueteria.Infrastructure/Voice/TwilioTwiml.cs`) y `Record=false`
> (`TwilioVoiceBridgeProvider.cs`). Límites por repartidor: 3 llamadas por orden cada 15 minutos y 20 por hora.
> `Voice:Provider` = `Disabled` por omisión; el piloto no lo configura. Quien devuelve la llamada al número de la
> Plataforma escucha un mensaje fijo o se desvía a un número opcional (`Voice:Twilio:InboundForwardNumber`).

---

## 12. Responsabilidad y reclamaciones

12.1. **Plazo para reclamar.** El Cliente puede presentar una reclamación sobre un envío entregado dentro de las
**72 horas** siguientes al momento en que el sistema lo marcó como entregado. Pasado ese plazo, el sistema ya no
permite abrir una reclamación sobre esa orden.

**[PENDIENTE, IMPORTANTE: si un plazo contractual de 72 horas es válido. El Código de Comercio, artículos 592 y 593,
extingue la responsabilidad del porteador por el recibo sin reclamación o a los seis meses, y en averías el plazo
corre después de las 24 horas de la entrega. Si el Cliente es consumidor, la Ley Federal de Protección al Consumidor,
artículo 90, fracción IV, anula los plazos de prescripción inferiores a los legales. El plazo de 72 horas es un
parámetro técnico (de 1 a 720 horas) y puede ajustarse a lo que decida el abogado.]**

12.2. **Cómo se reclama.** **[PENDIENTE: canal (correo, teléfono, formulario) y datos que debe dar el Cliente. Hoy el
Cliente no tiene acceso al sistema: el personal abre la reclamación en la orden con el motivo y la cierra con la
descripción de cómo se resolvió. El sistema no calcula ninguna compensación.]**

12.3. **Lo que el sistema no cubre hoy.** Solo una orden entregada puede tener reclamación. No hay reclamación en el
sistema para:

- un envío devuelto o cancelado;
- un paquete que se pierde antes de entregarse: no existe un estado de "extraviado". Un problema durante el traslado se
  registra como intento fallido.

**[PENDIENTE: cómo se atienden esos casos y en qué plazo.]**

12.4. **Responsabilidad de la Plataforma.** **[PENDIENTE: todo este punto. El abogado y el owner deciden:]**

- **[PENDIENTE: desde cuándo y hasta cuándo responde la Plataforma por el paquete. El sistema registra la custodia
  desde la foto de recolección hasta la foto de entrega o la devolución (Código de Comercio, artículo 590,
  fracción IV).]**
- **[PENDIENTE: monto máximo de responsabilidad por pérdida o daño, y su relación con el valor declarado. El sistema
  guarda el valor declarado de cada paquete, pero no lo usa para el precio ni para ningún límite.]**
- **[PENDIENTE: seguro. No hay seguro de carga ni de responsabilidad civil contratado (GATE-009). No se ofrece
  cobertura.]**
- **[PENDIENTE: exclusiones. Por ejemplo: vicio propio del paquete, empaque insuficiente, artículos prohibidos, datos
  incorrectos del Cliente, caso fortuito y fuerza mayor (Código de Comercio, artículos 588, fracción IV, y 591,
  fracción III).]**
- **[PENDIENTE: retrasos. Si se indemniza un retraso respecto de la ventana de entrega (Código de Comercio, artículo
  590, fracción VI) o si la ventana no es un compromiso (sección 3.3).]**
- **[PENDIENTE: responsabilidad por el efectivo del cobro contra entrega (sección 5.5).]**
- **[PENDIENTE: responsabilidad del Cliente frente a la Plataforma: artículos prohibidos, datos falsos, permiso falso
  para avisos.]**

> **Nota para revisión.** Plazo: `Orders:ClaimWindowHours` = 72 (`src/Paqueteria.Api/appsettings.json`), validado entre
> 1 y 720; el piloto no lo cambia. `claim_window_ends_at` se fija al primer paso a `DELIVERED`
> (`PostgreSqlOrderTransitionService`). Guarda `now_before_or_equal_claim_window_ends_at` y transiciones
> `DELIVERED → CLAIM_OPEN`, `CLOSED → CLAIM_OPEN` y `CLAIM_OPEN → CLAIM_RESOLVED` (AI-04). LIF-001 fija
> `finalized_at` al vencer el plazo y está encendido en el piloto (`Orders__ClaimWindowFinalization__Enabled`); la
> orden entregada se cierra sola si no tiene incidencias abiertas y el cobro está conciliado, y aun cerrada admite
> reclamación dentro del plazo (ORD-AUTO-CLOSE-2026-10-10). Abren y resuelven: `DISPATCHER` y `PLATFORM_ADMIN` con MFA
> de la organización dueña (`OrderTransitionAuthorizer`). Textos: "Abrir reclamación", "Motivo de la reclamación",
> "Cómo se resolvió la reclamación" (`order-transitions.ts`). Custodia: `custody_acquired` (AI-04 `guard_semantics`).
> Valor declarado: solo se guarda (`orders.package_items.declared_value_cents`). Seguro: AI-10 `GATE-009`.

---

## 13. Protección de datos personales

13.1. La Plataforma trata los datos personales conforme a su aviso de privacidad, publicado en [URL DEL AVISO]. Ver los
borradores del [aviso de privacidad integral](aviso-de-privacidad-integral-borrador.md) y del
[aviso simplificado](aviso-de-privacidad-simplificado-borrador.md), y las
[reglas de retención](reglas-de-retencion-borrador.md).

13.2. El Cliente da a la Plataforma los datos del destinatario y del contacto de recolección o devolución: nombre,
teléfono, dirección, referencias y ubicación en el mapa. El Cliente declara que puede darlos y que informó a esas
personas que los usará la Plataforma para hacer el envío. **[PENDIENTE: el abogado decide si esta declaración es
necesaria y suficiente, y si la Plataforma es responsable o encargada de esos datos (aviso integral, sección 1, y
README, decisiones 3 y 5).]**

13.3. El Cliente no debe dar datos personales sensibles ni datos que no hagan falta para el envío, por ejemplo en la
descripción del paquete (sección 6.5).

13.4. La Plataforma no vende datos personales ni los usa para publicidad.

> **Nota para revisión.** Datos y cifrado: aviso integral, secciones 3.1 y 3.2. Sin finalidades secundarias: aviso
> integral, sección 4.2 (pendiente de confirmar por el owner).

---

## 14. Suspensión

14.1. **[PENDIENTE: causas por las que la Plataforma puede suspender o terminar el servicio a un Cliente (por ejemplo,
falta de pago, artículos prohibidos, datos falsos, abuso), si avisa antes y con cuánto tiempo, y qué pasa con los
envíos en curso y con el efectivo cobrado pendiente de entregar.]**

14.2. Lo que el sistema permite hoy:

- Una cuenta cliente que no está activa no puede recibir cotizaciones.
- Una organización que no está activa no puede operar en el sistema.
- No existe una pantalla ni una operación para suspender: habría que hacerlo con un cambio manual en la base de datos.
  **[PENDIENTE: procedimiento y quién lo autoriza; si se quiere una herramienta auditada, es un cambio técnico.]**
- La Plataforma puede cancelar una orden antes de la recolección (sección 4.10).

> **Nota para revisión.** Cuenta cliente: `PricingTierSelector` falla con `ClientAccountUnavailable` si la cuenta no
> está activa; estados `ACTIVE`, `SUSPENDED`, `CLOSED` (`clients.client_accounts`, AI-06). Organización:
> IDENTITY-ORG-ACTIVE-REQUIRED (`security.resolve_identity_context` exige `organizations.status='ACTIVE'`). AI-05 no
> tiene ninguna operación para cambiar el estado de una organización o de una cuenta cliente.

---

## 15. Repartidores

**[PENDIENTE: los repartidores (propios, externos y de aliados) necesitan su propio documento, separado de estos
términos: contrato, relación jurídica, seguridad social y, en su caso, las reglas de trabajo en plataformas digitales
de la Ley Federal del Trabajo (GATE-008, abierta). Este borrador no lo redacta.]**

---

## 16. Cambios a estos términos y versiones

16.1. Cada versión de estos términos tiene un identificador y una fecha. La versión vigente se publica en
[URL DE LOS TÉRMINOS] **[PENDIENTE: URL]**.

16.2. Cada orden guarda la versión que el Cliente aceptó al solicitarla (sección 2.3). Un cambio de términos aplica a
las órdenes que se creen después; las órdenes anteriores conservan la versión registrada.

16.3. La Plataforma conserva el texto completo de cada versión publicada, porque el registro de la orden solo guarda el
identificador. **[PENDIENTE: dónde y cómo se archiva cada versión, por ejemplo en este repositorio con su fecha, y
durante cuánto tiempo (Código de Comercio, artículo 49: diez años como mínimo).]**

16.4. **[PENDIENTE: cómo y con cuánta anticipación se avisa al Cliente de un cambio. Hoy el sistema no tiene un
mecanismo para avisar.]**

16.5. **[PENDIENTE: formato de la versión. Se propone la fecha de entrada en vigor, por ejemplo `2026-11-01`, que cumple
el formato que exige el sistema: letras, dígitos, punto, guion bajo y guion, de 1 a 64 caracteres, sin espacios ni
acentos.]**

> **Nota para revisión.** Cómo se cambia la versión: un PR revisado a `webTermsVersion` en
> `deploy/azure/pilot/web.parameters.json` y un nuevo despliegue; la pantalla lee la variable en cada solicitud, así
> que las órdenes nuevas registran la nueva versión (`docs/operations/env-001-pilot/README.md`;
> `apps/web/src/app/ops/orders/new/page.tsx`). Solo el contenedor web recibe la variable; la API no la conoce, así que
> no puede comprobar que la versión de una orden sea la vigente. En la carga por CSV la versión viene escrita en el
> archivo y nadie la compara con la vigente (sección 2). **[PENDIENTE: decidir si eso basta o si la carga por archivo
> debe usar la versión configurada, lo que es un cambio técnico.]** Si el Cliente es consumidor, la Ley Federal de
> Protección al Consumidor, artículo 90, fracción I, anula las cláusulas que permiten al proveedor modificar el contrato
> por sí solo.

---

## 17. Ley aplicable y jurisdicción

**[PENDIENTE: ley aplicable, tribunales competentes (por ejemplo, los de la ciudad del domicilio de la Plataforma) o
arbitraje, y renuncia a otro fuero. El piloto opera en Culiacán, Sinaloa. Si el Cliente es consumidor, puede presentar
quejas ante la Procuraduría Federal del Consumidor (artículo 99) y no valen las cláusulas que lo obliguen a renunciar
a la protección de la ley o lo sometan a tribunales extranjeros (artículos 1 y 90, fracción VI).]**

## 18. Otras cláusulas

**[PENDIENTE: el abogado decide si se incluyen: notificaciones entre las partes, cesión, caso fortuito y fuerza mayor,
divisibilidad de cláusulas, acuerdo completo, idioma (Ley Federal de Protección al Consumidor, artículo 85: en español
y legible) y confidencialidad.]**

---

## Notas legales para el abogado (se borran del texto final)

### A. ¿Aplica la Ley Federal de Protección al Consumidor?

- **Consumidor** es quien adquiere o disfruta un servicio como destinatario final (artículo 2, fracción I). Una empresa
  que contrata envíos para su negocio no lo es, salvo para quejas y arbitraje ante la Procuraduría (artículos 99 y
  117), y en el caso de personas morales solo si están acreditadas como microempresas o microindustrias (artículo 2,
  fracción I, segundo párrafo).
- **[PENDIENTE: si el servicio será solo para empresas. Con la tarifa ocasional y sin cuenta cliente puede contratar
  cualquier persona. Opciones: (a) limitar el servicio a empresas y decirlo en los términos; (b) aceptar consumidores y
  ajustar el borrador.]**
- Si hay consumidores, estos términos son un **contrato de adhesión** (artículo 85) y, entre otras cosas:
  - no valen las cláusulas que permiten al proveedor cambiar el contrato por sí solo, lo liberan de su
    responsabilidad civil, fijan plazos de prescripción inferiores a los legales o exigen formalidades para reclamar
    (artículo 90, fracciones I, II, IV y V). Revisar las secciones 12 y 16;
  - las disposiciones de la ley son irrenunciables (artículo 1);
  - en operaciones por medios electrónicos, el proveedor debe dar antes su domicilio físico, teléfonos y medios para
    reclamar, y toda la información de términos, costos y cargos adicionales (artículo 76 BIS, fracciones III y V), y
    seguir la Norma Mexicana que menciona el artículo 76 BIS 1 **[PENDIENTE: el abogado identifica cuál es y si aplica a
    la venta asistida por el personal]**;
  - **[PENDIENTE: verificar si alguna norma oficial mexicana obliga a registrar ante la Procuraduría el contrato de
    adhesión de este servicio (artículos 86 y 87). No se encontró en esta revisión.]**

### B. Aceptación por medios electrónicos (Código de Comercio)

- Un contrato mercantil celebrado por medios electrónicos se perfecciona cuando se recibe la aceptación (artículo 80).
- No se niega validez a la información por estar en un mensaje de datos, que puede usarse como prueba (artículo 89 bis).
- Se presume que un mensaje de datos proviene del emisor si lo envió él, alguien facultado para actuar en su nombre o
  un sistema programado por él (artículo 90). **En la aceptación asistida, quien genera el registro es el personal de
  la Plataforma, no el Cliente.** Este es el punto débil de la evidencia actual (sección 2).
- La forma escrita se cumple con un mensaje de datos íntegro y accesible para consulta posterior (artículo 93), y la
  forma original exige una garantía confiable de integridad (artículo 93 bis). La huella SHA-256 del registro ayuda a
  probar que el registro no cambió, pero no cubre el texto de los términos, que no se guarda con la orden.
- El comerciante conserva al menos diez años los mensajes de datos en que consten contratos, íntegros e inalterados, y
  la Secretaría de Economía fija los requisitos en una norma oficial mexicana (artículo 49). La norma vigente sobre
  conservación de mensajes de datos es la NOM-151-SCFI-2016. **[PENDIENTE: confirmar que es la que aplica y si hace
  falta una constancia de conservación conforme a ella para la evidencia de aceptación y para el texto de cada
  versión.]**

### C. Contrato de transporte terrestre (Código de Comercio, artículos 576 a 604)

**[PENDIENTE: confirmar si este servicio es un contrato de transporte mercantil (artículo 576) y, si lo es, revisar con
estos términos:]**

- el porteador puede encargar la conducción a otro y conserva su carácter frente a quien contrató primero (artículo
  577; sección 3.6);
- la carta de porte y sus datos (artículos 581 a 585). El sistema no emite ningún documento para el Cliente
  (sección 3.7);
- las obligaciones del porteador: cuidar las mercancías desde que las recibe hasta que las entrega, probar que una
  pérdida o avería no fue su culpa, y pagar pérdidas y averías según el valor que fijen peritos (artículo 590);
- la extinción de la responsabilidad por pérdidas y averías (artículos 592 y 593; sección 12.1);
- la cancelación por el cargador (artículo 578; sección 4.10).

### D. Lo que este borrador no pudo verificar

1. Si alguna norma oficial mexicana obliga a registrar el contrato de adhesión ante la Procuraduría Federal del
   Consumidor.
2. La regulación estatal o municipal del reparto local en Culiacán, si existe.
3. Cuál es la Norma Mexicana a que se refiere el artículo 76 BIS 1 de la Ley Federal de Protección al Consumidor.
4. Las condiciones de Meta (WhatsApp Business) sobre el permiso del destinatario, citadas solo como las describió la
   pregunta del owner E4-P1; no se abrieron sus políticas.
5. Las decisiones E4 a E4-P13 están en las respuestas literales del owner, pero todavía no en el decision log ni en el
   código. Si su implementación cambia, la sección 10 cambia.
6. Qué hará la Plataforma fuera del sistema: cobro del envío, entrega del efectivo al Cliente, atención de
   reclamaciones y copias de fotos. Nada de eso existe en el código.

## Fuentes

Consultadas el 2026-10-20:

1. Código de Comercio, texto vigente, última reforma DOF 14-11-2025. Cámara de Diputados:
   <https://www.diputados.gob.mx/LeyesBiblio/pdf/CCom.pdf>. Artículos 49, 80, 89, 89 bis, 90, 93, 93 bis, 576 a 578,
   581 a 585, 588, 590 a 593, 601, 603 y 604.
2. Ley Federal de Protección al Consumidor, texto vigente, última reforma DOF 12-12-2025. Cámara de Diputados:
   <https://www.diputados.gob.mx/LeyesBiblio/pdf/LFPC.pdf>. Artículos 1, 2, 76 BIS, 76 BIS 1, 85 a 87, 90, 99 y 117.
3. Secretaría de Economía, Catálogo Mexicano de Normas, ficha de la NOM-151-SCFI-2016, "Requisitos que deben
   observarse para la conservación de mensajes de datos y digitalización de documentos", vigente, DOF 30-03-2017:
   <https://platiica.economia.gob.mx/normalizacion/nom-151-scfi-2016/>. Solo se abrió la ficha, no el texto de la
   norma.

Las fuentes de protección de datos personales (LFPDPPP, su Reglamento y los Lineamientos) están en el
[aviso de privacidad integral](aviso-de-privacidad-integral-borrador.md), sección "Fuentes".
