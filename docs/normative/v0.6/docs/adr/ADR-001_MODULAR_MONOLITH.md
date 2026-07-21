# ADR-001 — Monolito modular

**Estado:** Aprobado

## Contexto
El MVP requiere velocidad, transacciones fuertes y un equipo pequeño/IA. Microservicios aumentarían despliegues, observabilidad y consistencia distribuida.

## Decisión
Usar monolito modular. API y Worker se despliegan por separado, pero comparten módulos y base. Los límites se validan con pruebas de arquitectura.

## Consecuencias
Menor complejidad inicial y transacciones simples. La disciplina modular es obligatoria para poder extraer componentes más adelante.
