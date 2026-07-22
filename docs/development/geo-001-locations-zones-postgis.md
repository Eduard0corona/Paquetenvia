# GEO-001: ubicaciones, zonas y PostGIS

GEO-001 implementa el modulo `Locations` con las capas Domain, Application,
Infrastructure y Endpoints. Adopta sin recrear las tablas canonicas de AI-06 y
usa PostgreSQL 18/PostGIS 3.6 como autoridad para cobertura geografica y RLS.

## Alcance

El modulo modela `City`, `ServiceArea`, `OperatingZone` y `Location`. Expone:

- `GET /api/v1/cities`
- `GET /api/v1/service-areas?city_id={uuid}`
- `GET /api/v1/operating-zones?service_area_id={uuid}`
- `GET /api/v1/locations`
- `POST /api/v1/locations`

Todos requieren identidad activa y `X-Organization-Id`. El POST tambien exige
`Idempotency-Key`. La seleccion tenant reutiliza TEN-001/TEN-002: abre una
transaccion, configura usuario y organizaciones con parametros tipados y usa
`SET LOCAL ROLE paqueteria_app`. Los registros de otro tenant no son visibles.

## Convencion geografica

Los puntos se construyen con SRID 4326 y el orden `X=longitude`, `Y=latitude`.
La evaluacion de cobertura ocurre dentro de PostgreSQL mediante PostGIS y
`ST_Covers`; por tanto, un punto en el borde se considera cubierto. Una zona
`EXCLUDED` activa tiene precedencia sobre CORE, STANDARD y EXTENDED.

Los resultados internos de serviceability son `SERVICEABLE`,
`OUTSIDE_SERVICE_AREA`, `EXCLUDED_ZONE`, `INVALID_CITY`,
`INACCESSIBLE_SERVICE_AREA` e `INACCESSIBLE_OPERATING_ZONE`. Los identificadores
inaccesibles se traducen a un 404 uniforme y no revelan existencia cross-tenant.
`IServiceabilityEvaluator` es un puerto independiente de HTTP y persistencia;
PRC-001 y ORD-001 podran consumirlo para rechazar cotizaciones u ordenes antes
de persistirlas, sin iniciar esas tareas en GEO-001.

## PII y geocodificacion

`address_text`, `contact_name` y `phone` nunca forman parte de una respuesta ni
del payload de auditoria. Se persisten solo en columnas `bytea` despues de
pasar por `ILocationPiiProtector`. `address_summary` es deliberadamente no
sensible y tiene un maximo de 180 caracteres.

Configuracion:

```json
{
  "Locations": {
    "Provider": "Disabled",
    "GeocodingProvider": "Disabled",
    "PiiProtector": "Disabled",
    "CommandTimeoutSeconds": 30
  }
}
```

`Provider` admite `Disabled` o `PostgreSql`; `GeocodingProvider` admite
`Disabled`, `Manual` o `Mock`; `PiiProtector` admite `Disabled` o `Mock`. Los
mocks son deterministas, no usan red y solo arrancan en Development o Testing.
No son cifrado productivo. Staging y Production los rechazan al inicio.
El resultado de geocodificacion registra el modo `MANUAL` o `MOCK` y si las
coordenadas manuales fueron la autoridad, sin exponerlo en el DTO publico.

## Migracion y auditoria

`20260722_AdoptCanonicalLocationsBaseline` comprueba las cuatro tablas, las
geometrias SRID 4326 y los tres indices GiST. No crea, altera ni elimina objetos
canonicos. El migrador conserva el orden AI-06, AI-18, Identity, Organizations
y Locations.

Una creacion aceptada inserta `locations.locations` y el evento
`LOCATION_CREATED` de AUD-001 dentro de la misma transaccion. El audit solo
contiene organizacion, actor, accion, tipo, id de ubicacion y request id.

## Gates y limites

- GATE-003: pendiente proveedor real de mapas/geocodificacion.
- GATE-007: pendiente proteccion productiva de PII.
- GATE-010: pendiente dataset productivo de zonas/SLA.
- GATE-014: pendiente topologia final de base de datos.
- GATE-017: pendiente activacion operativa de ciudades adicionales.

Las pruebas usan exclusivamente datos sinteticos. No se consumen APIs de mapas,
no se cargan poligonos reales y no se modifica la base persistente de FND-002.

Riesgos residuales: no existe proveedor geografico ni protector PII productivo,
las zonas y ciudades operativas reales siguen bloqueadas por gates, y la
topologia final de base de datos no esta resuelta. El idempotency lock serializa
creaciones concurrentes por tenant y clave, pero no sustituye un contrato
productivo de retencion de claves que debera definirse en una tarea posterior.

## Validacion

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test .\tests\Paqueteria.UnitTests\Paqueteria.UnitTests.csproj --no-build
dotnet test .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj --no-build
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj --no-build
dotnet test .\tests\Paqueteria.ArchitectureTests\Paqueteria.ArchitectureTests.csproj --no-build
python .\docs\normative\v0.6\tools\validate_contracts.py
git diff --exit-code -- docs/normative/v0.6
```

Los contratos PostgreSQL usan un Testcontainer efimero `postgis/postgis:18-3.6`
y validan SRID, orden lng/lat, borde, interior, exterior, zona excluida, RLS,
ciphertext, auditoria y ownership del historial de adopcion.

## Rollback

El rollback de codigo retira el registro del modulo y revierte sus proyectos.
No se ejecuta un `Down` destructivo ni se eliminan tablas, datos, indices, roles
o policies canonicos. Los datos de aplicacion requieren un procedimiento
operativo separado y autorizado.
