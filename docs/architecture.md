# Architecture

This document explains *how* the isolation, workflow, SLA and audit guarantees are built, and where each one is tested.

## 1. Request path and tenant resolution

```
HTTP request (Authorization: Bearer <JWT>)
  -> security headers (CSP, nosniff, no-store for /api ...)
  -> JwtBearer validates signature, issuer, audience, lifetime (TimeProvider)
  -> TenantResolutionMiddleware
       reads tid / sub / role / slug from the VALIDATED principal (role must be a defined enum value)
       -> TenantContext.Use(...)            (scoped, one per request)
       -> TenantDirectory.IsAccessAllowed   (tenant + user still active? cached 30 s, evicted on deactivation)
  -> Controller -> Service -> OpsFlowDbContext
```

- The tenant is never read from a header, query string or body. Tests: `A_client_supplied_tenant_header_has_no_effect`, `A_tenant_header_cannot_redirect_a_write_into_another_tenant`, `Forged_or_malformed_tokens_are_all_rejected`, `A_correctly_signed_token_that_lacks_a_valid_tenant_context_is_refused`.
- Login is the one pre-authentication place that needs a tenant: it looks the tenant up by slug (the `Tenants` table is the only unfiltered table), then calls `TenantContext.Use(...)` so the user lookup is *also* filtered.
- A fallback authorization policy requires an authenticated user everywhere; an endpoint is public only if it says `AllowAnonymous` (login, platform bootstrap, `/health`, `/api/meta`). A test pins that list.
- The platform endpoint is guarded by `PlatformKeyFilter`, a *resource* filter: it runs before model binding, so a stranger cannot learn anything from validation errors, and a disabled endpoint is a plain 404.

## 2. Isolation layers

| Layer | Mechanism | Protects against | Tests |
| --- | --- | --- | --- |
| 1. Query filter | `HasQueryFilter(e => e.TenantId == CurrentTenantId)` applied by reflection to every `ITenantEntity` | reads; updates/deletes of foreign rows (they cannot be loaded) | `DbContextGuardTests`, `TenantIsolationTests` |
| 2. Fail-closed | no tenant -> `Guid.Empty` -> no rows | forgetting to resolve a tenant | `With_no_tenant_resolved_every_tenant_scoped_query_is_empty` (with a control that the rows exist) |
| 3. Stamping interceptor | sets `TenantId` on insert; rejects foreign/empty; `TenantId` immutable | writes that skip the filter (e.g. entity loaded with `IgnoreQueryFilters`) | `Inserting_*`, `Modifying_another_tenants_row_*` |
| 4. Composite FKs | `(TenantId, UserId) -> Users(TenantId, Id)`, same for tickets | anything that reaches the database some other way | `The_database_itself_refuses_*` (PostgreSQL; checks the constraint name) |
| 5. Source-level guards | allow-lists for `IgnoreQueryFilters`, `UseSystem`, the `TenantContext` type; no raw SQL / bulk operations; no hand-written `x.TenantId ==` in services; one-directional layering; the list of anonymous endpoints | someone adding a new escape hatch | `ArchitectureGuardTests` |

`Every_tenant_owned_entity_has_a_query_filter_and_is_mapped` (it inspects the filter expression, not just its existence) and `The_tenant_table_itself_is_the_only_unfiltered_root` make a future entity impossible to ship unfiltered.

Cross-tenant ids return **404**. The service layer additionally applies a second, *intra*-tenant boundary: an employee only ever queries `CreatedByUserId == me`.

The guard tests scan source files relative to `src/` and fail if they find fewer files than expected or if an allow-list entry no longer exists, so they cannot pass by scanning nothing.

## 3. System scope (cross-tenant jobs)

`TenantContext.UseSystem()` marks a scope as a background job. It relaxes only the stamping rule (rows must carry an explicit `TenantId`) and stops the sweep's writes from bumping the human `Version`; it does **not** open the query filter - the SLA sweep opts out per query with `IgnoreQueryFilters()`. Callers: the SLA monitor, tenant provisioning, the demo seeder.

## 4. Workflow

`TicketStateMachine` (Core) is a pure function over `(from, to, role, isRequester, hasAssignee, summary)`:

1. same status -> `same_status`
2. not in the matrix -> `invalid_transition`
3. role check (closed tickets: admin only; employees: only their own `Resolved -> Closed | InProgress`) -> `forbidden` / `closed_reopen_forbidden`
4. `Assigned` needs an assignee; `Resolved` needs a summary of at least 10 characters after trimming -> `assignee_required` / `resolution_summary_required` (HTTP 400: missing input, as opposed to 409 for a move that is not allowed)

`AllowedFor(...)` feeds the UI buttons. The tests spell the expected rules out literally (11 edges, role rules) instead of deriving them from the production matrix, so the UI can never offer a move the server would refuse and the matrix cannot drift unnoticed.

## 5. SLA engine

