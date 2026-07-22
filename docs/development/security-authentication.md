# Autenticacion y autorizacion SEC-001/SEC-002

## Separacion de responsabilidades

La autenticacion externa y la autorizacion interna son contratos distintos.
`IIdentityProvider` valida una credencial y solo produce `ExternalIdentity`
(`Subject` y evidencia MFA). No contiene estado de usuario, organizaciones,
membresias, roles, permisos ni el identificador interno.

`IIdentityContextResolver` toma el `Subject` ya autenticado y resuelve el
contexto autorizado de Paquetenvia. Su implementacion PostgreSQL invoca
exclusivamente:

```sql
SELECT security.resolve_identity_context(@identity_subject);
```

AuthCenter fue elegido en GATE-002, pero su issuer, audience, client ID, URLs y
scopes no estan definidos en el repositorio. SEC-002 no inventa esos valores ni
agrega conectividad OIDC real. Un adaptador futuro debera producir solo `sub` y
`amr=mfa`; claims externos de roles, permisos, aplicaciones u organizaciones
se ignoran para autorizacion tenant.

## Pipeline y sesion

```text
Bearer -> IIdentityProvider -> ExternalIdentity
       -> IIdentityContextResolver -> contexto interno
       -> ClaimsPrincipal -> IAuthenticatedSession -> policies
```

La funcion SQL devuelve `NULL` tanto para subject desconocido como para usuario
suspendido o deshabilitado. Esos casos son indistinguibles y crean una identidad
externa autenticada sin status ni memberships; las policies activas responden
403 generico. Un fallo tecnico o drift del JSON responde 503 generico, nunca
401/404.

La sesion es un snapshot inmutable, request-scoped y stateless. No usa cookies,
Redis, base de datos de sesion, `AsyncLocal`, cache global ni persistencia. Solo
acepta claims internos `sub`, `amr=mfa`, status y membership. Cada membership
conserva junto `organization_id`, rol e `is_default`; no existen roles globales
cross-organization ni seleccion de tenant activo (TEN-001 sigue pendiente).

## Providers y configuracion

```json
{
  "Authentication": { "Provider": "Disabled" },
  "IdentityBootstrap": { "Provider": "Disabled", "CommandTimeoutSeconds": 5 },
  "PublicTracking": { "Provider": "Disabled", "CommandTimeoutSeconds": 5 }
}
```

`Authentication` admite `Disabled` y `Mock`. `IdentityBootstrap` admite
`Disabled`, `Mock` y `PostgreSql`; `PublicTracking` admite `Disabled` y
`PostgreSql`. Los mocks solo pueden iniciar en Development/Testing. PostgreSQL
requiere `ConnectionStrings:Paqueteria`; la conexion real se inyecta por
configuracion externa y nunca se imprime.

El mock de autenticacion es determinista pero solo elige subject/MFA. El
`MockIdentityContextResolver` separado contiene las autorizaciones sinteticas y
no acepta IDs, roles, JSON ni headers aportados por el cliente.

## Seguridad PostgreSQL

El adaptador usa `NpgsqlDataSource`, parametros tipados, timeout explicito y
cancelacion. Abre una transaccion, ejecuta `SET LOCAL ROLE paqueteria_app`, llama
la funcion y confirma. Nunca asume `paqueteria_bootstrap`, establece GUC tenant,
consulta tablas directamente ni usa SQL dinamico. `SET LOCAL` impide que el rol
se filtre al pool.

El parser exige UUIDs validos, `status=ACTIVE`, arreglo de memberships, roles
normativos, booleano `is_default`, propiedades exactas y ausencia de duplicados
organization/role. JSON nulo, incompleto, malformado o desconocido es error
tecnico fail-closed.

## HTTP, SignalR y pruebas

- Credencial ausente o invalida: 401.
- Subject autenticado sin contexto autorizado: 403.
- Contexto activo sin capacidad tenant/MFA: 403.
- PostgreSQL o contrato JSON no disponible: 503.

Las respuestas Problem Details no revelan credenciales, subject, estado interno,
organizacion, connection string ni JSON. No se establecen cookies. Los probes
de identidad y SignalR existen solo con environment exacto `Testing`, quedan
fuera de OpenAPI y no son endpoints productivos.

La matriz `WebApplicationFactory` ejecuta el baseline mediante
`DatabaseBaselineDeployer` sobre PostgreSQL 18/PostGIS 3.6 efimero, crea un login
API `NOINHERIT`/`NOBYPASSRLS`, siembra datos sinteticos y valida 401, 403, 503,
MFA, multi-organizacion, SignalR y ausencia de fuga del role state.

Consulta tambien [security-bootstrap-tracking.md](security-bootstrap-tracking.md).
