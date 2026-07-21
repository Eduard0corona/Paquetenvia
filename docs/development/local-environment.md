# Entorno local reproducible

FND-002 proporciona dependencias locales para desarrollo y pruebas. El Compose
no ejecuta Paquetenvia.Api, Paquetenvia.Worker ni el cliente web, no aplica el
esquema normativo AI-06 y no pretende describir producción.

## Requisitos y preparación

- Docker Engine con Docker Compose V2.
- PowerShell 7 (`pwsh`) en Windows, macOS o Linux.
- Puertos configurados libres en la interfaz loopback.

Desde cualquier directorio, indica rutas absolutas o ejecuta desde la raíz:

```powershell
Copy-Item .\deploy\.env.example .\deploy\.env.local
pwsh .\tools\local-environment.ps1 Up
```

`.env.local` está ignorado por Git. Sus valores son sintéticos y solo sirven
para desarrollo; cambia puertos o credenciales allí sin modificar el ejemplo.
No reutilices esas credenciales fuera del equipo local.

## Servicios

| Servicio | Imagen fijada | Puerto interno | Puerto local configurable | Volumen | Healthcheck |
| --- | --- | ---: | --- | --- | --- |
| PostgreSQL + PostGIS | `postgis/postgis:17-3.5` | 5432 | `POSTGRES_HOST_PORT` (5432) | `postgres_data` | `pg_isready` |
| Redis con AOF | `redis:8.2.7-alpine` | 6379 | `REDIS_HOST_PORT` (6379) | `redis_data` | `redis-cli PING` autenticado |
| MinIO API | `minio/minio:RELEASE.2025-09-07T16-13-09Z` | 9000 | `MINIO_API_HOST_PORT` (9000) | `minio_data` | `mc ready local` |
| MinIO consola | misma imagen | 9001 | `MINIO_CONSOLE_HOST_PORT` (9001) | `minio_data` | mismo servicio |
| SMTP mock | `axllent/mailpit:v1.30.3` | 1025 | `MAIL_SMTP_HOST_PORT` (1025) | `mailpit_data` | `/mailpit readyz` |
| Mailpit UI/API | misma imagen | 8025 | `MAIL_UI_HOST_PORT` (8025) | `mailpit_data` | mismo servicio |

El bootstrap de MinIO usa `minio/mc:RELEASE.2025-08-13T08-35-41Z`. Todas las
imágenes conservan además un digest `sha256` en el Compose. Se eligió PostgreSQL
17 para mantener la ruta de datos tradicional comprobada por la imagen PostGIS;
Redis Alpine reduce el tamaño sin cambiar su persistencia AOF; las versiones de
MinIO, `mc` y Mailpit son releases explícitos, no canales flotantes.

`minio-init` es un contenedor efímero e idempotente: espera a MinIO, crea el
bucket configurado si falta y finaliza con código cero. Todos los puertos se
publican solo en loopback. La red bridge y los nombres de recursos quedan
acotados por `COMPOSE_PROJECT_NAME`.

La cuenta `POSTGRES_USER` solo administra el bootstrap local. No representa los
roles runtime de la API o del Worker, no es una credencial productiva y será
reemplazada por el modelo posterior definido por AI-18. `MINIO_BUCKET` es una
convención local reversible para el smoke test, no una taxonomía productiva.

La imagen de PostGIS seleccionada es `linux/amd64`. Docker Desktop puede usar
emulación en Apple Silicon; si el rendimiento local resulta insuficiente, no
cambies la imagen fijada sin una revisión explícita de compatibilidad PostGIS.

## Variables y accesos locales

| Variable | Finalidad |
| --- | --- |
| `COMPOSE_PROJECT_NAME` | Aísla nombres de contenedores, red y volúmenes. |
| `POSTGRES_HOST_PORT`, `POSTGRES_DB`, `POSTGRES_USER`, `POSTGRES_PASSWORD` | Puerto y bootstrap local de PostgreSQL. |
| `REDIS_HOST_PORT`, `REDIS_PASSWORD` | Puerto y autenticación local de Redis. |
| `MINIO_API_HOST_PORT`, `MINIO_CONSOLE_HOST_PORT` | Puertos S3 y consola. |
| `MINIO_ROOT_USER`, `MINIO_ROOT_PASSWORD`, `MINIO_BUCKET` | Bootstrap local de MinIO. |
| `MAIL_SMTP_HOST_PORT`, `MAIL_UI_HOST_PORT` | Puertos SMTP y UI/API de Mailpit. |

Con los valores de ejemplo, la consola MinIO está en
`http://127.0.0.1:9001` y usa `MINIO_ROOT_USER` / `MINIO_ROOT_PASSWORD` de
`.env.local`. Mailpit se inspecciona en `http://127.0.0.1:8025`; aplicaciones
locales pueden enviar únicamente al mock `127.0.0.1:1025`. Si cambias un puerto,
la URL cambia de forma correspondiente.

## Operación diaria

```powershell
# Crear o reconciliar; puede repetirse sin perder datos
pwsh .\tools\local-environment.ps1 Up

# Mostrar contenedores y health
pwsh .\tools\local-environment.ps1 Status

# Mostrar las últimas 200 líneas de logs
pwsh .\tools\local-environment.ps1 Logs

# Reiniciar servicios conservando volúmenes
pwsh .\tools\local-environment.ps1 Restart

# Probar APIs, extensiones, persistencia y diagnósticos
pwsh .\tools\local-environment.ps1 Smoke

# Detener y borrar contenedores/red, conservando volúmenes
pwsh .\tools\local-environment.ps1 Down
```

