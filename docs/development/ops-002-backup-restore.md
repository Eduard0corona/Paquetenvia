# OPS-002 — Backup cifrado y restore drill

## Propósito y alcance

OPS-002 agrega un procedimiento manual y reproducible para respaldar la topología persistente de MVP-0 y demostrar su restauración en infraestructura efímera nueva. El backup lógico incluye PostgreSQL/PostGIS completo y todos los objetos del bucket S3-compatible configurado. El restore drill destruye la fuente antes de crear el target, reinicia PostgreSQL y MinIO después del restore y repite las verificaciones.

Esto no es una solución productiva de disaster recovery. No implementa PITR, replicación, alta disponibilidad, backups programados, retención off-site, selección de proveedor cloud, KMS productivo ni REL-000. No cierra gates pendientes.

## Fuentes autoritativas y exclusiones

PostgreSQL es la fuente transaccional. El dump custom conserva schemas, datos, funciones, extensiones, triggers, RLS/policies, ownership, ACL, default privileges, histories de migrations, auditoría, outbox, tracking y referencias POD.

El bucket privado S3-compatible es la fuente de bytes POD. Se copia lógicamente con el cliente `mc` fijado en Compose. Cada proof se coteja por key, tamaño y SHA-256 sin imprimir esos valores.

Redis se excluye porque es caché, locks y estado reconstruible; el target inicia vacío. Mailpit se excluye porque es un mock SMTP; el target inicia sin mensajes. Tampoco se respaldan `.env.local`, connection strings, passwords, access keys, identities privadas, tokens, signed URLs, certificados privados ni credenciales de ningún proveedor. No se copian volúmenes Docker.

## Formato y cifrado

El payload previo al cifrado tiene esta forma:

```text
payload/
  manifest.json
  object-inventory.json
  postgres/
    database.dump
  object-storage/
    ...
```

`database.dump` usa `pg_dump --format=custom`. El inventario interno contiene keys, tamaños y hashes porque permanece dentro del payload cifrado. El manifest registra formato, backup ID no sensible, timestamps UTC, commit, versiones PostgreSQL/PostGIS, hashes y tamaños, histories de migrations, conteos, fingerprint antes/después y confirmación de quiescence.

El bundle se comprime como tar/gzip y se cifra con `age` para un recipient X25519. El artifact final se llama `paquetenvia-backup-v1-<UTC>-<random>.tar.gz.age`. La identity nunca se copia al bundle, al reporte, al repositorio ni a `.env.example`.

Antes de extraer, el restore autentica el archivo con `age`, valida el SHA-256 externo, limita el archivo a 10 000 entradas y 10 GiB expandidos, aplica una allowlist y rechaza rutas absolutas, `..`, symlinks, hardlinks y entradas especiales.

## Manejo de recipient e identity

Genere una identity de prueba fuera del repositorio:

```powershell
$identity = Join-Path $env:TEMP "paquetenvia-ops002-test-identity.txt"
age-keygen -o $identity
$recipient = age-keygen -y $identity
```

En Unix, restrinja la identity:

```bash
chmod 600 "$identity"
```

`backup-environment.ps1` recibe solamente el recipient público. `restore-environment.ps1` recibe la ruta de la identity mediante `-IdentityFile`, rechaza identities dentro del repositorio y, en Unix, rechaza permisos de grupo u otros. No pase el contenido de la identity como argumento ni lo escriba en logs, artifacts o variables de salida.

## Preflight y quiescence

Antes del backup:

1. Bloquee nuevas mutaciones operativas.
2. Detenga API y Worker que puedan escribir.
3. Espere a que terminen las operaciones en curso.
4. Verifique outbox y POD.
5. Ejecute el backup con `-ConfirmQuiesced`.
6. Verifique el artifact y su reporte redactado.
7. Reanude servicios sólo después de completar la verificación.

El script valida Docker/Compose, política estática, PostgreSQL y MinIO saludables, bucket privado, output nuevo fuera del repositorio, cero mensajes `PROCESSING` en ambos lanes y cero sesiones POD en `CREATED`, `UPLOADED`, `VALIDATING` o `READY`.

La consistencia DB/S3 se protege con una ventana quiesced y un fingerprint agregado antes y después del dump/mirror. El fingerprint cubre órdenes, eventos, acceptances, assignments, proofs, sesiones POD, tracking, auditoría y ambos outbox sin exponer sus datos de entrada. Si cambia, el script responde `SOURCE_CHANGED_DURING_BACKUP`, elimina staging y no produce artifact.

