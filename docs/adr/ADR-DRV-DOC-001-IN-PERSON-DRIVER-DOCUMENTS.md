# ADR-DRV-DOC-001: documentos del repartidor registrados en persona por despacho

- Estado: aceptado (DRV-DOC-001-IN-PERSON-2026-11-02; las subdecisiones técnicas de la sección "Subdecisiones" esperan
  el visto bueno del owner antes de fusionar)
- Fecha: 2026-11-02
- Alcance: módulo Drivers (columnas de `drivers.driver_documents`, servicio, endpoints y configuración
  `Drivers:Documents`), la regla de evidencia de DSP-001 y sus seis lectores, la columna "Documentos" de
  "Repartidores" en el panel y la lista de solo lectura de "Cuenta" en la app del repartidor. Ítem de backlog:
  DRV-DOC-001 (AI-08), puerta GATE-007. **Apagado por defecto y apagado en el piloto** hasta que el owner registre
  GATE-007.

## Contexto

AI-08 DRV-DOC-001 pide "a way to record the documents of each real driver in drivers.driver_documents - AI-05
registerMyDriverDocument and listMyDriverDocuments (/driver/me/documents), or an MDM-001 loader extension that carries
the document metadata; the implementing task chooses with an ADR", más una operación de despacho que marque VALID o
REJECTED. La investigación (base 974a11cf) encontró que:

- AI-05 `RegisterDriverDocumentRequest` dejaba al cliente elegir `object_key` y `sha256` y `DriverDocument` devolvía
  `object_key`, en contra del patrón POD (ADR-020); no había operación de despacho.
- AI-06 exigía `object_key text NOT NULL` y `sha256 bytea NOT NULL`, y DSP-001 rechaza una llave vacía o un hash que no
  mida 32 bytes (`DOCUMENT_HASH_INVALID`): un registro sin archivo necesitaba un cambio de esquema o un valor de
  atestación.
- El cargador MDM-001 no corre en el piloto ("is not wired" en jobs.bicep) y los borradores legales decían "Hoy no se
  recaban".

El owner contestó la ronda Y (2026-11-01) de doce preguntas; sus textos literales (contexto, pregunta, respuesta, texto
de la opción y cada opción no elegida) están en la fila `DRV-DOC-001-IN-PERSON-2026-11-02` de
`docs/normative/v0.6/decision-log.md`. En resumen:

| # | Respuesta | Efecto |
| --- | --- | --- |
| Y1 | "Despacho en persona (Recommended)" | Tercera vía, además de las dos que nombra AI-08, aprobada explícitamente: el repartidor muestra el original; despacho anota tipo y vencimiento en el panel y lo aprueba o rechaza. |
| Y2 | "Ya, apagado (Recommended)" | Se construye con datos sintéticos; en el piloto queda apagado hasta GATE-007. |
| Y3 | "No, solo datos (Recommended)" | Tipo, vencimiento, estado y quién lo revisó; ninguna foto, archivo ni número. |
| Y4 | "Él y despacho (Recommended)" | El repartidor ve los suyos; DISPATCHER de su empresa y PLATFORM_ADMIN con MFA ven tipo, vencimiento y estado; FINANCE y VIEWER nada. |
| Y5 | "Cada año (Recommended)" | Licencia permanente de Sinaloa: fecha = día de revisión + 12 meses, con la etiqueta "Revisión". |
| Y6 | "Aviso en el panel (Recommended)" | «Vence en N días» desde 30 días antes y «Vencido» después; nada se guarda como EXPIRED. |
| Y7 | "Lista fija (Recommended)" | Motivos fijos, sin texto libre. |
| Y8 | "En la app (Recommended)" | «Cuenta > Documentos» al abrir o actualizar; sin mensajes. |
| Y9 | "Vigencia + 1 año (Recommended)" | Plazo que se propone al abogado; la purga es otra tarea. |
| Y10 | "Revocar, lo actual sigue (Recommended)" | Despacho puede revocar; sin pedidos nuevos, lo actual sigue, despacho reasigna; sin séptimo flujo. |
| Y11 | "Solo los exigidos (Recommended)" | Solo los tipos que exige el vehículo; ni BACKGROUND_CHECK ni OTHER. |
| Y12 | "Sí, todo ese día (Recommended)" | Una fecha impresa vale todo ese día en hora de Culiacán. |

