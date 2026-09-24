# SCL-001: API y Worker stateless / distributed-ready

SCL-001 elimina el estado de proceso que impedía agregar, reemplazar o
reiniciar réplicas de API y Worker. Dos piezas concentran el trabajo: el key
ring de ASP.NET Data Protection deja de vivir en el nodo y pasa a PostgreSQL, y
cada claim de outbox queda atribuido a la réplica que lo hizo. La seguridad del
claim no cambia: sigue garantizada por `FOR UPDATE SKIP LOCKED` más el lease
token.

## Key ring compartido

`platform.data_protection_keys` es propiedad de `paqueteria_migrator`, tiene RLS
`ENABLED` y `FORCED`, y en runtime es *append-only*: `paqueteria_app` y
`paqueteria_worker` reciben `SELECT, INSERT` y un trigger `BEFORE UPDATE OR
DELETE` rechaza cualquier reescritura. Ninguna réplica puede invalidar el
material de otra.

`PostgreSqlXmlRepository` implementa `IXmlRepository`. Los logins de runtime son
`NOINHERIT`, así que el rol canónico se asume por transacción
(`SET LOCAL ROLE`), igual que en los stores de outbox.

## Carril de migración

El key ring viaja en su propio carril EF, `PlatformDataProtectionDbContext`, con
historia dedicada en `platform.__ef_migrations_history_platform`.

El carril pertenece al migrador canónico de despliegue,
`ModuleMigrationCoordinator`, no a los fixtures de prueba:

- `VerifySources` verifica que la migración exista, declare su identificador y
  no sea destructiva. El rollback falla cerrado con
  `SCL001_SCHEMA_DOWNGRADE_NOT_SUPPORTED`, porque eliminar el ring invalidaría
  todo payload protegido por cualquier réplica;
- `PlanAsync` reporta `PENDING`, `APPLIED` o `DRIFT` para el módulo
  `DataProtection`;
- `ApplyAsync` ejecuta la migración real de `PlatformDataProtectionDbContext`
  como `paqueteria_migrator`;
- `AssertAsync` exige historia aplicada, propiedad de la tabla de historia, y
  además que el ring exista, fuerce RLS y conserve `SELECT` e `INSERT` para
  ambos roles de runtime.

Una base limpia procesada sólo por el migrador canónico queda con la migración,
la tabla y los grants. El conteo de módulos del migrador es 10.

## Perfil `ScaleReady`

El valor por defecto de `DataProtection:Provider` sigue siendo `Disabled` en
`appsettings.json` de API y Worker, lo que preserva el comportamiento
single-instance y el key ring local del framework.

SCL-001 agrega **un** perfil versionado y reproducible que sí activa el ring
compartido, usando el mecanismo de entorno de configuración ya existente:

| Host | Archivo | `ConnectionStringName` | `RuntimeRole` |
| --- | --- | --- | --- |
| API | `src/Paqueteria.Api/appsettings.ScaleReady.json` | `Paqueteria` | `paqueteria_app` |
| Worker | `src/Paqueteria.Worker/appsettings.ScaleReady.json` | `PaqueteriaWorker` | `paqueteria_worker` |

Ambos resuelven el mismo `ApplicationName` (`Paquetenvia`), que es el
discriminador del ring. El perfil sólo se selecciona cuando el despliegue pide
explícitamente el entorno `ScaleReady`: una configuración normal de `Production`
nunca habilita el modo distribuido de forma implícita.

`DataProtectionHealthCheck` está etiquetado `ready`. Con
`Provider=PostgreSql`, la réplica reporta `unhealthy` si no puede asumir su rol
canónico, si falta el carril de migración o la tabla, o si perdió `SELECT` o
`INSERT` sobre el ring. Una réplica que no alcanza el ring compartido nunca
reporta ready, porque atendería con material de clave propio.

### El perfil no es autorización productiva

> **An external key-encryption protector is required before productive distributed Data Protection activation.**

El ring XML en PostgreSQL todavía no tiene un `IXmlEncryptor` / KEK externo: el
material queda protegido sólo por los grants y RLS de la base. `ScaleReady` es
un perfil de validación de escalado, no una autorización de producción, y no
provisiona recursos de nube, certificados ni secretos. La activación productiva
del Data Protection distribuido queda bloqueada hasta que exista un protector de
clave externo aprobado.

## Identidad de réplica en el outbox

`locked_by` está acotado a 100 caracteres por contrato.
`InstanceIdentity.QualifyWorkerId` combina el worker id configurado —
compartido por el despliegue — con la identidad de la réplica
(`PAQUETERIA_INSTANCE_ID`, o host y proceso).

Cuando ambos segmentos caben, el valor queda legible
(`ntf001-local:<instancia>`). Cuando no caben, el worker id se recorta a 83
caracteres y la réplica se representa con un digest estable de 16 caracteres de
su identidad. La identidad de la réplica **siempre** sobrevive: dos réplicas con
el mismo worker id largo nunca colapsan en el mismo valor, y el resultado es
determinista para las mismas entradas.

Esto no cambia la semántica de propiedad del lease: `locked_by` es atribución,
el dueño sigue siendo quien tiene el lease token.

## Validación

| Criterio de aceptación | Prueba |
| --- | --- |
| El migrador canónico aplica el carril desde una base limpia | `Scl001DataProtectionMigrationLaneTests` |
| Plan/assert detectan carril ausente o con drift | `Scl001DataProtectionMigrationLaneTests` |
| Dos réplicas API atienden indistintamente sin perder estado | `Scl001ScaleReadyProfileTests`, `Scl001StatelessDistributedContractTests` |
| Readiness falla sin el key ring compartido | `Scl001ScaleReadyProfileTests` |
| Dos Workers no duplican efectos | `Scl001TwoWorkerNoDuplicateEffectTests` |
| Reinicio de nodo no pierde jobs confirmados | `Scl001StatelessDistributedContractTests` |
| El worker id largo preserva la identidad de réplica | `InstanceIdentityTests` |

`Scl001TwoWorkerNoDuplicateEffectTests` ejercita el camino real del procesador
con dos hosts de Worker y un sink instrumentado que cuenta efectos externos, no
claims: el Worker A produce el efecto y muere antes de liquidar, el lease
vence, el Worker B procesa la misma operación lógica reutilizando la misma
clave de idempotencia de producción, y queda exactamente un efecto con estado
durable liquidado con éxito.

## Fuera de alcance

No se agregaron recursos de Azure, Key Vault, certificados ni provisión de
secretos productivos. Tampoco se rediseñó el outbox: el contrato de clave de
idempotencia (`NotificationIdempotencyKey`) se conserva sin cambios.
