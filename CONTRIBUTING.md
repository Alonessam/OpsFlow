# Contributing

1. `dotnet build` must be warning-free (warnings are errors).
2. `dotnet test` must pass. For database-facing changes also run with `OPSFLOW_TEST_PG` set to a PostgreSQL server (see README); CI does, and it fails if any test is skipped.
3. **A new entity that belongs to a tenant must implement `ITenantEntity`.** It then gets the query filter automatically; `DbContextGuardTests` fails if it is not mapped or not filtered.
4. Do not use `IgnoreQueryFilters`, `TenantContext`, `UseSystem` or raw SQL / `ExecuteUpdate` / `ExecuteDelete` outside the files allowed by `ArchitectureGuardTests`. If you genuinely need a new cross-tenant job, extend that allow-list in the same PR and explain why.
5. Model change -> add a migration (`dotnet tool restore`, then `dotnet ef migrations add ...`; see `docs/architecture.md`). A PostgreSQL test fails on drift.
6. Request DTOs: mark anything that must be present as `required`, and give enum properties `[EnumDataType]`. Enums are sent as names only.
7. Never commit secrets. `Jwt:Key`, `Platform:ApiKey` and `ConnectionStrings:Default` stay empty in `appsettings.json`; demo data stays opt-in.
