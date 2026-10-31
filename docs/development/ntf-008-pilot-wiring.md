# NTF-008: conexión de los avisos al destinatario en el piloto

Decisiones: `NTF-RECIPIENT-NOTICE-PILOT-WIRING-2026-10-31` (ronda V: V1; ronda W: W1 a W11, citadas en
`docs/normative/v0.6/decision-log.md`) y `NTF-RECIPIENT-NOTICES-2026-10-20` (E4-P2, E4-P7 y E4-P12); aplica además
`NTF-RECIPIENT-NOTICE-RECEIPTS-2026-10-25` (O8 y O11 elección 11). Serie: [`ntf-recipient-notices.md`](ntf-recipient-notices.md),
PR-7. Contrato: AI-08 NTF-008; AI-24 `recipient_notices.pilot_wiring`, `switches.pilot` y `contact.notice_key`. Guía del
owner: [`docs/operations/env-001-pilot/guia-owner-avisos.md`](../operations/env-001-pilot/guia-owner-avisos.md).

**Estado: PR en borrador, todo apagado.** Por V1 y W3 el PR no se fusiona hasta que el owner confirme que el primer
despliegue del piloto salió bien y que existen todos los recursos de Azure, Meta y DNS de la guía: un secreto mapeado
que no existe detiene la API o el Worker al arrancar. Sin migración, sin código de producción nuevo (solo dos
comentarios corregidos por W4), sin dependencias nuevas. GATE-004 y GATE-007 siguen abiertas.

## Qué cambia

| Archivo | Cambio |
| --- | --- |
| `deploy/azure/pilot/security.bicep` | La llave RSA-3072 `recipient-notice-kek` (solo `wrapKey`/`unwrapKey`, como `pii-kek`; W11 punto 3); *Key Vault Reader* para la API y el rol propio `Paquetenvia Pilot Recipient Notice Unwrap` para el Worker, cada uno solo sobre esa llave (W5); la salida `recipientNoticeKeyUri` |
| `deploy/azure/pilot/apps.bicep` | Los mapeos de Key Vault de AI-24 `pilot_wiring` con los nombres de W11 punto 1 (API: la llave de bajas y los secretos de los webhooks con sus ranuras anteriores, el `phone_number_id` y el id del recurso de ACS; Worker: la llave del enlace, la llave de bajas, el token y el `phone_number_id` de WhatsApp) y su *Key Vault Secrets User* por secreto; `ContactLookup__CurrentKeyVersion=1` y `PiiProtection__RecipientNoticeKey__KeyId` en los dos; `PublicTracking__PublicBaseUrl` y `CurrentLinkKeyVersion` en el Worker; el rol propio `Paquetenvia Pilot Email Sender` del Worker sobre el recurso de ACS (`acsName`, de `PILOT_ACS_NAME`; W6). Ningún `Messaging__*`, `Notifications__RecipientNotices__*` ni `Locations__NoticeKeyProtector` |
| `deploy/azure/pilot/events.bicep` (nuevo) | El tema de sistema global del recurso de ACS y la suscripción de los reportes de entrega, solo con `emailEventsPhase=subscribe`; el repositorio guarda `none` (O8; W11 puntos 5 y 6) |
| `.github/workflows/deploy-azure-pilot.yml` | `PILOT_ACS_NAME` validada antes de Azure y comprobada tras el login; los dos roles propios buscados por nombre exacto y revisados acción por acción (`custom-role-check`) antes de la etapa 1; la ventana 1 genera una vez los siete secretos que nadie escribe (W7); la ventana 2 se detiene si falta un valor de Meta (W11 punto 8); la etapa 4 recibe la llave, el rol y el recurso; la etapa 6 lee la fase de la plantilla compilada después de la prueba de humo y solo con `subscribe` lee el secreto (ventana 3) y despliega; el resumen imprime las dos URL de webhook |
| `tools/azr-001/env001_pilot_guards.py` | `Notifications__RecipientNotices__`, `Messaging__` y `PublicTracking__` administrados por la plataforma; `ContactLookup__Keys__*` sensible; P13 exige la llave; P14 admite *Key Vault Reader* y los roles propios solo en su plantilla; P16 exige la llave de avisos en los dos anfitriones; P23 permite compartir los tres secretos que no son credencial y nunca el token de WhatsApp en la API ni un secreto de webhook en el Worker; P28, P29 y P30 nuevos; el comando `custom-role-check` |
| `tests/Paqueteria.IntegrationTests/Hosting/` | `PilotAppsTemplate` (lector compartido de `apiEnv` y `workerEnv`), dos pruebas nuevas de la API y `PilotWorkerStartupConfigurationTests` (el Worker arranca en `Production` con exactamente la configuración del piloto; W11 punto 12) |
| Documentación | La guía del owner en español (W11 punto 11), el README del piloto (§1, §4, §8, §9), `bootstrap.md`, la guía de la sección A, las notas de NTF-002, NTF-005, ADP-001 y el borrador del aviso de privacidad (W4) |

## Guardas nuevas

- **P28** (W11 punto 7): ningún interruptor (`Notifications__RecipientNotices__Enabled`,
  `Messaging__WhatsApp__Webhook__Enabled`, `Messaging__Email__Events__Enabled`) distinto de `false`, ningún proveedor
  distinto de `Disabled`, `Locations__NoticeKeyProtector` solo `Disabled` (W4) y `emailEventsPhase` solo `none`, salvo
  que `Notifications__RecipientNotices__Gate004DecisionId` y `Gate007DecisionId` del Worker nombren filas del decision log
  con Type exactamente `Gate resolution`. Los nombres se comparan sin distinguir mayúsculas, y una expresión cuenta como
  encendida.
