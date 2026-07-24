# Decision log

## Pending

See `specs/AI-10_DECISIONS_AND_GATES.yaml`.

## Entries

| Date | ID | Type | Decision | Impacted files | Approved by |
|---|---|---|---|---|---|
| 2026-07-19 | ADR-000 | Architecture | Modular TypeScript monolith for MVP (superseded) | historical architecture | Project owner |
| 2026-07-19 | ADR-001 | Architecture | Use a modular monolith as the primary architecture | architecture, backlog | Project owner |
| 2026-07-19 | ADR-002 | Architecture | Clean Architecture inside each module | architecture | Project owner |
| 2026-07-19 | ADR-003 | Architecture | REST/PostgreSQL authoritative; SignalR distributes committed events | architecture, contracts | Project owner |
| 2026-07-19 | ADR-004 | Architecture | Transactional outbox for external effects and realtime publication | architecture, database | Project owner |
| 2026-07-19 | ADR-005 | Security | Multi-tenancy with organization context and PostgreSQL RLS | architecture, database | Project owner |
| 2026-07-19 | ADR-006 | Data | PostgreSQL/PostGIS as source of truth | architecture, database | Project owner |
| 2026-07-19 | ADR-007 | Architecture | Redis is non-authoritative | architecture | Project owner |
| 2026-07-19 | ADR-008 | Data | Object storage for evidence | architecture | Project owner |
| 2026-07-19 | ADR-009 | Scale | Progressive horizontal scaling before service extraction | scalability | Project owner |
| 2026-07-19 | ADR-010 | Architecture | Replace backend runtime with .NET 10/ASP.NET Core; retain Next.js PWA; add SignalR | architecture, contracts, backlog | Project owner |
| 2026-07-20 | ADR-011 | Contract | Quote persists normalized locations and package snapshot; order copies them | domain, API, database | Project owner |
| 2026-07-20 | ADR-012 | Security | Separate DB roles and FORCE RLS for application access | architecture, database, tests | Project owner |
| 2026-07-20 | ADR-013 | Identity | Organization-scoped memberships replace a global user role | domain, API, database | Project owner |
| 2026-07-20 | ADR-014 | Domain | Cancellation/retry semantics record custody and prohibit FAILED_ATTEMPT -> DELIVERED | domain, tests | Project owner |
| 2026-07-20 | ADR-015 | Realtime | Driver location ingests through REST, persists, then publishes via outbox/SignalR | API, database, SignalR | Project owner |

| 2026-07-20 | ADR-016 | Security | Append-only grants; outbox content immutable and operational columns mutable | database, tests | Project owner |
| 2026-07-20 | ADR-017 | Security | Dedicated NOLOGIN BYPASSRLS bootstrap function owner | database, identity, tracking | Project owner |
| 2026-07-20 | ADR-018 | Data | One physical PostgreSQL schema per normative module | database, architecture | Project owner |
| 2026-07-20 | ADR-019 | Security | Tenant context is transaction-local and reapplied on retry | persistence, tests | Project owner |
| 2026-07-20 | ADR-020 | Custody | Signed URL, quarantine and JSON proof finalization | API, storage, worker | Project owner |
| 2026-07-20 | ADR-021 | Pricing | Persist pricing tier and minimum total snapshot | pricing, database | Project owner |
| 2026-07-20 | ADR-022 | Scale | Separate telemetry outbox and batch GPS ingestion | drivers, worker, SignalR | Project owner |
| 2026-07-20 | ADR-023 | Security | Uniform 401/403/404 visibility policy | API, domain, tests | Project owner |
| 2026-07-20 | ADR-024 | Domain | Claim window and CLOSED finalization | orders, incidents, retention | Project owner |
| 2026-07-20 | ADR-025 | Security | Worker NOBYPASSRLS; privileged claim only through functions | worker, database | Project owner |
| 2026-07-20 | ADR-026 | Security | pgcrypto in extensions; PostGIS remains public; C#/SQL token hash symmetry | database, tracking, tests | Project owner |
| 2026-07-20 | ADR-027 | Reliability | Lease-protected function-only outbox lifecycle | database, worker, tests | Project owner |
| 2026-07-20 | ADR-028 | Privacy | Public status vocabulary and private-by-default timeline | domain, SQL, SignalR | Project owner |
| 2026-07-20 | ADR-029 | Legal | Canonical append-only order acceptance evidence | orders, contracts, database | Project owner |
| 2026-07-20 | ADR-030 | Security | Dedicated maintenance role for terminal outbox purge | database, operations | Project owner |
| 2026-07-20 | ADR-031 | Security | Transactional provisioning with application-generated pre-authorized UUIDs | identity, tenancy | Project owner |
| 2026-07-21 | ADR-032 | Design reference | Accepted as v0.7 design reference: live tracking, PICKUP_IN_PROGRESS and secure delivery verification; no implementation authorization | docs/adr design references only; normative v0.6 contracts unchanged | Project owner |
| 2026-07-21 | ADR-033 | Design reference | Accepted as v0.7 design reference: tamper-seal inventory and physical chain of custody; no implementation authorization | docs/adr design references only; normative v0.6 contracts unchanged | Project owner |
| 2026-07-21 | ARC-002-PURGE | Controlled normative remediation | Remove candidate row locking from purge, recheck terminal predicates in DELETE, preserve maintenance SELECT/DELETE without UPDATE, and validate on PostgreSQL 18/PostGIS 3.6 | AI-06, canonical integrity, runtime tests and FND-002 image | Project owner |
| 2026-07-21 | GATE-002 | Gate resolution | Resolved: AuthCenter (in-house OIDC) as identity provider; 99.5% SLA; Azure Mexico Central; RS256 access tokens verified; key rotation pending before production | identity, staging auth unblocked; normative v0.6 contracts unchanged | Project owner |
| 2026-07-23 | DSP-002-CONTRACT-REMEDIATION | Controlled normative remediation | Declare 409 safe conflicts, 404 uniform resource visibility, nullable-but-disabled route reference and OWN-only incremental capability without changing the global assignment vocabulary | AI-05, AI-08, AI-10, Dispatch endpoint/adoption/tests and development documentation | Project owner |
| 2026-07-23 | DSP-002-NON-ENUMERABLE-VISIBILITY | Security interpretation of AI-04/ADR-023 | Capability-first returns 403 before resource access for actors without Dispatch capability; authorized actors execute the same order/packages and driver/profile-documents plan before one uniform 404; artificial delays and cause-specific normal logging are prohibited | AI-05, AI-08, AI-10, Dispatch resolver/tests and development documentation | Project owner |
| 2026-07-24 | DSP-002-CAPABILITY-BEFORE-PERSISTED-STATE | Security refinement of DSP-002-NON-ENUMERABLE-VISIBILITY | Pure request-shape validation may return 409 INVALID_REQUEST without productive data access; every valid request authorizes current tenant role/MFA before idempotency lock/record, replay evidence or business resources, so unauthorized actors receive 403 independently of key state | AI-05, AI-08, AI-10, Dispatch coordinator/tests and development documentation | Project owner |
