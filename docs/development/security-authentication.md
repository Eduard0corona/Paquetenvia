# Autenticación y autorización SEC-001

## Alcance

SEC-001 agrega una base reemplazable de autenticación y autorización a la API,
sin elegir un proveedor OIDC productivo. La implementación es stateless y se
limita a resolver una credencial sintética, normalizar la identidad y construir
el contexto autenticado de una sola petición.

`GATE-002` permanece abierto. Por ello no existen issuer, discovery URL, client
ID, secretos, callbacks, emisión de tokens, refresh tokens, cookies ni sesiones
persistentes. Tampoco existen usuarios, organizaciones o membresías en EF/Core
o PostgreSQL.

## Módulo Identity

El módulo conserva las cuatro capas canónicas:

- `Identity.Domain`: estados y roles normativos puros, sin ASP.NET Core.
- `Identity.Application`: `IIdentityProvider`, resultado de autenticación,
  identidad normalizada y `IAuthenticatedSession`, todos independientes de
  `HttpContext`, `ClaimsPrincipal` y SDK externos.
- `Identity.Infrastructure`: catálogo de perfiles sintéticos y
  `MockIdentityProvider`, sin red ni base de datos.
- `Identity.Endpoints`: schemes ASP.NET Core, transformación de claims, sesión
  request-scoped, requirements/handlers, respuestas Problem Details y probes
  exclusivos de Testing.

`Paqueteria.Api` es el composition root. Registra Infrastructure y Endpoints;
ninguna capa Application o Endpoints referencia Infrastructure.

## Proveedor y schemes

La opción tipada es:

```json
{
  "Authentication": {
    "Provider": "Disabled"
  }
}
```

Los únicos valores admitidos son `Disabled` y `Mock`. El scheme lógico
`Paquetenvia.Authentication` reenvía a:

- `Paquetenvia.Disabled`: fail-closed; nunca autentica y responde challenge
  genérico 401.
- `Paquetenvia.MockOidc`: lee exclusivamente `Authorization: Bearer <perfil>`
  y consulta `IIdentityProvider`.

`Mock` se valida al iniciar y solo se acepta con environment `Development` o
`Testing`. Configurarlo en `Staging` o `Production` detiene el inicio con un
error explícito. `Disabled` es el valor predeterminado para que la aplicación
inicie cerrada mientras `GATE-002` siga pendiente.

El bearer sintético selecciona uno de estos perfiles preconstruidos:

| Credencial | Resultado sintético |
|---|---|
| `active-viewer` | identidad ACTIVE, VIEWER sin MFA |
| `active-platform-admin-mfa` | PLATFORM_ADMIN con MFA |
| `active-platform-admin-no-mfa` | PLATFORM_ADMIN sin MFA |
| `active-multi-org` | VIEWER en una organización y DISPATCHER en otra |
| `suspended-user` | identidad SUSPENDED |
| `disabled-user` | identidad DISABLED |
| `suspended-membership` | membresía SUSPENDED |
| `revoked-membership` | membresía REVOKED |

No se aceptan IDs, roles, estados, MFA, permisos o JSON enviados por el cliente.
No se leen headers `X-User-Role`, `X-Organization-Role`, `X-Is-Admin` ni `X-MFA`.
La credencial completa y los claims completos no se registran.

Para habilitar el adaptador durante desarrollo, sin publicar probes:

```powershell
$env:Authentication__Provider = "Mock"
dotnet run --project .\src\Paqueteria.Api --environment Development
Remove-Item Env:\Authentication__Provider
```

Los perfiles no son secretos, pero son únicamente datos de desarrollo/prueba.

## Claims internos y sesión

El handler crea primero una identidad fuente normalizada que el cliente no
puede construir mediante headers. `PaquetenviaClaimsTransformation` valida esa
fuente y emite:

- `sub`, como identificador OIDC normalizado;
- `amr=mfa`, solo cuando el proveedor autenticado aportó esa evidencia;
- `urn:paquetenvia:identity:v1:status`;
- `urn:paquetenvia:identity:v1:membership`.