- **P29** (W5, W6, W7, W11 puntos 1 a 4 y 8): quién tiene qué sobre `recipient-notice-kek`, `pii-kek` y el recurso de
  ACS; los dos roles propios con su nombre exacto en el workflow, revisados antes de la etapa 1 y pasados a las etapas 1
  y 4; `PILOT_ACS_NAME` leída una vez, validada antes del login y comprobada después; los siete secretos generados y los
  tres de Meta exigidos.
- **P30** (O8, W11 puntos 5 y 6): la suscripción fijada (URL, un evento por lote, solo `EmailDeliveryReportReceived`,
  esquema de Event Grid, encabezado secreto estático desde un parámetro `@secure()`), solo con `subscribe`; el workflow
  nunca pasa la fase; `subscribe` exige `custom_domain_phase=bind` y `Messaging__Email__Events__Enabled=true`.

## Elecciones al aplicar las respuestas (no preguntadas)

- Los nombres de los roles propios (`Paquetenvia Pilot Recipient Notice Unwrap`, `Paquetenvia Pilot Email Sender`) y de
  las variables del workflow (`RECIPIENT_NOTICE_UNWRAP_ROLE`, `EMAIL_SENDER_ROLE`), siguiendo el rol de etiquetas de blob.
- El workflow revisa cada rol propio acción por acción antes de desplegar (`custom-role-check`), además de buscarlo por
  nombre: un rol con una acción de más detiene el run.
- Los nombres del tema de sistema (`egst-pv-pilot-acs`) y de la suscripción (`egsub-pv-pilot-email-events`); la URL del
  webhook escrita literal en `events.bicep` en lugar de construirla con `publicHost`, para que la guarda la fije entera;
  `preferredBatchSizeInKilobytes` 64 (el límite de cuerpo de la API).
- Los valores generados: la llave de bajas con 32 bytes en base64 (como la de correos de inicio de sesión); el token de
  verificación, el secreto de Event Grid y las tres ranuras anteriores con 32 bytes en hexadecimal (64 caracteres, sin
  espacios, dentro de las reglas de inicio de NTF-006); el id del recurso de ACS tal como lo devuelve
  `az resource show`, guardado una sola vez (W7 dice "una sola vez si faltan").
- La guía pide al owner la caducidad "Nunca" y solo el permiso `whatsapp_business_messaging` para el token (W11 punto 9);
  si Meta no ofrece "Nunca", el owner se detiene y avisa.
- La guía pide el aumento de límites de envío de ACS: Microsoft solo permite remitentes propios (MailFrom) en dominios con
  límites mayores que los de omisión.
- Las pruebas de arranque comparten un lector de `apps.bicep` (`PilotAppsTemplate`) en lugar de duplicarlo.

## Riesgos residuales

- **Bloqueo de despliegues si se fusiona antes** de que existan los recursos (por eso queda en borrador, V1).
- **El rol de correo** es el mínimo que documenta Microsoft para mandar correo con un rol propio (página de SMTP); incluye
  `CommunicationServices/Write`, que también permite cambiar la configuración del recurso. Nunca incluye `ListKeys` ni
  `RegenerateKey`. Que baste para la API REST de correo con identidad administrada se comprueba con el primer envío real.
- **Key Vault Reader** incluye además acciones de control (`Microsoft.Support/*`, alertas clásicas y lectura de
  asignaciones) sobre el ámbito de la llave; ninguna lee un secreto ni la parte privada de la llave.
- **Remitente `avisos@`**: depende de que Microsoft apruebe el aumento de límites (revisión manual, hasta 72 horas, mira
  la reputación del dominio). Si lo rechaza, hace falta una decisión nueva sobre E4-P12. Microsoft recomienda un registro
  MX para la reputación, y E4-P12 no quiere buzón de respuestas: sin MX algunos proveedores pueden marcar más correos
  como spam.
- **What-if previo**: los siete secretos que genera el workflow no existen hasta el primer despliegue con NTF-008, así
  que el what-if de `apps.bicep` puede mostrar sus asignaciones como nuevas o con aviso.
- **Ranuras anteriores**: una rotación a medias deja aceptado un valor viejo hasta que la ranura se vuelve a llenar con
  un valor aleatorio.
- **El id del recurso de ACS** se guarda una vez: cambiar `PILOT_ACS_NAME` después no lo actualiza.
- **«Detener avisos»** deja de responder 503 en el piloto con el primer despliegue con NTF-008: registra bajas reales,
  globales y definitivas aunque ningún aviso salga todavía (T4, E4-P16).
- **El Worker gana la llave del enlace** (puede derivar el enlace de cualquier orden, con el mismo alcance que su lectura
  de la base) y el permiso de abrir copias de avisos, que no existen hasta el cambio de compuertas (W4).

## Reversión

Revertir el PR quita de las plantillas los mapeos, los roles y la plantilla de eventos, y el siguiente despliegue crea
la API y el Worker sin ellos. El despliegue es incremental: la llave, las asignaciones de roles y los secretos ya creados
se quedan en Azure hasta que el owner los quite (la bóveda tiene purge protection; nunca se borran secretos), y nadie los
lee. Ningún dato ni migración cambia.
