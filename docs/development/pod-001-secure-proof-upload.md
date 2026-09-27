# POD-001: carga segura de evidencia

POD-001 implementa el flujo de evidencia de Custody sin transportar bytes por
la API. La API crea una sesión y firma un `PUT` directo hacia cuarentena; el
Worker valida el objeto y lo promueve; solo una sesión `READY` puede producir
un registro append-only en `custody.proofs`.

La implementación no cambia `docs/normative/v0.6`, no cambia automáticamente el
estado de la orden y no incorpora OCR, biometría ni las capacidades reservadas
para ADR-032/ADR-033.

## Flujo y límites de confianza

1. El cliente solicita una sesión mediante JSON.
2. La API reautoriza al actor y la orden dentro de una transacción tenant/RLS,
   reserva la idempotencia y persiste una sesión `CREATED`.
3. La API devuelve una URL `PUT` corta y los headers firmados exactos. El objeto
   solo puede escribirse en
   `quarantine/{owner_org_id}/{upload_session_id}`.
4. El Worker lista únicamente cuarentena, reclama la sesión con
   `SET LOCAL ROLE paqueteria_worker`, lee el objeto fuera de la transacción y
   valida tamaño, content type, magic bytes, SHA-256 y el scanner estricto.
5. La promoción usa copia condicional por ETag para impedir TOCTOU. El objeto
   final queda en
   `proofs/{owner_org_id}/{order_id}/{upload_session_id}` con metadata
   reconstruida por el servidor.
6. Tras verificar el objeto final, el Worker marca la sesión `READY`, audita y
   elimina cuarentena. Si ese último borrado falla, una ejecución posterior
   vuelve a verificar el objeto final antes de limpiar una sesión `READY` o
   `CONSUMED`.
7. La finalización reautoriza y vuelve a comprobar orden, sesión, SHA-256 y
   metadata. En una sola transacción crea exactamente un `Proof`, cambia la
   sesión a `CONSUMED`, escribe auditoría y completa la idempotencia.

El bucket debe ser privado. No hay ACL pública, ruta HTTP de descarga ni acceso
anónimo. `IProofDownloadService` es un puerto interno: vuelve a autorizar actor,
tenant y orden, y solo entonces emite un `GET` firmado de corta duración.

## Contratos HTTP

Ambas operaciones requieren autenticación, tenant activo e
`Idempotency-Key`.

### Crear sesión

`POST /api/v1/orders/{orderId}/proof-upload-sessions`

```json
{
  "proof_type": "DELIVERY_PHOTO",
  "content_type": "image/png",
  "size_bytes": 8,
  "sha256": "4c4b6a3be1314ab86138bef4314dde0226b8b492db1c14f3dd807acd74709a5b"
}
```

La respuesta `201` contiene solamente `id`, `status`, `upload_url`,
`object_key`, `expires_at` y `required_headers`. El cliente debe enviar el
contenido con `PUT` y copiar todos los `required_headers` sin modificarlos.
La firma cubre `Content-Type`, `x-amz-meta-session-id`,
`x-amz-meta-order-id`, `x-amz-meta-owner-org-id`,
`x-amz-meta-requested-by`, `x-amz-meta-proof-type` y
`x-amz-meta-size-bytes`; agrega `x-amz-meta-sha256` solo cuando el request
incluye el hash opcional. Bucket, key, ACL y metadata no son elegibles por el
cliente.

### Finalizar evidencia

`POST /api/v1/orders/{orderId}/proofs`

```json
{
  "upload_session_id": "00000000-0000-0000-0000-000000000000",
  "proof_type": "DELIVERY_PHOTO",
  "captured_at": "2026-07-25T12:00:00Z",
  "sha256": "4c4b6a3be1314ab86138bef4314dde0226b8b492db1c14f3dd807acd74709a5b",
  "lat": 28.6353,
  "lng": -106.0889
}
```

