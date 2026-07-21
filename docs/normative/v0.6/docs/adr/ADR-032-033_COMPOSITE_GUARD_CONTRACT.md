# Contrato conjunto ADR-032 ↔ ADR-033 — Guards y atomicidad

**Estado:** Anexo normativo del candidato para revisión.  
**Versión:** `0.7-review-1`

## 1. Invariantes de configuración

```text
STRICT_CODE_ONLY
⇒ delivery_code_required=true
AND tamper_seal_required=true

supervised_alternate_is_valid
⇒ delivery_code_failure_policy=CODE_OR_SUPERVISED_ALTERNATE
AND tamper_seal_requirement_complete por separado
```

## 2. Secuencia pickup

```text
READY_FOR_PICKUP
→ ASSIGNED
→ PICKUP_IN_PROGRESS
→ AT_PICKUP
→ CompletePickup
→ PICKED_UP
```

- La disponibilidad de sello se valida antes de salir hacia pickup.
- El sello concreto se selecciona y liga en `CompletePickup`.
- `custody_leg` y `order_seal_link` nacen en la misma transacción.

## 3. Tabla de verdad de entrega

| Código | Sello | Resultado de orden | Challenge | Sello/leg | Incidente |
|---|---|---|---|---|---|
| válido/no requerido | válido/no requerido | `DELIVERED` | consumido o N/A | verified/completed | según POD normal |
| inválido | válido/no requerido | permanece `DELIVERING`; `CODE_REJECTED` | intento++/lockout | sin cambio | alerta si policy |
| válido | mismatch/roto | `FAILED_ATTEMPT` `SEAL_SECURITY_HOLD` | revocado, `VALID_BUT_DELIVERY_BLOCKED` | investigation/hold | obligatorio |
| inválido | mismatch/roto | `FAILED_ATTEMPT` `SEAL_SECURITY_HOLD` | intento registrado y revocado | investigation/hold | obligatorio |
| excepción supervisada | válido | `DELIVERED` solo policy alternate | no consumido como código | verified | incidente/evidence |
| excepción supervisada | mismatch/roto | prohibido; `SECURITY_HOLD` | revocado | investigation/hold | obligatorio |

## 4. Atomicidad

El éxito y `SECURITY_HOLD` son resultados confirmables del mismo comando. El handler no lanza una excepción después de escribir evidencia de seguridad.

La entrada existente `proof_custody_to_order_transition` se amplía en AI-04 para autorizar de forma explícita:

- Orders;
- Custody (proof, code, seal, leg, completion evidence);
- Incidents;
- Audit;
- Outbox.

No incluye suspensión síncrona del driver. El outbox solicita hold después del commit.

## 5. Precedencia

Un defecto de sello domina el resultado operativo porque afecta integridad física. Un código válido no compensa un sello fallido y se revoca para evitar reutilización.

Un fallo de código sin fallo de sello no crea security hold; sigue la policy de código.

## 6. Reporting

La orden conserva los 18 estados. La forma de entrega se reporta mediante `delivery_completion_method` y evidencia append-only. El security hold usa `FAILED_ATTEMPT` con reason code, no un estado 19.

## 7. Idempotencia y concurrencia

- `CompletePickup` y `CompleteDelivery` exigen `Idempotency-Key`.
- `expected_order_version`, assignment/attempt y custody leg se verifican.
- Una sola transacción puede consumir challenge/verificar sello/transicionar order.
- Repetición con misma key devuelve el resultado original.
- Key distinta sobre versión obsoleta no produce efectos parciales.
