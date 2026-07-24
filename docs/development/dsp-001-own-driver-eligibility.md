# DSP-001: elegibilidad de repartidor propio

DSP-001 incorpora el contrato interno que permitirá a Dispatch validar un
repartidor propio antes de asignarle trabajo. No publica rutas HTTP, no crea
asignaciones y no implementa telemetría. `driver_positions` permanece fuera del
mapeo y del alcance de este módulo.

## Superficie

El módulo Drivers sigue las capas canónicas:

- `Drivers.Domain`: perfiles, áreas de servicio, documentos y enums de contrato.
- `Drivers.Application`: comando, resultado, códigos de rechazo, puerto
  `IDriverEligibilityService` y política pura.
- `Drivers.Infrastructure`: EF Core, lectura PostgreSQL, configuración y
  proveedor Disabled.
- `Drivers.Endpoints`: ensamblado vacío para conservar la simetría de la
  solución; no registra operaciones.

Drivers es dueño exclusivamente de:

- `drivers.driver_profiles`
- `drivers.driver_service_areas`
- `drivers.driver_documents`

La migración `20260723_AdoptCanonicalDriversBaseline` sólo adopta y verifica esas
tres tablas, el índice canónico y sus políticas RLS. Usa
`platform.__ef_migrations_history_drivers`, no emite DDL de negocio y su `Down`
es intencionalmente vacío.

## Contrato de evaluación

El consumidor construye `EvaluateOwnDriverEligibilityCommand` con actor,
organización, repartidor, ciudad, área opcional, requisitos agregados de
paquetes y `EvaluatedAt`. El consumidor debe tomar `IClock.UtcNow` una sola vez
y pasar ese valor; la política nunca llama al reloj ni sustituye un timestamp
omitido por `UtcNow`.

La evaluación usa una conexión y una transacción PostgreSQL consistentes.
Dentro de cada intento de la estrategia de reintento aplica:

1. `app.current_user_id`;
2. `app.current_org_ids`;
3. `SET LOCAL ROLE paqueteria_app`;
4. lectura del perfil, identidad, membresía, área y documentos;
5. evaluación pura y commit.

Si RLS no deja ver el perfil, el resultado contiene únicamente
`DRIVER_UNAVAILABLE`, sin confirmar si el UUID existe. El resultado no contiene
metadata documental, claves de objeto, hashes, identidad ni otra PII.

Los rechazos se producen en orden estable:

1. disponibilidad;
2. tipo y estado del perfil;
3. usuario y membresía `DRIVER`;
4. ciudad;
5. área de servicio;
6. documentos;
7. capacidad.

Se reportan todos los códigos aplicables sin duplicados. Estados desconocidos,
políticas ausentes y valores no válidos fallan cerrados.

### Perfil y área

Un repartidor es candidato si el perfil visible pertenece a la organización
activa, es `OWN`, está `ACTIVE`, su usuario está `ACTIVE`, cuenta con una
membresía interna `DRIVER` `ACTIVE` en la misma organización y su ciudad base
coincide exactamente.

`ServiceAreaId=null` es válido y conserva la evaluación por ciudad. Cuando se
proporciona un UUID no vacío, debe existir una relación activa del repartidor y
el área debe pertenecer al mismo tenant, a la ciudad solicitada y estar activa.
DSP-001 no interpreta polígonos ni geocodifica.

### Documentos

La versión autoritativa por tipo es la fila más reciente por
`created_at DESC, id DESC`. Para cada tipo requerido:

- la fila más reciente debe existir y tener estado `VALID`;
- `object_key` no puede estar vacío;
- `sha256` debe medir exactamente 32 bytes;
- `expires_at` debe ser estrictamente posterior a `EvaluatedAt`, salvo que el
  tipo esté declarado explícitamente como no expirable.

Una expiración exactamente igual a `EvaluatedAt` bloquea. Un documento anterior
válido no reemplaza a una versión más reciente revocada, rechazada o expirada.
DSP-001 sólo evalúa metadata sintética; no carga, descarga, escanea ni conserva
documentos reales.

### Capacidad

