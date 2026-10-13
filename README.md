# OpsFlow

[![CI](https://github.com/Alonessam/OpsFlow/actions/workflows/ci.yml/badge.svg)](https://github.com/Alonessam/OpsFlow/actions/workflows/ci.yml)
[![.NET 10](https://img.shields.io/badge/.NET-10-512bd4)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](LICENSE)

**English** | [Türkçe](README.tr.md)

A **multi-tenant operations desk** (ticketing + SLA tracking + live dashboard) built so that one company's data cannot reach another company's users - even if a developer forgets a `WHERE` clause.

> **Everything in this repository is simulated.** The companies, people and tickets are fictional demo data. This is a portfolio project, not a hardened product; see [Known limitations](#known-limitations).

**Live demo: <https://opsflow-lw45.onrender.com>** - click one of the demo accounts on the login page. It runs on free hosting (Render + Neon): after 15 minutes without visitors the service sleeps, so the first request can take about a minute.

![The live operations desk: KPIs, tickets with live SLA countdowns](docs/screenshot-desk.jpg)

## Engineering highlights

- **Tenant isolation is enforced by the DbContext, not by each query.** A global query filter is attached to *every* entity that implements `ITenantEntity` (discovered by reflection), new rows are stamped with the caller's tenant automatically, and a `SaveChanges` interceptor refuses any write that would cross a tenant boundary. Services contain no hand-written tenant predicate; a source-level test fails `dotnet test` (and therefore CI) if one appears.
- **Fail-closed.** If no tenant is resolved the filter compares against `Guid.Empty`, which no row can carry: the result is an empty set, never "everything".
- **The database says no, too.** Tickets, comments and audit rows reference users/tickets through **composite `(TenantId, Id)` foreign keys**. PostgreSQL itself rejects a cross-tenant reference (`23503`, and the test checks *which* constraint fired), verified against a real server.
- **Tenant comes from the signed JWT only.** A client-supplied `X-Tenant-Id` header is ignored on reads *and* writes (tested), because anyone can type a header.
- **Workflow as a pure state machine.** A transition matrix + guard rules (assignee required, resolution summary required, only an admin may reopen a closed ticket, employees may only confirm/reject *their own* resolved ticket). The tests spell the 11 legal edges and the role rules out literally and check every status pair for every role and requester flag, so adding or removing an edge breaks a test.
- **SLA engine with a pausing clock.** Deadlines are snapshotted per ticket; waiting for the customer (`PendingClient`) stops the clock and pushes deadlines out on resume; reopening a ticket starts a fresh resolution window. A background worker raises *warning* (last 20 % of the window), *first-response breach* and *resolution breach*, **each once per window**, even when several sweeps overlap.
- **Automatic audit trail.** An EF Core interceptor writes one row per changed `[Audited]` ticket property (old -> new, who, when) in the same transaction as the change. System changes (the SLA worker) are recorded with no user.
- **Live operations desk.** SignalR pushes events to the right people only: staff of that tenant get everything; a requester gets events for their own tickets; SLA alarms and internal notes never reach the requester. Deactivating a user cuts their open socket immediately.
- **Optimistic concurrency.** Every ticket carries a `Version`; a stale edit gets `409`, and eight racing writers produce exactly one winner (tested on PostgreSQL). The SLA sweep uses its own token, so it never makes an editor's copy look stale.

## Try the demo

Demo data is **opt-in** (`Seed__Demo=true`; on by default only in the Development environment). Sign in with one of the demo chips on the login page (or type the tenant/e-mail yourself). All demo accounts share the **published** password `Demo!Passw0rd1` - never switch the demo on for an instance that holds real data.

| Tenant | Accounts |
| --- | --- |
| `acme` | `admin@acme.test` (tenant admin), `agent1@acme.test`, `agent2@acme.test`, `employee1@acme.test`, `employee2@acme.test` |
| `globex` | the same five, with `@globex.test` |

Things worth trying:

1. Open two browsers: `agent1@acme.test` in one, `agent1@globex.test` in the other. Create a ticket as `employee1@acme.test` - only Acme's desk lights up.
2. As an Acme agent, open a ticket and try *Resolve* with an empty summary (refused), then with one.
3. Move a ticket to *Pending client*: its SLA chip turns to **Paused**. Resume it and watch the deadline shift.
4. Look at a ticket's **Timeline** tab: every change, by whom.
5. Call the API with an Acme token and a Globex ticket id: you get `404`, not `403` - to Acme that ticket does not exist.

## Quick start

### Docker (PostgreSQL + API + UI)

```bash
cp .env.example .env        # set JWT_KEY to 32+ random characters; set SEED_DEMO=true for the demo
docker compose up --build
```

Open <http://localhost:8080>.

### Local (.NET 10 SDK)

With a PostgreSQL server (create an empty database; Development uses `appsettings.Development.json`):

```bash
dotnet run --project src/OpsFlow.API
```

Development defaults to `Host=localhost;Database=opsflow;Username=postgres;Password=postgres`, applies migrations on start and loads the demo data. Override with `ConnectionStrings__Default`. On Windows, `baslat.bat` does the same and opens the browser. (On the very first start against an empty database EF logs one `fail: ... "__EFMigrationsHistory"` line while it probes for its history table; that is expected.)

No PostgreSQL? Run entirely in memory (data is lost when the app stops; demo data reloads every start):

```bash
# bash / zsh
Database__Provider=InMemory dotnet run --project src/OpsFlow.API
```

```powershell
# PowerShell
$env:Database__Provider = "InMemory"; dotnet run --project src/OpsFlow.API
```

```bat
:: cmd.exe
set Database__Provider=InMemory
dotnet run --project src/OpsFlow.API
```

The app listens on <http://localhost:5080>.

### Tests

```bash
dotnet test                                   # everything that needs no external service
OPSFLOW_TEST_PG="Host=localhost;Username=postgres;Password=..." dotnet test   # + the real-PostgreSQL tests
```

Without `OPSFLOW_TEST_PG` the 18 PostgreSQL tests are skipped (CI always sets it and fails if any test is skipped). The connection string must not name a database: each test class creates and drops its own.

## Deploy for free (Render + Neon)

The repository ships a [`render.yaml`](render.yaml) Blueprint; the database is a free [Neon](https://neon.com) PostgreSQL.

1. **Neon:** create a project (pick a region near Render's, e.g. AWS Frankfurt) and copy the **direct** connection string (untick "Connection pooling"). It looks like `postgresql://user:password@ep-xxx.neon.tech/neondb?sslmode=require`; paste it as it is, the app converts the URL itself.
2. **Render:** *New -> Blueprint*, choose this repository, paste the Neon URL when asked for `ConnectionStrings__Default`, apply. `Jwt__Key` is generated for you; migrations run on start. The first build takes a few minutes.
3. Open the URL Render shows (`https://opsflow-xxxx.onrender.com`). With `Seed__Demo=true` (the Blueprint default) the demo chips appear on the login page.

What the free tiers mean in practice:

- **Render's free web service sleeps after 15 minutes without traffic** and needs about a minute to wake up. While it sleeps nothing runs - including the SLA worker, which catches up with one sweep when the service wakes.
- **Do not keep it awake with a pinger.** The SLA sweep queries the database every minute, so the database would never scale to zero, and Neon's free plan includes only 100 compute-hours a month (a continuously running minimum-size compute needs roughly 180): the database would be suspended for the rest of the month.
- **The demo is public.** `Seed__Demo=true` publishes the demo password on the login page, anyone can use the admin account, and the data is not reset. Set it to `false` for anything that is not a showcase; then create your own tenant with `Platform__ApiKey` (min. 16 characters) and `POST /api/platform/tenants`.
- Render's own free PostgreSQL expires after 30 days, which is why the database is on Neon.

Updates deploy automatically on every push to the default branch (`autoDeployTrigger: commit`).

## Features

1. **Tenants** - provisioned through a platform endpoint guarded by a static key (disabled unless configured, rate-limited). Each gets default SLA policies and an admin.
2. **Roles** - `Employee` (own tickets only), `Agent`, `TenantAdmin`.
3. **Tickets** - per-tenant numbering (`ACME-0007`), priorities, search/filter/paging, comments, internal notes. A requester states the urgency when opening a ticket; only staff can change it afterwards (triage).
4. **Workflow** - `New -> Assigned -> InProgress <-> PendingClient -> Resolved -> Closed`.
5. **SLA** - per-priority policies, editable by the tenant admin (applies to new tickets), live countdown chips.
6. **Audit timeline** per ticket.
7. **Live updates** over SignalR (with sound alert for breaches, off by default).
8. **Admin panel** - SLA policies, add/deactivate users. Deactivation is immediate on the node that handles it (cached access decisions are evicted, open sockets are cut) and at most 30 seconds on other nodes.

## Architecture

```mermaid
flowchart LR
    B[Browser<br/>vanilla JS + SignalR] -->|HTTP + JWT<br/>TLS at the proxy| MW
    subgraph API[OpsFlow.API]
      MW[Auth -> TenantResolution<br/>middleware] --> C[Controllers]
      C --> S
      H[OpsHub<br/>SignalR]
    end
    subgraph INF[OpsFlow.Infrastructure]
      S[TicketService / AuthService / ...] --> DB[(OpsFlowDbContext)]
      DB --- F[[Global query filter<br/>on every ITenantEntity]]
      DB --- I[[Stamping + Audit<br/>interceptors]]
      W[SlaMonitoringWorker] --> S2[SlaMonitor<br/>cross-tenant sweep]
      S2 --> DB
    end
    DB --> PG[(PostgreSQL 16<br/>composite tenant FKs)]
    S -->|TicketEvent| H
    S2 -->|TicketEvent| H
    H -->|staff group / requester group| B
```

Layers: `OpsFlow.Core` (entities, state machine, SLA rules, contracts - no dependencies), `OpsFlow.Infrastructure` (EF Core, services, worker), `OpsFlow.API` (controllers, hub, middleware, static UI). Details in [docs/architecture.md](docs/architecture.md).

## Design notes

1. **Why a global filter *and* composite foreign keys?** The filter protects reads and normal writes; the composite FKs protect against everything else (a bug, a manual SQL fix, a future code path that skips the filter). Two independent layers, each tested.
2. **Why 404 instead of 403 across tenants?** `403` confirms the row exists. From another tenant's point of view it does not.
3. **Why the tenant is not a header.** A header is attacker-controlled. The tenant id is a claim inside a signed token; changing it invalidates the signature (tested by tampering with the payload).
4. **Where the escape hatches are allowed.** `IgnoreQueryFilters` only in the cross-tenant SLA sweep; the mutable `TenantContext` type only in login, the middleware, system jobs and DI wiring; raw SQL and bulk `ExecuteUpdate/Delete` nowhere. Tests pin those lists (matching on the type name, not on a variable name) and fail if an allow-list entry goes stale, so a new escape hatch shows up in review.
5. **SLA snapshots and windows.** Editing a policy never rewrites existing tickets. Changing a ticket's *priority* recomputes its deadline from the start of the current window and keeps time already spent paused. **Reopening** a resolved/closed ticket starts a new resolution window from the reopen time (the old breach, if any, stays visible in the audit timeline); the first-response history is kept. A customer's reply does not resume a paused clock by itself - only a staff transition does.
6. **Ticket numbers** are allocated on the tenant row under a concurrency token with bounded, jittered retries. This is simple and correct under contention (tested with 25 parallel creates on PostgreSQL) but it serialises ticket creation per tenant; a database sequence would scale better.
7. **One clock.** Everything reads `TimeProvider`, including JWT lifetime validation, so tests drive time forward instead of sleeping.
8. **Two concurrency tokens.** Humans bump `Version`; the SLA sweep bumps its own `SlaRevision`. If the sweep bumped `Version`, every open editor's copy would turn stale exactly when a ticket becomes urgent; if it bumped nothing, two overlapping sweeps could both raise the same event. (Tests force the interleaving deterministically - two contexts load the same ticket before either saves - and also run the real overlap on PostgreSQL.)
9. **Strict JSON.** Enums travel as names only (`"priority": "High"`); numbers are a `400`. Required fields (`version`, `to`, `isActive` ...) are declared `required`, so an empty `{}` can never silently mean "false" or "0".

## Workflow

```mermaid
stateDiagram-v2
    [*] --> New
    New --> Assigned: assignee required
    Assigned --> InProgress
    Assigned --> New: unassign
    InProgress --> PendingClient: SLA clock pauses
    PendingClient --> InProgress: SLA clock resumes
    InProgress --> Resolved: summary required
    PendingClient --> Resolved: summary required
    InProgress --> Assigned
    Resolved --> Closed
    Resolved --> InProgress: requester rejects fix (new SLA window)
    Closed --> InProgress: tenant admin only (new SLA window)
```

Default SLA targets (editable per tenant):

| Priority | First response | Resolution |
| --- | --- | --- |
| Critical | 30 min | 2 h |
| High | 1 h | 8 h |
| Medium | 4 h | 24 h |
| Low | 8 h | 72 h |

## Technology stack

- **Backend:** .NET 10, ASP.NET Core Web API, EF Core 10 + Npgsql, SignalR, BCrypt.Net (enhanced mode), JWT bearer
- **Database:** PostgreSQL 16 (in-memory provider for fast tests and the no-database mode)
- **Frontend:** semantic HTML, CSS (light/dark), vanilla ES2020, SignalR JS client (vendored, no CDN)
- **Quality:** xUnit, `WebApplicationFactory`, warnings-as-errors, NuGet audit, CodeQL, Dependabot

## Security model

| Risk | Response |
| --- | --- |
| Cross-tenant read | Global query filter on every `ITenantEntity`; fail-closed when no tenant; `404` for foreign ids |
| Cross-tenant write | Stamping interceptor rejects foreign/empty tenant ids; `TenantId` is immutable; composite FKs in PostgreSQL |
| Forged tenant / token | Tenant only from the signed JWT; headers ignored; wrong key, wrong issuer/audience, expired, `alg=none`, missing or malformed tenant claims are all `401` (each tested) |
| Privilege inside a tenant | Employees see only their own tickets and no internal notes; role rules are enforced in the service layer (and by `[Authorize]` on the admin endpoints) |
| Credential guessing | BCrypt (enhanced, so long passwords are fully significant); 5 failures lock the account for 15 min (counted correctly under parallel guessing); login rate-limited per IP; unknown tenant, unknown user, deactivated user, inactive tenant, locked account and wrong password give an identical response and cost one bcrypt each |
| Stale access | Deactivating a user evicts the cached access decision and removes their open sockets from every group at once (single node); other nodes wait at most 30 s; a socket also closes when its token expires. An inactive tenant is refused the same way |
| Lost updates | Optimistic concurrency `Version` -> `409`; a rejected stale write leaves nothing in the audit trail |
| Injection / XSS | EF parameterisation; UI renders with `textContent` only; strict CSP (`script-src 'self'`), `nosniff`, `frame-ancestors 'none'` - re-applied on error responses; `Cache-Control: no-store` on API data |
| Weak or leaked secrets | App refuses to start with a short or repetitive JWT key, with the committed development key outside Development, or with a platform key under 16 characters; no secrets in `appsettings.json` (tested); compose requires `JWT_KEY`; demo data is off unless asked for |
| Abuse of anonymous endpoints | Login and platform endpoints are rate-limited; the platform key is checked before the body is read; request bodies are capped at 1 MB (`413`); unknown paths are a plain `404`; HSTS behind a trusted HTTPS proxy |
| Vulnerable dependencies | NuGet audit fails the build; CI re-audits weekly |

## Known limitations

This is honest about what it is not:

- **Simulated data, single-purpose.** No e-mail, attachments, password reset, MFA or SSO.
- **Isolation is application-level plus FKs.** PostgreSQL Row-Level Security is not used; it would be the next hardening step.
- **SLA uses wall-clock minutes.** No business hours, holidays or timezones. The sweep re-reads every open ticket of every tenant each interval (batch-limited, with a warning when the limit is hit); fine for a demo, it would need a smarter query at scale.
- **Single node.** SignalR groups and the access cache live in memory; scaling out needs a Redis backplane, shared revocation and a shared rate limiter. Connection revocation and cache eviction act on the node that handled the request.
- **JWT lives in `sessionStorage`.** The strict CSP reduces XSS risk, but an HttpOnly-cookie flow with refresh-token rotation would be stronger. Tokens last 30 minutes. For WebSockets the token travels in the query string of the hub URL (browsers cannot set headers there), which can end up in proxy logs.
- **TLS is the proxy's job.** The container speaks plain HTTP and does not redirect; it sends `Strict-Transport-Security` only for requests that arrived over HTTPS behind a trusted proxy. Set `Proxy__TrustForwardedHeaders=true` **only** behind a trusted proxy; otherwise clients can spoof their IP and dodge the rate limiter - and behind a proxy without it, every client shares one IP and one login bucket. (Tests cover both cases, including a forged `X-Forwarded-For`.)
- **The lockout can be abused.** Five wrong guesses against a known e-mail lock that account for 15 minutes; an attacker can keep a known account locked. That is the usual trade-off of per-account lockout.
- **The platform key is a static shared secret.** Fine for a demo bootstrap; a real operator plane would use proper identity. There is no endpoint to suspend a tenant (the access check honours `IsActive`, but it can only be flipped in the database).
- **Audit scope is the ticket.** SLA-policy edits and user (de)activations are not recorded. Tickets assigned to an agent who is later deactivated stay assigned until someone reassigns them.
- **Search** is a case-insensitive substring match on title and number; for non-ASCII letters the result depends on the database collation.
- **Ticket creation is serialised per tenant** (see design note 6).
- **Docker files are provided and built in CI**, but I have not tested them on every platform.

## Setup and configuration

Configuration is read from `appsettings*.json` and environment variables (`__` for nesting).

| Variable | Default | Purpose |
| --- | --- | --- |
| `ConnectionStrings__Default` | - | PostgreSQL connection string |
| `Jwt__Key` | **required**, 32+ chars, not repetitive | Token signing key (the committed development key is refused outside Development) |
| `Jwt__ExpiryMinutes` | `30` | Token lifetime |
| `Database__Provider` | `Postgres` | `Postgres` or `InMemory` |
| `Database__MigrateOnStartup` | `false` (`true` in Development) | Apply EF migrations at start |
| `Seed__Demo` | `false` (`true` in Development) | Load the two demo companies **and publish their password** |
| `Platform__ApiKey` | empty (disabled) | Enables `POST /api/platform/tenants`; min. 16 characters |
| `Sla__IntervalSeconds` | `60` | SLA sweep interval |
| `Sla__BatchSize` | `2000` | Tickets examined per sweep |
| `Sla__WorkerEnabled` | `true` | Run the background worker |
| `RateLimit__LoginPerMinute` | `10` | Login attempts per IP per minute |
| `RateLimit__PlatformPerMinute` | `10` | Platform-endpoint attempts per IP per minute |
| `Security__BcryptWorkFactor` | `12` | BCrypt cost |
| `Proxy__TrustForwardedHeaders` | `false` | Set `true` **only** behind a trusted reverse proxy |
| `Proxy__ForwardLimit` | `1` | Number of proxies in front of the app; the client address is that many entries from the right of `X-Forwarded-For` (Render: `3`) |

### API overview

JSON bodies use camelCase; enums are **names** (`"priority": "High"`). Errors are `application/problem+json` with a machine-readable `code`.

| Method | Path | Who |
| --- | --- | --- |
| `POST` | `/api/auth/login` | anonymous (rate-limited) |
| `GET` | `/api/auth/me` | any |
| `GET` | `/api/tickets` `?status&priority&assignedToId&breached&mine&search&page&pageSize` | any (employees: own only) |
| `POST` | `/api/tickets` | any |
| `GET` / `PATCH` | `/api/tickets/{id}` | any (own for employees; `version` required on PATCH) |
| `POST` | `/api/tickets/{id}/transitions` | per workflow rules (`to` and `version` required) |
| `POST` | `/api/tickets/{id}/assign` | staff |
| `GET` / `POST` | `/api/tickets/{id}/comments` | any (internal notes: staff) |
| `GET` | `/api/tickets/{id}/history` | any (own for employees) |
| `GET` | `/api/dashboard/summary` | staff |
| `GET` / `POST` | `/api/users` | staff list / admin create |
| `PATCH` | `/api/users/{id}/status` | admin (`isActive` required) |
| `GET` / `PUT` | `/api/sla-policies`, `/api/sla-policies/{priority}` | any read / admin write |
| `POST` | `/api/platform/tenants` | `X-Platform-Key` |
| `GET` | `/health`, `/api/meta` | anonymous |
| WS | `/hubs/ops` | authenticated |

## More documentation

- [Architecture](docs/architecture.md)
- [Tasarım kararları (TR)](docs/tasarim-kararlari.md) - design decisions and their reasons, in Turkish
- [Changelog](CHANGELOG.md) · [Contributing](CONTRIBUTING.md) · [Security policy](SECURITY.md)

## License

MIT - see [LICENSE](LICENSE). Author: Alonessam.
