# MDM-001: carga de datos maestros del piloto (herramienta de operador)

## Alcance implementado

MDM-001 aplica la decisión del owner `MDM-001-OPERATOR-LOADER` ("Herramienta de operador"): ciudades,
áreas de servicio, zonas operativas, tarifas y perfiles de repartidor se cargan con un job de operador que
lee un archivo revisado y lo carga de forma idempotente, auditada y acotada a un tenant. No hay API
pública nueva, ni estados, eventos, SignalR o UI nuevos.

Se construye y prueba sólo con datos sintéticos. Las zonas y tarifas reales esperan GATE-010 y GATE-011;
los perfiles de repartidor reales, GATE-007. El único archivo de ejemplo es
`tests/fixtures/mdm-001/synthetic-master-data.json` (`classification: SYNTHETIC`), nunca `database/seeds`.

## Formato: JSON `paquetenvia.master-data.v1`

Se eligió JSON y no CSV porque:

- las áreas y zonas llevan polígonos (GeoJSON `MultiPolygon`) y referencias anidadas (zona → área →
  ciudad), que CSV sólo podría llevar como texto incrustado;
- un token numérico JSON conserva su escritura exacta, así que `amount_cents: 12000` se distingue de
  `12000.0`, `1.2e4` o `"12000"` y todos esos se rechazan (AI-01 §4.15); en CSV todo es texto sensible a la
  configuración regional;
- `System.Text.Json` viene con .NET: no se agrega ningún paquete NuGet ni npm.

Estructura (todas las propiedades son obligatorias; una propiedad desconocida o repetida se rechaza):

