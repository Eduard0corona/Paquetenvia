# SET-001: reglas D7 de liquidación en el backend

Decisión: `SET-001-D7-RULES-ENFORCED-2026-11-06` (respuesta S1 del owner), que traduce `D7-SETTLEMENT-RULES`
(2026-09-26): "Solo se liquidan periodos ya cerrados; un total negativo se rechaza; aprobar y pagar requieren MFA; la
exportación se audita". El MFA para aprobar y pagar ya estaba implementado (`D7-SETTLEMENT-MFA`, PR 108). Esta tarea
publica en AI-05 e implementa los otros tres deltas.

## Reglas

| Delta AI-05 | Operación | Respuesta | Momento |
| --- | --- | --- | --- |
| `D7-SETTLEMENT-CLOSED-PERIOD` | `createSettlement` | 409 `SETTLEMENT_PERIOD_NOT_CLOSED` | después de la capacidad y de la repetición idempotente, antes de leer al repartidor |
| `D7-SETTLEMENT-NEGATIVE-TOTAL` | `createSettlement` | 409 `SETTLEMENT_TOTAL_NEGATIVE` | con el total calculado, antes de mover el encabezado |
| `D7-SETTLEMENT-NEGATIVE-TOTAL` | `addSettlementAdjustment` | 409 `SETTLEMENT_TOTAL_NEGATIVE` | después del estado (`SETTLEMENT_STATE_CONFLICT`) y del rango bigint (`INVALID_REQUEST`) |
| `D7-SETTLEMENT-EXPORT-AUDIT` | `exportSettlementCsv` | 200 y una entrada de auditoría | en la misma transacción que la lectura |

### Periodo cerrado

- La fecha operativa actual es la fecha de calendario del instante de la petición (el `IClock` inyectado) en
  `Finance:OperationalTimeZone`, IANA, `America/Mazatlan` por omisión. Es la misma zona en la que AI-05 expresa
  `period_from` y `period_to` (`SettlementPeriod.OperationalDate`).
- El periodo está cerrado sólo si `period_to` es estrictamente anterior a esa fecha (`SettlementPeriod.IsClosedOn`).
  Un periodo que termina hoy sigue abierto; la medianoche que cuenta es la de Culiacán, no la de UTC.
- Una repetición con la misma Idempotency-Key devuelve la respuesta guardada sin evaluar la regla.
- Un rechazo no lee el repartidor ni guarda encabezado, línea, auditoría ni registro de idempotencia. La misma llave
  puede reintentarse cuando el periodo cierre.
- Los preajustes de la pantalla (AI-07 `settlement_desk`) ya terminan antes de hoy; sólo "Fechas a mano" puede recibir
  este 409, que la web ya traduce como «Solo se liquidan periodos ya cerrados.».

### Total negativo

- El total resultante no puede quedar por debajo de cero (`SettlementLedger.AdmitsTotal`); exactamente 0 se acepta.
- En un ajuste cuenta el total que deja, no el signo del ajuste: un descuento que deja el total en 0 o más se acepta.
- Un total calculado es la suma exacta de `cost_cents` de las asignaciones, que AI-06 mantiene en 0 o más. Por eso hoy
  ningún cálculo llega al rechazo; la revisión en `createSettlement` es defensiva y la prueba PostgreSQL deja constancia
  de que un costo negativo no se puede guardar.
- Es una regla de la aplicación, sin CHECK en AI-06 ni migración: S1 pidió la decisión "tal como ya está en el contrato
  AI-05", los deltas sólo nombran las dos respuestas 409, el servicio de liquidaciones es el único que escribe
  `finance.settlements.total_cents`, y `COD-CAP-20000-2026-10-02` aplicó igual un límite de negocio.

### Exportación auditada

- Cada CSV servido escribe una entrada append-only `finance.settlement.exported` (entidad `Settlement`), sea cual sea
  el estado: `DRAFT`, `CALCULATED`, `APPROVED`, `PAID` o `VOID`.
- La carga útil sólo lleva `settlement_id`, `status`, `total_cents` y `line_count`, y pasa por el redactor de la
  plataforma. No lleva el beneficiario, órdenes, motivos de ajuste ni otro texto libre. `request_id` es el trace id de
  la petición, como en las demás acciones.
- La lectura, el CSV y la entrada comparten una transacción tenant, y el CSV sólo se devuelve cuando esa transacción
  confirma. Si la entrada no se puede escribir, la exportación es el 503 uniforme y no sale el CSV.
- Una exportación rechazada (401, 403, 404, 429 o 503) no escribe nada.
- La exportación es un GET sin Idempotency-Key, así que cada descarga es una entrada; SEC-004 la limita a 10 por
  minuto por usuario.

## Contrato

- AI-05: `SettlementConflictProblem.code` agrega `SETTLEMENT_PERIOD_NOT_CLOSED` y `SETTLEMENT_TOTAL_NEGATIVE`. Las
  descripciones de `createSettlement`, `addSettlementAdjustment`, `exportSettlementCsv` y de la respuesta
  `SettlementConflict` dicen las reglas. Los tres deltas tienen su línea `implementation`.
- Sin cambios en AI-06, AI-07, AI-08 (SET-001 ya lista los cuatro criterios D7), AI-18, roles, permisos, migraciones,
  dependencias ni la web.
- El piloto nunca se desplegó: no existe una liquidación ni un registro de idempotencia anterior a las reglas.

## Pruebas

- Unitarias: `SettlementPeriodTests` (fecha operativa en Culiacán y frontera del periodo cerrado) y
  `SettlementPolicyTests` (`AdmitsTotal`).
- Contrato: `SettlementImplementationContractTests` (vocabulario 409, publicación de los deltas y orden de las
  decisiones en el servicio).
- PostgreSQL real con el rol de runtime: `SettlementD7RulesPostgreSqlContractTests` (frontera de medianoche, capacidad
  y repetición antes de la regla, ajuste por debajo de cero, auditoría de cada exportación sin datos personales y
  exportación rechazada o fallida sin entrada). `SettlementWorkflowPostgreSqlContractTests` ajusta un periodo que ya no
  estaba cerrado.
- HTTP: `SettlementD7RulesHttpTests` (los dos 409 y la entrada con su trace id); `SettlementHttpTests` cuenta las dos
  entradas de exportación.

## Rollback

Revertir el PR. Las entradas de exportación escritas mientras tanto se quedan, porque la auditoría es append-only.