Es un cambio MAJOR de operación (AI-01 §7) con alcance de privacidad (GATE-007): agrega una pantalla de despacho que
muestra datos de documentos y una forma de registrar documentos de personas reales.

## Decisión

1. **Método.** Despacho registra en persona. No existe carga del repartidor: `registerMyDriverDocument` queda en AI-05
   como reservada y no implementada (`x-implementation-status: RESERVED_NOT_IMPLEMENTED`), con un cuerpo que ya no deja
   elegir llave ni hash (`upload_session_id`, tipo y fecha impresa), para que una carga futura (con su propia decisión,
   sesión de carga de ADR-020 y GATE-007) no rompa el esquema.
2. **Semántica de la evidencia: variante explícita.** `drivers.driver_documents` gana `evidence_kind` (`FILE`, valor por
   omisión, o `IN_PERSON`); `object_key` y `sha256` pasan a ser nulos y `driver_documents_evidence_check` exige ambos en
   una fila FILE y ninguno en una IN_PERSON. DSP-001 conserva su garantía por tipo de evidencia: una fila FILE necesita
   llave no vacía y SHA-256 de 32 bytes; una IN_PERSON no debe tener ninguna de las dos (su evidencia es la revisión de
   despacho, que el esquema exige); cualquier otra forma es `DOCUMENT_HASH_INVALID`. Los seis lectores de DSP-001
   (Drivers, asignación, selector, ofertas externas, transiciones de orden y rutas) leen `evidence_kind` y aceptan llave
   y hash nulos. Se descartó la variante de "marcador de llave fijo + SHA-256 de un registro canónico de atestación"
   porque sobrecarga el significado de `object_key`/`sha256` (una fila parecería tener archivo sin tenerlo), obligaría a
   definir y versionar una forma canónica nueva y haría ambigua una carga futura; la variante explícita cuesta tocar seis
   consultas de lectura pero deja la carga futura sin cambio de esquema y sin valores ficticios.
3. **Columnas de revisión.** `expiry_basis` (`PRINTED_EXPIRY` o `PERMANENT_LICENSE_REVIEW`, solo para licencia de
   conducir y siempre con vencimiento), `rejection_code` (los seis motivos de Y7, solo en REJECTED), `reviewed_by`/
   `reviewed_at` y `revoked_by`/`revoked_at` (en pares, con llave foránea a `identity.users`; una fila revocada es
   REVOKED). `driver_documents_in_person_check`: una fila IN_PERSON fue revisada, es de uno de los cuatro tipos
   registrables y es VALID o REVOKED con vencimiento y base, o REJECTED con motivo y sin vencimiento. AI-06 lleva el mismo
   DDL para instalaciones nuevas; el paso del carril Drivers `20261102000100_AddInPersonDriverDocuments` lo aplica o lo
   adopta, con Up, Down y Up probados en PostgreSQL 18/PostGIS 3.6. El Down se niega mientras existan registros en persona,
   revisiones, motivos o revocaciones (`DRVDOC001_DOWNGRADE_BLOCKED_DATA_PRESENT`). AI-18 no cambia.
4. **Operaciones (AI-05).**
   - `listDriverDocuments` (`GET /drivers/documents`): una página de los repartidores OWN no INACTIVE de la organización
     (los mismos que `listOrganizationDrivers`, mismo orden y tamaño), cada uno con un espacio por tipo exigido por su
     vehículo y el documento más reciente de ese tipo (la regla de DSP-001) o null. `documents_enabled` dice si está
     encendido.
   - `recordDriverDocument` (`POST /drivers/{driverId}/documents`): `Idempotency-Key`; VALID con
     `expiry_basis`+`expires_on` (o licencia permanente sin fecha) o REJECTED con `rejection_code`; 201 con el documento.
   - `revokeDriverDocument` (`POST /drivers/{driverId}/documents/{documentId}/revoke`): sin cuerpo; 200 con el documento
     REVOKED.
   - `listMyDriverDocuments` (`GET /driver/me/documents`): los del propio repartidor, solo lectura.
   - `DriverDocument` pierde `object_key` y gana `rejection_code`, `valid_through`, `expiry_basis`, `recorded_at` y
     `revoked_at`, con `additionalProperties: false`. Nunca se devuelve foto, archivo, número, llave, hash ni el revisor.
