# Changelog

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
