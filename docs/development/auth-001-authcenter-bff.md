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
4. Las sesiones BFF irán a una tabla PostgreSQL (cambio normativo pendiente, fuera de este PR).
   Mientras tanto el ticket queda en memoria detrás de `ITicketStore`/`IDistributedCache` (§4).
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
Navegador ──POST /auth/logout (cookie + X-AuthCenter-CSRF)──▶ API revoca refresh (POST /oauth/revoke) y destruye la sesión
```

Si falla el callback (state, nonce, firma, issuer, audience, expiración, PKCE, error del IdP), la API
redirige a `/login?error=signin_failed` sin sesión y sin detalles. El log solo registra el tipo de
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
`acr_values`. El owner eligió **step-up** (`acr_values`) en lugar de `RequireMfa`; el step-up para
roles `PrivilegedMfa` llega en el PR de seguimiento. Mientras tanto, esos roles responden 403 si
la sesión no trae `mfa` en `amr`.

## 4. Sesión del lado servidor

- La cookie `__Host-Paquetenvia.Session` es `Secure`, `HttpOnly`, `SameSite=Lax`, `Path=/`, sin
  `Domain` y de sesión (no persistente). Solo contiene una clave aleatoria de 256 bits protegida
  con Data Protection.
- El ticket (identidad mínima, secreto CSRF y refresh token) se protege con el key ring de la
  plataforma (`DataProtection:Provider=PostgreSql` en ScaleReady) y se guarda en
  `IDistributedCache`. No se guardan el ID token ni el access token, porque Paquetenvia no los usa.
- La vida es fija (`AuthCenter:SessionLifetimeMinutes`, 480 por defecto, máximo 1440), sin
  expiración deslizante. No hay endpoint de refresh: el refresh token solo sirve para revocar.
- Las cookies de correlación y nonce del handler OIDC también usan el prefijo `__Host-`, `Secure`,
  `HttpOnly` y `SameSite=Lax`, porque AuthCenter devuelve el código por query en un GET de nivel
  superior.
- **Limitación single-instance.** La caché por defecto es `MemoryDistributedCache`. Con más de
  una réplica, una petición que llega a otra instancia no encuentra el ticket y responde 401
  (falla cerrado, y el usuario vuelve a iniciar sesión). **Decisión del owner:** las sesiones irán
  a una tabla PostgreSQL, lo que exige un cambio normativo (AI-06/AI-18) que no forma parte de este
  PR. El almacenamiento ya está detrás de interfaces (`ITicketStore` → `AuthCenterTicketStore` →
  `IDistributedCache`), así que el cambio sustituye la implementación sin tocar endpoints ni web.

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
   `acr_values` para los roles `PrivilegedMfa` (llega en el PR de seguimiento).
6. Logout: AuthCenter ya publica `end_session_endpoint` (`/oauth/logout`) y back-channel logout
   (`backchannel_logout_supported` y `backchannel_logout_session_supported` en discovery; el
   `sid` del ID token coincide con el del `logout_token`). El owner aprobó RP-initiated logout y
   back-channel logout; se implementan en el PR de seguimiento. Hasta entonces el logout de
   Paquetenvia revoca el refresh y destruye la sesión local, pero la sesión SSO de AuthCenter
   puede seguir activa en equipos compartidos.
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

## 11. Preguntas abiertas para el owner

1. ~~Primer ingreso~~: resuelto por el owner, por invitación previa en un PR aparte.
2. ~~Sesión multi-instancia~~: resuelto por el owner, tabla PostgreSQL tras el cambio normativo.
3. ¿Hosts web por ambiente y redirect URIs definitivos (dev/staging/prod)?

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

Web (`vitest`): estrategia de credenciales, parser de sesión, `return_url` local, logout,
instalación de sesión y rewrites.

## 13. Rollback

Poner `Authentication:Provider` en `Disabled` o `Mock` (solo entornos no productivos) y quitar
`NEXT_PUBLIC_AUTH_MODE`. No hay migraciones. Para retirar el código, revertir el PR. El cliente en
AuthCenter y el secreto se conservan durante una ventana de solapamiento; si hubo exposición, se
revocan las sesiones y se rota el secreto.
