# PRC-001: motor de cotizacion basico

PRC-001 implementa cotizaciones sinteticas, deterministas y aisladas por tenant. No habilita tarifas, cobertura ni cotizaciones comerciales reales. Los gates GEO y fiscales siguen abiertos.

## Arquitectura y dependencias

Pricing conserva cuatro capas:

- `Pricing.Domain` contiene dinero `long` en centavos, reglas, precedencia, tiers, invariantes y calculo puro. No referencia frameworks ni otros modulos.
- `Pricing.Application` publica comandos, resultados, errores estructurados e `IQuoteService`.
- `Pricing.Infrastructure` contiene `PricingDbContext`, PostgreSQL, idempotencia, snapshots, configuracion, la migracion de adopcion y la implementacion del servicio.
- `Pricing.Endpoints` hace binding HTTP, autorizacion, validacion superficial y mapeo de problemas.

La unica dependencia productiva hacia GEO-001 es `Pricing.Infrastructure -> Locations.Application`. El puerto estrecho `IQuoteLocationResolver` acepta la identidad interna, la organizacion activa, un `AddressInput`, una subclave y el rol origen/destino. Pricing no referencia `Locations.Domain`, `Locations.Infrastructure`, `Locations.Endpoints` ni `LocationsDbContext`. Endpoints reutiliza solamente los contratos publicos de tenant y autorizacion de Organizations.

`IdempotencyKeyPolicy` se movio sin cambiar sus limites (16 a 128 caracteres) a `Paqueteria.Application`, de modo que Locations y Pricing comparten una politica neutral.

## Resolucion geografica

Origen y destino requieren latitud y longitud; no existe geocodificacion de red. Locations usa los modos manual/mock de GEO-001, valida coordenadas, resuelve ciudad, service area y operating zone con PostGIS, da precedencia a zonas `EXCLUDED`, rechaza cobertura ausente o ambigua y protege la PII antes de persistir. El modo productivo permanece fail-closed cuando el protector autorizado no esta disponible.

Se crean o reutilizan dos ubicaciones tenant-scoped. Cada subclave es un SHA-256 estable de organizacion, clave de cotizacion y discriminador `ORIGIN`/`DESTINATION`, codificado sin PII y acotado a 71 caracteres. Un replay reutiliza las ubicaciones; los dos roles producen IDs distintos.

PRC-001 exige que ambos puntos pertenezcan a la misma ciudad. Si comparten service area, esta se persiste; si no, `service_area_id` queda `NULL` y solo puede aplicar una tarifa de ciudad. Una regla de operating zone solo aplica cuando ambos puntos comparten la misma zona, y una regla de service area solo cuando comparten la misma area. Nunca se toma arbitrariamente la geografia de uno de los extremos. Esta convencion same-city es reversible, local a PRC-001 y no normativa; no calcula distancia, ruta ni ETA.

## Evaluacion de tarifas

Una regla candidata debe pertenecer al tenant y ciudad activos, coincidir en tipo de servicio y tier, estar `ACTIVE`, vigente y ser espacialmente aplicable. Para reglas publicas la precedencia determinista es:

1. operating zone comun;
2. service area comun;
3. ciudad.

Dos reglas activas de igual especificidad fallan con 422 y emiten una senal tecnica sin PII. La ausencia de regla tambien falla con 422 y no crea quote ni registro idempotente.

Sin `client_account_id` se usa `OCCASIONAL`. Con cuenta, la proyeccion de `clients.client_accounts` es estrictamente de solo lectura y queda filtrada por tenant. Una cuenta activa con `private_tariff_id` usa exactamente esa regla y su tier; una cuenta ausente, inactiva, cross-tenant o sin tarifa privada falla con 422. No se inventan volumenes. Los tiers `BUSINESS_200_499` y `BUSINESS_500_PLUS` exigen `consolidated_route=true`.

El precio procede exclusivamente de `pricing.tariff_rules.amount_cents`. `Money` usa `long`, moneda fija `MXN` y operaciones checked:

```text
subtotal = amount_cents
discount = 0
tax = 0
total = subtotal
minimum_total_cents_snapshot = amount_cents
```

