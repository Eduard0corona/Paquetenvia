# Entorno local

Este directorio contiene exclusivamente la infraestructura local de FND-002:
PostgreSQL con PostGIS y pgcrypto, Redis, MinIO y Mailpit. No contiene servicios
de la aplicación ni representa una topología productiva.

Inicio rápido desde la raíz del repositorio:

```powershell
Copy-Item .\deploy\.env.example .\deploy\.env.local
pwsh .\tools\local-environment.ps1 Up
pwsh .\tools\local-environment.ps1 Smoke
```

La guía completa de operación, persistencia, puertos, diagnóstico y limpieza está
en [docs/development/local-environment.md](../docs/development/local-environment.md).
