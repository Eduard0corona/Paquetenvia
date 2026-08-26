# Entorno integrado para pruebas manuales

Este flujo levanta la infraestructura existente de FND-002, aplica DBA-001,
crea exclusivamente datos sintéticos y ejecuta API, Worker y web como procesos
del host. Está diseñado para una sesión guiada de 10 a 15 minutos y no habilita
proveedores, credenciales ni datos reales.

## Requisitos y límites de seguridad

- SDK .NET `10.0.101`, Node `24.13.0`, pnpm `11.15.1`, PowerShell 7 y Docker.
- `Authentication:Provider=Mock` sólo bajo `Development`.
- `IdentityBootstrap:Provider=PostgreSql`; roles, tenant y MFA no se aceptan
  desde headers arbitrarios.
- El portal `/dev` requiere simultáneamente `NODE_ENV=development` y
  `PAQUETERIA_DEV_PORTAL_ENABLED=true`; de otro modo responde 404.
- `.local/` contiene estado, PIDs, logs y secretos runtime locales ignorados.
  `PrintAccess` nunca muestra passwords de PostgreSQL, Redis o MinIO.
- Todo nombre, contacto, teléfono, dirección y objeto del seed es sintético.

## Recorrido de 10–15 minutos

Desde la raíz del repositorio:

```powershell
pwsh ./tools/dev-platform.ps1 Doctor
pwsh ./tools/dev-platform.ps1 Bootstrap
pwsh ./tools/dev-platform.ps1 Start
pwsh ./tools/dev-platform.ps1 Status
pwsh ./tools/dev-platform.ps1 PrintAccess
```

`Bootstrap` es idempotente: sintetiza `deploy/.env.local` cuando falta, reutiliza
`tools/local-environment.ps1 Up`, espera healthchecks, crea una base de aplicación
local aislada del database de health de FND-002, aplica allí el baseline y las
migraciones oficiales, crea logins locales `NOINHERIT/NOBYPASSRLS` y ejecuta el
seeder con guardas de Development y opt-in explícito. También restaura las
dependencias fijadas y construye API/Worker secuencialmente antes de iniciarlos.
Ejecutarlo otra vez no crea duplicados.

Abra `http://127.0.0.1:3000/dev`. Active primero **Synthetic dispatcher (MFA)**
y navegue a Operations. Después vuelva a `/dev`, cambie a **Synthetic own
driver** y abra Driver PWA. El cambio elimina ambas sesiones en memoria antes de
instalar la nueva; no guarda tokens.

Para crear una orden mediante contratos reales de API:

```powershell
pwsh ./tools/dev-platform.ps1 Scenario -Name FreshOrder
```

El escenario ejecuta `quote -> order`, publica `orders.created`, espera al Worker
y devuelve sólo `publicId`, estado, URL de Operations y resumen de notificaciones.
No imprime payloads. `PrintAccess` muestra además una muestra de tracking emitida
por el servicio interno existente para la orden entregada del seed.

Compruebe que la orden aparece en Operations sin recargar para observar la señal
realtime y que NTF-001 termina su notificación IN_APP. El seed deja un conductor
propio activo y elegible, áreas y tarifa deterministas. `FreshOrder` queda
disponible para despacho; no salta la máquina de estados ni escribe una orden
funcional directamente en PostgreSQL.

Como conductor, abra sus stops, active el modo offline del navegador, vuelva a
online y confirme la resincronización REST. Abra después la URL de tracking que
muestra `PrintAccess` y revise el historial entregado. La organización decoy no
debe aparecer en Operations ni Driver. Mailpit (`http://127.0.0.1:8025`) y MinIO
(`http://127.0.0.1:9001`) son sólo superficies auxiliares: revíselas cuando el
flujo ejercitado produzca correo u objetos de prueba.

Para revisar diagnósticos sin mostrar variables de entorno:

```powershell
pwsh ./tools/dev-platform.ps1 Logs
pwsh ./tools/dev-platform.ps1 Logs -Component Api
pwsh ./tools/dev-platform.ps1 Logs -Component Worker
pwsh ./tools/dev-platform.ps1 Logs -Component Web
```

Detenga sólo los procesos host, conservando contenedores, volúmenes y seed:

```powershell
pwsh ./tools/dev-platform.ps1 Stop
pwsh ./tools/dev-platform.ps1 Start
```

El segundo `Start` demuestra persistencia. Para borrar únicamente los recursos
Compose de este proyecto y `.local/`:

```powershell
pwsh ./tools/dev-platform.ps1 Reset
# automatización controlada:
pwsh ./tools/dev-platform.ps1 Reset -Force
```

`Reset` confirma antes de destruir, verifica propiedad mediante el reset de
FND-002 y no mata procesos por puerto.

## Datos deterministas

- Organización principal: `11111111-1111-1111-1111-111111111111`.
- Organización decoy: `33333333-3333-3333-3333-333333333333`.
- Dispatcher sintético con MFA: credential `local-dispatcher-mfa`.
- Conductor propio sintético: credential `active-driver`.
- Ciudad, área de servicio, zona, tarifa, documento y perfil de conductor con
  IDs estables.

El acceso a la organización decoy no se concede a los perfiles locales. Los
prerrequisitos privilegiados siguen el patrón OPS-001; quotes y orders se crean
por API para preservar RLS, idempotencia, auditoría y outbox.

## Pruebas automatizadas

La aceptación estática verifica las guardas, rechazo fuera de Development,
opt-in, allowlist de identidad, ausencia de PII aparente y que DevSeed no agregue
PackageReferences:

```powershell
pwsh ./tools/test-dev-platform.ps1
```

La aceptación integral limpia ejecuta dos bootstraps, arranque, healthchecks,
`FreshOrder`, stop/start con persistencia y reset acotado:

```powershell
pwsh ./tools/test-dev-platform.ps1 -Full
```

Foundation CI ejecuta la variante `-CI` dentro del job existente de
infraestructura; el workflow conserva sus 13 jobs.

## Limitaciones actuales

- No hay provider externo, entrega real, PII real, piloto, deployment ni go-live.
- El portal no sustituye `/login` ni es un mecanismo de autenticación productivo.
- Realtime usa backplane `InProcess`, adecuado sólo para este host local único.
- `FreshOrder` no inventa un endpoint para emitir tracking; la muestra estable
  del seed utiliza el servicio de aplicación interno existente.
- `Stop` conserva infraestructura; use `Reset` sólo cuando quiera eliminar los
  volúmenes locales del proyecto.