Esta verificación es apropiada para MVP-0; no sustituye snapshots coordinados, PITR o infraestructura administrada.

## Crear y verificar un backup

El output debe ser una ruta nueva y externa al repositorio:

```powershell
pwsh ./tools/backup-environment.ps1 `
  -ComposeFile ./deploy/docker-compose.yml `
  -EnvironmentFile ./deploy/.env.local `
  -ProjectName paquetenvia-local `
  -OutputDirectory (Join-Path $env:TEMP "paquetenvia-backup-output") `
  -Recipient $recipient `
  -ConfirmQuiesced `
  -TimeoutSeconds 900
```

El script ejecuta `pg_restore --list`, valida entradas críticas del dump, calcula SHA-256, verifica privacidad del bucket antes/después del mirror y coteja cada proof con sus bytes. Si se proporciona `-TestIdentityFile` en un entorno de prueba, también realiza una verificación controlada de descifrado antes de publicar el artifact.

`-Database` es opcional y permite seleccionar explícitamente una base de datos del mismo clúster cuando `POSTGRES_DB` se reserva como bootstrap. El nombre debe ser un identificador PostgreSQL seguro; si se omite, se usa `POSTGRES_DB`.

El reporte sidecar `<artifact>.report.json` es redactado: sólo contiene conteos, tamaños, timestamps, hashes, flags, versiones, commit y duraciones.

Para verificar manualmente la autenticación sin extraer:

```powershell
$check = Join-Path $env:TEMP "paquetenvia-ops002-check.tar.gz"
age --decrypt --identity $identity --output $check <artifact.tar.gz.age>
tar -tzf $check | Out-Null
Remove-Item -LiteralPath $check -Force
```

No imprima el listado: contiene keys internas.

## Restaurar en un target limpio

Use un proyecto Compose, bucket y base distintos de la fuente. La base solicitada no debe existir y el bucket debe estar vacío:

```powershell
pwsh ./tools/restore-environment.ps1 `
  -Artifact <artifact.tar.gz.age> `
  -IdentityFile $identity `
  -ComposeFile ./deploy/docker-compose.yml `
  -EnvironmentFile <target.env> `
  -ProjectName paquetenvia-restore-target `
  -RestoredDatabase paquetenvia_restored `
  -ConfirmRestore `
  -ConfirmSourceDestroyed `
  -TimeoutSeconds 1200
```

El restore falla antes de mutar si la identity, el artifact, el manifest, el dump o un objeto no validan. No acepta `-Force`, no usa `pg_restore --clean`, no borra bases/buckets existentes y no restaura encima del proyecto o base fuente.

El orden operativo es:

1. Validar SHA-256 externo y autenticación `age`.
2. Validar tar, allowlist, límites y hashes internos.
3. Probar `pg_restore --list` con la imagen PostgreSQL fijada.
4. Verificar proyecto, base y bucket target.
5. Levantar la infraestructura target.
6. Aplicar AI-06/AI-18 y migrations en la base bootstrap para recrear roles globales normativos.
7. Crear una base vacía y restaurar con `--single-transaction --exit-on-error`.
8. Restaurar objetos con `mc mirror` y forzar acceso anónimo `none`.
9. Ejecutar baseline, migrations, datos, RLS, append-only y cotejo DB/S3.
10. Reiniciar PostgreSQL y MinIO conservando volúmenes y repetir verificaciones.
11. Confirmar Redis y Mailpit vacíos.
12. Emitir un reporte redactado y eliminar plaintext.

Para aplicar el baseline sin aceptar un estado parcial creado por scripts de inicialización de la imagen, el restore crea desde `template0` una base bootstrap temporal de nombre aleatorio y la elimina inmediatamente después. Nunca limpia ni reutiliza una base preexistente para este paso.

Los logins externos y sus passwords no están en el backup. El operador debe proveer previamente los logins externos requeridos. El drill usa el mismo nombre de admin sintético en source/target, con passwords distintos, y recrea los roles normativos `NOLOGIN` mediante el baseline.

## Verificación posterior

`database-baseline.ps1 Assert` comprueba PostgreSQL 18, PostGIS 3.6, ubicación de extensiones, schemas, funciones, flags/memberships de roles, ownership, FORCE RLS, policies, triggers, revocaciones, grants, default privileges e histories.

