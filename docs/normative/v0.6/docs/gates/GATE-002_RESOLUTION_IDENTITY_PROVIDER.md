# GATE-002 — Resolución: Proveedor de identidad

**Estado:** RESUELTO
**Fecha de resolución:** 2026-07-21
**Decidido por:** Project owner
**Severidad original:** `BLOCKER_FOR_STAGING_AUTH`
**Base normativa:** Paquetería Culiacán v0.6 Full Canonical Sync

---

## 1. Decisión

Se adopta **AuthCenter**, servidor de identidad OIDC propio (.NET, Clean Architecture, desarrollado in-house), como proveedor de identidad de la plataforma Paquetería.

Esta es una variante de la opción `self-hosted OIDC` contemplada en el gate: la plataforma no opera un producto de terceros (Keycloak, Duende), sino código propio bajo control directo del equipo.

### Lo que el gate pedía y su respuesta

| Requisito del gate | Resolución |
|---|---|
| **Provider** | AuthCenter (IdP propio, OIDC estándar). |
| **SLA** | 99.5% mensual comprometido (≈3.6 h de indisponibilidad/mes). Objetivo técnico 99.9% según SLA de plataforma Azure. |
| **Cost** | Infraestructura propia en Azure; sin costo por usuario activo (relevante dado el volumen esperado de usuarios ocasionales). |
| **Data region** | **Azure Mexico Central** — verificado que App Service, base de datos y Key Vault están disponibles en la región. Los datos personales no salen del territorio nacional; **no hay transferencia internacional que declarar** en el aviso de privacidad (insumo para GATE-007). |

---

## 2. Separación de responsabilidades (normativa)

Esta separación es condición de la decisión y no debe alterarse sin un nuevo gate o ADR:

- **AuthCenter autentica.** Responde exclusivamente *"¿quién es esta persona?"*. Emite el claim `sub`, que Paquetería mapea a `identity.users.identity_subject`.
- **Paquetería autoriza.** Toda la autorización de tenant —organizaciones, membresías, roles y capacidades— vive en `organizations.organization_memberships` y en las políticas RLS de Paquetería. El bootstrap de identidad (ADR-017/ADR-031) resuelve membresías consultando la base de Paquetería, no el token.
- **Los claims de `roles`, `permissions` y `applications` que emite AuthCenter se ignoran** para efectos de autorización de tenant en Paquetería.
- **No se replican las organizaciones de Paquetería dentro de AuthCenter.** El modelo multi-tenant de AuthCenter es por aplicación (`ApplicationSystem`); el de Paquetería es por organización con membresías múltiples. Son modelos distintos y deben permanecer separados: mezclarlos rompería el diseño de aislamiento por RLS.

Consecuencia práctica: Paquetería figura como **una** `ApplicationSystem` en AuthCenter; las 20+ organizaciones de Paquetería son invisibles para el IdP.

---

## 3. Requisitos técnicos — estado

### 3.1 Cumplidos (verificados 2026-07-21)

- **OIDC estándar:** discovery (`/.well-known/openid-configuration`), JWKS (`/.well-known/jwks.json`), `/oauth/authorize`, `/oauth/token`; ID tokens con `nonce`, `auth_time`, `email_verified`.
- **Firma asimétrica en access tokens:** migración completa a **RS256**. Los access tokens (`GenerateAccessToken` y `GenerateOAuthAccessToken`) se firman con RSA; Paquetería valida contra la llave pública del JWKS y **no posee material capaz de firmar tokens**. Esto cierra el riesgo de que un consumidor pudiera fabricar tokens válidos.
- **Fallo explícito por configuración:** la aplicación no arranca si falta una llave RSA privada válida; se exige un mínimo de 2048 bits. No existe camino de degradación silenciosa a HMAC.
- **Custodia de llave:** llave privada RSA en **Azure Key Vault**; retirada de `appsettings.Development.json` (sustituida por placeholder).
- **Cobertura de pruebas:** 54/54 pruebas en verde (26 unitarias, 28 de integración), incluyendo validación de un access token OAuth **exclusivamente contra el JWKS publicado** y verificación de que la aplicación no arranca sin RSA.
- **Uso legítimo remanente de HMAC:** `Jwt:SigningKey` se conserva únicamente para tokens internos HS256 (MFA pendiente, cambio forzado de contraseña, magic links), emitidos y consumidos por la propia AuthCenter. Los refresh tokens son valores opacos aleatorios almacenados con hash SHA-256. Este uso no expone material de firma a terceros y por tanto no está afectado por el riesgo anterior.

