# ADR-009 — Escalabilidad progresiva

**Estado:** Aprobado

## Decisión
Escalar primero réplicas de API/Worker, base administrada, Redis y SignalR scale-out. Extraer servicios solo con métricas y criterios de `AI-15_SCALABILITY_CONTRACT.yaml`.

## Consecuencias
Se evita complejidad prematura, manteniendo un camino explícito a crecimiento local y regional.