La respuesta `201` contiene `id`, `proof_type`, `sha256` y `captured_at`.
Campos desconocidos, hashes no canónicos, coordenadas incompletas o fuera de
rango y tipos no exactos se rechazan.

`recipient_name` se rechaza de forma fail-closed con
`PII_PROTECTION_UNAVAILABLE`: AI-06 exige ciphertext y versión de llave, pero
POD-001 no tiene autorización para inventar un proveedor criptográfico.

## Autorización y estados

La autorización se reevalúa contra usuarios y membresías `ACTIVE`:

- `DISPATCHER`;
- `PLATFORM_ADMIN` únicamente con MFA satisfecho;
- `DRIVER` con perfil `OWN/ACTIVE` o `EXTERNAL/ACTIVE` y asignación del mismo
  tipo (`a.assignment_type = d.driver_type`) en `ACCEPTED` o `ACTIVE` para esa
  orden. Un perfil `EXTERNAL` con asignación `OWN` (o viceversa) recibe `403`.

Una denegación explícita devuelve `403`; un recurso no visible por RLS conserva
el `404` uniforme. El servicio interno de descarga aplica la misma regla.

La finalización bloquea la orden con `SELECT 1 FROM orders.orders WHERE id=@order
FOR SHARE` como sentencia propia dentro de la transacción que inserta la prueba,
y solo después lee el estado y evalúa la autorización en una sentencia nueva.
Bajo `READ COMMITTED` esa segunda sentencia toma una snapshot fresca, así que ve
todo lo que confirmó mientras esperaba el lock: el estado de la orden y también
membresías, perfil de conductor y assignment de las subconsultas de
autorización. (Con `FOR SHARE` en la misma sentencia, PostgreSQL solo
reevaluaría la fila bloqueada y las subconsultas usarían la snapshot previa.)
Una transición ORD-002 concurrente (que toma `FOR UPDATE`) espera a que la
prueba confirme o, si confirmó primero, la finalización responde
`409 ORDER_STATE_NOT_ALLOWED` o `403` según lo que cambió; una orden `CANCELLED`
no recibe evidencia de custodia. Sesión, replay y descarga no toman el lock.

### POD-001-DEF-001: autorización antes del replay

Una URL firmada, su object key, expiración y headers requeridos son estado
protegido. Estar autenticado no basta: la membresía tenant activa permite
seleccionar el contexto, la capability actual autoriza la operación sobre la
orden y solo después la idempotencia decide si existe un replay.

La creación de sesiones aplica esta precedencia dentro de una transacción
tenant con `SET LOCAL ROLE` y contexto RLS:

1. shape o `Idempotency-Key` inválida: `409 INVALID_REQUEST`;
2. orden tenant-visible pero capability actual inválida: `403`;
3. orden inexistente o cross-tenant: `404` uniforme;
4. actor autorizado con la misma key y hash diferente:
   `409 IDEMPOTENCY_CONFLICT`;
5. actor autorizado con la misma key y hash: el mismo `201` almacenado.

`ReadAuthorizedOrderAsync` se ejecuta antes del advisory lock, de la fila
idempotente y de cualquier respuesta almacenada. El actor, MFA, roles y
assignment no forman parte del hash: otro `DISPATCHER` activo del mismo tenant
puede reproducir la sesión, pero un `PLATFORM_ADMIN` necesita MFA actual y un
`DRIVER` necesita perfil `OWN/ACTIVE` o `EXTERNAL/ACTIVE`, membresía
`DRIVER/ACTIVE` y assignment del mismo tipo, tenant-consistente `ACCEPTED` o `ACTIVE` para esa orden. Una assignment
`CANCELLED` o `COMPLETED` revoca el replay.

El replay no firma otra URL, no extiende expiraciones, no cambia el lifecycle
y no agrega sesiones, grants, auditorías o filas idempotentes. Puede devolver
el resultado histórico aunque la sesión ya no esté en `CREATED`.

