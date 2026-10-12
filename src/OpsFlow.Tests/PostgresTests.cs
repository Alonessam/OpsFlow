using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Sla;
using OpsFlow.Infrastructure.Tenancy;
using OpsFlow.Tests.Support;

namespace OpsFlow.Tests;

/// <summary>
/// Set OPSFLOW_TEST_PG to a server connection string WITHOUT a database, e.g.
/// "Host=localhost;Port=5432;Username=postgres;Password=...". Each class gets a fresh database that is
/// created from the real EF migrations and dropped afterwards. CI always sets it; locally the tests
/// are skipped when it is absent, so `dotnet test` still works without a server.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string EnvVar = "OPSFLOW_TEST_PG";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvVar)))
        {
            Skip = $"{EnvVar} is not set; real-PostgreSQL tests skipped.";
        }
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly string? _server = Environment.GetEnvironmentVariable(PostgresFactAttribute.EnvVar);
    private readonly string _dbName = $"opsflow_t_{Guid.NewGuid():N}";

    public TestApp? App { get; private set; }

    public string ConnectionString => new NpgsqlConnectionStringBuilder(_server!) { Database = _dbName, Pooling = true, MaxPoolSize = 50 }.ConnectionString;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_server))
        {
            return;
        }

        await Admin($"CREATE DATABASE \"{_dbName}\"");
        App = TestApp.With(postgres: ConnectionString);
        _ = App.Server; // boot: applies migrations
    }

    public async Task DisposeAsync()
    {
        App?.Dispose();
        if (!string.IsNullOrWhiteSpace(_server))
        {
            NpgsqlConnection.ClearAllPools();
            await Admin($"DROP DATABASE IF EXISTS \"{_dbName}\" WITH (FORCE)");
        }
    }

    private async Task Admin(string sql)
    {
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_server!) { Database = "postgres", Pooling = false }.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}

