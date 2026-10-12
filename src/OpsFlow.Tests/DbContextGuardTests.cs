using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Core.Domain;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Tenancy;
using OpsFlow.Tests.Support;

namespace OpsFlow.Tests;

/// <summary>
/// The isolation guarantee lives in the DbContext, so it is tested there directly, below the HTTP layer:
/// even code that bypasses the controllers and services cannot leak or cross-write data.
/// </summary>
public class DbContextGuardTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;

    public DbContextGuardTests(TestApp app) => _app = app;

    private (IServiceScope Scope, OpsFlowDbContext Db, TenantContext Tenant) NewScope()
    {
        var scope = _app.Services.CreateScope();
        return (scope, scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>(), scope.ServiceProvider.GetRequiredService<TenantContext>());
    }

    /// <summary>Two tenants, each with an employee, an agent, a ticket, a comment (so every table holds rows for both).</summary>
    private async Task<(TenantHarness A, TenantHarness B)> TwoTenantsWithTicketsAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var a = await _app.CreateTenantAsync($"guard-a-{suffix}");
        var b = await _app.CreateTenantAsync($"guard-b-{suffix}");
        foreach (var t in new[] { a, b })
        {
            var emp = await t.AddUserAsync(UserRole.Employee, "emp");
            var agent = await t.AddUserAsync(UserRole.Agent, "agent");
            var ticket = await emp.CreateTicketAsync();
            await agent.PostAsync<OpsFlow.Core.Contracts.CommentDto>($"/api/tickets/{ticket.Id}/comments", new OpsFlow.Core.Contracts.AddCommentRequest { Content = "hello" });
        }

        return (a, b);
    }

    [Fact]
    public async Task With_no_tenant_resolved_every_tenant_scoped_query_is_empty()
    {
        await TwoTenantsWithTicketsAsync();
        var (scope, db, _) = NewScope();
        using var _ = scope;

        // Control: the rows really exist...
        Assert.NotEmpty(await db.Tickets.IgnoreQueryFilters().ToListAsync());
        Assert.NotEmpty(await db.Users.IgnoreQueryFilters().ToListAsync());
        Assert.NotEmpty(await db.SlaPolicies.IgnoreQueryFilters().ToListAsync());
        Assert.NotEmpty(await db.TicketComments.IgnoreQueryFilters().ToListAsync());
        Assert.NotEmpty(await db.TicketAuditLogs.IgnoreQueryFilters().ToListAsync());

        // ...yet without a tenant, nothing is visible.
        Assert.Empty(await db.Tickets.ToListAsync());
        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.SlaPolicies.ToListAsync());
        Assert.Empty(await db.TicketComments.ToListAsync());
        Assert.Empty(await db.TicketAuditLogs.ToListAsync());
        Assert.Equal(0, await db.Tickets.CountAsync());
        Assert.False(await db.Tickets.AnyAsync());
    }

    [Fact]
    public async Task A_context_scoped_to_one_tenant_sees_only_that_tenants_rows()
    {
        var (a, b) = await TwoTenantsWithTicketsAsync();
        var (scope, db, tenant) = NewScope();
        using var _ = scope;

        tenant.Use(a.Tenant.Id, a.Tenant.Slug);

        var tickets = await db.Tickets.ToListAsync();
        var users = await db.Users.ToListAsync();
        var logs = await db.TicketAuditLogs.ToListAsync();
        var comments = await db.TicketComments.ToListAsync();
        var policies = await db.SlaPolicies.ToListAsync();

        Assert.NotEmpty(tickets); Assert.NotEmpty(users); Assert.NotEmpty(logs); Assert.NotEmpty(comments); Assert.NotEmpty(policies); // not vacuous
        Assert.All(tickets, t => Assert.Equal(a.Tenant.Id, t.TenantId));
        Assert.All(users, u => Assert.Equal(a.Tenant.Id, u.TenantId));
        Assert.All(logs, l => Assert.Equal(a.Tenant.Id, l.TenantId));
        Assert.All(comments, c => Assert.Equal(a.Tenant.Id, c.TenantId));
        Assert.All(policies, p => Assert.Equal(a.Tenant.Id, p.TenantId));

        // Exactly the caller's rows - not fewer, not more.
        Assert.Equal(await db.Tickets.IgnoreQueryFilters().CountAsync(t => t.TenantId == a.Tenant.Id), tickets.Count);
        Assert.Equal(await db.Users.IgnoreQueryFilters().CountAsync(u => u.TenantId == a.Tenant.Id), users.Count);
        Assert.Equal(await db.TicketAuditLogs.IgnoreQueryFilters().CountAsync(l => l.TenantId == a.Tenant.Id), logs.Count);
        Assert.NotEmpty(await db.Tickets.IgnoreQueryFilters().Where(t => t.TenantId == b.Tenant.Id).ToListAsync()); // the other tenant's data really is there
    }

    [Fact]
    public async Task New_rows_are_stamped_with_the_current_tenant_automatically()
    {
        var (a, _) = await TwoTenantsWithTicketsAsync();
        var (scope, db, tenant) = NewScope();
        using var _ = scope;
        tenant.Use(a.Tenant.Id, a.Tenant.Slug);

        db.Users.Add(new User { Email = "stamp@test.dev", FullName = "Stamp Me", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        Assert.Equal(a.Tenant.Id, (await db.Users.SingleAsync(u => u.Email == "stamp@test.dev")).TenantId);
    }

    [Fact]
    public async Task Inserting_without_any_tenant_is_refused()
    {
        var (scope, db, _) = NewScope();
        using var _ = scope;

        db.Users.Add(new User { Email = "orphan@test.dev", FullName = "Orphan", PasswordHash = "x" });

        await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Inserting_a_row_for_a_different_tenant_is_refused()
    {
        var (a, b) = await TwoTenantsWithTicketsAsync();
        var (scope, db, tenant) = NewScope();
        using var _ = scope;
        tenant.Use(a.Tenant.Id, a.Tenant.Slug);

        db.Users.Add(new User { TenantId = b.Tenant.Id, Email = "smuggled@test.dev", FullName = "Smuggled", PasswordHash = "x" });

        await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Modifying_another_tenants_row_that_was_loaded_by_bypassing_the_filter_is_refused()
    {
        var (a, b) = await TwoTenantsWithTicketsAsync();
        var (scope, db, tenant) = NewScope();
        using var _ = scope;
        tenant.Use(a.Tenant.Id, a.Tenant.Slug);

        var foreign = await db.Tickets.IgnoreQueryFilters().FirstAsync(t => t.TenantId == b.Tenant.Id);
        foreign.Title = "tampered";

        await Assert.ThrowsAsync<TenantViolationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task A_rows_tenant_can_never_be_reassigned_not_by_ef_and_not_by_us()
    {
        var (a, b) = await TwoTenantsWithTicketsAsync();
        var (scope, db, tenant) = NewScope();
        using var _ = scope;
        tenant.Use(a.Tenant.Id, a.Tenant.Slug);

        var own = await db.Tickets.FirstAsync();
        own.TenantId = b.Tenant.Id;

        // EF itself refuses (TenantId is part of the composite alternate key); our guard is the second line.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public void Every_tenant_owned_entity_has_a_query_filter_and_is_mapped()
    {
        var (scope, db, _) = NewScope();
        using var _ = scope;

        var tenantEntityClrTypes = typeof(ITenantEntity).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ITenantEntity).IsAssignableFrom(t))
            .ToList();
        Assert.NotEmpty(tenantEntityClrTypes);

        foreach (var clr in tenantEntityClrTypes)
        {
            var entityType = db.Model.FindEntityType(clr);
            Assert.True(entityType is not null, $"{clr.Name} implements ITenantEntity but is not mapped.");
            var filter = Assert.Single(entityType.GetDeclaredQueryFilters());
            var text = filter.Expression!.ToString();
            Assert.True(text.Contains("TenantId") && text.Contains("CurrentTenantId"), $"{clr.Name} has a query filter, but it is not the tenant filter: {text}");
        }
    }

    [Fact]
    public void The_tenant_table_itself_is_the_only_unfiltered_root()
    {
        var (scope, db, _) = NewScope();
        using var _ = scope;

        var unfiltered = db.Model.GetEntityTypes()
            .Where(e => e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.ClrType)
            .ToList();

        Assert.Equal([typeof(Tenant)], unfiltered);
    }
}



