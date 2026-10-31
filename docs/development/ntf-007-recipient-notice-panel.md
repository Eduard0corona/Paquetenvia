# NTF-007: panel "Avisos al destinatario" y "Detener avisos"

Decisiones: `NTF-RECIPIENT-NOTICES-2026-10-20` (E4-P8 "Las tres vías (Recommended)" y E4-P16) y
`NTF-RECIPIENT-NOTICE-PANEL-2026-10-30` (ronda T: T1 a T6, citadas en `docs/normative/v0.6/decision-log.md`; T1 registra
la decisión I4). Serie: [`ntf-recipient-notices.md`](ntf-recipient-notices.md), PR-6. Contrato: AI-05
`x-pilot-contract-deltas` `NTF-RECIPIENT-NOTICES` parte NTF-007, `listOrderRecipientNotices`, `stopOrderRecipientNotices`
y `x-capability-matrix` `recipient_notice_operations`; AI-07 `/ops/orders/:id` `recipient_notices`; AI-24
`recipient_notices.dispatcher_view` y `opt_out.dispatcher`; AI-08 NTF-007.

**Estado: implementado y apagado.** Sin migración, sin permisos ni funciones nuevas. `Notifications:RecipientNotices:Enabled`
sigue falso, así que el Worker no crea avisos y la lista de cada orden está vacía; el panel lo dice con la línea fija de
T4. "Detener avisos" responde 503 en el piloto hasta que AI-08 NTF-008 conecte `paquetenvia-contact-lookup-key-1` a la API.
GATE-004 y GATE-007 siguen abiertas.

## Configuración (solo nombres de llave)

- `ContactLookup:Keys` y `ContactLookup:CurrentKeyVersion` (API, NTF-006): sin llave, la baja responde 503.
- `Locations:PiiProtector`: `AzureKeyVault` en el piloto (el teléfono y el correo se abren con la llave de datos personales
  de la API), `Mock` en desarrollo, pruebas y `DEV_SYNTHETIC` (no se descifra nada: se usan los marcadores comunes
  `+520000000002` y `destinatario@example.invalid`), `Disabled` sin protector (503).
- Ninguna de las dos operaciones tiene interruptor ni límite de SEC-004 (T6, punto 10).

## Superficie HTTP (API)

Las dos rutas viven en el módulo Notifications (`Notifications.Endpoints/RecipientNoticePanelEndpoints.cs`) y conservan la
etiqueta Orders de AI-05 (T6, punto 1). Ambas responden `Cache-Control: no-store` en el 200 y en cada problema que dan
ellas mismas (el 401 de la autenticación y el 403 del contexto de organización llegan antes).

- `GET /api/v1/orders/{orderId}/recipient-notices` (`listOrderRecipientNotices`): sesión, organización seleccionada y
  capacidad (DISPATCHER; PLATFORM_ADMIN con MFA, si no 403 `MFA_REQUIRED`; cualquier otro rol 403) antes de leer nada;
  luego un id mal formado es el 404 uniforme. Una transacción de la organización seleccionada lee la orden solo si esa
  organización es la dueña (RLS deja pasar al operador; el filtro explícito por `owner_org_id` lo vuelve 404), la casilla
  de WhatsApp (versión vigente), si el destino guarda correo bajo la llave de datos personales (T6, punto 3) y los avisos
  `ORDER_DESTINATION` de la orden (nunca IN_APP), por creación y WhatsApp antes que correo, máximo 100 (T6, punto 2). No se
  descifra nada. Un valor fuera de los conjuntos cerrados (plantilla, canal, estado o código) es 503 y nunca se reenvía
  (T6, punto 4). Sin hora programada (T2).
- `POST /api/v1/orders/{orderId}/recipient-notices/stop` (`stopOrderRecipientNotices`): primero la forma - una sola
  `Idempotency-Key` válida, sin consulta y sin cuerpo; si no, 409 `INVALID_REQUEST` -, luego la capacidad y el 404 uniforme
  como la lista.

## Qué hace "Detener avisos"

`RecipientNoticePanelService` y `PostgreSqlRecipientNoticePanelStore` (T6, puntos 5 a 8):

1. Una transacción de la organización seleccionada devuelve la respuesta guardada de la misma clave (repetición: no escribe
   nada) o lee los cifrados del teléfono y el correo del destino de una orden propia, y se cierra. La clave usada para otra
   orden o por otra persona es 409 `IDEMPOTENCY_CONFLICT`.
2. Sin transacción abierta (AI-13 §7.2), los abridores de Locations abren cada valor en memoria con la llave de datos
   personales de la API: el nuevo `IRecipientPhoneOpener` (propósito `locations.phone`, normalizado a E.164 `+52`) y el
   `IRecipientEmailOpener` de NTF-006. Nunca se usa la llave de avisos.
3. Cada contacto usable se convierte en un hash con cada versión configurada de la llave de bajas. Un contacto que no se
   puede abrir o usar deja fuera su canal; si no queda ninguno, 409 `RECIPIENT_CONTACT_UNAVAILABLE` sin registrar ni
   auditar nada; sin la llave de datos personales o la de bajas, 503.
4. Una transacción registra las bajas con origen `DISPATCHER` mediante `security.record_recipient_opt_out` (globales: todas
   las empresas; un contacto ya dado de baja no se registra de nuevo), la auditoría `RECIPIENT_NOTICES_STOPPED` de la orden
   (entidad `Order`, la clave como `request_id`, `{"channels": [...]}` como carga) y la respuesta guardada 24 horas. Una
   falla en cualquier paso deshace todo.