### 3.2 Pendientes antes de producción

1. **Rotación de llaves (key ring).** Hoy existe una sola llave (`kid = authcenter-key-1`) sin rotación. Se requiere soportar llave actual para firmar más llaves anteriores válidas para verificación, retiradas tras el TTL máximo de los tokens emitidos. Azure Key Vault ya maneja versiones de secretos de forma nativa, lo que reduce el alcance del cambio a configuración, servicio criptográfico, JWKS, autenticación y pruebas. **No bloquea la integración con Paquetería; sí bloquea producción**, porque sin rotación un compromiso de llave obliga a invalidar todas las sesiones simultáneamente.
2. **Rotación de la llave de desarrollo previamente embebida.** La llave privada que estuvo en `appsettings.Development.json` debe considerarse comprometida si el archivo existió en el historial de control de versiones (el placeholder actual no borra los commits anteriores). Debe generarse una llave nueva y verificarse que `.gitignore` cubra los archivos de secretos locales.
3. **Verificación de disponibilidad multi-instancia.** El SLA de plataforma de Azure App Service es 99.95% con dos o más instancias y 99.9% con instancia única; la disponibilidad efectiva es el producto de todos los eslabones (App Service × base de datos × Key Vault). Debe confirmarse que la topología desplegada sostiene el 99.5% comprometido.

---

## 4. Compromisos de operación

Al ser el proveedor de identidad propio, el SLA es una obligación interna y no un contrato con un tercero. Se registran explícitamente:

| Aspecto | Compromiso |
|---|---|
| Disponibilidad | 99.5% mensual (≈3.6 h de margen). |
| Hospedaje | Azure App Service, región Mexico Central. |
| Custodia de secretos | Azure Key Vault. |
| Respuesta a incidentes | Responsable único: project owner, 24/7. |
| Monitoreo | Alertas de caída configuradas. |
| Criticidad | **Dependencia de disponibilidad equivalente a la base de datos**: si AuthCenter está caído, ningún actor (operaciones, negocios, aliados, repartidores) puede autenticarse en Paquetería, aunque la plataforma siga operando. |

**Riesgo aceptado conscientemente:** con un único responsable de guardia, un incidente nocturno prolongado puede consumir el presupuesto mensual de indisponibilidad completo. El compromiso de 99.5% (en lugar de 99.9%) se eligió precisamente para que sea sostenible por una persona.

---

## 5. Impacto en el paquete normativo

- `specs/AI-10_DECISIONS_AND_GATES.yaml`: GATE-002 pasa de `open_decisions` a resuelto (ver parche asociado).
- `decision-log.md`: entrada de resolución.
- **Insumo para GATE-007:** la región de datos queda definida (México, sin transferencia internacional), lo que simplifica el aviso de privacidad.
- **Sin cambios en contratos normativos v0.6.** Esta resolución no modifica AI-02, AI-04, AI-05, AI-06, AI-18 ni ningún contrato funcional. El diseño de Paquetería ya asumía OIDC (ADR-017, ADR-031) y esa suposición se confirma sin alteraciones.

---

## 6. Condiciones de revisión

Esta resolución debe revisarse si ocurre cualquiera de estas circunstancias:

- Un cliente empresarial exige contractualmente un SLA superior al 99.5%.
- El volumen de usuarios o la criticidad operativa hacen insostenible el modelo de responsable único.
- Se requiere certificación formal de seguridad (SOC 2, ISO 27001) que un IdP propio no pueda acreditar.
- Cambia el requisito de residencia de datos.
