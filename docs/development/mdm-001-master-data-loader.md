# MDM-001: carga de datos maestros del piloto (herramienta de operador)

## Alcance implementado

MDM-001 aplica la decisión del owner `MDM-001-OPERATOR-LOADER` ("Herramienta de operador"): ciudades,
áreas de servicio, zonas operativas, tarifas y perfiles de repartidor se cargan con un job de operador que
lee un archivo revisado y lo carga de forma idempotente y auditada, para una organización a la vez. No hay
API pública nueva, ni estados, eventos, SignalR o UI nuevos.

Se construye y prueba sólo con datos sintéticos. Las zonas y tarifas reales esperan GATE-010 y GATE-011;
los perfiles de repartidor reales, GATE-007. Los únicos archivos de ejemplo están en
`tests/fixtures/mdm-001/` (`classification: SYNTHETIC`), nunca en `database/seeds`:
`synthetic-platform-cities.json` (ciudades, carga de una organización `PLATFORM`) y
`synthetic-master-data.json` (áreas, zonas y tarifas de una organización).

## Quién puede cargar: capacidad de operador de plataforma, no frontera de tenant

`paqueteria_master_data_loader` es una **capacidad de operador de plataforma**. Quien la tiene puede
cargar datos maestros de cualquier organización activa: nombra la organización (`--organization-id`), la
repite en el archivo (`owner_org_id`) y la fija como contexto de tenant; la función rechaza cualquier otro
contexto (`MDM001_TENANT_CONTEXT_MISMATCH`). Eso evita que un error de captura cargue en otra organización,
pero **no** es una frontera de seguridad entre tenants: la frontera es quién recibe el rol. Por eso:

- sus únicos miembros son logins de operador **con nombre**, creados y retirados por el owner (por
  ejemplo `pv_pilot_mdm_<iniciales>`), nunca `paqueteria_app`, `paqueteria_worker` ni `paqueteria_bootstrap`;
  ningún miembro que pueda usarlo (`INHERIT` o `SET`) puede ser a la vez miembro de `paqueteria_migrator`
  (lo verifican la lane y `DatabaseBaselineAssertions`): quien carga no es quien despliega;
- cada carga deja en `platform.audit_logs` al operador como actor registrado, con un **seudónimo**:
  `operator_ref` = SHA-256 hex de `paquetenvia.mdm-001.operator:` + login. Los tenants pueden leer sus
  filas de auditoría, así que el nombre del login nunca se guarda; el personal de plataforma identifica al
  operador recalculando `operator_ref` para los logins que creó. `actor_id` queda `NULL`: ningún usuario de
  la aplicación hace la carga. El seudónimo es un hash sin sal de un nombre corto: identifica, pero no
  oculta el login a quien pueda adivinarlo; no uses nombres de persona completos.

## Compuerta de despliegue (GATE-007) en la base

`platform.master_data_deployment_gate` (AI-06) es una sola fila global que sólo escribe el migrador
(`master-data-gate`, como `paqueteria_migrator`) y sólo lee el ejecutor. Como toda tabla de aplicación
salvo `locations.cities`, fuerza RLS; su única política (AI-18) admite a `paqueteria_migrator`, su dueño.
Cada cambio queda como fila de auditoría append-only (`MASTER_DATA_GATE_CHANGED`) de la organización
`PLATFORM`, con el estado anterior, el nuevo y el `operator_ref` del login de despliegue. El comando nunca
vuelve `SYNTHETIC` una base marcada `REAL` (`MDM001_GATE_REAL_TO_SYNTHETIC_REFUSED`). La función aplica la
marca en cada llamada, con independencia de las variables de entorno de la máquina del operador:

| Marca | Archivos `SYNTHETIC` | Archivos `REVIEWED` | `driver_profiles` |
| --- | --- | --- | --- |
| sin fila (predeterminado) = `REAL` | rechazados | aceptados | rechazados |
| `REAL`, `gate_007_closed=false` | rechazados | aceptados | rechazados (`MDM001_DRIVER_PROFILES_REQUIRE_GATE_007`) |
| `REAL`, `gate_007_closed=true` | rechazados | aceptados | aceptados |
| `SYNTHETIC` | aceptados | rechazados (`MDM001_CLASSIFICATION_NOT_ALLOWED`) | aceptados |