- `Ticket` snapshots `FirstResponseMinutes` / `ResolutionMinutes` and derives `ResponseDeadline` / `ResolutionDeadline` from `CreatedAt` / the current window start plus time spent paused.
- `PauseSla` / `ResumeSla` (entering/leaving `PendingClient`) move the deadlines by the paused duration. **Resume happens before any deadline comparison**, so resolving straight out of `PendingClient` is judged against the extended deadline (`Resolving_straight_out_of_pending_client_is_judged_with_the_pause_credited`).
- `RestartResolutionWindow` runs when a ticket is reopened: new window start, paused time reset, breach and warning markers cleared (the earlier breach stays in the audit timeline), first-response history kept.
- `Evaluate(now)` is pure: `ResponseBreached`, `ResolutionBreached`, `Warning` (remaining <= 20 % of the window). Paused or finished tickets are always healthy.
- `HasMissedTarget` (`IsSlaBreached || ResponseBreachedAt != null`) is what the API exposes as `isSlaBreached`, what the dashboard's *Breached* counts and what `?breached=` filters on - a missed first response stays visible after the late answer.
- `SlaMonitor.RunOnceAsync` sweeps open, un-paused tickets of **all** tenants, sets one-shot markers (`SlaWarningAt`, `ResponseBreachedAt`, `SlaBreachedAt`), saves, and **only then** publishes events - never an event for a change that did not persist. A failing push is logged and does not stop the rest. A concurrent human edit makes the sweep drop that batch and retry on the next tick. Moving a deadline (priority change, resume) clears the warning marker so the new window can warn again.
- `SlaMonitoringWorker` is a `BackgroundService` on a `PeriodicTimer`; an exception in one sweep is logged and the next tick runs. The sweep reads at most `Sla:BatchSize` tickets, oldest deadline first, and logs a warning when it hits the limit.

## 6. Audit trail

`AuditInterceptor` runs inside `SaveChanges`. For every `Added` ticket it writes a `Created` row; for every `Modified` ticket it diffs each property marked `[Audited]` (`Title`, `Description`, `Priority`, `Status`, `AssignedToUserId`, `ResolutionSummary`, `IsSlaBreached`). Rows are written in the same transaction, copy the ticket's `TenantId`, and record `ChangedByUserId` (null for the system). Rows written by one save are spaced one microsecond apart so the timeline order is deterministic. Raw ids are stored; the API resolves assignee ids to names only for display. A stale write that is rejected with 409 leaves no row. The trail covers tickets only (not SLA policies or user changes).

## 7. Concurrency

- `Ticket.Version` is a concurrency token for human edits; the stamping interceptor sets `Version = original + 1`. The service also compares the client's version first so a stale client gets a clean `409 version_conflict` without touching the row. A no-op edit writes nothing and keeps the version.
- `Ticket.SlaRevision` is the sweep's own token. System writes bump it instead of `Version`, so overlapping sweeps cannot both succeed and editors are never made stale by bookkeeping.
- `User.FailedLoginCount` is a concurrency token: parallel wrong guesses re-read and count on top of each other, so the lockout cannot be dodged by racing.
- Ticket numbers: `Tenant.TicketSequence` is a concurrency token. Two creators racing for the same number surface as either a concurrency exception or (on PostgreSQL) a unique-index violation on `(TenantId, Sequence)`; both are retried with a jittered back-off, then `409 ticket_number_contention`.
- A first-response comment also touches the ticket row, so `AddCommentAsync` re-reads and retries on a collision instead of failing.
- Unique-index races on tenant slugs and user e-mails are caught and returned as `409`.

## 8. Real-time

`OpsHub` accepts no client calls; membership is derived from the validated token at connect time: staff join `tenant:{id}:staff` (and nothing else, so they hear each event once); employees join `tenant:{id}:user:{userId}`. `SignalRTicketEventPublisher` sends every event to the staff group and, unless `StaffOnly`, to the requester's group. The browser passes the token as `access_token` on the hub path only. `CloseOnAuthenticationExpiration` ends a socket when its token expires; `HubConnectionRevoker` removes a deactivated user's open connections from all groups immediately (single node - see README limitations).

## 9. Persistence

PostgreSQL via Npgsql. Enums are stored as strings, timestamps as `timestamptz` (all values are UTC offsets of zero from `TimeProvider`). Because priority is text, anything ordered by priority is sorted in memory on the enum's numeric value. The initial migration lives in `src/OpsFlow.Infrastructure/Persistence/Migrations`; a PostgreSQL test fails if the model drifts from the migrations (`HasPendingModelChanges`). Create a migration with:

```bash
dotnet tool restore
dotnet ef migrations add <Name> -p src/OpsFlow.Infrastructure -s src/OpsFlow.Infrastructure -o Persistence/Migrations
```

## 10. Testing map

| Area | Where | Notes |
| --- | --- | --- |
| State machine | `StateMachineTests` | literal expected edges and role rules; every (from, to, role, requester) combination; summary boundary |
| SLA rules | `SlaTests`, `SlaMonitorTests` | fake clock; once-per-window events; pause/resume; reopen; staff-only |
| Isolation | `TenantIsolationTests`, `DbContextGuardTests` | two real tenants through the real HTTP pipeline, plus below-HTTP guards |
| Workflow API | `WorkflowApiTests` | lifecycle, roles per endpoint, assignment, validation limits, paging, races |
| Auth | `AuthTests` | lockout (incl. parallel), identical failure responses, token forgery, expiry, rate limits, platform key, start-up guards |
| Audit | `AuditTests` | old/new/actor, ordering, no noise |
| Real-time | `RealtimeTests` | routing per tenant / per requester, SLA alarms staff-only, revocation; listeners are warmed up before negative assertions |
| Demo data | `DemoDataTests` | every README account logs in; counts and numbering |
| Source-level guards | `ArchitectureGuardTests` | allow-lists, layering, secrets files |
| PostgreSQL | `PostgresTests` | composite FKs (constraint names), concurrency, parallel numbering and provisioning, overlapping sweeps, ordering, search, migration drift (needs `OPSFLOW_TEST_PG`) |

Mutation spot-checks were run while writing these tests: removing the tenant filter, the stamping guard, the audit interceptor, the employee visibility rule, the internal-note filter, the SLA resume ordering, the reopen restart, strict enums, the lockout token, the sweep token and the connection revoker each turns at least one test red.