Antes de devolverlo se valida de forma fail-closed el status HTTP almacenado,
resource ID, order ID, key canónica, URL, shape y headers exactos, junto con la
sesión tenant-visible, su owner, orden, content type y tamaño. Evidencia
ausente o inconsistente produce un `409` uniforme sin revelar el campo
corrupto.

| Evidencia | Estado permitido de la orden | Content types |
| --- | --- | --- |
| `PICKUP_PHOTO` | `AT_PICKUP` | `image/jpeg`, `image/png` |
| `DELIVERY_PHOTO` | `DELIVERING` | `image/jpeg`, `image/png` |
| `SIGNATURE` | `DELIVERING` | `image/png` |
| `DELIVERY_CODE` | `DELIVERING` | `text/plain` UTF-8 sin NUL |
| `RETURN_PHOTO` | `RETURNING` | `image/jpeg`, `image/png` |

La máquina de sesión es
`CREATED -> UPLOADED -> VALIDATING -> READY -> CONSUMED`, con salidas
fail-closed a `REJECTED` o `EXPIRED`. Un claim `VALIDATING` obsoleto puede
recuperarse. La expiración se aplica al procesar un objeto o finalizar una
sesión; no se hace un barrido global que eluda RLS para sesiones sin objeto.
`DELIVERY_CODE` además usa `MaximumTextBytes` (4096 por defecto), separado del
límite binario global.

## Configuración segura

API y Worker comparten la sección `ProofStorage`. `Disabled` es el valor seguro
por defecto. Para el entorno local:

```powershell
$env:ProofStorage__Provider = "S3Compatible"
$env:ProofStorage__ThreatScanner = "Synthetic"
$env:ProofStorage__ServiceUrl = "http://127.0.0.1:9000"
$env:ProofStorage__PublicPresignUrl = "http://127.0.0.1:9000"
$env:ProofStorage__Region = "us-east-1"
$env:ProofStorage__Bucket = "paquetenvia-proofs"
$env:ProofStorage__ProcessingIntervalSeconds = "5"
$env:ProofStorage__StaleValidationSeconds = "300"
$env:ProofStorage__MaximumConcurrency = "4"
$env:AWS_ACCESS_KEY_ID = "paquetenvia-local"
$env:AWS_SECRET_ACCESS_KEY = "local-only-minio-change-me"
```

Las credenciales se leen exclusivamente de `AWS_ACCESS_KEY_ID` y
`AWS_SECRET_ACCESS_KEY`; no existen propiedades de secreto en appsettings. En
producción, `ServiceUrl` es la dirección interna usada por API/Worker y
`PublicPresignUrl` la dirección alcanzable por el cliente. TLS y las políticas
del bucket son responsabilidad del despliegue.

El scanner `Synthetic` detecta fixtures EICAR y está restringido por validación
de inicio a Development/Testing; no se presenta como antivirus productivo. Con
scanner o storage `Disabled`, las operaciones fallan con `503` y nunca generan
una URL o un `READY` falso. Fuera de Development/Testing, S3 exige HTTPS.
Además, readiness es `unhealthy` con storage o scanner `Disabled`; un scanner
productivo concreto sigue pendiente de GATE-007.

`GET /health/ready` incluye `proof_storage`. Con el proveedor deshabilitado el
check es neutral únicamente en Development/Testing porque POD queda totalmente
desactivado y falla cerrado; con S3 habilitado exige bucket accesible, ACL no
pública y capacidad local de firma. El Worker expone sus propios
`/health/live` y `/health/ready` en `127.0.0.1:5081`; readiness comprueba
storage, scanner, conexión PostgreSQL, rol efectivo `paqueteria_worker` y
`NOBYPASSRLS`.

## Descubrimiento, retries y observabilidad