El flujo ejecutable solo acepta `EXEMPT`. `PLUS_VAT` y `VAT_INCLUDED` fallan cerrados. No se supone tasa, redondeo ni presentacion fiscal; GATE-011 sigue sin resolverse.

## Paquetes, snapshots y redaccion

Se requiere al menos un paquete. La descripcion es obligatoria y de hasta 250 caracteres; peso minimo 1 gramo; valor declarado no negativo; dimensiones opcionales pero positivas. No se crean `orders.package_items`.

Los tres snapshots JSON son deterministas:

- `request_snapshot_redacted` contiene solo IDs internos no sensibles, servicio, ruta consolidada, cantidad de paquetes e indicadores.
- `package_snapshot` contiene paquetes normalizados despues de la redaccion central AUD-001.
- `breakdown` contiene tipo de linea, rule ID, centavos, tier y tax mode.

No contienen direccion, referencias, contacto, telefono, ciphertext, headers, secretos ni claves. La cotizacion referencia las ubicaciones protegidas; no duplica su ciphertext. El SHA-256 `input_hash` se calcula sobre una serializacion canonica del request y no almacena el request original en la tabla idempotente.

## Idempotencia y expiracion

POST usa `platform.idempotency_keys` con scope `PRC-001:CREATE_QUOTE`. Desde la correccion `PRC-001-DEF-001`, la clave queda vinculada al SHA-256 canonico antes de cualquier llamada a Locations:

1. una transaccion Pricing corta adquiere el advisory lock de tenant, scope y key;
2. si no existe fila, inserta y confirma una reserva con hash, timestamps y expiracion, dejando `response_status`, `response_body` y `resource_id` en `NULL`;
3. si existe, compara el hash en tiempo constante;
4. un hash diferente produce 422 antes de geocoding, proteccion PII, PostGIS o creacion de ubicaciones;
5. una respuesta completada con el mismo hash se reproduce sin invocar Locations;
6. una reserva pendiente con el mismo hash permite reanudar y reutilizar las subclaves deterministas de Locations.

La reserva no contiene request original, direccion, referencias, contacto, telefono ni paquetes. Un fallo de cobertura, tarifa, tax mode, cancelacion u otra validacion posterior puede dejar la reserva incompleta; no se elimina ni cambia su hash. Esto impide que otro body adopte las ubicaciones creadas por el primer intento.

Despues de resolver Locations, la transaccion final vuelve a adquirir el mismo advisory lock, exige la reserva, compara otra vez el hash y vuelve a comprobar si otro intento ya la completo. Si sigue pendiente, evalua la tarifa, inserta la quote y completa la misma fila mediante un `UPDATE` que debe afectar exactamente una fila. Quote y finalizacion confirman o revierten juntas. El `UPDATE` establece status 201, response seguro, `resource_id` y `expires_at = quote.expires_at`; nunca inserta una segunda fila.

La expiracion inicial es la vigencia configurada de quote mas un buffer derivado de tres command timeouts. Es posterior al instante de reserva y cubre la vigencia maxima esperada del flujo acotado. Al completar, se reemplaza por la expiracion efectiva de la quote. No existe purga ni politica general de reutilizacion de claves expiradas.

Concurrencia del mismo hash genera una quote, dos ubicaciones y una fila. Con hashes distintos, el primer hash confirmado gana y los demas fallan antes de Locations. La clave puede repetirse en tenants diferentes porque la PK y el advisory lock incluyen organizacion; RLS impide replay cross-tenant.

`Pricing:QuoteLifetimeMinutes` es una vigencia operativa, positiva y acotada a 24 horas; no es normativa. `expires_at` se acota ademas por `active_to` de la regla. GET oculta con 404 uniforme cotizaciones inexistentes, cross-tenant, `EXPIRED`, `REVOKED` o vencidas. Una cotizacion `USED` sigue visible. No existe job de expiracion.

## Persistencia, RLS y migracion

`PricingDbContext` mapea de forma explicita `pricing.tariff_rules`, `pricing.quotes`, la proyeccion read-only de clientes y `platform.idempotency_keys`. Usa `bigint`, `uuid[]`, `jsonb`, `bytea`, timestamps UTC, filtros tenant y `ValueGeneratedNever`. El INSERT de quote envia todos los campos, incluido UUID y timestamps generados por la aplicacion, y no usa `RETURNING`.

