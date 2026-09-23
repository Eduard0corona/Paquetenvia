# CSV-001: carga CSV con prevalidación

CSV-001 permite cargar un archivo CSV de órdenes, revisarlo fila por fila antes de crear nada y confirmarlo en un paso explícito e idempotente. No reimplementa ninguna regla de ORD-001: cada fila confirmada se crea por el mismo `IOrderService` que usa `POST /api/v1/orders`, con su transacción, su idempotencia y su validación de cotización.

No incluye XLSX, formatos de hoja de cálculo arbitrarios, ETL genérico, procesamiento en segundo plano ni UI de operaciones.

## Arquitectura

- `Orders.Application/Csv`: contrato, lector RFC 4180, prevalidador puro, derivación de idempotency key y servicio de commit. Sin frameworks, sin persistencia, sin EF ni Npgsql.
- `Orders.Endpoints/CsvOrderImportEndpoints.cs`: binding multipart, autorización, tenant, digest y DTOs. Archivo separado de `OrderEndpoints.cs`, que conserva exactamente sus cuatro operaciones normativas.

El handler de preview no resuelve `ICsvOrderImportCommitService` ni `IOrderService`. "Ninguna fila se crea antes de confirmar" es una propiedad estructural del grafo de dependencias, no una convención.

## Formato del archivo

| Aspecto | Contrato |
| --- | --- |
| Transporte | `multipart/form-data`, parte de archivo `file` |
| Codificación | UTF-8 estricto, BOM opcional |
| Separador | coma |
| Comillas | RFC 4180: `"` delimita, `""` escapa una comilla literal |
| Fin de línea | `LF` o `CRLF`; la línea final puede terminar o no en salto |
| Tamaño | 1 048 576 bytes como máximo |
| Filas de datos | de 1 a 500 |
| Líneas en blanco | se ignoran |

La primera línea es el encabezado y debe contener exactamente estas seis columnas en este orden. La comparación ignora mayúsculas y espacios alrededor de cada nombre.

```text
quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel
```

Ejemplo:

```csv
quote_id,payer_type,terms_version,privacy_version,accepted_at,acceptance_channel
81000000-0000-0000-0000-00000000000a,SENDER,terms-synthetic-v1,privacy-synthetic-v1,2026-07-22T12:00:00.1234567Z,WEB
81000000-0000-0000-0000-00000000000b,BUSINESS_ACCOUNT,terms-synthetic-v1,privacy-synthetic-v1,2026-07-22T12:00:00.1234567Z,API
```

Cada fila corresponde a una cotización `ACTIVE` ya existente del tenant. CSV-001 no cotiza: `quote_id` es el insumo, igual que en ORD-001.

La aceptación legal viaja por fila porque `orders.order_acceptances` guarda una evidencia por orden. Repetir las cuatro columnas legales en cada fila es intencional: el archivo es autodescriptivo y el digest cubre toda la evidencia confirmada.

## Identificación de filas

`row_number` es el número de línea física del CSV, con base 1. El encabezado ocupa la línea 1, así que la primera fila de datos es la 2. Una fila con campos entrecomillados de varias líneas se identifica por la línea en la que comienza. El mismo número aparece en preview, en el reporte de errores y en el resultado del commit.

## Reglas de validación

Las reglas de columna delegan en las políticas autoritativas de ORD-001 (`OrderInputPolicy`, `OrderAcceptanceInputPolicy`) en lugar de repetirlas.

| Columna | Regla | Código |
| --- | --- | --- |
| `quote_id` | UUID en formato `D`, distinto de vacío | `QUOTE_ID_INVALID` |
| `quote_id` | único dentro del archivo | `QUOTE_ID_DUPLICATED` |
| `payer_type` | `SENDER`, `RECIPIENT` o `BUSINESS_ACCOUNT` | `PAYER_TYPE_INVALID` |
| `terms_version` | no vacío, 64 caracteres como máximo | `TERMS_VERSION_INVALID` |
| `privacy_version` | no vacío, 64 caracteres como máximo | `PRIVACY_VERSION_INVALID` |
| `accepted_at` | ISO-8601 con offset explícito (`Z` o `±HH:MM`), distinto de `default` | `ACCEPTED_AT_INVALID` |
| `acceptance_channel` | `WEB`, `PWA`, `ASSISTED` o `API` | `ACCEPTANCE_CHANNEL_INVALID` |
| fila completa | exactamente seis campos | `COLUMN_COUNT_INVALID` |

Un timestamp sin offset se rechaza: resolverlo contra la zona del servidor volvería ambiguo el instante de aceptación legal. `QUOTE_ID_DUPLICATED` se reporta en la segunda aparición y siguientes; la primera permanece válida, y como el commit exige el archivo completamente válido, el duplicado bloquea todo el lote.

Errores de archivo, que invalidan el documento entero y devuelven `rows` vacío:

| Código | Causa |
| --- | --- |
| `ENCODING_INVALID` | bytes que no son UTF-8 válido |
| `MALFORMED_QUOTING` | campo entrecomillado sin cerrar |
| `HEADER_INVALID` | encabezado ausente, incompleto, reordenado o con columnas extra |
| `FILE_EMPTY` | sin filas de datos |
| `ROW_LIMIT_EXCEEDED` | más de 500 filas de datos |

