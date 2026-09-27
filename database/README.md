# Database deployment assets

`database/migrations/v0.6-baseline.json` is the executable inventory for the
initial v0.6 database baseline. It references the frozen canonical SQL in
`docs/normative/v0.6/database`; it does not duplicate or modify that SQL.

The mandatory order is:

1. AI-06 creates the physical schemas, tables, extensions, RLS policies and
   append-only controls.
2. AI-18 creates the NOLOGIN roles and normalizes ownership, grants, default
   privileges and security-function execution.

The migrator verifies the canonical SHA-256 values before it opens PostgreSQL:

```text
AI-06 abce084a17ea4c91b9d10973e595e48b8e398604135ba0b77b825531df54a9e1
AI-18 bd8479df7abd9b636c70dc06d228d14b828fb5593b384aa96a1fb2e899c934aa
```

Use `tools/database-baseline.ps1`; see
`docs/development/database-baseline.md` for preflight, deployment, assertions,
credential separation and rollback. There is intentionally no destructive
`down`, `drop` or `reset` command.

TEN-001 adds migrator-owned, independent EF adoption histories for Identity and
Organizations after this baseline. The migrations assert existing objects and
do not duplicate or recreate AI-06 SQL. Operational details are in
[`docs/development/tenant-context-rls.md`](../docs/development/tenant-context-rls.md).