```bash
# con la conexión de despliegue (migración), nunca con el login de operador
Paqueteria.DatabaseMigrator master-data-gate --connection-env PAQUETERIA_MIGRATION_CONNECTION \
  --deployment-class SYNTHETIC|REAL --platform-organization-id <uuid PLATFORM> [--gate-007-closed]
```

`--gate-007-closed` sólo vale con `REAL` y **sólo puede usarse cuando GATE-007 esté cerrado** (aviso,
base legal, retención y ARCO de datos de repartidores). El comando se niega a marcar `SYNTHETIC` si
`PAQUETERIA_DEPLOYMENT_CLASS=PILOT_REAL_PEOPLE`. El job mantiene además su propia verificación de entorno
como defensa en profundidad (ver Ejecución).

## Formato: JSON `paquetenvia.master-data.v1`

Se eligió JSON y no CSV porque:

- las áreas y zonas llevan polígonos (GeoJSON `MultiPolygon`) y referencias anidadas (zona → área →
  ciudad), que CSV sólo podría llevar como texto incrustado;
- un token numérico JSON conserva su escritura exacta, así que `amount_cents: 12000` se distingue de
  `12000.0`, `1.2e4`, `-0` o `"12000"`, y todos esos se rechazan (AI-01 §4.15); en CSV todo es texto
  sensible a la configuración regional;
- `System.Text.Json` viene con .NET: no se agrega ningún paquete NuGet ni npm.

Estructura (todas las propiedades son obligatorias; una propiedad desconocida o repetida se rechaza):

```json
{
  "format": "paquetenvia.master-data.v1",
  "classification": "SYNTHETIC | REVIEWED",
  "owner_org_id": "<uuid en minúsculas, igual a --organization-id>",
  "cities":          [{ "country_code": "MX", "state_code", "name", "timezone" }],
  "service_areas":   [{ "city": {country_code,state_code,name}, "name", "status", "polygon": <MultiPolygon> }],
  "operating_zones": [{ "service_area": {"city": {...}, "name"}, "name", "zone_type", "status", "polygon" }],
  "tariff_rules":    [{ "city": {...}, "service_area": "<nombre>|null", "operating_zone": "<nombre>|null",
                        "pricing_tier", "service_type", "amount_cents": <entero>, "tax_mode",
                        "policy_version": "<[A-Za-z0-9._-]{1,64}>",
                        "active_from": "YYYY-MM-DDTHH:MM:SSZ", "active_to": "...|null", "status" }],
  "driver_profiles": [{ "user_id", "home_city": {...}, "driver_type", "vehicle_type", "status",
                        "service_areas": [{ "city": {...}, "name", "status" }] }]
}
```

Los valores de enumeraciones son exactamente los `CHECK` de AI-06. Coordenadas en WGS84
`[longitud, latitud]`; cada anillo cerrado y con al menos cuatro posiciones.

**Ciudades.** Son globales, así que sólo una carga de una organización de tipo `PLATFORM` puede traer la
sección `cities` (`MDM001_CITIES_REQUIRE_PLATFORM_ORGANIZATION`). Una ciudad nueva se crea siempre
`ACTIVE` (la entrada no lleva `status`), sólo en México (`country_code: MX`) y con una zona IANA de la
lista permitida: `America/Bahia_Banderas`, `America/Cancun`, `America/Chihuahua`, `America/Ciudad_Juarez`,
`America/Hermosillo`, `America/Matamoros`, `America/Mazatlan`, `America/Merida`, `America/Mexico_City`,
`America/Monterrey`, `America/Ojinaga`, `America/Tijuana`. Una ciudad existente nunca se reescribe: otra
zona horaria o una ciudad `INACTIVE` es `MDM001_CITY_CONFLICT`. Las cargas de los tenants sólo pueden
**referenciar** ciudades existentes y `ACTIVE` (`MDM001_CITY_NOT_FOUND` si no).