public class PostgresTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;

    public PostgresTests(PostgresFixture pg) => _pg = pg;

    private TestApp App => _pg.App!;

    private async Task<(TenantHarness T, Actor Agent, Actor Employee)> SetupAsync(string prefix)
    {
        var t = await App.CreateTenantAsync($"{prefix}-{Guid.NewGuid():N}"[..16]);
        return (t, await t.AddUserAsync(UserRole.Agent, "agent"), await t.AddUserAsync(UserRole.Employee, "emp"));
    }

    private (IServiceScope Scope, OpsFlowDbContext Db, TenantContext Tenant) NewScope()
    {
        var scope = App.Services.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>(), scope.ServiceProvider.GetRequiredService<TenantContext>());
    }

    private static PostgresException AsPostgres(DbUpdateException ex) => Assert.IsType<PostgresException>(ex.InnerException);

    // ---- schema ---------------------------------------------------------------------------------------

    [PostgresFact]
    public void The_migrations_are_in_sync_with_the_model()
    {
        var (scope, db, _) = NewScope();
        using var _ = scope;

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a new migration (dotnet ef migrations add ...).");
        Assert.Empty(db.Database.GetPendingMigrations());
    }

    [PostgresFact]
    public void The_generated_sql_binds_the_tenant_in_the_where_clause()
    {
        var (scope, db, tenant) = NewScope();
        using var _ = scope;
        var tenantId = Guid.NewGuid();
        tenant.Use(tenantId, "x");

        var sql = db.Tickets.Where(t => t.Title == "x").ToQueryString();

        // Not just "the column is selected" (every SELECT lists it): the WHERE clause itself must compare it.
        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        Assert.Matches(new Regex(@"""TenantId""\s*=\s*@", RegexOptions.Singleline), where);
        Assert.Contains(tenantId.ToString(), sql); // and the bound value is the caller's tenant

        // A context with NO tenant must bind the impossible id, not drop the predicate.
        var (scope2, db2, _) = NewScope();
        using var _2 = scope2;
        var sqlNone = db2.Tickets.ToQueryString();
        Assert.Contains(Guid.Empty.ToString(), sqlNone);
        Assert.Matches(new Regex(@"WHERE.*""TenantId""\s*=\s*@", RegexOptions.Singleline), sqlNone);
    }

    // ---- the database refuses cross-tenant references --------------------------------------------------

    [PostgresFact]
    public async Task The_database_itself_refuses_a_ticket_that_references_another_tenants_user()
    {
        var (a, _, _) = await SetupAsync("fk-a");
        var (_, agentB, _) = await SetupAsync("fk-b");
        var (scope, db, tenant) = NewScope();
        using var _ = scope;
        tenant.UseSystem(); // bypass every application-level check on purpose

        db.Tickets.Add(new Ticket
        {
            TenantId = a.Tenant.Id, Sequence = 999, TicketNumber = "FK-0999", Title = "smuggled", Description = "points at tenant B's user",
            CreatedByUserId = agentB.Id, // user of ANOTHER tenant
            CreatedAt = DateTimeOffset.UtcNow
        });

        var pg = AsPostgres(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, pg.SqlState);
        Assert.Contains("CreatedByUserId", pg.ConstraintName); // THIS foreign key fired, not some other one
    }

    [PostgresFact]
    public async Task The_database_itself_refuses_a_cross_tenant_assignee_comment_author_and_audit_row()
    {
        var (a, _, empA) = await SetupAsync("fk2-a");
        var (b, agentB, _) = await SetupAsync("fk2-b");
        var ticket = await empA.CreateTicketAsync();
        var (scope, db, tenant) = NewScope();
        using var _s = scope;
        tenant.UseSystem();

        var entity = await db.Tickets.IgnoreQueryFilters().FirstAsync(t => t.Id == ticket.Id);
        entity.AssignedToUserId = agentB.Id; // assignee from another tenant
        var assign = AsPostgres(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, assign.SqlState);
        Assert.Contains("AssignedToUserId", assign.ConstraintName);

        db.ChangeTracker.Clear();
        db.TicketComments.Add(new TicketComment { TenantId = a.Tenant.Id, TicketId = ticket.Id, UserId = agentB.Id, Content = "x", CreatedAt = DateTimeOffset.UtcNow });
        var author = AsPostgres(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, author.SqlState);
        Assert.Contains("UserId", author.ConstraintName);

        db.ChangeTracker.Clear();
        db.TicketComments.Add(new TicketComment { TenantId = b.Tenant.Id, TicketId = ticket.Id, UserId = agentB.Id, Content = "x", CreatedAt = DateTimeOffset.UtcNow }); // B's tenant, A's ticket
        var commentOnForeignTicket = AsPostgres(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, commentOnForeignTicket.SqlState);
        Assert.Contains("Tickets", commentOnForeignTicket.ConstraintName);

        db.ChangeTracker.Clear();
        db.TicketAuditLogs.Add(new TicketAuditLog { TenantId = b.Tenant.Id, TicketId = ticket.Id, PropertyName = "Title", Timestamp = DateTimeOffset.UtcNow }); // forged audit row
        var audit = AsPostgres(await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, audit.SqlState);
        Assert.Contains("Tickets", audit.ConstraintName);
    }

    // ---- concurrency -----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Two_writers_holding_the_same_version_cannot_both_save()
    {
        var (a, _, employee) = await SetupAsync("occ");
        var ticket = await employee.CreateTicketAsync();

        var (s1, db1, t1) = NewScope();
        var (s2, db2, t2) = NewScope();
        using var _1 = s1;
        using var _2 = s2;
        t1.Use(a.Tenant.Id, a.Tenant.Slug, employee.Id, UserRole.Employee);
        t2.Use(a.Tenant.Id, a.Tenant.Slug, employee.Id, UserRole.Employee);

        var first = await db1.Tickets.FirstAsync(t => t.Id == ticket.Id);
        var second = await db2.Tickets.FirstAsync(t => t.Id == ticket.Id);
        first.Title = "writer one";
        second.Title = "writer two";

        await db1.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());
        Assert.Equal("writer one", (await employee.GetTicketAsync(ticket.Id)).Title);
    }

    [PostgresFact]
    public async Task Parallel_creates_yield_unique_gapless_ticket_numbers()
    {
        var (_, _, employee) = await SetupAsync("seq");

        var tickets = await Task.WhenAll(Enumerable.Range(0, 25).Select(i => employee.CreateTicketAsync($"burst {i}")));

        var numbers = tickets.Select(t => int.Parse(t.TicketNumber[^4..])).Order().ToList();
        Assert.Equal(Enumerable.Range(1, 25), numbers);
    }

    [PostgresFact]
    public async Task Racing_patches_over_http_produce_exactly_one_winner()
    {
        var (_, agent, employee) = await SetupAsync("race");
        var ticket = await employee.CreateTicketAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Title = $"racer {i}", Version = ticket.Version })));

        Assert.Equal(1, results.Count(r => r.Response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(7, results.Count(r => r.Response.StatusCode == HttpStatusCode.Conflict));
    }

    [PostgresFact]
    public async Task A_first_response_comment_racing_with_other_writes_never_ends_in_a_server_error()
    {
        var (_, agent, employee) = await SetupAsync("crace");

        for (var round = 0; round < 5; round++)
        {
            var ticket = await employee.CreateTicketAsync($"race {round}");
            var statuses = await Task.WhenAll(
                agent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest { Content = "On it." }).ContinueWith(t => t.Result.Response.StatusCode),
                agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Title = "renamed", Version = ticket.Version }).ContinueWith(t => t.Result.Response.StatusCode),
                agent.TransitionAsync(ticket, TicketStatus.Assigned, agent.Id).ContinueWith(t => t.Result.Response.StatusCode));

            Assert.DoesNotContain(statuses, s => (int)s >= 500);

            // A real transaction: the comment exists if and only if the request said 201 (no duplicates from a retry).
            var (_, comments) = await agent.GetAsync<List<CommentDto>>($"/api/tickets/{ticket.Id}/comments");
            Assert.Equal(statuses[0] == HttpStatusCode.Created ? 1 : 0, comments!.Count);
        }
    }

    [PostgresFact]
    public async Task Overlapping_sla_sweeps_raise_each_event_once()
    {
        var (_, agent, employee) = await SetupAsync("sweep-race");
        var ticket = await employee.CreateTicketAsync(priority: TicketPriority.Critical);
        var (_, assigned) = await agent.TransitionAsync(ticket, TicketStatus.Assigned, agent.Id);
        await agent.TransitionAsync(assigned!, TicketStatus.InProgress);
        App.Clock.Advance(TimeSpan.FromHours(3));

        var monitor = App.Services.GetRequiredService<SlaMonitor>();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => monitor.RunOnceAsync()));

        Assert.Single(App.Publisher.Events, e => e.TicketId == ticket.Id && e.Type == TicketEventType.SlaBreached);
    }

    // ---- behaviour that differs between InMemory and a real database -----------------------------------

    [PostgresFact]
    public async Task Sla_policies_come_back_most_urgent_first_not_alphabetically()
    {
        var (_, agent, _) = await SetupAsync("policy-order");

        var (_, policies) = await agent.GetAsync<List<SlaPolicyDto>>("/api/sla-policies");

        Assert.Equal([TicketPriority.Critical, TicketPriority.High, TicketPriority.Medium, TicketPriority.Low], policies!.Select(p => p.Priority));
    }

    [PostgresFact]
    public async Task Search_treats_wildcards_literally_and_ignores_case()
    {
        var (_, agent, employee) = await SetupAsync("search");
        var percent = await employee.CreateTicketAsync("Disk 100% full");
        await employee.CreateTicketAsync("Disk 100 full");
        var underscore = await employee.CreateTicketAsync("file_name missing");
        await employee.CreateTicketAsync("fileXname missing");

        var (_, byPercent) = await agent.GetAsync<PagedResult<TicketDto>>("/api/tickets?search=100%25");
        var (_, byUnderscore) = await agent.GetAsync<PagedResult<TicketDto>>("/api/tickets?search=FILE_NAME");

        Assert.Equal([percent.Id], byPercent!.Items.Select(t => t.Id));
        Assert.Equal([underscore.Id], byUnderscore!.Items.Select(t => t.Id));
    }

    [PostgresFact]
    public async Task Paging_is_stable_when_every_row_has_the_same_timestamp()
    {
        var (_, agent, employee) = await SetupAsync("paging");
        for (var i = 0; i < 7; i++) await employee.CreateTicketAsync($"tie {i}");

        var ids = new List<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var (_, p) = await agent.GetAsync<PagedResult<TicketDto>>($"/api/tickets?pageSize=2&page={page}");
            ids.AddRange(p!.Items.Select(t => t.Id));
        }

        Assert.Equal(7, ids.Distinct().Count());
    }

    [PostgresFact]
    public async Task Audit_rows_of_one_save_come_back_in_a_stable_order()
    {
        var (_, agent, employee) = await SetupAsync("audit-order");
        var ticket = await employee.CreateTicketAsync();
        await agent.TransitionAsync(ticket, TicketStatus.Assigned, agent.Id);

        for (var i = 0; i < 3; i++)
        {
            var (_, history) = await agent.GetAsync<List<AuditEntryDto>>($"/api/tickets/{ticket.Id}/history");
            Assert.Equal(["Created", "Status", "AssignedToUserId"], history!.Select(h => h.Field));
        }
    }

    // ---- end to end on the real database ---------------------------------------------------------------

    [PostgresFact]
    public async Task Isolation_and_audit_hold_end_to_end_on_postgres()
    {
        var (_, agentA, empA) = await SetupAsync("e2e-a");
        var (_, _, empB) = await SetupAsync("e2e-b");
        var mine = await empA.CreateTicketAsync("mine");
        var theirs = await empB.CreateTicketAsync("theirs");

        var (_, list) = await agentA.GetAsync<PagedResult<TicketDto>>("/api/tickets");
        var peek = await agentA.GetAsync<TicketDto>($"/api/tickets/{theirs.Id}");
        var assigned = await agentA.TransitionAsync(mine, TicketStatus.Assigned, agentA.Id);
        var (_, history) = await agentA.GetAsync<List<AuditEntryDto>>($"/api/tickets/{mine.Id}/history");

        Assert.Equal([mine.Id], list!.Items.Select(t => t.Id));
        Assert.Equal(HttpStatusCode.NotFound, peek.Response.StatusCode);
        Assert.True(assigned.Response.IsSuccessStatusCode);
        Assert.Contains(history!, h => h is { Field: "Status", OldValue: "New", NewValue: "Assigned" });
    }

    [PostgresFact]
    public async Task The_sla_sweep_works_across_tenants_on_postgres()
    {
        var (_, _, empA) = await SetupAsync("sweep-a");
        var (_, _, empB) = await SetupAsync("sweep-b");
        var a = await empA.CreateTicketAsync(priority: TicketPriority.Critical);
        var b = await empB.CreateTicketAsync(priority: TicketPriority.Critical);

        App.Clock.Advance(TimeSpan.FromHours(3));
        var raised = await App.Services.GetRequiredService<SlaMonitor>().RunOnceAsync();

        Assert.True(raised >= 2);
        Assert.Contains(App.Publisher.Events, e => e.TicketId == a.Id && e.Type == TicketEventType.SlaBreached);
        Assert.Contains(App.Publisher.Events, e => e.TicketId == b.Id && e.Type == TicketEventType.SlaBreached);
    }

    [PostgresFact]
    public async Task Parallel_provisioning_of_one_slug_creates_one_tenant_and_the_rest_get_a_clean_conflict()
    {
        async Task<HttpStatusCode> Provision()
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/platform/tenants")
            {
                Content = JsonContent.Create(new CreateTenantRequest { Name = "Racers", Slug = "pg-race-slug", AdminEmail = "a@x.test", AdminFullName = "Ad Min", AdminPassword = TestApp.Password })
            };
            request.Headers.Add("X-Platform-Key", TestApp.PlatformKey);
            return (await App.Anonymous().SendAsync(request)).StatusCode;
        }

        var statuses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Provision()));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.All(statuses.Where(s => s != HttpStatusCode.Created), s => Assert.Equal(HttpStatusCode.Conflict, s)); // never a 500
    }

    [PostgresFact]
    public async Task Parallel_creation_of_one_email_creates_one_user_and_the_rest_get_a_clean_conflict()
    {
        var (t, _, _) = await SetupAsync("mail-race");

        var statuses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => t.Admin.PostAsync<UserDto>("/api/users",
            new CreateUserRequest { Email = "same@x.test", FullName = "Same Person", Role = UserRole.Agent, Password = TestApp.Password }))
            .Select(async call => (await call).Response.StatusCode));

        Assert.Equal(1, statuses.Count(s => s == HttpStatusCode.Created));
        Assert.All(statuses.Where(s => s != HttpStatusCode.Created), s => Assert.Equal(HttpStatusCode.Conflict, s));
    }
    [PostgresFact]
    public async Task Provisioning_a_tenant_is_atomic_and_a_duplicate_slug_is_a_clean_conflict()
    {
        await App.CreateTenantAsync("pg-dupe");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/platform/tenants")
        {
            Content = JsonContent.Create(new CreateTenantRequest { Name = "Dupe", Slug = "pg-dupe", AdminEmail = "x@y.test", AdminFullName = "Ad Min", AdminPassword = TestApp.Password })
        };
        request.Headers.Add("X-Platform-Key", TestApp.PlatformKey);
        var response = await App.Anonymous().SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var (scope, db, tenant) = NewScope();
        using var _ = scope;
        tenant.UseSystem();
        Assert.Equal(1, await db.Tenants.CountAsync(t => t.Slug == "pg-dupe"));
    }
}