La migracion no destructiva `20260722_AdoptCanonicalPricingBaseline` usa `platform.__ef_migrations_history_pricing`. Solo comprueba objetos, columnas, tipos, constraints e indices canonicos; no crea, altera ni elimina objetos. Pricing se ejecuta despues de Locations en el migrador. API y Worker no migran al arrancar, y Worker sigue sin conexion PostgreSQL.

Las tablas canonicas mantienen `ENABLE/FORCE RLS`; `paqueteria_app` y `paqueteria_worker` son `NOBYPASSRLS`. El contexto transaccional aplica actor, organizaciones y rol en cada intento, falla cerrado sin contexto y limpia conexiones pooled. Las pruebas Testcontainers cubren visibilidad y mutaciones cross-tenant; la capa transaccional compartida prueba rollback, cancelacion, retry y pooling sobre PostgreSQL 18/PostGIS.

## Configuracion

```json
{
  "Pricing": {
    "Provider": "Disabled",
    "QuoteLifetimeMinutes": 30,
    "CommandTimeoutSeconds": 30,
    "PricingPolicyVersion": "PRC-001-v1"
  }
}
```

`Disabled` es el valor predeterminado y falla cerrado. `PostgreSql` exige vigencia mayor que cero y no mayor que 1440 minutos, timeout positivo y acotado, y version de politica no vacia. No hay seed, precio, poligono, IVA, secreto ni credencial en configuracion.

## Endpoints

- `POST /api/v1/quotes`: miembro activo, `X-Organization-Id`, `Idempotency-Key`; devuelve 201 o 422, y 401/403 para identidad/contexto/capability.
- `GET /api/v1/quotes/{quoteId}`: miembro activo y tenant; devuelve 200 o 404 uniforme, y 401/403 para identidad/contexto/capability.

No existe endpoint de lista ni CRUD de tarifas/clientes.

## Validacion

Desde la raiz:

```powershell
python .\docs\normative\v0.6\tools\validate_contracts.py
dotnet tool restore
dotnet restore --locked-mode
dotnet format Paqueteria.sln --verify-no-changes --no-restore
dotnet build Paqueteria.sln --configuration Debug --no-restore
dotnet test Paqueteria.sln --configuration Debug --no-build
dotnet test .\tests\Paqueteria.ContractTests\Paqueteria.ContractTests.csproj --filter "Category=PostgreSqlContract"
dotnet ef migrations list --project .\src\Modules\Pricing\Pricing.Infrastructure\Pricing.Infrastructure.csproj --startup-project .\src\Paqueteria.Api\Paqueteria.Api.csproj --context PricingDbContext
.\tools\database-baseline.ps1 Verify
```

La matriz completa tambien ejecuta Release con `CI=true`, cada suite por separado, web, Compose, auditoria de paquetes, hashes normativos y diff de la linea base.

## Riesgos y rollback

Riesgos residuales: cobertura y tarifas son sinteticas; no existe proveedor geografico real; la proteccion PII productiva debe configurarse; no hay politica fiscal aprobada; no existe seleccion por volumen, pricing inter-city, expirador ni reutilizacion de reservas vencidas. Una operacion que exceda el buffer configurado puede dejar una reserva con `expires_at` pasado, pero su hash sigue siendo inmutable y no se reutiliza para otro body. Los gates GATE-003, GATE-007, GATE-010, GATE-011, GATE-014 y GATE-017 permanecen abiertos.

Rollback operativo:

1. cambiar `Pricing:Provider` a `Disabled` para detener nuevas cotizaciones de forma fail-closed;
2. revertir el commit correctivo y, si se requiere retirar PRC-001 completo, sus commits originales;
3. no ejecutar DDL inverso: la migracion de adopcion tiene `Down` intencionalmente vacio y no es propietaria del baseline;
4. conservar quotes, ubicaciones, reservas idempotentes y auditoria existentes para investigacion y retencion conforme a politica; no borrar automaticamente reservas incompletas.

No se debe borrar ni alterar manualmente el baseline AI-06/AI-18.