5. **Autorización.** Capacidad en el backend antes de leer cualquier estado (`x-capability-matrix
   driver_document_operations`) y RLS forzado: DISPATCHER sin MFA y PLATFORM_ADMIN con MFA para las tres de despacho;
   DRIVER solo para la suya; FINANCE, VIEWER y cualquier otro rol, 403 uniforme. Un repartidor o documento desconocido,
   mal formado, de otra organización, retirado o no OWN es el 404 uniforme.
6. **Escritura.** Una transacción de tenant por registro o revocación: perfil bloqueado `FOR UPDATE`, la fila, la
   auditoría (`DRIVER_DOCUMENT_RECORDED` / `DRIVER_DOCUMENT_REVOKED`, solo tipos y códigos) y, al registrar, la respuesta
   de idempotencia. Solo se escriben `drivers.driver_documents`, `platform.audit_logs` y `platform.idempotency_keys`:
   ningún flujo de AI-13 §4 nuevo (`CrossSchemaWriteCatalogTests` sin cambios). Revocar no toca paradas en curso (Y10).
7. **Calendario.** Hora de Culiacán (`America/Mazatlan`). Fecha impresa: válida todo ese día; `expires_at` = día siguiente
   a las 00:00 locales (Y12). Licencia permanente: día de revisión + 12 meses, tratado igual (Y5). La conversión a
   instante la hace PostgreSQL (`AT TIME ZONE`).
8. **Apagado y puerta.** `Drivers:Documents:Enabled` es falso por omisión. Mientras esté apagado no se lee ni escribe
   nada: las listas responden `documents_enabled: false` y registrar o revocar responde 503. Encenderlo fuera de
   Development, Testing y DEV_SYNTHETIC, y siempre con `PAQUETERIA_DEPLOYMENT_CLASS=PILOT_REAL_PEOPLE`, exige
   `Drivers:Documents:Gate007DecisionId` con forma `GATE-007-…` (validación al iniciar, como
   `Voice:Twilio:Gate007DecisionId` y `Notifications:RecipientNotices:Gate007DecisionId`). En el piloto
   `Drivers__Documents__` es configuración administrada por `apps.bicep` (`tools/azr-001/env001_pilot_guards.py`): el
   archivo de settings no lo puede encender.
9. **Privacidad.** Ninguna línea de log, auditoría, outbox, evento o respuesta lleva número, archivo, llave ni hash de
   documento (no existen para un registro en persona). La retención propuesta al abogado es la vigencia más un año
   (Y9); DRV-DOC-001 no agrega purga.

## Subdecisiones técnicas (no están en Y1 a Y12; el owner las revisa antes de fusionar)

1. Evidencia explícita `evidence_kind` en lugar de hash de atestación (punto 2).
2. Registrar y revocar admiten también PLATFORM_ADMIN con MFA (la regla de `assignDriver`); AC3 nombra "a dispatcher of
   the driver organization".
3. La revocación cambia la fila VALID vigente a REVOKED (no inserta otra); solo la más reciente VALID de su tipo es
   revocable (409 `DOCUMENT_NOT_REVOCABLE`); revocar de nuevo una REVOKED responde lo mismo sin escribir; sin motivo de
   revocación.
4. Registrar exige `Idempotency-Key` (24 h, ligada a organización, persona, repartidor y cuerpo); revocar no lleva llave
   (es idempotente por estado).
5. Un REJECTED no lleva fecha ni base de vencimiento. Consecuencia: mientras un REJECTED sea el más reciente de su
   tipo, el selector de repartidores muestra dos motivos, `DOCUMENT_STATUS_NOT_VALID` y `DOCUMENT_EXPIRY_MISSING`
   («Un documento no tiene fecha de vencimiento»), por la regla vigente de DSP-001.