El listado privado de `quarantine/` es la estrategia reversible de
descubrimiento de MVP-0, no una topología final de producción. No se agregó
cola, lease column, función de claim ni rol `BYPASSRLS`. Errores transitorios
de S3/PostgreSQL dejan el claim recuperable tras el umbral stale y el polling
del Worker aplica el backoff acotado; errores permanentes terminan en
`REJECTED`. La evidencia rechazada permanece privada porque su retención no
está autorizada.

Las métricas son `custody.proof.sessions`,
`custody.proof.objects_discovered`, `custody.proof.processing_duration`,
`custody.proof.validation_outcomes`, `custody.proof.stale_recoveries`,
`custody.proof.finalizations` y `custody.proof.storage_failures`. Solo usan
tags allow-listed (`provider`, `proof_type`, `outcome`, `reason_code`); no
incluyen tenant, IDs, key, hash, coordenadas o PII. Las auditorías tampoco
incluyen URLs firmadas, object keys, bytes ni hashes completos.

## Ejecución local y pruebas

```powershell
Copy-Item .\deploy\.env.example .\deploy\.env.local
pwsh .\tools\local-environment.ps1 Up

dotnet run --project .\src\Paqueteria.Api
dotnet run --project .\src\Paqueteria.Worker

dotnet test .\tests\Paqueteria.UnitTests\Paqueteria.UnitTests.csproj `
  --filter "FullyQualifiedName~Custody"
dotnet test .\tests\Paqueteria.ArchitectureTests\Paqueteria.ArchitectureTests.csproj `
  --filter "FullyQualifiedName~Custody"
dotnet test .\tests\Paqueteria.IntegrationTests\Paqueteria.IntegrationTests.csproj `
  --filter "Category=SecureProofUpload"
```

La categoría runtime levanta MinIO fijado por digest y PostgreSQL/PostGIS real.
Comprueba bucket privado, metadata firmada, promoción idempotente, rechazo de
sobrescritura TOCTOU, pipeline de Worker, finalización única, auditoría,
append-only, descarga autorizada/no autorizada y limpieza recuperable.
También cubre replays HTTP y PostgreSQL/MinIO con autorización actual,
precedencia `403/404/409`, MFA, assignments vigentes/revocadas, cero efectos,
vigencia del grant y corrupción sintética de evidencia persistida.

## Migración y rollback

`AdoptCanonicalCustodyProofsBaseline` no crea ni modifica el esquema funcional.
Solo adopta el baseline AI-06 después de comprobar columnas exactas, RLS
forzado, políticas tenant, índices y el trigger append-only. El migrador
independiente la aplica con su historial por módulo; API y Worker nunca migran
al arrancar.

El rollback operativo es:

1. configurar `ProofStorage:Provider=Disabled`;
2. detener el Worker de validación;
3. revertir los commits de POD-001.

No se borran `custody.proofs`, sesiones u objetos automáticamente. Cualquier
limpieza o restauración requiere un procedimiento explícito de datos.

## Gap normativo registrado

AI-05 enumera `201/401/403/404` para estas operaciones, pero no define una
respuesta para petición/estado/idempotencia incompatibles ni para storage
indisponible. La implementación conserva las convenciones existentes del
repositorio con `409` y falla de forma cerrada con `503`. Este gap debe
formalizarse en una revisión normativa futura; POD-001 no modifica AI-05 ni
amplía silenciosamente su baseline.

## Riesgos y gates abiertos

- MinIO y el scanner sintético son únicamente locales/de prueba.
- `GATE-007` permanece abierto: no se permite PII real, no existe política
  productiva de retención/ARCO/borrado criptográfico y no se afirma readiness
  legal.
- No existe endpoint público de descarga; solo el puerto interno autorizado.
- Los objetos rechazados o huérfanos permanecen privados hasta una decisión de
  retención.
- El descubrimiento por listado y la recuperación por `updated_at` son el
  mecanismo reversible de MVP-0; AI-06 no autoriza una función SQL de claim.
- ADR-032 y ADR-033 no están implementados y se conservan los 17 estados v0.6.
- La finalización de un Proof no transiciona automáticamente la orden.
