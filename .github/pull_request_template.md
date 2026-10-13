## What and why

## Checklist

- [ ] `dotnet build` is warning-free and `dotnet test` passes (with `OPSFLOW_TEST_PG` set if the change touches the database)
- [ ] New tenant-owned entities implement `ITenantEntity`; no `IgnoreQueryFilters`, `TenantContext` or raw SQL outside the allow-listed files (`ArchitectureGuardTests`)
- [ ] Model change -> a migration was added
- [ ] Behaviour change -> a test that fails without the change
- [ ] No secrets, tokens or connection strings in the diff
