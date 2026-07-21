# ADR-024 — Ventana de reclamación y finalización

**Estado:** Aprobado

Una orden CLOSED acepta reclamación hasta `claim_window_ends_at`. Un job idempotente fija `finalized_at` al vencer. CLAIM_RESOLVED es final inmediatamente. El archivado depende de la política legal de retención y no modifica eventos de evidencia.