**`policy_version`** es obligatorio en cada regla de tarifa (decisión del owner
PRC-POLICY-VERSION-PER-ORG: la versión de la política de precios es por organización). Se valida con
`^[A-Za-z0-9._-]{1,64}$` en el job y en PostgreSQL y se guarda en `pricing.tariff_rules.policy_version`
desde la lane de Pricing `20260928000300_StoreTariffPolicyVersionInMasterDataLoader`, que concede al
ejecutor `SELECT` e `INSERT` sobre esa columna (nunca `UPDATE`) y reemplaza la función con el cuerpo
publicado más ediciones revisadas. Una versión guardada es inmutable, como el precio: recargar una regla
existente con otra versión es `MDM001_TARIFF_POLICY_VERSION_IMMUTABLE`; una versión nueva va en una regla
nueva que cierra la anterior con `active_to` en el mismo archivo. Cada cotización congela la versión de la
regla que seleccionó.

## Llaves naturales e idempotencia

| Entidad | Llave natural | Qué puede cambiar una recarga |
| --- | --- | --- |
| Ciudad (global, sólo `PLATFORM`) | `(country_code, state_code, name)` | nada: otra zona horaria o una ciudad `INACTIVE` falla (`MDM001_CITY_CONFLICT`) |
| Área de servicio | `(owner_org_id, city, name)` | `polygon`, `status` |
| Zona operativa | `(owner_org_id, service_area, name)` | `zone_type`, `polygon`, `status` |
| Tarifa | `(owner_org_id, city, service_area, operating_zone, pricing_tier, service_type, active_from)` | `active_to`, `status`; un `amount_cents` o `tax_mode` distinto falla (`MDM001_TARIFF_AMOUNT_IMMUTABLE`) |
| Perfil de repartidor | `user_id` | `home_city`, `driver_type`, `vehicle_type`, `status` |
| Área del repartidor | `(driver, service_area)` | `status` |

**Vigencias de tarifa sin traslape.** Dos reglas `ACTIVE` del mismo `(organización, ciudad, área, zona,
pricing_tier, service_type)` nunca se traslapan en `[active_from, active_to)` (`null` = sin fin), ni dentro
del archivo ni contra las reglas guardadas (`MDM001_TARIFF_OVERLAP`). Un precio nuevo es una regla nueva
con un `active_from` posterior, y el mismo archivo debe cerrar la regla anterior poniéndole `active_to`;
si no, la carga se rechaza. Una regla `INACTIVE` no cuenta.

La carga sólo crea o actualiza; nunca borra. Para retirar algo se carga con `status: INACTIVE`. El mismo
archivo cargado dos veces no cambia ninguna fila de datos maestros (sólo agrega su fila de auditoría).
Toda carga toma un bloqueo consultivo por organización, y las que escriben filas globales (ciudades o
perfiles de repartidor) toman antes uno global, porque `pricing.tariff_rules` no tiene índice único sobre su
llave natural; si ya hay dos reglas con la misma llave, la carga falla (`MDM001_TARIFF_AMBIGUOUS`). Un error
de restricción al escribir se reporta como `MDM001_WRITE_CONFLICT` con la referencia y el SQLSTATE, sin
valores de llave, para que el log del servidor no los registre.

## Validación antes de escribir