`tests/fixtures/ops-002/assert-restored.sql` comprueba conteos, estados, timestamps, aceptación, nueve eventos, assignment, POD, tracking hash, auditoría, outbox y tenant señuelo. Como `paqueteria_app`, valida lectura del tenant A, bloqueo del tenant B, falla cerrada sin contexto y reinicio del contexto transaccional. El harness también crea dos logins `NOBYPASSRLS` con passwords aleatorios sólo en memoria, los conecta por TCP, repite el aislamiento de ambos tenants y los elimina al terminar. También demuestra que UPDATE/DELETE siguen rechazados para eventos, acceptances, proofs y audit logs.

Los objetos restaurados se vuelven a espejar a staging, se recalculan tamaño/SHA-256/digest agregado y se cotejan uno a uno con `custody.proofs`. El bucket debe permanecer privado y no puede contener objetos faltantes ni adicionales.

## Restore drill canónico

```powershell
pwsh ./tools/test-backup-restore.ps1 -CI
```

El drill crea source y target aislados con puertos, proyectos, volúmenes y credenciales sintéticos; crea desde `template0` una base de datos fuente limpia; aplica baseline/migrations; inserta la fixture; sube tres objetos reales; ejecuta pruebas negativas; crea el backup; destruye source con `down --volumes --remove-orphans`; verifica ausencia de sus recursos; restaura target; reinicia; verifica y limpia.

Las 35 pruebas negativas obligatorias y ocho guardas adicionales (43 comprobaciones en total) cubren preflight, recipient/identity, unhealthy/quiescence, cambios concurrentes, fallos de dump/mirror/cifrado, ausencia de sentinels visibles, integridad/tampering, limpieza/cancelación, timeout, target no limpio, exclusiones, restart, RLS, append-only, ownership/grants, traversal, rutas absolutas, enlaces, allowlist, cantidad y tamaño expandido.

## RPO y RTO

En el drill quiesced:

```text
observed_data_loss = 0
```

Esto es una medición sintética, no un RPO productivo. La capacidad actual es manual:

```text
current_guaranteed_rpo = NOT_ESTABLISHED
```

El RPO operacional dependería de la antigüedad del último backup válido. No se aprueba aquí un objetivo de 24 h, 1 h ni otro valor.

El RTO medido va desde el inicio del restore validado hasta infraestructura restaurada, reiniciada y con todas las verificaciones verdes. Se registra en `measured_rto`; el reporte también separa `backup_duration`, `restore_duration`, `validation_duration` y `total_drill_duration`.

### Propuesta pendiente de aprobación del owner

La frecuencia, retención, ubicación off-site, RPO/RTO objetivo y SLA requieren una decisión posterior del owner. OPS-002 no cierra esa aprobación.

## Diagnóstico y cleanup

Estado redactado:

```powershell
pwsh ./tools/local-environment.ps1 Status `
  -EnvironmentFile <target.env> `
  -ProjectName <target-project>
```

No publique logs Docker completos: pueden contener información operacional. Use conteos/estado y el reporte redactado.

Para limpiar únicamente un proyecto sintético conocido:

```powershell
docker compose `
  --project-name <synthetic-project> `
  --env-file <synthetic.env> `
  --file ./deploy/docker-compose.yml `
  down --volumes --remove-orphans
```

Confirme el nombre exacto antes de ejecutar. No use este comando contra un entorno real ni borre artifacts externos sin confirmación separada.

Backup y restore crean staging aleatorio bajo el temporal del sistema, con permisos restrictivos y cleanup en `finally`. La eliminación lógica no garantiza secure erase físico en SSD, filesystems copy-on-write o runners administrados.

## Riesgos, limitaciones y rollback

La ventana quiesced es manual. No hay transacción distribuida PostgreSQL/S3. El fingerprint detecta cambios, pero no reemplaza snapshots coordinados. El artifact depende de custodia externa correcta de la identity y de almacenamiento duradero, aspectos no implementados aquí. El límite de 10 GiB expandido corresponde al drill MVP-0, no a capacidad productiva aprobada.

Rotación de keys, retención, off-site, alertamiento, programación y política legal permanecen pendientes. GATE-003, GATE-007, GATE-010, GATE-011, GATE-013, GATE-014, GATE-015, RTM-001-CUSTOMER-SUPPORT-ROLE e Issue #5 siguen abiertos.

Rollback de código: retire `backup-environment.ps1`, `restore-environment.ps1`, `test-backup-restore.ps1`, el common compartido, fixtures OPS-002, el job `Validate backup and restore` y este runbook. API, Worker, web, módulos, schema normativo y datos productivos permanecen intactos. No ejecute DDL ni borre backups externos o keys del operador.