6. Un VALID debe tener último día válido entre hoy y diez años adelante (409 `EXPIRY_DATE_OUT_OF_RANGE`); una fecha
   impresa ya pasada se rechaza con el motivo "Vencido".
7. La fecha de revisión de la licencia permanente vale todo ese día, como una fecha impresa; 29 de febrero + 12 meses es
   28 de febrero.
8. Se registran documentos de perfiles OWN que no están INACTIVE (PENDING y SUSPENDED incluidos, para el alta); la lista
   del repartidor muestra su propio perfil con cualquier estado.
9. Las listas muestran solo el documento más reciente por tipo exigido (sin historial); la columna del panel sale de una
   operación propia del módulo Drivers paginada como "Repartidores", en lugar de ampliar `listOrganizationDrivers`
   (el módulo Dispatch no lee documentos y la web actual, que rechaza campos desconocidos, no se rompe).
10. La API no devuelve quién revisó (Y4 nombra tipo, vencimiento y estado); se guarda y se audita.
11. `documents_enabled` en las listas; apagado no lee nada; con `Drivers:Provider=Disabled` también está apagado.
12. La puerta exige el id de GATE-007 en todo entorno que no sea Development, Testing o DEV_SYNTHETIC (más estricto que
    "solo con PILOT_REAL_PEOPLE"); se valida solo la forma.
13. La validación al iniciar también exige, con Enabled, el proveedor PostgreSQL, tipos exigidos dentro de los cuatro
    registrables y `NonExpiringDocumentTypes` vacío.
14. `created_at`/`reviewed_at` = hora de la sentencia después del bloqueo del perfil, para que los documentos de un
    repartidor queden ordenados por commit.
15. Llave foránea de `reviewed_by`/`revoked_by` a `identity.users`.
16. Textos es-MX de AI-07 que no dio el owner: etiquetas de tipo ("Identificación oficial", "Licencia de conducir",
    "Tarjeta de circulación", "Seguro del vehículo"), estados de la columna ("Completos", "Incompletos", «Vence hoy»,
    «Revisión en N días», «Revisión vencida»), la línea fija del diálogo ("El repartidor te muestra el original. No
    tomes fotos ni copias; solo anota el tipo y la fecha."), la confirmación de revocar y la línea de "Cuenta".
17. AI-18 no cambia: el runtime conserva `DELETE` sobre `drivers.driver_documents` (riesgo residual; la auditoría
    append-only guarda la traza).
18. Sin límite SEC-004 (ninguna decisión del owner lo nombra).

## Alternativas descartadas

- El repartidor sube una foto (Y1 no elegida): más trabajo, almacenamiento con antivirus y datos sensibles guardados.
- Cargador MDM-001 (Y1 no elegida): lento para renovaciones y sin job en el piloto.
- Marcador de llave fijo + hash de atestación: ver punto 2.
- Proceso nocturno que marque EXPIRED (Y6 no elegida): permiso privilegiado nuevo; el bloqueo al vencer ya es automático.
- Revocar quitando también las entregas en curso (Y10 no elegida): séptimo flujo entre módulos.

## Consecuencias

- Ningún repartidor real se puede asignar hasta que existan sus documentos, y eso espera GATE-007 (y, además, los
  perfiles reales esperan GATE-007 y el job del cargador MDM-001).
- El repartidor debe ir en persona en el alta y en cada renovación; no queda copia como evidencia si una aseguradora o
  un reclamo laboral la pide (riesgo que el abogado debe valorar).
- Un registro REJECTED más reciente pausa al repartidor aunque el anterior del mismo tipo fuera VALID (regla vigente de
  DSP-001, "la fila más reciente gana").

## Reversión

`Drivers:Documents:Enabled=false` (por omisión). El Down del paso del carril se niega mientras haya datos en persona
(`DRVDOC001_DOWNGRADE_BLOCKED_DATA_PRESENT`); sin datos, quita exactamente las cinco restricciones y las siete columnas y
vuelve a exigir `object_key` y `sha256`.