`DriverCapacityRequirement` exige al menos un paquete, pesos positivos,
peso individual máximo no mayor que el total y dimensiones positivas cuando
existan. Las comparaciones usan enteros (`int`/`long`) y no suman valores, por
lo que no existe una operación aritmética susceptible de overflow.

Cada vehículo debe configurar límites de cantidad, peso total, peso individual
y largo/ancho/alto. Si `RequireDimensions=true`, una dimensión nula produce el
código específico de esa dimensión. No hay cifras de producto hardcodeadas.

## Configuración

La configuración base es segura y no habilita producto:

```json
{
  "Drivers": {
    "Provider": "Disabled",
    "CommandTimeoutSeconds": 30,
    "Eligibility": {
      "PolicyVersion": "synthetic-v1",
      "RequiredDocumentTypesByVehicleType": {},
      "NonExpiringDocumentTypes": [],
      "VehicleCapacity": {}
    }
  }
}
```

`Disabled` devuelve `DRIVER_UNAVAILABLE`. Para usar `PostgreSql` se requiere
`ConnectionStrings:Paqueteria` y políticas completas para `MOTORCYCLE`, `CAR`,
`VAN`, `BICYCLE` y `WALKER`. `ValidateOnStart` rechaza tipos desconocidos,
políticas incompletas, límites no positivos o un máximo individual superior al
total. Los valores de pruebas son sintéticos y no constituyen parámetros
operativos aprobados.

Ejemplo exclusivamente sintético de una entrada:

```json
{
  "MaximumPackageCount": 2,
  "MaximumTotalWeightGrams": 2000,
  "MaximumSinglePackageWeightGrams": 1000,
  "MaximumLengthMillimeters": 300,
  "MaximumWidthMillimeters": 200,
  "MaximumHeightMillimeters": 150,
  "RequireDimensions": true
}
```

## Operación y observabilidad

La API registra el servicio, pero no lo expone directamente. API y Worker nunca
aplican migraciones. La adopción se ejecuta únicamente mediante
`Paqueteria.DatabaseMigrator`, después de Identity, Organizations y Locations:

```powershell
pwsh .\tools\database-baseline.ps1 Verify
pwsh .\tools\database-baseline.ps1 Plan -ConnectionEnvironment PAQUETERIA_DEPLOYMENT_DB
pwsh .\tools\database-baseline.ps1 Apply -ConnectionEnvironment PAQUETERIA_DEPLOYMENT_DB -ConfirmInitialBaseline
pwsh .\tools\database-baseline.ps1 Assert -ConnectionEnvironment PAQUETERIA_DEPLOYMENT_DB
```

Una operación debe registrar solamente identificadores técnicos ya permitidos,
versión de política, latencia, elegibilidad y códigos de rechazo. Nunca debe
registrar `object_key`, hashes, datos de identidad ni contenido documental.

## Validación

```powershell
dotnet restore --locked-mode
dotnet build --no-restore
dotnet test .\tests\Paqueteria.UnitTests\Paqueteria.UnitTests.csproj --no-build
dotnet test .\tests\Paqueteria.ArchitectureTests\Paqueteria.ArchitectureTests.csproj --no-build
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj --no-build
python .\docs\normative\v0.6\tools\validate_contracts.py
git diff --exit-code -- docs/normative/v0.6
```

Los contratos PostgreSQL usan Testcontainers y prueban adopción sin pendientes,
RLS forzado, rol `NOBYPASSRLS`, contexto vacío, acceso extranjero, mutaciones
cross-tenant, reutilización de pool, cancelación, documentos más recientes y
evaluación elegible.

## Riesgos, gates y rollback

GATE-007 sigue bloqueando documentos reales, retención/ARCO y decisiones de
privacidad. GATE-010 sigue bloqueando reglas geográficas y SLA reales. El issue
#5 y los gates no se cierran con DSP-001.

Rollback operativo:

1. establecer `Drivers:Provider=Disabled`;
2. desplegar la configuración y verificar que toda evaluación devuelva
   `DRIVER_UNAVAILABLE`;
3. revertir los tres commits de DSP-001 si se necesita retirar el código;
4. conservar las tres tablas canónicas y sus datos.

No se eliminan tablas, índices, políticas, historiales ni datos. La migración de
adopción no tiene rollback destructivo.
