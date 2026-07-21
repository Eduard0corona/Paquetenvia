# ADR-006 — PostgreSQL y PostGIS como fuente de verdad

**Estado:** Aprobado

## Decisión
Usar PostgreSQL para transacciones y PostGIS para zonas, coordenadas y consultas geográficas. Redis y read models son derivados.

## Consecuencias
Un modelo consistente y capacidad geoespacial; exige índices, pooling y observabilidad de consultas.
