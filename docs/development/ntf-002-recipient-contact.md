# NTF-002: contacto del destinatario para avisos (correo opcional y llave de avisos)

Ítem AI-08 **NTF-002**, PR-1 de la secuencia de avisos al destinatario
([`ntf-recipient-notices.md`](ntf-recipient-notices.md)). Decisión: `NTF-RECIPIENT-NOTICES-2026-10-20` (E4 "Los 4 del
plan (Recommended)": "se agrega como campo opcional en la nueva orden"; E4-P2 "Llave propia para avisos
(Recommended)"; E4-P17 "Dos copias, como el teléfono (Recommended)"). ADR:
[`ADR-NTF-002-RECIPIENT-NOTICES.md`](../adr/ADR-NTF-002-RECIPIENT-NOTICES.md). Contratos: AI-05
`x-pilot-contract-deltas` NTF-RECIPIENT-NOTICES (parte NTF-002), AI-06 `locations.locations`, AI-24
`recipient_notices.contact`, AI-07 `create_order.recipient_notices.email`.

**Apagado e inerte.** Nada se envía. La configuración del piloto no cambia: `Locations__NoticeKeyProtector` no existe
en `deploy/azure/pilot` y vale `Disabled`, así que ninguna fila recibe copias de avisos hasta AI-08 NTF-008. Ningún dato
real entra antes de GATE-007 (PILOT-REAL-PEOPLE).

## Qué cambia

| Pieza | Dónde |
| --- | --- |
| `DestinationAddressInput` (AI-05): el destino de `createQuote` es `AddressInput` más `email` opcional; el origen sigue siendo `AddressInput` | `Pricing.Endpoints/QuoteEndpoints.cs` |
| `RecipientEmailPolicy`: máximo 254 caracteres como se escribió; se recorta el espacio ASCII de los extremos; luego ASCII imprimible sin espacios, una sola `@` tras una parte local no vacía y un dominio con un punto que tenga un carácter antes y otro después; el dominio va en minúsculas y la parte local se conserva | `Paqueteria.Application/Contacts/RecipientEmailPolicy.cs` (el patrón de AI-05 lo fija una prueba) |
| Hash de la cotización y huella de la Idempotency-Key: `"email"` es el último miembro de `destination` solo cuando viene; sin correo los bytes son idénticos a los de antes | `PostgreSqlQuoteService.ComputeInputHash` |
| Columnas de Locations y sus checks, con el DDL de AI-06 | carril Locations `20261020000100_AddRecipientNoticeContact` |
| Correo bajo la llave PII (propósito `locations.email`) | `AzureKeyVaultLocationPiiProtector` (y el mock) |
| Copias de avisos del teléfono y del correo bajo `recipient-notice-kek` (propósitos `locations.notice_phone` y `locations.notice_email`), solo en el destino | `ILocationNoticeContactSealer` y sus tres modos |
| Sellador de la llave de avisos que solo envuelve | `Paqueteria.Infrastructure/Security/Pii/RecipientNoticeKey.cs` |
| Campo "Correo del destinatario (opcional)" en el paso "Dónde" | `apps/web/src/operations/components/create-order-wizard.tsx`, `contracts/create-order.ts` |

Un `email` enviado en el origen es un miembro desconocido de `AddressInput`: se ignora como cualquier otro, nunca se
lee, entra al hash ni se guarda. `null` se lee como ausente (como `references`). Un valor de otro tipo JSON (un número)
sigue el comportamiento del endpoint para cualquier campo mal tipado: el enlace del cuerpo falla antes del handler.

## Columnas (AI-06)

```sql
ALTER TABLE locations.locations
  ADD COLUMN email_ciphertext bytea,
  ADD COLUMN notice_phone_ciphertext bytea,
  ADD COLUMN notice_email_ciphertext bytea,
  ADD COLUMN notice_key_version text,
  ADD CONSTRAINT locations_notice_key_version_check
    CHECK ((notice_key_version IS NULL) = (notice_phone_ciphertext IS NULL AND notice_email_ciphertext IS NULL)),
  ADD CONSTRAINT locations_notice_email_check
    CHECK (notice_email_ciphertext IS NULL OR email_ciphertext IS NOT NULL);
```

- AI-06 lleva este texto exacto y el paso del carril ejecuta el mismo (`AddRecipientNoticeContact.CanonicalDdl`; una
  prueba de arquitectura compara los bytes). En una instalación nueva el paso adopta lo que AI-06 creó; en una que lo
  antecede lo crea sin reescribir filas; con las columnas a medias o con otra forma se niega.
- La bajada se niega con `NTF002_DOWNGRADE_BLOCKED_DATA_PRESENT` mientras alguna de las cuatro columnas tenga valor en
  cualquier fila (un CHECK de prueba, porque FORCE RLS oculta las filas al migrador); sin datos quita exactamente los dos
  checks y las cuatro columnas.
- Aserciones de la línea base (`DatabaseBaselineAssertions.AssertRecipientNoticeContactAsync`): con el historial del
  carril ausente (línea base nueva) o con el paso registrado, las cuatro columnas y los dos checks deben ser exactos; sin
  el paso registrado (instalación previa o después de la bajada), exactos o ausentes por completo.
- La tabla conserva su política FORCE RLS y sus privilegios. AI-18 no cambia: el Worker ya lee `locations.locations`
  bajo RLS (paso 4 del contrato declarado).

## Llave de avisos (solo envolver)

- `AzureKeyVaultRecipientNoticeKeyWrapClient` lee la llave RSA con `GET /keys/{name}` (versión vigente, caché de
  `CurrentVersionRefreshSeconds`), comprueba que esté habilitada, vigente, que sea RSA de al menos 2048 bits y que
  permita `wrapKey`, y envuelve cada llave de datos **en el proceso con la parte pública**, RSA-OAEP-256 (OAEP con
  SHA-256 y MGF1-SHA-256, el algoritmo `RSA-OAEP-256` con el que el Worker desenvolverá en Key Vault). Nunca llama a
  `wrapKey`, `unwrapKey` ni `decrypt`: a la API le basta leer la llave
  (`Microsoft.KeyVault/vaults/keys/read`), y su contrato `IRecipientNoticeKeyWrapClient` no tiene ninguna operación
  para abrir. La versión guardada es `akv:{nombre}/{versión}`.
- El formato es el sobre ADP-001 v1 (`PiiEnvelopeFormat`, compartido con la llave PII): llave AES-256-GCM propia de cada
  valor, ligada a la organización, la fila, el propósito y la versión. Rotar crea una versión nueva; las anteriores
  siguen abriendo lo que sellaron.
- `Locations:NoticeKeyProtector`:

| Valor | Efecto | Dónde se permite |
| --- | --- | --- |
| `Disabled` (por omisión) | Ninguna copia de avisos; la cotización no cambia | Siempre (piloto incluido) |
| `Mock` | Huellas SHA-256 de una sola vía con la versión `ntf002-notice-mock-v1` | Development, Testing y DEV_SYNTHETIC |
| `AzureKeyVault` | Sobre ADP-001 bajo `PiiProtection:RecipientNoticeKey:KeyId` | Solo con `Locations:PiiProtector=AzureKeyVault` y una llave distinta de la PII |

- `PiiProtection:RecipientNoticeKey` (`KeyId` sin versión, `CurrentVersionRefreshSeconds` y `HealthCacheSeconds` de 30
  a 3600) se valida al arrancar solo si se selecciona; el chequeo `ready` `recipient_notice_key` lee la llave y envuelve
  una prueba. Los prefijos `Locations__` y `PiiProtection__` ya son administrados por la plataforma en el piloto
  (`tools/azr-001/env001_pilot_guards.py`), así que ningún ajuste libre puede encenderla.
- Una llave configurada que falla termina en 503 sin escribir nada, igual que la llave PII.
- El Worker no gana nada en este ítem: no registra Locations, la llave PII ni la de avisos (prueba de arquitectura).
  Desenvolver es de AI-08 NTF-005 y el cableado del piloto (secretos, RBAC) de NTF-008.

## Privacidad

El correo y las copias nunca se devuelven (ni la respuesta de la cotización ni su repetición), ni se registran en logs,
auditoría (`LOCATION_CREATED` lleva `{}`), `request_snapshot_redacted`, la respuesta guardada de idempotencia, la orden,
el outbox ni un evento. Los tipos que los llevan (`DestinationAddressInput`, `AddressInput`, `QuoteAddressInput`,
`ResolveQuoteLocationCommand`, `LocationPiiValues`, `ProtectedLocationPii`, `LocationNoticeContactValues`,
`SealedLocationNoticeContact`) imprimen `[redacted]`. La web lo guarda solo en memoria con el resto del borrador, lo
manda solo si se escribió y solo el resumen del asistente lo repite; ninguna pantalla lo muestra después de crear la
orden.

## Pruebas

| Requisito de AI-08 | Pruebas |
| --- | --- |
| unit - RecipientEmailPolicy, vectores de estabilidad del hash, redacción y ToString | `Ntf002RecipientEmailTests`, `Ntf002RecipientNoticeContactTests`, `Ntf002RecipientNoticeKeyTests` (UnitTests) |
| PostgreSQL - columnas, checks, RLS y bajada protegida; subida, bajada y subida otra vez | `RecipientNoticeContactMigrationPostgreSqlContractTests`; `PricingPostgreSqlContractTests.RecipientContact` (sellado doble, sin fugas tras crear la orden, checks, FORCE RLS para API y Worker) |
| contract - los casos 422 de AI-05 y las propiedades de los DTO | `PricingOpenApiImplementationTests` (patrón de AI-05 = política, DTO, referencias), `OpenApiContractTests` (126 esquemas, 663 referencias), `QuoteHttpTests` y `QuoteRecipientEmailLogRedactionTests` (IntegrationTests: 422 sin eco, logs) |
| Vitest - el campo del asistente | `create-order-recipient-email.test.ts` (contrato y componente) y `create-order-controller.test.ts` |
| La API nunca desenvuelve; el Worker sin llave PII | `RecipientNoticeContactArchitectureTests`, `Ntf002RecipientNoticeKeyTests` |

```bash
MSBUILDDISABLENODEREUSE=1 dotnet build Paqueteria.sln
dotnet test tests/Paqueteria.UnitTests --filter "FullyQualifiedName~Ntf002"
dotnet test tests/Paqueteria.ArchitectureTests --filter "FullyQualifiedName~RecipientNoticeContact"
dotnet test tests/Paqueteria.ContractTests --filter "FullyQualifiedName~RecipientNoticeContact|FullyQualifiedName~PricingPostgreSqlContractTests|FullyQualifiedName~PricingOpenApi"
dotnet test tests/Paqueteria.IntegrationTests --filter "FullyQualifiedName~Pricing.Quote"
pnpm --dir apps/web exec vitest run src/operations
```

## Riesgos residuales

- Las órdenes cotizadas antes de que NTF-008 configure la llave de avisos no tienen copias: nunca recibirán avisos
  (consecuencia ya aceptada en el ADR para las órdenes anteriores al encendido).
- La política de correo es estructural (no verifica que el buzón exista); un correo mal escrito pero válido termina, en
  NTF-005, como un fallo permanente del proveedor.
- El hash de la cotización es SHA-256 sin llave y ahora puede incluir el correo, como ya incluye nombre, teléfono y
  dirección (`docs/legal/reglas-de-retencion-borrador.md`, sección 3.2).
- Ningún dato real antes de GATE-007; los borradores de `docs/legal` listan el correo como pendiente.

## Reversión

Sin migración: dejar `Locations:NoticeKeyProtector=Disabled` (ya es el valor). Para quitar el campo, revertir el código;
el paso del carril se baja solo si ninguna fila tiene correo ni copia (si no, se niega con
`NTF002_DOWNGRADE_BLOCKED_DATA_PRESENT`), y la versión anterior de la API funciona con las columnas puestas porque son
nulas y sin valor por omisión.
