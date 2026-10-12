# Changelog

## 1.0.0

First release.

- Multi-tenant core: global query filter on every `ITenantEntity`, stamping interceptor, composite `(TenantId, Id)` foreign keys, fail-closed when no tenant is resolved.
- JWT authentication with tenant claim, account lockout (race-safe), login and platform rate limiting, platform bootstrap endpoint guarded before body binding.
- Ticket workflow as a pure state machine; per-tenant ticket numbers; optimistic concurrency (`Version`), with a separate `SlaRevision` token for the SLA sweep.
- SLA engine with per-ticket snapshots, pausing clock, fresh window on reopen, and a cross-tenant background worker (warning / first-response breach / resolution breach, each once per window).
- Automatic audit trail via `SaveChangesInterceptor`, with deterministic ordering within a save.
- SignalR live desk with per-tenant / per-requester routing, socket closure on token expiry, immediate cut-off of deactivated users.
- Web UI (login, KPIs, ticket list, drawer with comments + timeline, admin panel).
- Strict JSON (enum names only, `required` fields), security headers on every response including errors, `no-store` on API data.
- Test suite: state machine (literal oracle), SLA, tenant isolation, workflow API, auth, audit, real-time, demo data, source-level guards, PostgreSQL integration; CI with CodeQL and a Docker health check.
