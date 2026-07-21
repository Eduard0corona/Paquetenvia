# ADR-017 — Bootstrap de identidad y tracking bajo FORCE RLS

**Estado:** Aprobado

Se crea `paqueteria_bootstrap NOLOGIN BYPASSRLS`, propietario únicamente de dos funciones `SECURITY DEFINER`: resolución de identidad/membresías y proyección pública de tracking. Tiene SELECT limitado por columna, `search_path` fijo, sin SQL dinámico y EXECUTE revocado a PUBLIC. API/Worker no pueden asumir el rol.