El claim de membership conserva en un solo valor interno versionado la tupla
`organization_id | role | membership_status`. No se crean claims
`ClaimTypes.Role`: un rol de la organización A nunca se convierte en permiso
global ni autoriza la organización B. Los valores desconocidos invalidan la
transformación completa.

La transformación elimina y reconstruye su identidad derivada, por lo que es
idempotente y no duplica claims cuando ASP.NET Core la ejecuta varias veces.
Claims arbitrarios de otra identidad o issuer no ingresan a la sesión.

`IAuthenticatedSession` es un snapshot inmutable y scoped de la petición. Solo
expone subject, estado, MFA y membresías ACTIVE, además de consultas por
organización y rol exactos. No usa estado estático, `AsyncLocal`, cookies,
sesión de servidor, Redis, caché global, base de datos ni persistencia entre
peticiones. No selecciona una organización activa; eso corresponde a TEN-001.

## Policies

- `Identity.Authenticated`: identidad técnicamente autenticada.
- `Identity.Active`: exige estado `ACTIVE`; `SUSPENDED` y `DISABLED` producen
  403.
- `Identity.RequireMfa`: exige `amr=mfa` derivado de la identidad normalizada.
- `Identity.PrivilegedMfa`: exige identidad ACTIVE, una membresía ACTIVE con
  `PLATFORM_ADMIN` y MFA.
- `OrganizationMembershipRequirement`: autorización resource-based que exige
  una membresía ACTIVE y el rol solicitado en el mismo `organizationId`.

No existe middleware `X-Organization-Id` ni contexto de organización activa.
Las policies no implementan SEC-002 o TEN-001.

Una autenticación ausente o inválida devuelve 401. Una identidad válida sin
capacidad, inactiva, sin MFA o con membresía incorrecta devuelve 403. Ambos
casos usan Problem Details genérico con `status`, título y trace ID; no revelan
token, estado, rol, organización, perfil mock ni stack trace. `GET /health/live`
está marcado explícitamente como anónimo.

## Probes y pruebas

Solo el environment exacto `Testing` registra estas rutas, excluidas de
OpenAPI:

```text
/__tests/security/authenticated
/__tests/security/privileged
/__tests/security/organization/{organizationId}
/__tests/hubs/security
```

El hub es interno, no tiene métodos de negocio ni grupos y únicamente valida el
pipeline de autorización. No representa OperationsHub, DriverHub o TrackingHub.
Las pruebas ejercitan el endpoint HTTP de `negotiate`; no agregan el cliente
SignalR.

Ejecuta la matriz de seguridad con:

```powershell
dotnet test .\tests\Paqueteria.UnitTests\Paqueteria.UnitTests.csproj
dotnet test .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj
dotnet test .\tests\Paqueteria.ArchitectureTests\Paqueteria.ArchitectureTests.csproj
```

IntegrationTests usa `WebApplicationFactory<Program>` con environment
`Testing` y `Authentication:Provider=Mock`. Cubre 401, 403, separación de roles
multi-organización, ausencia de cookie, SignalR, ausencia de probes fuera de
Testing y rechazo de Mock en ambientes productivos.

## Reemplazo futuro, límites y rollback

Después de resolver `GATE-002`, un adaptador real implementará
`IIdentityProvider` y producirá el mismo modelo normalizado. El scheme real
deberá validar issuer, audience, firma y evidencia MFA según la decisión
aprobada; no debe cambiar las policies ni convertir roles tenant en roles
globales.

SEC-002 deberá resolver identidad/membresías persistidas. TEN-001 deberá
seleccionar el contexto activo autorizado. El frontend de login, PKCE, refresh,
provisioning, RLS y los hubs productivos también quedan pendientes.

Para revertir SEC-001, elimina los cuatro proyectos Identity de la solución,
sus entradas del catálogo, las referencias del API, las pruebas y esta guía;
restaura `Program.cs`, `appsettings.json` y README. La reversión no requiere
migraciones, datos, secretos ni limpieza de sesiones porque SEC-001 no crea
ninguno.
