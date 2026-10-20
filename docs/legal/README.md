# Documentos legales: borradores

**Estado: BORRADORES. No resuelven nada.** Ningún dato personal real entra al piloto hasta que el owner registre su
aprobación en el decision log, y ningún texto de términos y condiciones está vigente hasta que el abogado del owner lo
apruebe.

Los avisos de privacidad y las reglas de retención existen por esta decisión del owner
(`docs/normative/v0.6/decision-log.md`):

> | 2026-09-27 | GATE-007-PRIVACY-DRAFT | Legal process decision | Literal: "Redacta aviso de privacidad". Claude drafts
> the privacy notice and retention rules for review by the owner's lawyer; the draft resolves nothing and no real PII
> enters the pilot until the owner records its approval | GATE-007, docs/legal | Project owner (2026-09-27, sesión de
> trabajo con el owner vía Claude Code) |

El borrador de términos y condiciones existe por la decisión D1 del owner (2026-10-20, sesión de trabajo con el owner
vía Claude Code; todavía no tiene fila en el decision log). Pregunta y respuesta literales:

> Pregunta: "El asistente de nueva orden dice que el cliente \"aceptó los términos\", pero no existe texto de términos y
> condiciones (la guía del despliegue lo pide). ¿Los redacto?"
>
> Respuesta: "Borrador para el abogado (Recommended)". Texto de la opción: "Igual que el aviso de privacidad: borrador
> en docs/legal con marcas de pendiente, que no resuelve nada hasta que tu abogado lo apruebe."
>
> Opción no elegida: "Los da mi abogado" ("No redacto nada; tu abogado entrega el texto y yo solo lo publico con su
> versión.")

Los redactó Claude para que los revise el abogado del owner. No son normativos: si contradicen un contrato de
`docs/normative/v0.6/`, manda el contrato. Ningún documento de esta carpeta cambia el comportamiento del sistema.

## Archivos

| Archivo | Contenido |
| --- | --- |
| [`aviso-de-privacidad-integral-borrador.md`](aviso-de-privacidad-integral-borrador.md) | Aviso integral: responsable, datos por tipo de titular, finalidades, fundamento, encargados y transferencias, ARCO, revocación, cookies, cambios y fuentes legales |
| [`aviso-de-privacidad-simplificado-borrador.md`](aviso-de-privacidad-simplificado-borrador.md) | Tres avisos simplificados: asistente de órdenes, página de seguimiento y alta del repartidor |
| [`reglas-de-retencion-borrador.md`](reglas-de-retencion-borrador.md) | Plazos propuestos por dato, con tabla y columna, justificación, método de borrado y qué existe hoy en el código; tablas de solo inserción; trabajo técnico pendiente |
| [`terminos-y-condiciones-borrador.md`](terminos-y-condiciones-borrador.md) | Términos del servicio con los clientes que envían (decisión D1): aceptación, servicio, precio con IVA incluido, cobro contra entrega, artículos prohibidos, intentos y devolución, fotos de entrega, avisos al destinatario, llamadas enmascaradas, responsabilidad y reclamaciones, datos personales, suspensión, versiones y jurisdicción; notas legales para el abogado y fuentes |

Convenciones de los cuatro archivos:

- `[RAZÓN SOCIAL]`, `[DOMICILIO]`, `[CORREO ARCO]`, `[CORREO DE ATENCIÓN]`, `[NOMBRE DEL SERVICIO]`, `[URL DEL AVISO]`,
  `[URL DE LOS TÉRMINOS]`, `[VERSIÓN]` y `[FECHA]` son espacios para datos del owner. No se inventaron.
- **[PENDIENTE: …]** marca una decisión del abogado o del owner.
- Los recuadros "Nota para revisión" apuntan al código o a la decisión que respalda cada afirmación. Se borran del texto
  final.

## Qué pide GATE-007

AI-10 (`docs/normative/v0.6/specs/AI-10_DECISIONS_AND_GATES.yaml`), severidad `BLOCKER_FOR_REAL_PII`:

> approved notice/lawful basis/retention/ARCO for customer PII, legal acceptance evidence, POD, driver documents and
> driver location telemetry; cryptographic erasure/access restriction rules

| Elemento | Dónde se trata | Estado del borrador |
| --- | --- | --- |
| Aviso | Aviso integral y simplificado | Redactado; faltan los datos del responsable |
| Fundamento | Aviso integral, sección 5 | Propuesto; el abogado decide la base para destinatarios |
| Retención | Reglas de retención, sección 3 | Plazos propuestos, ninguno aprobado; casi nada está implementado |
| ARCO | Aviso integral, sección 7; reglas de retención, sección 5 | Procedimiento redactado; no existen herramientas técnicas |
| Evidencia legal de aceptación | Aviso integral, sección 3.2; reglas de retención, secciones 3.2 y 4 | Descrita como existe (registro asistido, sin IP) |
| Evidencia de entrega (fotos) | Aviso integral, secciones 3.1 y 3.6; reglas de retención, secciones 3.2 y 4 | Plazo propuesto; sin política de borrado |
| Documentos del repartidor | Aviso integral, sección 3.4 | Hoy no se recaban; pendiente decidir si se piden |
| Ubicación del repartidor | Aviso integral, sección 3.4; reglas de retención, sección 3.3 | La API existe y la app no la envía; pendiente decidir |
| Borrado criptográfico y restricción de acceso | Reglas de retención, secciones 2 y 4 | Explica qué permite el sobre de cifrado actual y qué no |

## Qué deben decidir el owner y el abogado

Identidad y papeles:

1. Razón social, domicilio, correo y persona o departamento de datos personales.
2. Cómo se identifica al responsable en la página pública sin contradecir GATE-001-NEUTRAL-PUBLIC-BRAND-2026-10-10.
3. Papel de cada organización: si la plataforma es responsable o encargada de los datos que capturan las empresas
   clientes, y cómo se trata el aliado que opera una orden ajena.
4. Si AuthCenter es parte del mismo responsable o una entidad aparte.

Fundamento y consentimiento:

5. Base legal para tratar los datos del destinatario, que no tiene relación directa con la plataforma, y si el
   remitente debe garantizar que puede darlos.
6. Si basta la aceptación asistida que registra el personal ("El cliente vio el desglose y aceptó…") y cómo se entrega
   el aviso al cliente en ese momento.
7. Si los pagos a repartidores necesitan consentimiento expreso (datos patrimoniales, LFPDPPP artículo 7).
8. Confirmar que el piloto no tiene finalidades secundarias.

Repartidores (ligado a GATE-008):

9. Si se habilita la ubicación del repartidor: base legal, consentimiento, horario, quién la ve, plazo y si se trata
   como dato sensible.
10. Si se piden documentos, cuáles, dónde se guardan y por cuánto tiempo.
11. Si aplican las reglas de trabajo en plataformas digitales de la Ley Federal del Trabajo (portabilidad de datos,
    política de gestión algorítmica).

Proveedores y transferencias:

12. Contratos o cláusulas de encargo con Microsoft, Google, Meta y Twilio, y dónde trata los datos cada uno.
13. Si alguna transferencia requiere la cláusula de aceptación del artículo 35.
14. Si se menciona el botón "Cómo llegar" (Google Maps en el teléfono del repartidor).

Retención y ARCO:

15. Cada plazo propuesto en las reglas de retención y la definición de "fin del envío".
16. Formato de solicitud ARCO y forma de verificar la identidad.
17. Cómo se avisará de cambios al aviso: hoy no existe un mecanismo en el sistema.

Otros:

18. Si siguen aplicando el Reglamento de 2011 y los Lineamientos del Aviso de Privacidad de 2013 con la ley de 2025.
19. Si las cookies técnicas requieren advertencia.
20. Metadatos EXIF de las fotos (pueden llevar GPS): informarlos o eliminarlos.
21. Trato de "usted" o "tú".

## Lo que viene después de la aprobación (no lo hace este cambio)

Cuando el abogado y el owner terminen:

1. El owner registra en el decision log una fila `GATE-007-…` con tipo `Gate resolution` o `Gate scoping`. El
   despliegue del piloto exige esa fila: la variable `PILOT_GATE_007_DECISION` y la verificación `gate-decision` de
   `tools/azr-001/env001_pilot_guards.py`. La fila `GATE-007-PRIVACY-DRAFT` no sirve porque no resuelve la compuerta
   (`docs/operations/env-001-pilot/README.md`).
2. Se publica el aviso integral aprobado en su URL y su versión se pone en `webPrivacyVersion`
   (`deploy/azure/pilot/web.parameters.json`), que hoy vale `OWNER_DECISION_REQUIRED`.
3. Cambios de interfaz para mostrar los avisos simplificados (ver las notas de implementación del aviso simplificado).
4. Trabajo técnico de retención y ARCO (reglas de retención, sección 6).
5. Solo entonces se pueden usar las banderas que dependen de GATE-007: `--gate-007-closed` del comando
   `master-data-gate` del migrador, que permite cargar perfiles reales de repartidores (MDM-001), y
   `Voice:Twilio:Gate007DecisionId` (VOICE-001).

Cada paso tiene su propio PR y sus propias decisiones. Este cambio no agrega filas al decision log ni toca
`docs/normative/**` ni parámetros de despliegue.

## Términos y condiciones (decisión D1)

El borrador **no resuelve nada**: no fija precios, límites, seguros ni datos de la empresa, y no está vigente. Hoy el
asistente de nueva orden registra que el cliente "aceptó los términos", pero el sistema no tiene ningún texto de
términos. Cada orden guarda solo un identificador de versión: en el asistente, el configurado en `webTermsVersion`
(`deploy/azure/pilot/web.parameters.json`), que vale `OWNER_DECISION_REQUIRED`; en la carga por CSV, el que trae el
archivo.

Lo principal que deben decidir el owner y el abogado (la lista completa está en las marcas **[PENDIENTE: …]** del
borrador):

1. Con quién se celebran: solo empresas o también personas. Con la tarifa ocasional puede contratar cualquier persona,
   y entonces aplica la Ley Federal de Protección al Consumidor.
2. Si basta la aceptación asistida, que registra el personal y no el cliente, y cómo se le pone el texto a disposición.
3. Tarifas, forma de pago del envío, facturación y cargos por reintento, devolución o cancelación (GATE-005, GATE-006 y
   GATE-010).
4. Plazo y forma de entregar al cliente el efectivo del cobro contra entrega.
5. Lista de artículos prohibidos (GATE-010) y si la confirmación del personal basta.
6. Responsabilidad: monto máximo, valor declarado, exclusiones, seguro (GATE-009) y el plazo de reclamación de
   72 horas frente a los plazos del Código de Comercio.
7. Obligación del cliente sobre el permiso del destinatario para recibir avisos por WhatsApp (decisión E4-P1).
8. Causas y procedimiento de suspensión.
9. Dónde se archiva el texto de cada versión, cómo se avisa de los cambios, ley aplicable y jurisdicción.
10. Los términos de los repartidores, propios, externos y de aliados, son un documento aparte (GATE-008). No se
    redactaron.

Después de la aprobación (no lo hace este cambio):

1. El owner registra la decisión en el decision log.
2. Se publica el texto aprobado en [URL DE LOS TÉRMINOS] y se archiva cada versión.
3. Se pone su versión en `webTermsVersion` mediante el proceso del owner. Las órdenes nuevas registran esa versión
   (`docs/operations/env-001-pilot/README.md`).
4. Cambios de interfaz para mostrar o enlazar los términos en el asistente de nueva orden; hoy no se muestran.
5. Si el owner lo decide, cambios técnicos que el borrador señala: que la carga por CSV use la versión vigente (hoy la
   toma del archivo), ver y descargar fotos de entrega para atender reclamaciones, y foto al cerrar una devolución.

## Mantenimiento

Los avisos y las reglas de retención describen el código al 2026-10-19; los términos, al 2026-10-20. Si un cambio
posterior altera qué datos personales se recaban, dónde se guardan, a quién se envían o cuánto duran, o lo que el
sistema registra al aceptar, cobra, entrega, devuelve o reclama, actualice estos documentos en el mismo PR.
