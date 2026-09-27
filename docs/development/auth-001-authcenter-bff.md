# AUTH-001: integración AuthCenter con patrón BFF

Estado: implementado detrás de `Authentication:Provider=AuthCenter`. Está bloqueado para fusionarse
hasta que la dependencia `Microsoft.AspNetCore.Authentication.OpenIdConnect` llegue a `development`
por back-sync desde `main` (ver "Dependencias").

Base normativa: GATE-002 (AuthCenter autentica, Paquetenvia autoriza), AI-03 §17 (OIDC, MFA para
roles privilegiados, CORS cerrado, secretos en secret manager, sin tokens en logs), AI-03 §24.2 (un
único contexto activo por petición) y AI-07 `/login`.

## 1. Decisiones del owner (26-sep-2026)

1. Se usa BFF en la API .NET con cookie HttpOnly. La web consume la API por el **mismo origen**:
   En el piloto de Azure el **ingress** enruta `/api`, `/hubs`, `/auth` y `/signin-authcenter` a la
   API; las rewrites de Next quedan solo para desarrollo local.
2. Las dependencias nuevas entran por `main`. El PR Gate rechaza `DEPS` hacia `development`, y solo
   un back-sync `MAIN_BACKSYNC` certificado las trae a `development`.
3. La autorización de negocio sigue en Paquetenvia (`organizations.organization_memberships` + RLS).
   AuthCenter solo autentica, y el `sub` validado alimenta `identity.users.identity_subject`.
4. Las sesiones BFF viven en la tabla PostgreSQL `identity.bff_sessions`
   (`BFF-SESSION-TABLE-SHAPE`, implementada en `feature/bff-session-table`, §4).
5. Primer ingreso por invitación previa; se implementa en un PR aparte (§8).