1. **En el job (C#)**, antes de abrir la conexión: tamaño (≤ 16 MiB), ≤ 5 000 entradas por sección y
   ≤ 20 000 en total, JSON estricto, forma exacta sin llaves repetidas, enumeraciones, nombres (1–200, sin
   espacios en los extremos ni caracteres de control), UUID canónicos, centavos enteros por el token crudo,
   instantes UTC exactos, `active_to > active_from`, zona con área, ciudad `MX` con zona permitida,
   geometría estructural (MultiPolygon, anillos cerrados, coordenadas en rango, ≤ 200 000 vértices), llaves
   naturales únicas y vigencias sin traslape dentro del archivo. Los errores muestran sólo
   `sección[n].campo: CÓDIGO`.
2. **En PostgreSQL**, dentro de `security.load_master_data`, la función recibe el texto `json` exacto (no
   `jsonb`), así que juzga los tokens numéricos y las llaves repetidas igual que el job. Repite los mismos
   límites (tamaño, entradas, rango de coordenadas con `ST_XMin/XMax/YMin/YMax`, presupuesto de vértices
   con `ST_NPoints`) y, en una primera fase sin escribir, valida y resuelve todo el documento: forma y
   enumeraciones, compuerta de despliegue, organización `PLATFORM` para ciudades, zona horaria permitida,
   `ST_IsValid` y tipo `MultiPolygon`, que cada zona quede dentro de su área (`ST_CoveredBy`), referencias a
   ciudades `ACTIVE`, áreas y zonas existentes o del mismo archivo, membresía de repartidor, traslapes y los
   conflictos anteriores. Sólo después aplica el plan. Todo ocurre en una sola transacción: cualquier error
   deja la base igual.

## Base de datos (AI-06, AI-18, carril Pricing)

La migración Pricing `20260928000100_AddMasterDataLoader` (Pricing corre después de Locations y Drivers):

- elimina, si existe, la sobrecarga `security.load_master_data(uuid,uuid,jsonb,bytea,boolean)` de la
  primera versión publicada, que no tenía compuerta (también lo hace el `Down`);
- adopta `platform.master_data_deployment_gate` (AI-06), o la crea como `paqueteria_migrator` en una
  instalación cuya línea base es anterior, le fuerza RLS con su única política para el migrador, y falla
  si su forma difiere;
- crea, si faltan, `paqueteria_master_data_executor NOLOGIN BYPASSRLS` (dueño de la función) y
  `paqueteria_master_data_loader NOLOGIN NOBYPASSRLS` (el beneficiario del operador) y falla si ya existen
  con atributos, membresías u objetos fuera de contrato; sólo los principales de despliegue (miembros de
  `paqueteria_migrator`) pueden ser miembros del ejecutor;
- concede al ejecutor sólo `USAGE` sobre `identity`, `organizations`, `locations`, `pricing`, `drivers` y
  `platform` y las 116 columnas exactas de AI-18 (lecturas de usuario/organización/membresía, `SELECT`/
  `INSERT` y `UPDATE` limitado en las seis tablas maestras, lectura de la marca de despliegue, `INSERT` en
  `platform.audit_logs`); sin `DELETE`, sin permisos de tabla completa y sin opción de concesión;
- concede al beneficiario sólo `USAGE` sobre `security` y `EXECUTE` sobre
  `security.load_master_data(uuid,uuid,json,bytea,boolean)`, `SECURITY DEFINER` con
  `search_path=pg_catalog, pg_temp` (PostGIS se califica como `public.*`); la ACL de la función es
  exactamente dueño + beneficiario;
- `paqueteria_app` y `paqueteria_worker` no ganan ningún permiso (las ciudades siguen siendo de sólo lectura
  en runtime y la marca de despliegue les es inaccesible).

Las verificaciones de la migración y de `DatabaseBaselineAssertions` leen los privilegios del catálogo
(`aclexplode` sobre `proacl`, `relacl` y `pg_attribute.attacl`), no de `information_schema`, que sólo
muestra lo visible para el rol actual.

**Rollback del carril:** el `Down` elimina sólo la función. Los roles, sus permisos y la marca de
despliegue se quedan a propósito: en instalaciones nuevas los declaran AI-06 y AI-18, un rollback no debe
dejar la base distinta de la línea base con que se construyó, y sin la función son inertes (el ejecutor no
es dueño de nada y ningún login lo alcanza). Los datos cargados y las filas de auditoría (append-only)
también se quedan. Volver a aplicar el carril restaura la función.

Mantienen el contrato: `DatabaseBaselineAssertions` (límite exacto del ejecutor, del beneficiario y de la
marca), el mapa E-002 (`_PLUS_MDM001`, por historial del carril Pricing), la ACL de `security`, el puente
de propiedad de Azure del carril Pricing y `validate_contracts.py` (permisos exactos y la tabla de AI-06).

## Auditoría y salida sin PII

Cada carga real escribe una fila en `platform.audit_logs`: `org_id` = organización cargada,
`actor_id` = `NULL`, `action` = `MASTER_DATA_LOADED`, `entity_type` = `MASTER_DATA_LOAD`, `entity_id` = id
de carga, `request_id` = `mdm-001` y `payload_redacted` con formato, clasificación, clase de despliegue,
`operator_ref`, `file_sha256_reported` (SHA-256 de los bytes del archivo, calculado por el job),
`document_sha256_computed` (SHA-256 del texto exacto del documento, calculado por PostgreSQL; coinciden
para un archivo UTF-8 sin BOM), `dry_run` y conteos por entidad. Ni la
auditoría ni la consola llevan nombres de lugares, identificadores de usuario ni coordenadas: los cambios
se reportan como `ACCIÓN sección[n] fields=...`.

## Ejecución

El job es un modo del migrador canónico (misma imagen `paquetenvia-db-ops`, sin lockfile nuevo):

```bash
Paqueteria.DatabaseMigrator master-data-load \
  --connection-env PAQUETERIA_MASTER_DATA_CONNECTION \
  --file ./reviewed-master-data.json \
  --organization-id <uuid> \
  [--dry-run] [--allow-real-driver-profiles]
```

- La conexión debe ser un login de operador `LOGIN NOINHERIT NOBYPASSRLS` miembro sólo de
  `paqueteria_master_data_loader` (el job hace `SET LOCAL ROLE` por transacción). Se rechaza un superusuario,
  un rol `BYPASSRLS` o un login miembro de `paqueteria_app`, `paqueteria_worker` o `paqueteria_migrator`
  (`MDM001_OPERATOR_LOGIN_REQUIRED`).
- La transacción fija `statement_timeout = 300s` y `lock_timeout = 30s`.
- `--dry-run` corre la validación completa y el diff en una transacción `READ ONLY` que se revierte: no
  escribe datos ni auditoría.
- **Perfiles de repartidor (GATE-007), defensa en profundidad:** además de la compuerta de la base, el job
  sólo envía un archivo con `driver_profiles` cuando `DOTNET_ENVIRONMENT` es `Development` o `Testing`, o
  `DevSynthetic` con `PAQUETERIA_DEPLOYMENT_CLASS=DEV_SYNTHETIC` (`PILOT_REAL_PEOPLE` nunca cuenta como
  sintético); en cualquier otro entorno exige `--allow-real-driver-profiles`, que sólo puede usarse con
  GATE-007 cerrado. Aun con el flag, la base rechaza los perfiles si la marca no lo permite.
- Códigos de salida: 0 correcto, 8 archivo rechazado, 9 rechazo de la base (nada escrito), 10 compuerta
  (GATE-007 o login indebido).

Localmente (después de `DevSeed bootstrap` y `apply`), marca la base como sintética y crea el login:

```bash
Paqueteria.DatabaseMigrator master-data-gate --connection-env PAQUETERIA_MIGRATION_CONNECTION \
  --deployment-class SYNTHETIC --platform-organization-id <uuid de la organización PLATFORM sintética>
```

```sql
CREATE ROLE paqueteria_local_mdm LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
  PASSWORD '<64 hex>';
GRANT paqueteria_master_data_loader TO paqueteria_local_mdm;
```

Primero se cargan las ciudades con una organización `PLATFORM` (`synthetic-platform-cities.json`, con su
`owner_org_id`) y luego los datos de cada organización. El procedimiento del piloto está en
`docs/operations/env-001-pilot/README.md` §6.8.

## Pruebas

- `MasterDataLoaderPostgreSqlContractTests` (categoría `PostgreSqlContract`, PostGIS 18-3.6 real): límite
  exacto de roles/función/permisos desde el catálogo y su detección de ampliaciones, sin capacidad nueva
  para runtime, la marca de despliegue escrita sólo por el migrador y aplicada por la base en una llamada
  directa (GATE-007 y clasificación), ciudades sólo por `PLATFORM` con zonas permitidas y referencias sólo a
  ciudades `ACTIVE`, vigencias sin traslape, recarga idempotente de los ejemplos sintéticos, aislamiento
  entre tenants, auditoría con el login de operador y ambos hashes, dry-run, los mismos centavos y llaves
  repetidas rechazados por el job y por PostgreSQL, límites de la base, rechazos sin escrituras parciales,
  membresía de repartidor, rechazo de logins privilegiados o de runtime, y el carril Pricing `Down`/`Up` en
  una base aislada (incluida la recreación de la marca).
- `tools/ci/test_validate_contracts_grants.py`: permisos exactos del ejecutor y del beneficiario.