5. La respuesta nombra los canales cuyo contacto sirvió, aunque ya estuvieran dados de baja, para no revelar bajas hechas
   en otras empresas (T6, punto 6); `recorded_at` es la hora del servidor.

No cancela nada por sí misma: el Worker termina `CANCELLED` con `RECIPIENT_OPTED_OUT` cada aviso pendiente de ese contacto
cuando le toca salir (AI-24 `opt_out.check`). No le manda nada al destinatario y no se puede deshacer durante el piloto
(E4-P16). Otra pulsación (clave nueva) se audita de nuevo y no cambia nada para el destinatario (T3).

## Web: `/ops/orders/:id`

- `operations-recipient-notices.tsx` se monta solo cuando la organización seleccionada es la dueña de la orden y el rol es
  DISPATCHER o PLATFORM_ADMIN (`canManageRecipientNotices`); un 403 `MFA_REQUIRED` ofrece "Verificar identidad".
- Todos los textos salen de `operations/contracts/recipient-notices.ts` (T5); su prueba fija cada cadena y la compara con
  AI-07 `texts`. Siempre se ven la línea de T4 ("Los avisos al destinatario todavía no están activos."), la de T2 ("Los
  avisos solo salen de 8:00 a 21:00, hora de Mazatlán") y el botón (T6, punto 13).
- El diálogo es el de T1 (`ConfirmDialog`, foco en "Cancelar"). Una pulsación usa una `Idempotency-Key`; un reintento tras
  una falla de red o un 503 reusa la misma; cualquier respuesta definitiva la descarta. La confirmación vive solo en memoria
  y el botón sigue disponible (T3).
- Tiempo real (T6, punto 9): solo el detalle se suscribe a `NotificationStatusChanged` (opcional en
  `createOperationsDashboardRealtime`; el tablero y la bandeja no lo hacen). Un aviso v2 de esta orden pide recargar solo
  el panel con la espera de 250 ms y el espaciado de 3 s de la pantalla (`ScreenReloads`, clase "notices"); el panel también
  se lee con cada lectura de la orden y después de detener, y se pausa con toda la pantalla durante la espera de un 429 del
  tablero.

## Pruebas

- Unitarias: `tests/Paqueteria.UnitTests/Notifications/RecipientNoticePanelTests.cs` (conjuntos cerrados, mapeo de
  códigos, decisiones de la baja, abridores del teléfono, redacción).
- Contrato: `RecipientNoticePanelImplementationContractTests`, `Normative/RecipientNoticePanelNormativeTests`,
  `CapabilityMatrixContractTests` (sección `recipient_notice_operations`) y
  `PostgreSql/RecipientNoticePanelPostgreSqlContractTests` (`Category=PostgreSqlContract`, PostgreSQL 18 / PostGIS 3.6).
- Integración: `Notifications/RecipientNoticePanelHttpTests.cs` (matriz de autorización, 404 uniforme, 409 con código,
  503, `no-store`, repetición y captura de logs sin teléfono, correo ni hash).
- Arquitectura: `CrossSchemaWriteCatalogTests` (8 entradas y 6 flujos; el panel solo escribe `platform` y llama la función
  de bajas) y el catálogo de referencias de `Notifications.Endpoints`.
- Vitest: `recipient-notices.test.ts`, `recipient-notices-api.test.ts`, `recipient-notices-controller.test.ts`,
  `operations-recipient-notices.test.ts`, `order-detail-reloads.test.ts`, `capabilities.test.ts` y `realtime/contracts.test.ts`.

## Elecciones de implementación (no escritas en los contratos ni en T1 a T6)

- El código del 409 sin contacto usable es `RECIPIENT_CONTACT_UNAVAILABLE`.
- Un cuerpo o una consulta en la baja es 409 `INVALID_REQUEST` (precedente: `requestRecipientCall` de VOICE-001).
- La identidad de la solicitud une la organización, la persona y la orden (alcance `NTF-007:STOP_RECIPIENT_NOTICES`, 24
  horas).
- "Listo. Se detuvieron los avisos por correo." cuando solo el correo sirvió (la vista previa de T5 nombra "WhatsApp y
  correo" y "WhatsApp").
- Las etiquetas de estado sin las explicaciones entre paréntesis de la vista previa; "Reintentando" como la línea bajo su
  fila; la línea de T2 tal como se citó, sin punto final.
- La API registra `NotificationsDbContext` en su pool `paqueteria_app` para que la auditoría pase por el escritor general.

## Riesgos residuales

- Una baja por error es global y no se puede deshacer durante el piloto (E4-P16); el diálogo es la única guarda.
- Un aviso cuyo envío ya estaba tomado al detener puede salir todavía (la baja se revisa antes de llamar al proveedor).
- Los avisos pendientes siguen diciendo "Pendiente" hasta su hora de envío (posiblemente las 8:00 siguientes).
- En desarrollo y `DEV_SYNTHETIC` una baja cae sobre los marcadores comunes y silencia los avisos de prueba de ese
  ambiente hasta reiniciar su base (T6, punto 11).
- La casilla y el correo pueden decir "permitido" y "registrado" sin que exista ningún aviso: sin la copia para avisos,
  que la API guarda solo desde el cambio que cierre GATE-004 y GATE-007 (NTF-008 conecta la llave pero la deja apagada,
  decisión W4), el Worker no envía.
- Cada baja cuesta hasta dos aperturas en Key Vault y no tiene límite de SEC-004.

## Reversión

Revertir el PR quita las dos operaciones, el panel y los textos; no hay migración. Las bajas ya registradas se quedan, como
decidió E4-P16.