`AuthCenter.Client` no está publicado en NuGet. Por eso se replica su contrato BFF con el handler
estándar de Microsoft (`Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.10, la misma versión
del SDK) y el esquema Cookies del framework.

## 2. Esquemas y selección de proveedor

| `Authentication:Provider` | Entornos | Credencial del navegador | Esquema efectivo |
|---|---|---|---|
| `Disabled` | todos | ninguna | `Paquetenvia.Disabled` (siempre 401) |
| `Mock` | Development, Testing, DevSynthetic autorizado | `Authorization: Bearer <perfil>` | `Paquetenvia.MockOidc` |
| `AuthCenter` | todos | cookie `__Host-Paquetenvia.Session` | `Paquetenvia.AuthCenter.Cookie` (+ `Paquetenvia.AuthCenter.Oidc` para el reto) |

El esquema por defecto sigue siendo el policy scheme `Paquetenvia.Authentication`, que reenvía al
esquema del proveedor. Los esquemas cookie/OIDC solo se agregan a `AuthenticationOptions` cuando el
proveedor en runtime es `AuthCenter`. Así, los demás hosts nunca instancian el handler OIDC ni
necesitan su configuración. En modo AuthCenter se **ignora** cualquier `Authorization: Bearer`: no
existe una segunda vía de entrada.

## 3. Flujo

```text
Navegador ──GET /auth/login?return_url=/ops/dashboard──▶ API (mismo origen)
  API: valida return_url local ─▶ Challenge OIDC
  302 ─▶ AuthCenter /oauth/authorize?response_type=code&client_id=…
           &redirect_uri=<PublicOrigin>/signin-authcenter   (fijo, no deriva de Host)
           &scope=openid profile email offline_access
           &state=…&nonce=…&code_challenge=…&code_challenge_method=S256&response_mode=query
AuthCenter (login + consentimiento) ─302─▶ <PublicOrigin>/signin-authcenter?code&state&iss
  API: valida correlación (state) y cookie de nonce; valida iss de la respuesta (RFC 9207)
       canal trasero POST /oauth/token (client_secret_post + code_verifier, redirect_uri idéntico)
       valida ID token: RS256 solamente, firma JWKS, iss exacto (ordinal), aud = ClientId,
       exp/nbf (skew 30 s), nonce
       TicketReceived: principal mínimo {sub, mfa(amr contiene "mfa"), name, email};
       descarta roles/permissions/applications; conserva solo refresh_token; genera CSRF
  Set-Cookie __Host-Paquetenvia.Session (clave opaca); 302 ─▶ return_url
Navegador ──GET /auth/session (cookie)──▶ {authenticated, authorized, mfa, csrfToken, sessionNamespace, user{name,email}}
Navegador ──GET /api/v1/me/organization-contexts (cookie)──▶ membresías → instala sesión en memoria
Navegador ──POST /api/... (cookie + X-AuthCenter-CSRF + X-Organization-Id)──▶ API
Navegador ──POST /auth/logout (cookie + X-AuthCenter-CSRF)──▶ API revoca refresh (POST /oauth/revoke), destruye la sesión
  y responde 200 {endSessionUrl} ─▶ window.location.assign(endSessionUrl) ─▶ AuthCenter /oauth/logout ─302─▶ /login
AuthCenter ──POST /auth/backchannel-logout (logout_token, servidor a servidor)──▶ API marca el sid terminado
```

Si falla el callback (state, nonce, firma, issuer, audience, expiración, PKCE, error del IdP), la API
redirige a `/login?error=signin_failed` sin sesión y sin detalles. La única excepción es
`error=access_denied` con state, correlación e `iss` válidos, que va a `/login?error=access_denied`
(§14.4). El log solo registra el tipo de
excepción (EventId 4101), nunca códigos, tokens, verifiers, `sub` ni `error_description`.

### Resolución de `identity_subject`

En **cada petición** `CookieAuthenticationEvents.ValidatePrincipal` toma el `sub` guardado y llama a
`IIdentityContextResolver.ResolveAsync(sub)` (`security.resolve_identity_context` en PostgreSQL).
Con el resultado arma el principal interno con `IdentityClaimsPrincipalFactory`, el mismo que usa el
proveedor Mock. Por eso:

- las políticas, `IAuthenticatedSession`, `TenantContextMiddleware` y la selección por
  `X-Organization-Id` funcionan igual que con Mock;
- una suspensión o un cambio de membresía en Paquetenvia aplica en la siguiente petición, sin
  esperar a que venza la cookie;
- un `sub` desconocido queda **autenticado sin contexto**: `/auth/session` responde
  `authorized=false` y las políticas activas responden 403 genérico;
- un fallo técnico de PostgreSQL responde 503 genérico, como en SEC-002.

MFA: `ExternalIdentity.MfaSatisfied` sale únicamente del claim `amr` del ID token validado
(contiene `mfa`). AuthCenter ya emite `amr` (por ejemplo `["pwd","otp","mfa"]` o `["pop","mfa"]`
con passkey) y `acr` (`urn:authcenter:acr:1fa`, `urn:authcenter:acr:mfa`,
`urn:authcenter:acr:phr`); la decisión `AUTHCENTER-AMR-MFA` está cumplida del lado de AuthCenter
(AuthCenter#36). AuthCenter solo exige el segundo factor si la aplicación tiene `RequireMfa`, si
el usuario ya tiene uno activo, si una política de acceso lo pide o si el cliente envía
`acr_values`. El owner eligió **step-up** (`acr_values`) en lugar de `RequireMfa`; está
implementado en §14.3: un 403 de `PrivilegedMfa` sin `mfa` en `amr` lleva el código
`MFA_REQUIRED` y la web ofrece "Verificar identidad".

## 4. Sesión del lado servidor

- La cookie `__Host-Paquetenvia.Session` es `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, sin
  `Domain` y de sesión (no persistente). Solo contiene una clave aleatoria de 256 bits protegida
  con Data Protection.
- El ticket (identidad mínima con `sub` y `sid`, secreto CSRF, momento de inicio en milisegundos,
  refresh token e ID token) se protege con el key ring de la plataforma
  (`DataProtection:Provider=PostgreSql` para varias réplicas) y se guarda en
  `identity.bff_sessions`. El ID token solo se usa como `id_token_hint` al cerrar sesión (§14.1);
  el access token no se guarda.
- Tabla (`AuthCenter:SessionStore=PostgreSql`, valor por defecto; exige
  `ConnectionStrings:Paqueteria`): clave primaria = SHA-256 de los bytes UTF-8 de la clave opaca
  (nunca la clave ni la cookie), `identity_subject`, `authcenter_sid`, `ticket_ciphertext`,
  `created_at` (reloj de la BD), `expires_at` (el del ticket, máximo 24 h) y `revoked_at`. Es
  previa al tenant: FORCE RLS sin política, sin columnas de organización. `paqueteria_app` no
  tiene grants sobre ella; la API asume `paqueteria_app` y llama las funciones
  `security.create_bff_session`, `security.resolve_bff_session(bytea)` y
  `security.revoke_bff_session` (por clave, por `sid` y por `sub` anterior a un momento), dueñas
  de `paqueteria_session_executor NOLOGIN BYPASSRLS`. Revocar borra el ticket de inmediato; una
  clave desconocida, revocada o vencida resuelve `NULL` y la petición es anónima (401). Un fallo
  técnico de PostgreSQL responde 503, como la resolución de identidad.
- Migraciones: lane de Identity `20260927000400_AddBffSessionStore` (tabla si falta, rol, grants,
  funciones; adopta la tabla de AI-06 solo si es exactamente canónica) y lane de Custody
  `20260927000400_AddBffSessionPurge` (purga por `paqueteria_cleanup_executor`). Ambas fallan
  cerrado en `Down`. El job del Worker `OperationalCleanup:BffSessions` (desactivado por defecto)
  borra en lotes las filas revocadas o vencidas.
- Cada inicio de sesión exitoso reemplaza la sesión previa del navegador: se borra el ticket
  anterior y se emite una clave nueva (§14.3).
- La vida es fija (`AuthCenter:SessionLifetimeMinutes`, 480 por defecto, máximo 1440), sin
  expiración deslizante. No hay endpoint de refresh: el refresh token solo sirve para revocar.
- Las cookies de correlación y nonce del handler OIDC también usan el prefijo `__Host-`, `Secure`,
  `HttpOnly` y `SameSite=Lax`, porque AuthCenter devuelve el código por query en un GET de nivel
  superior.
- Las sesiones sobreviven reinicios y sirven en cualquier réplica que comparta la BD y el key
  ring. `AuthCenter:SessionStore=Memory` conserva el almacén anterior (`AuthCenterTicketStore` →
  `IDistributedCache`, una sola réplica) como interruptor de rollback; endpoints y web no cambian.
- `RenewAsync` nunca ocurre (vida fija, `ShouldRenew=false` y clave nueva en cada inicio de
  sesión); si ocurriera, el almacén PostgreSQL revoca la clave y registra EventId 4105.

## 5. CSRF y mismo origen

- `GET /auth/session` entrega `csrfToken`: 32 bytes aleatorios generados al iniciar sesión y
  guardados en el ticket del servidor. La web lo conserva solo en memoria.
- Toda petición autenticada por cookie con método distinto de GET/HEAD/OPTIONS debe traer
  `X-AuthCenter-CSRF` idéntico (comparación en tiempo constante). Si falta o no coincide, la
  petición se trata como anónima: 401 en endpoints protegidos. Aplica a `/api/**`, a
  `POST /auth/logout` y a las negociaciones SignalR.
- Si la petición trae `Origin`, debe ser exactamente `AuthCenter:PublicOrigin`. Los navegadores lo
  envían en toda petición cross-origin, en los handshakes WebSocket y en los POST same-origin. Un
  valor distinto (incluido `null` o el mismo host en `http`) invalida la cookie para esa petición.
- `SameSite=Lax` impide que la cookie viaje en subpeticiones cross-site, incluido un handshake
  WebSocket desde otro sitio.
- Como la web es same-origin, CORS no se abre para el navegador. La política `Realtime` existente
  no cambia.

## 6. SignalR con cookie

- El cliente web crea el `HubConnection` con `withCredentials: true` y
  `headers: { "X-AuthCenter-CSRF": … }`. En modo cookie no usa `accessTokenFactory`.
- `POST /hubs/*/negotiate` y los POST de long-polling/SSE llevan la cabecera CSRF. El upgrade
  WebSocket es un GET sin cabeceras personalizadas: lo protegen la comprobación de `Origin` y
  `SameSite=Lax`.
- `RealtimeConnectionGateMiddleware` y la selección `organization_id` no cambian: leen
  `IAuthenticatedSession`, que se resuelve igual desde la cookie.
- Una conexión ya establecida no se re-autentica por mensaje. La suspensión aplica en el siguiente
  negotiate/reconexión, igual que antes con bearer.

## 7. Mismo origen en la web

- `NEXT_PUBLIC_AUTH_MODE=bff` activa el modo BFF en la web. En ese modo
  `NEXT_PUBLIC_API_BASE_URL` debe estar **vacía**; `next.config.ts` falla si no lo está. Los
  clientes usan `window.location.origin`.
- `PAQUETENVIA_API_PROXY_ORIGIN` (solo servidor, sin prefijo `NEXT_PUBLIC_`) activa las rewrites
  `/api/:path*`, `/hubs/:path*`, `/auth/:path*` y `/signin-authcenter` hacia la API. Next resuelve
  las rewrites al evaluar la configuración en `next dev`/`next build` (quedan en el routes
  manifest). Sin la variable no se agregan rewrites (caso del ingress, que es el del piloto).
- La lógica de rewrites vive **dentro** de `next.config.ts`, sin imports relativos: la imagen de
  runtime (`deploy/azure/Dockerfile.web`) solo copia `.next`, `node_modules`, `public`,
  `package.json` y `next.config.ts`, y `next start` evalúa la configuración. Una prueba
  (`src/lib/api-proxy.test.ts`) verifica que el único import sea `next`.
- `deploy/azure/Dockerfile.web` declara `NEXT_PUBLIC_AUTH_MODE` como `ARG`/`ENV` de build
  (guarda AZR-001 G06). Sin valor, la web queda en el modo previo.
- Verificado localmente con `next start` (16.3.3) contra un upstream de prueba: HTTP reenvía la
  cookie y agrega `x-forwarded-host`; el upgrade WebSocket de `/hubs/*` se proxifica
  correctamente. Si un proxy intermedio no soporta upgrade, SignalR cae a SSE o long-polling, que
  también funcionan por las rewrites.
- **Azure (decisión del owner para el piloto):** el ingress enruta esos cuatro prefijos
  directamente a la API y el resto a Next (sin salto extra y con WebSocket nativo).
  `PAQUETENVIA_API_PROXY_ORIGIN` queda vacía.
- La API **no** deriva el `redirect_uri` de `Host`/`X-Forwarded-*`: usa
  `AuthCenter:PublicOrigin`. Esto evita depender de los forwarded headers que configura otro PR.
- `/login`, `/auth/*` y `/signin-authcenter` son network-only en el Service Worker.

### Web

- `src/auth/request-credentials.ts` es la estrategia única de credenciales: `bearer` (Mock) o
  `cookie` (BFF, `credentials: "include"` y CSRF en escrituras). La usan operaciones, rutas, paradas
  y ofertas del repartidor, la sincronización offline y SignalR.
- `/login` tiene un botón que navega a `/auth/login?return_url=…` (solo rutas locales), muestra el
  estado con `GET /auth/session` y cierra sesión con `POST /auth/logout`.
- `BffSessionBootstrap`, en el layout, reconstruye en cada carga completa la sesión en memoria
  (`window.__paquetenvia*Session`, `credentialMode: "cookie"`). Toma
  `GET /api/v1/me/organization-contexts` y elige la membresía por defecto (rol `DRIVER` → PWA del
  repartidor; resto → operaciones). La API sigue validando `X-Organization-Id` contra las
  membresías de la BD.
- `sessionNamespace` es un SHA-256 truncado del `sub`, estable y no reversible. Separa los cachés
  del navegador (cola offline del repartidor) entre personas que comparten dispositivo.
- El portal `/dev` y el modo Mock no cambian.

## 8. Primer ingreso (TEN-003)

La opción segura por defecto es la que queda implementada:

- el login **nunca** crea usuarios, organizaciones ni membresías;
- un `sub` sin `identity.users` queda autenticado sin autorización (`authorized=false`, 403);
- el aprovisionador inicial de TEN-003 sigue sin invocarse, y su autorizador por defecto
  (`DenyInitialOrganizationProvisioningAuthorizer`) sigue negando todo.

Vinculación posible hoy: un administrador preaprovisiona `identity.users.identity_subject = <sub>`
con el `sub` que muestra AuthCenter (UUID del usuario) y sus membresías. El esquema AI-06 exige
`identity_subject NOT NULL UNIQUE` y no tiene tabla de invitaciones, así que la vinculación por
invitación o por correo verificado requiere un cambio normativo. **Decisión del owner:** el primer
ingreso será por invitación previa y se implementa en un PR aparte.

## 9. Configuración

| Clave | Origen | Ejemplo |
|---|---|---|
| `Authentication__Provider` | App Settings | `AuthCenter` |
| `IdentityBootstrap__Provider` | App Settings | `PostgreSql` |
| `AuthCenter__Authority` | App Settings | `https://<host-authcenter>` (HTTPS, sin path/query) |
| `AuthCenter__Issuer` | App Settings | valor exacto de `Jwt:Issuer` de AuthCenter (se compara ordinalmente) |
| `AuthCenter__ClientId` | App Settings | `paquetenvia-web-<ambiente>` |
| `AuthCenter__ClientSecret` | **Key Vault reference** | `@Microsoft.KeyVault(SecretUri=https://<kv>.vault.azure.net/secrets/authcenter-paquetenvia-client-secret)` |
| `AuthCenter__PublicOrigin` | App Settings | `https://<host-web>` (sin path) |
| `AuthCenter__SessionLifetimeMinutes` | App Settings (opcional) | `480` |
| `NEXT_PUBLIC_AUTH_MODE` (web, build) | pipeline | `bff` |
| `PAQUETENVIA_API_PROXY_ORIGIN` (web, solo local) | entorno de desarrollo | `http://localhost:8080`; vacío en Azure (enruta el ingress) |

El secreto **nunca** va en `appsettings*.json`, en GitHub, en variables de pipeline, en logs ni en
tickets. En local se usa `dotnet user-secrets`. La API no arranca (`ValidateOnStart`) si falta alguno
de estos valores, si `Authority` no es HTTPS, si el secreto tiene menos de 32 caracteres o si
`PublicOrigin` tiene path. En `Development`/`Testing` también se acepta `http://localhost` como
`PublicOrigin`.

## 10. Registro que debe hacer el owner en AuthCenter

1. Crear la `ApplicationSystem` **Paquetenvia**, única para todas las organizaciones (GATE-002).
   No se crean roles ni permisos para autorización de tenant, porque se ignoran.
2. Crear un cliente OAuth **confidencial** por ambiente, ligado a esa aplicación:
   - grants: `authorization_code`, `refresh_token`;
   - PKCE S256 obligatorio;
   - scopes exactos: `openid profile email offline_access`;
   - redirect URI exacta: `https://<host-web-del-ambiente>/signin-authcenter`;
   - autenticación en el token endpoint: `client_secret_post` (el handler de Microsoft envía
     `client_id`/`client_secret` en el cuerpo). La revocación usa `client_secret_basic`. Ambos
     métodos aparecen en discovery.
3. Entregar el secreto una sola vez, escribiéndolo directamente en el Key Vault del ambiente
   (`authcenter-paquetenvia-client-secret`). La API lee el valor con su identidad administrada
   (`Key Vault Secrets User` solo sobre ese secreto).
4. Issuer: copiar **exactamente** el valor `issuer` del discovery de producción
   (`/.well-known/openid-configuration`) a `AuthCenter__Issuer`; se compara de forma ordinal.
   Confirmar también la URL pública (`Oidc:PublicOrigin`) por ambiente.
5. MFA: AuthCenter ya emite `amr` y `acr` en el ID token (`AUTHCENTER-AMR-MFA` cumplida,
   AuthCenter#36). **No** activar `RequireMfa` en la aplicación: el owner eligió step-up con
   `acr_values` para los roles `PrivilegedMfa` (§14.3).
6. Logout: AuthCenter ya publica `end_session_endpoint` (`/oauth/logout`) y back-channel logout
   (`backchannel_logout_supported` y `backchannel_logout_session_supported` en discovery; el
   `sid` del ID token coincide con el del `logout_token`). El owner aprobó RP-initiated logout y
   back-channel logout; están implementados en §14.1 y §14.2. Requieren registrar la
   post-logout redirect URI y la back-channel logout URI del punto 7.
7. Datos adicionales de registro:
   - `LoginUrl` = `https://<host-authcenter>/login` (obligatorio, login hospedado).
   - `AutoConsent` habilitado (aplicación first-party; si no, el primer login muestra
     consentimiento).
   - Post-logout redirect URI exacta: `https://<host-web>/login`.
   - Back-channel logout URI: `https://<host-web>/auth/backchannel-logout` (HTTPS).
   - En la aplicación: métodos de login (contraseña, magic link, Google, Microsoft); `RequireMfa`
     **desactivado** (step-up); modo de registro `InviteOnly`, alineado con
     `AUTH-FIRST-LOGIN-INVITATION`; solicitudes de acceso opcionales.
   - Acceso de cada usuario a Paquetenvia: invitación, asignación directa, regla de grupo, SCIM o
     solicitud aprobada. Sin acceso activo, el login hospedado indica que no hay acceso y no
     regresa; con sesión SSO existente, el callback recibe `error=access_denied`.

### 10.1 Valores por ambiente (decisión del owner, 27-sep-2026)

Dominio de Paquetenvia: `paquetenvia.com`. AuthCenter: `https://authcenter.info`. Dos ambientes,
cada uno con su propio cliente confidencial y su propio secreto; no hay ambiente staging.

| Dato | Dev (piloto en Azure) | Producción |
|---|---|---|
| Host web (`AuthCenter__PublicOrigin`) | `https://dev.paquetenvia.com` | `https://paquetenvia.com` |
| `AuthCenter__ClientId` | `paquetenvia-web-dev` | `paquetenvia-web-prod` |
| Redirect URI | `https://dev.paquetenvia.com/signin-authcenter` | `https://paquetenvia.com/signin-authcenter` |
| Post-logout redirect URI | `https://dev.paquetenvia.com/login` | `https://paquetenvia.com/login` |
| Back-channel logout URI | `https://dev.paquetenvia.com/auth/backchannel-logout` | `https://paquetenvia.com/auth/backchannel-logout` |
| `AuthCenter__Authority` | `https://authcenter.info` | `https://authcenter.info` |
| `LoginUrl` (en AuthCenter) | `https://authcenter.info/login` | `https://authcenter.info/login` |
| `AuthCenter__Issuer` | `https://authcenter.info` | `https://authcenter.info` |

- Las URIs se registran **exactas**, sin `/` final: AuthCenter las compara de forma ordinal.
- `AuthCenter__Issuer`: el owner lo confirmó el 27-sep-2026 con el discovery de producción
  (`https://authcenter.info/.well-known/openid-configuration`): `https://authcenter.info`, sin `/`
  final. Ese discovery también publica lo que requiere este diseño:
  - endpoints `/oauth/authorize`, `/oauth/token`, `/oauth/revoke` y `/oauth/logout`
    (`end_session_endpoint`);
  - `code_challenge_methods_supported: [S256]`;
  - `client_secret_post` y `client_secret_basic`;
  - ID token `RS256`;
  - `authorization_response_iss_parameter_supported: true`;
  - `backchannel_logout_supported` y `backchannel_logout_session_supported`;
  - `acr_values_supported` con `urn:authcenter:acr:mfa` y `urn:authcenter:acr:phr`, que coinciden
    con `AuthCenterDefaults`;
  - el claim `email_verified`.
- El ingress de cada ambiente debe aceptar el `POST` sin `Origin` hacia `/auth/backchannel-logout`
  (§14.2). AuthCenter lo llama servidor a servidor.

## 11. Preguntas abiertas para el owner

1. ~~Primer ingreso~~: resuelto por el owner, por invitación previa en un PR aparte.
2. ~~Sesión multi-instancia~~: resuelto por el owner, tabla PostgreSQL tras el cambio normativo.
3. ~~Hosts web por ambiente y redirect URIs definitivos~~: resuelto por el owner (§10.1):
   `dev.paquetenvia.com` para dev y `paquetenvia.com` para producción, sin staging.

## 12. Pruebas

`tests/Paqueteria.IntegrationTests/Security/AuthCenter` levanta un servidor OIDC falso en proceso:
genera RSA-2048 y JWKS por prueba, sirve discovery, token y revoke, y valida cliente, redirect URI
exacta, scopes, PKCE S256, state y nonce. Cubre:

- redirect al IdP con code+PKCE S256+scopes y `redirect_uri` fijo aunque cambie `Host`;
- cookie `__Host-`, `Secure`, `HttpOnly`, `SameSite=Lax`, sin `Domain`, sin tokens en cookies ni
  en `/auth/session`;
- rechazo de issuer distinto o con `/` final, audience distinta, nonce reusado, token vencido,
  firma HS256, llave RSA ajena, verifier PKCE incorrecto, state alterado, callback sin cookie de
  correlación (login CSRF), error del IdP y `redirect_uri` alterada;
- `return_url` externo, `//host`, `/\host` y `javascript:` → `/`;
- autorización desde la BD y no desde el token (roles de AuthCenter ignorados), MFA solo por
  `amr`, `sub` no aprovisionado → 403, suspensión efectiva en la siguiente petición;
- CSRF obligatorio en escrituras y en negotiate de SignalR, CSRF de otra sesión rechazado,
  `Origin` ajeno rechazado incluso en lecturas, bearer ignorado;
- logout con CSRF que revoca el refresh (Basic auth), borra la cookie y deja inservible una cookie
  robada;
- configuración inválida → la API no arranca.

Logout, back-channel, step-up y `access_denied` (`AuthCenterLogoutAndStepUpTests.cs`, §14):

- logout: URL de end-session con `id_token_hint` y `post_logout_redirect_uri` exactos; cookie y
  ticket destruidos aunque falle el discovery; sin CSRF → 401 y la sesión sigue; `endSessionUrl`
  nulo sin ID token o con endpoint ausente, `http`, de otro host o relativo;
- back-channel: un token válido termina la sesión en la siguiente petición (y no otra sesión SSO
  de la misma persona); token solo con `sub` termina las sesiones anteriores y no las nuevas;
  400 y la sesión sigue ante `jti` repetido, ID token, `aud`/`iss`/`typ`/`alg` distintos, `alg`
  none, `nonce`, sin `events` o con otro evento, vencido, `iat` viejo o futuro, sin `sid` ni
  `sub`, sin `jti`, basura, JSON en vez de form, sin `logout_token` o duplicado;
- step-up: 403 `MFA_REQUIRED` solo cuando falta únicamente MFA (el 403 por rol sigue genérico);
  `mfa=required` envía `acr_values` y `mfa=REQUIRED` no; `acr`/`amr` insuficientes → rechazo y la
  sesión previa sigue; step-up exitoso reemplaza la sesión y la cookie anterior deja de servir;
  `phr` con passkey satisface; `return_url` externo → `/`;
- `access_denied` → `/login?error=access_denied`; `server_error`, `login_required`,
  `interaction_required`, `invalid_request`, `temporarily_unavailable` y `access_denied` con state
  falso → `signin_failed`.

Unitarias (`AuthCenterLogoutAndStepUpUnitTests.cs`): construcción de la URL de end-session,
validación de `acr`/`amr`, detección de "solo falta MFA" y el almacén de terminaciones.

Almacén PostgreSQL (`BFF-SESSION-TABLE-SHAPE`): `BffSessionStorePostgreSqlContractTests` (grants
exactos, sin acceso directo de runtime, resolución por hash, revocación por clave, `sid` y `sub`
anterior a un momento, purga solo de filas muertas, sin RLS tenant, migraciones up/down y
upgrade de una instalación previa) y `AuthCenterPostgreSqlSessionStoreTests` (el login crea la
fila, el logout la revoca, un back-channel en una instancia termina la sesión en otra y la sesión
sobrevive a un reinicio).

Web (`vitest`): estrategia de credenciales, parser de sesión, `return_url` local, logout
(`endSessionUrl`, navegación con `window.location.assign`), enlace de step-up, detección de
`MFA_REQUIRED`, mensajes de `/login`, instalación de sesión y rewrites.

## 13. Rollback

Poner `Authentication:Provider` en `Disabled` o `Mock` (solo entornos no productivos) y quitar
`NEXT_PUBLIC_AUTH_MODE`. Para volver solo del almacén PostgreSQL, `AuthCenter:SessionStore=Memory`
(una réplica); las migraciones de sesiones no se revierten (`Down` falla cerrado) y
`OperationalRollbackSql` revoca el `EXECUTE` de `paqueteria_app`. Para retirar el código, revertir el PR. El cliente en
AuthCenter y el secreto se conservan durante una ventana de solapamiento; si hubo exposición, se
revocan las sesiones y se rota el secreto.

## 14. Cierre de sesión, back-channel, step-up y `access_denied` (decisiones del 27-sep-2026)

Implementa las decisiones `AUTH-001-RP-INITIATED-LOGOUT`, `AUTH-001-BACKCHANNEL-LOGOUT`,
`AUTH-001-MFA-STEP-UP` y `AUTH-001-ACCESS-DENIED-MESSAGE` (registradas en `decision-log.md` y
traducidas a AI-05, AI-03 §17.1, AI-07 y AI-24 `bff_session`). El contrato se verificó contra el
código de AuthCenter (discovery, `/oauth/logout`, `GenerateLogoutToken`, `acr_values`,
`access_denied`) y contra la referencia `AuthCenter.Client` (`AuthCenterBackchannelLogout`).

### 14.1 RP-initiated logout

- `POST /auth/logout` mantiene CSRF y mismo origen, revoca el refresh token, destruye ticket y
  cookie (en un `finally`: también si el discovery falla) y responde
  `200 {"endSessionUrl": string|null}` con `Cache-Control: no-store`.
- `endSessionUrl` = `end_session_endpoint` del discovery (misma regla que la revocación: absoluto,
  HTTPS, sin credenciales ni fragmento, en la autoridad configurada) + `id_token_hint` (el ID token
  guardado en el ticket) + `post_logout_redirect_uri=<PublicOrigin>/login` (registrada exacta en
  AuthCenter). Sin ID token, sin endpoint o con endpoint inválido → `null`.
- La web navega con `window.location.assign(endSessionUrl)`, o a `/login` si es `null`. No sirve
  un 302: el logout es un `fetch` y no puede seguir una redirección cross-origin, y un form POST
  chocaría con `form-action` de la CSP. Si el `id_token_hint` coincide con la sesión del
  navegador, AuthCenter la cierra sin preguntar, notifica back-channel a las demás aplicaciones
  y regresa a `/login`; si no, pide confirmación en su página.
- Riesgo aceptado por el owner: el ID token (vida de 5 min, AuthCenter lo acepta vencido como
  hint) llega al JavaScript de la página dentro de `endSessionUrl`, solo después de destruir la
  sesión local. Paquetenvia no acepta bearer en modo AuthCenter, así que no sirve como credencial
  aquí. Es la única excepción a "sin tokens en el navegador" (AI-03 §17.1, AI-24).

### 14.2 Back-channel logout

- `POST /auth/backchannel-logout`: anónimo, sin cookie, CSRF ni `Origin`; solo
  `application/x-www-form-urlencoded` con un único `logout_token` (cuerpo limitado a 32 KiB,
  token a 16 KiB); `Cache-Control: no-store`. Responde 200 o `400 {"error":"invalid_request"}`
  (AuthCenter reintenta cualquier respuesta no 2xx).
- Validación (`AuthCenterBackchannelLogout`, espejo de `AuthCenter.Client`): firma con las llaves
  del discovery, solo RS256, `typ` exacto `logout+jwt`, `iss` exacto configurado, `aud` =
  ClientId, `exp` obligatorio con 30 s de tolerancia, `iat` presente, no futuro y de menos de
  5 minutos, `events` con el miembro `http://schemas.openid.net/event/backchannel-logout` (objeto),
  sin `nonce`, `jti` presente y no visto (se recuerda hasta `exp` + 5 min), y `sid` o `sub`. Un
  `kid` desconocido pide refrescar el discovery para el siguiente reintento. El log solo registra
  el tipo de rechazo (EventId 4103/4104), nunca tokens, `sub` ni `sid`.
- Efecto: `IAuthCenterSessionTerminationStore`. Con el almacén PostgreSQL
  (`PostgreSqlAuthCenterSessionTerminationStore`) un `sid` revoca sus filas
  (`security.revoke_bff_session(text)`) y un token solo con `sub` revoca las filas de ese `sub`
  creadas hasta el momento de recepción (`security.revoke_bff_session(text,timestamptz)`, acotado
  al reloj de la BD); todas las réplicas rechazan la sesión en su siguiente petición. Con
  `SessionStore=Memory`, `DistributedCacheAuthCenterSessionTerminationStore` guarda marcas en
  `IDistributedCache` como antes.
- El registro anti-replay del `jti` sigue en `IDistributedCache` (memoria por réplica) hasta
  `exp` + 5 min en ambos modos: persistirlo en PostgreSQL necesita un objeto que
  `BFF-SESSION-TABLE-SHAPE` no cubre (pregunta abierta `BFF-LOGOUT-JTI-PERSISTENCE`). Un replay
  hacia otra réplica dentro de esa ventana revocaría otra vez las sesiones del mismo `sid` (ya
  revocadas) o, con solo `sub`, las creadas después del primer envío.
- Ingress del piloto: `/auth` ya va a la API (`PILOT-SAME-ORIGIN-ROUTING`); debe aceptar un POST
  sin `Origin` hacia `/auth/backchannel-logout`.

### 14.3 Step-up MFA

- `GET /auth/login?mfa=required` (exactamente ese valor) guarda el requisito en el `state`
  protegido del handler OIDC y `RedirectToIdentityProvider` envía
  `acr_values=urn:authcenter:acr:mfa`. AuthCenter pide el segundo factor sin pedir de nuevo la
  contraseña si la sesión SSO sigue viva, o lo enrola.
- `TokenValidated` exige, además de lo de siempre, `acr` único igual a `urn:authcenter:acr:mfa` o
  `urn:authcenter:acr:phr` y `mfa` en `amr`; si no, el callback falla genérico
  (`signin_failed`) y la sesión previa sigue intacta.
- 403 distinguible: `IdentityAuthorizationResultHandler` agrega `code: "MFA_REQUIRED"` al problem
  details solo cuando el único requisito sin cumplir de la política es `RequireMfaRequirement`
  (por ejemplo un `PLATFORM_ADMIN` activo sin MFA en `PrivilegedMfa`). Cualquier otro 403 sigue
  genérico, así que el código no revela nada que el actor no pudiera inferir.
- Web: `/login?mfa=required&return_url=…` muestra "Verificar identidad" a una sesión autorizada
  sin MFA; el botón va a `/auth/login?mfa=required&return_url=…` con la misma regla de
  `return_url` local. `isMfaRequiredResponse` y `buildStepUpPromptHref` (`src/auth/step-up.ts`)
  permiten que cada cliente de API lleve a ese aviso cuando reciba `MFA_REQUIRED`.
- Reemplazo de sesión: el handler de cookies de ASP.NET Core reutiliza la clave del almacén si la
  petición del callback ya trae una cookie de sesión, así que la cookie anterior seguiría sirviendo
  con la identidad nueva. `AuthCenterSessionReplacement` (en `TicketReceived`) borra el ticket
  anterior y oculta esa cookie antes del `SignIn`, de modo que cada inicio de sesión emite una
  clave y un secreto CSRF nuevos. Aplica a todo inicio de sesión, no solo al step-up.
- No se fuerza MFA a nadie (tampoco a repartidores): sin `mfa=required` no se envía `acr_values`.

### 14.4 `access_denied`

- `error=access_denied` en el callback, después de validar state, correlación e `iss`, redirige
  a `/login?error=access_denied`; la web muestra "Tu cuenta no tiene acceso a Paquetenvia; pídelo
  a un administrador". Cualquier otro error (o `access_denied` con state inválido) sigue siendo
  `signin_failed` y el mensaje genérico. Nunca se reenvía `error_description`.
- AuthCenter también usa `access_denied` para una cuenta inactiva, una política de acceso que
  niega y un consentimiento rechazado (no aplica con `AutoConsent`); en todos esos casos el
  mensaje pide acceso a un administrador.

### 14.5 Rollback

Revertir el PR. No hay migraciones. La web anterior esperaba 204 en `/auth/logout`: revertir API y
web juntas. Las marcas de back-channel viven en caché y expiran solas.