Los scripts resuelven sus rutas con respecto al repositorio. También aceptan
`-ComposeFile`, `-EnvironmentFile` y `-ProjectName` para ejecuciones aisladas.
`Up` comprueba primero la configuración y los puertos; nunca mata procesos para
liberarlos.

Los comandos Compose directos equivalentes son:

```powershell
docker compose `
  --env-file .\deploy\.env.local `
  --file .\deploy\docker-compose.yml `
  config --quiet

docker compose `
  --env-file .\deploy\.env.local `
  --file .\deploy\docker-compose.yml `
  up --detach --wait

docker compose `
  --env-file .\deploy\.env.local `
  --file .\deploy\docker-compose.yml `
  down
```

## Persistencia, reset y limpieza

`Down` es la operación normal y no destructiva. PostgreSQL, el AOF de Redis,
los objetos de MinIO y la base SQLite de Mailpit sobreviven tanto a reinicios
individuales como a un ciclo `Down` / `Up`.

`Reset` es deliberadamente destructivo y elimina solo los recursos etiquetados
del proyecto Compose indicado. Sin `-Force` exige escribir `RESET`:

```powershell
pwsh .\tools\local-environment.ps1 Reset
# Automatización consciente del borrado:
pwsh .\tools\local-environment.ps1 Reset -Force
```

Al terminar, el script verifica que no queden contenedores, red ni volúmenes de
ese proyecto. Nunca uses `docker system prune` para esta tarea: su alcance es
mucho mayor que FND-002.

## Qué valida el smoke test

`tools/test-local-environment.ps1` comprueba la configuración renderizada antes
de levantar recursos y rechaza `latest`, imágenes sin digest, modo privilegiado,
red host, puertos no-loopback, Docker socket, montajes normativos, servicios de
aplicación o volúmenes ausentes. Después:

1. levanta el entorno dos veces y espera healthchecks;
2. confirma `postgis` en `public` y `pgcrypto` en `extensions`;
3. escribe y lee datos reales por PostgreSQL, Redis, MinIO y SMTP/API de Mailpit;
4. confirma persistencia después de reiniciar cada servicio y después de
   `Down` / `Up`;
5. detiene Redis intencionalmente y exige un error de health con diagnóstico;
6. ocupa un puerto efímero y exige un error de colisión legible;
7. elimina los datos de prueba que creó.

En CI, `-CI` parte de un proyecto aislado y termina con `down --volumes`, incluso
si falla. La limpieza final verifica que no sobrevivan recursos etiquetados.

## Diagnóstico

- **Docker no disponible:** inicia Docker Desktop o el daemon y repite. El script
  no intenta instalarlo ni cambiar su configuración.
- **Windows/WSL2:** Docker Desktop debe completar su backend Linux (normalmente
  WSL2) antes de ejecutar `Up`; confirma primero con `docker version`. Instalar o
  habilitar WSL2 puede exigir elevación y reinicio del equipo. No uses modo de
  contenedores Windows porque las cuatro imágenes son Linux.
- **Puerto ocupado:** cambia el valor `*_HOST_PORT` en `.env.local`; el mensaje
  identifica el puerto y ningún proceso se detiene automáticamente.
- **Servicio unhealthy:** ejecuta `Status` y `Logs`. El smoke imprime `ps` y las
  últimas 100 líneas antes de fallar.
- **Credenciales cambiadas tras el primer arranque:** los entrypoints no vuelven
  a inicializar volúmenes existentes. Exporta lo necesario, ejecuta `Reset` y
  vuelve a levantar.
- **`minio-init` exited (0):** es el estado esperado después de crear o verificar
  el bucket.

## Backup y exportación local

Antes de operaciones destructivas, elige una estrategia explícita por servicio:

- PostgreSQL: `pg_dump` desde el contenedor hacia un archivo fuera del volumen.
- Redis: fuerza `SAVE`/`BGREWRITEAOF`, detén el servicio y copia `dump.rdb` o el
  directorio AOF desde el volumen.
- MinIO: usa `mc mirror` hacia una ruta o bucket de respaldo.
- Mailpit: detén Mailpit y copia `/data/mailpit.db`, o exporta los mensajes que
  necesites mediante su API.

Los backups, restauraciones y retención productiva no están automatizados en
FND-002. No guardes exportaciones ni credenciales en Git.

Para recuperar datos, crea un entorno limpio con `Up` y restaura cada exportación
con su herramienta nativa (`psql`/`pg_restore`, copia controlada del AOF/RDB,
`mc mirror` o la base/API de Mailpit). Prueba la recuperación antes de borrar el
respaldo. Nunca reemplaces archivos dentro de un volumen mientras su servicio
está escribiendo.

## Rollback y trabajo futuro

El rollback no requiere revertir la línea base normativa: ejecuta `Reset -Force`
para retirar los recursos locales y revierte los commits de FND-002. Un `Down`
solo pausa el entorno y conserva los datos, por lo que no constituye rollback
completo.

Quedan para tareas posteriores las migraciones y tablas funcionales, AI-06,
AI-18, roles runtime, RLS, tenancy, autenticación, integración de API/Worker,
flujos POD/antivirus, proveedores productivos, TLS, backups automatizados y
despliegue. Ninguno de esos límites se adelanta aquí.

## Límites de seguridad

Las imágenes están fijadas por versión y digest; no se usa `latest`. Los
contenedores no son privilegiados, no montan el socket Docker ni artefactos de
`docs/normative/v0.6/`, y usan `no-new-privileges`. El acceso por loopback y las
credenciales sintéticas reducen exposición accidental, pero este entorno no
incluye TLS, rotación de secretos, alta disponibilidad ni hardening productivo.
