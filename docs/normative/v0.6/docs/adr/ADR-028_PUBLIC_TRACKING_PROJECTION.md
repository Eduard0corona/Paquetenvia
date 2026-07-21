# ADR-028 — Vocabulario y timeline público fail-closed

**Estado:** Aprobado

`AI-04` define el único mapa normativo de 17 estados internos a estados públicos. C# falla ruidosamente; SQL devuelve proyección nula y 404 uniforme.

Los eventos son privados por defecto. Solo `public_event_code` no nulo puede aparecer en el timeline, sin payload interno.