## Preview

`POST /api/v1/orders/csv/preview` — requiere sesión activa y tenant seleccionado.

Devuelve `200` con el reporte completo, incluso cuando el archivo es inválido: diagnosticar es justamente el propósito del paso.

```json
{
  "content_digest": "…",
  "total_rows": 3,
  "valid_rows": 1,
  "invalid_rows": 2,
  "file_errors": [],
  "rows": [
    { "row_number": 2, "quote_id": "8100…000a", "payer_type": "SENDER", "valid": true, "errors": [] },
    { "row_number": 3, "quote_id": null, "payer_type": null, "valid": false,
      "errors": [{ "column": "quote_id", "code": "QUOTE_ID_INVALID" }] }
  ]
}
```

`content_digest` es `Base64URL(SHA-256(bytes crudos))`, sin padding. Es el identificador del lote revisado.

Los rechazos de transporte —contenido que no es multipart, parte `file` ausente o vacía, archivo por encima del límite de bytes— devuelven el `409` uniforme del módulo, sin cuerpo informativo.

## Commit

`POST /api/v1/orders/csv/commit` — requiere sesión activa, tenant seleccionado, header `Idempotency-Key` (16 a 128 caracteres, política compartida), la parte `file` y el campo `content_digest`.

El paso de confirmación es explícito en dos sentidos: es una llamada distinta, y solo acepta el archivo cuyo digest coincide con el que devolvió el preview. Un archivo modificado después de revisarlo produce `409` y cero efectos.

El commit exige que el archivo prevalide por completo. Si hay un error de archivo o una sola fila inválida, responde `422` con el mismo reporte del preview y no crea ninguna orden. El operador corrige y vuelve a revisar.

Con el archivo válido, cada fila se envía a `IOrderService.CreateAsync` en su propia transacción y se reporta individualmente:

```json
{
  "content_digest": "…",
  "total_rows": 2,
  "created_rows": 1,
  "failed_rows": 1,
  "rows": [
    { "row_number": 2, "quote_id": "8100…000a", "status": "CREATED",
      "order_id": "…", "public_id": "ORD_…", "error_code": null },
    { "row_number": 3, "quote_id": "8100…000b", "status": "FAILED",
      "order_id": null, "public_id": null, "error_code": "QUOTE_UNAVAILABLE" }
  ]
}
```

`QUOTE_UNAVAILABLE` cubre de forma uniforme la cotización inexistente, expirada, ya consumida, revocada o de otro tenant, igual que ORD-001: el reporte no distingue entre esas causas y no revela nada sobre datos ajenos. `IDEMPOTENCY_CONFLICT` e `INVALID_REQUEST` completan el mapeo de `OrderConflictCode`. Si el servicio de órdenes queda indisponible, el commit responde `503` y el lote se reanuda reenviándolo con la misma key.

## Idempotencia

La key de cada fila es determinista:

```text
CSV1. + Base64URL(SHA-256(owner_org_id "\n" batch_key "\n" content_digest "\n" row_number))
```

Reenviar el mismo archivo con la misma `Idempotency-Key` reproduce exactamente las mismas keys de fila, que replican los registros existentes de `platform.idempotency_keys` en el scope `ORD-001:CREATE_ORDER`. La respuesta es idéntica byte a byte y no se crea ninguna orden nueva.

El tenant forma parte de la preimagen, de modo que dos organizaciones que elijan la misma `Idempotency-Key` nunca comparten keys de fila ni pueden replicar el resultado de la otra.

El respaldo final lo pone ORD-001: `orders.quote_id` es único. Una cotización produce como máximo una orden, sin importar cuántos lotes o keys distintas la intenten. Un segundo lote sobre las mismas cotizaciones no duplica nada: reporta `QUOTE_UNAVAILABLE` por fila.

## Aislamiento por tenant

Ambos endpoints exigen `OrganizationPolicies.ActiveOrganizationMember` y `RequireTenantContext`. El único `owner_org_id` que llega a `CreateOrderCommand` es `tenantContext.OrganizationId`; el CSV no puede declarar organización. Seleccionar un tenant que el actor no puede ver devuelve `403` antes de leer el archivo.

## Pruebas

- `tests/Paqueteria.UnitTests/Orders/CsvOrderImportPrevalidatorTests.cs`: formato, comillas, CRLF, BOM, UTF-8 inválido, numeración de filas, cada código de error, límites y derivación de keys.
- `tests/Paqueteria.IntegrationTests/Orders/CsvOrderImportHttpTests.cs`: e2e sobre el host real y el mismo `IOrderService` de ORD-001 — preview sin efectos, errores por fila, rechazo por digest, `422` sin efectos, commit, replay idéntico sin duplicados, segundo lote con fallo por fila y aislamiento entre tenants.

## Pendientes

- Los endpoints todavía no están en el contrato normativo `AI-05_OPENAPI.yaml`, cuyo SHA está fijado por `OpenApiBaselineTests`; incorporarlos requiere una actualización normativa aparte.
- No hay UI de operaciones para la carga; el vertical es de API.
- El commit procesa las filas en serie dentro de la petición. Para el límite contratado de 500 filas es suficiente; un lote mayor exigiría el procesamiento en segundo plano que CSV-001 deja fuera de alcance.