```json
{
  "format": "paquetenvia.master-data.v1",
  "classification": "SYNTHETIC | REVIEWED",
  "owner_org_id": "<uuid en minúsculas, igual a --organization-id>",
  "cities":          [{ "country_code", "state_code", "name", "timezone", "status" }],
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

`policy_version` es obligatorio en cada regla de tarifa (decisión del owner: la versión de la política de
precios es por organización). Se valida con `^[A-Za-z0-9._-]{1,64}$` en el job y en PostgreSQL, pero
**todavía no se persiste**: la columna `pricing.tariff_rules.policy_version` (NOT NULL, mismo patrón) la
agrega la rama paralela `feature/prc-policy-version-per-org`. Cuando esa lane llegue, la función debe
insertarla, compararla en la recarga y sumar la columna a los grants del ejecutor (hay un `TODO` en el
`INSERT` de tarifas); mientras tanto, si la columna llega primero, el `INSERT` falla cerrado por `NOT NULL`.

## Llaves naturales e idempotencia

| Entidad | Llave natural | Qué puede cambiar una recarga |
| --- | --- | --- |
| Ciudad (global) | `(country_code, state_code, name)` | nada: si existe con otra zona horaria o estado, falla (`MDM001_CITY_CONFLICT`) |
| Área de servicio | `(owner_org_id, city, name)` | `polygon`, `status` |
| Zona operativa | `(owner_org_id, service_area, name)` | `zone_type`, `polygon`, `status` |
| Tarifa | `(owner_org_id, city, service_area, operating_zone, pricing_tier, service_type, active_from)` | `active_to`, `status`; un `amount_cents` o `tax_mode` distinto falla (`MDM001_TARIFF_AMOUNT_IMMUTABLE`): un precio nuevo es una regla nueva con otro `active_from` |
| Perfil de repartidor | `user_id` | `home_city`, `driver_type`, `vehicle_type`, `status` |
| Área del repartidor | `(driver, service_area)` | `status` |

La carga sólo crea o actualiza; nunca borra. Para retirar algo se carga con `status: INACTIVE`. El mismo
archivo cargado dos veces no cambia ninguna fila de datos maestros (sólo agrega su fila de auditoría).
Un bloqueo consultivo por organización serializa cargas concurrentes, porque `pricing.tariff_rules` no
tiene índice único sobre su llave natural; si ya hay dos reglas con la misma llave, la carga falla
(`MDM001_TARIFF_AMBIGUOUS`).

## Validación antes de escribir

1. **En el job (C#)**, antes de abrir la conexión: tamaño (≤ 16 MiB), JSON estricto, forma exacta,
   enumeraciones, nombres (1–200, sin espacios en los extremos ni caracteres de control), UUID canónicos,
   centavos enteros por el token crudo, instantes UTC exactos, `active_to > active_from`, zona con área,
   geometría estructural (MultiPolygon, anillos cerrados, coordenadas en rango, ≤ 200 000 vértices) y
   llaves naturales únicas dentro del archivo. Los errores muestran sólo `sección[n].campo: CÓDIGO`.
2. **En PostgreSQL**, dentro de `security.load_master_data`, una primera fase valida y resuelve todo el
   documento sin escribir: de nuevo forma y enumeraciones, zona horaria contra `pg_timezone_names`,
   `ST_IsValid` y tipo `MultiPolygon`, que cada zona quede dentro de su área (`ST_CoveredBy`), referencias a
   ciudades/áreas/zonas existentes o del mismo archivo, membresía de repartidor, y los conflictos anteriores.
   Sólo después aplica el plan. Todo ocurre en una sola transacción: cualquier error deja la base igual.

## Base de datos (AI-18, carril Pricing)

La migración Pricing `20260928000100_AddMasterDataLoader` (Pricing corre después de Locations y Drivers):

- crea, si faltan, `paqueteria_master_data_executor NOLOGIN BYPASSRLS` (dueño de la función) y
  `paqueteria_master_data_loader NOLOGIN NOBYPASSRLS` (el beneficiario del operador) y falla si ya existen
  con atributos, membresías u objetos fuera de contrato;
- concede al ejecutor sólo `USAGE` sobre `identity`, `organizations`, `locations`, `pricing`, `drivers` y
  `platform` y las columnas exactas de AI-18 (lecturas de usuario/organización/membresía, `SELECT`/`INSERT`
  y `UPDATE` limitado en las seis tablas maestras, `INSERT` en `platform.audit_logs`); sin `DELETE` ni
  permisos de tabla completa;
- concede al beneficiario sólo `USAGE` sobre `security` y `EXECUTE` sobre
  `security.load_master_data(uuid,uuid,jsonb,bytea,boolean)`, `SECURITY DEFINER` con
  `search_path=pg_catalog, pg_temp` (PostGIS se califica como `public.*`);
- `paqueteria_app` y `paqueteria_worker` no ganan ningún permiso (las ciudades siguen siendo de sólo lectura
  en runtime) y ninguno de los dos roles se concede a un rol de runtime.

La función exige que la transacción lleve exactamente la organización destino en `app.current_org_ids`
(`MDM001_TENANT_CONTEXT_MISMATCH` si no) y que esa organización esté `ACTIVE`. Un perfil de repartidor
exige una membresía `DRIVER` `ACTIVE` del usuario en esa organización (REG-DRIVER-PROFILE-LATER), y un
perfil existente de otra organización nunca se reasigna.

**Rollback del carril:** el `Down` elimina sólo la función; los roles, sus permisos, los datos cargados y
las filas de auditoría (append-only) se quedan. Volver a aplicar el carril restaura la función.

Mantienen el contrato: `DatabaseBaselineAssertions` (límite exacto del ejecutor y del beneficiario), el
mapa E-002 (`_PLUS_MDM001`, por historial del carril Pricing), la ACL de `security`, el puente de
propiedad de Azure del carril Pricing y `validate_contracts.py` (permisos exactos).

## Auditoría y salida sin PII

Cada carga real escribe una fila en `platform.audit_logs`: `org_id` = organización cargada,
`actor_id` = `NULL` (job de operador), `action` = `MASTER_DATA_LOADED`, `entity_type` =
`MASTER_DATA_LOAD`, `entity_id` = id de carga, `request_id` = `mdm-001` y `payload_redacted` con formato,
clasificación, SHA-256 del archivo, `dry_run` y conteos por entidad. Ni la auditoría ni la consola llevan
nombres, identificadores de usuario ni coordenadas: los cambios se reportan como `ACCIÓN sección[n]
fields=...`.

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
- `--dry-run` corre la validación completa y el diff en una transacción `READ ONLY` que se revierte: no
  escribe datos ni auditoría.
- **Perfiles de repartidor (GATE-007):** un archivo con `driver_profiles` sólo se carga cuando
  `DOTNET_ENVIRONMENT` es `Development` o `Testing`, o `DevSynthetic` con
  `PAQUETERIA_DEPLOYMENT_CLASS=DEV_SYNTHETIC`; `PILOT_REAL_PEOPLE` nunca cuenta como sintético. En cualquier
  otro entorno exige `--allow-real-driver-profiles`, y **ese flag sólo puede usarse cuando GATE-007 esté
  cerrado** (aviso, base legal, retención y ARCO de datos de repartidores).
- Códigos de salida: 0 correcto, 8 archivo rechazado, 9 rechazo de la base (nada escrito), 10 compuerta
  (GATE-007 o login indebido).

Crear el login de operador localmente (después de `DevSeed bootstrap` y `apply`):

```sql
CREATE ROLE paqueteria_local_mdm LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
  PASSWORD '<64 hex>';
GRANT paqueteria_master_data_loader TO paqueteria_local_mdm;
```

El procedimiento del piloto está en `docs/operations/env-001-pilot/README.md` §6.8.

## Pruebas

- `MasterDataLoaderPostgreSqlContractTests` (categoría `PostgreSqlContract`, PostGIS 18-3.6 real): límite
  exacto de roles/función/permisos, sin capacidad nueva para runtime, recarga idempotente del ejemplo
  sintético, aislamiento entre tenants, auditoría, dry-run, rechazos del job y de la base sin escrituras
  parciales, compuerta GATE-007 y membresía de repartidor, rechazo de logins privilegiados o de runtime, y
  el carril Pricing `Down`/`Up` en una base aislada.
- `tools/ci/test_validate_contracts_grants.py`: permisos exactos del ejecutor y del beneficiario.
