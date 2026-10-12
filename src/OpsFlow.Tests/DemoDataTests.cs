using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Sla;
using OpsFlow.Infrastructure.Tenancy;
using OpsFlow.Tests.Support;

namespace OpsFlow.Tests;

/// <summary>The README promises specific demo accounts and data. This keeps the promise honest.</summary>
public class DemoDataTests
{
    private static readonly string[] Tenants = ["acme", "globex"];
    private static readonly string[] Accounts = ["admin", "agent1", "agent2", "employee1", "employee2"];

    [Fact]
    public async Task Meta_advertises_the_demo_only_when_it_is_switched_on()
    {
        using var app = TestApp.With(seedDemo: true);

        var meta = await app.Anonymous().GetStringAsync("/api/meta");

        Assert.Contains("\"demo\":true", meta);
        Assert.Contains(DemoDataSeeder.DemoPassword, meta);
    }

    [Fact]
    public async Task Every_account_listed_in_the_readme_can_log_in_with_the_published_password()
    {
        using var app = TestApp.With(seedDemo: true);

        foreach (var tenant in Tenants)
            foreach (var account in Accounts)
            {
                var response = await app.Anonymous().PostAsJsonAsync("/api/auth/login",
                    new LoginRequest { TenantSlug = tenant, Email = $"{account}@{tenant}.test", Password = DemoDataSeeder.DemoPassword });

                Assert.True(response.StatusCode == HttpStatusCode.OK, $"{account}@{tenant}.test could not log in ({response.StatusCode})");
            }
    }

    [Fact]
    public async Task Each_demo_tenant_has_eight_tickets_numbered_from_one_and_sees_only_its_own()
    {
        using var app = TestApp.With(seedDemo: true);
        var acme = await app.LoginAsync("acme", "agent1@acme.test", DemoDataSeeder.DemoPassword);
        var globex = await app.LoginAsync("globex", "agent1@globex.test", DemoDataSeeder.DemoPassword);

        var (_, acmeTickets) = await acme.GetAsync<PagedResult<TicketDto>>("/api/tickets?pageSize=100");
        var (_, globexTickets) = await globex.GetAsync<PagedResult<TicketDto>>("/api/tickets?pageSize=100");

        Assert.Equal(8, acmeTickets!.TotalCount);
        Assert.Equal(8, globexTickets!.TotalCount);
        Assert.All(acmeTickets.Items, t => Assert.StartsWith("ACME-", t.TicketNumber));
        Assert.All(globexTickets.Items, t => Assert.StartsWith("GLOBEX-", t.TicketNumber));
        Assert.Contains(acmeTickets.Items, t => t.TicketNumber == "ACME-0007"); // the README refers to this number
    }

    [Fact]
    public async Task Demo_people_have_plain_names_without_role_decorations()
    {
        using var app = TestApp.With(seedDemo: true);
        var admin = await app.LoginAsync("acme", "admin@acme.test", DemoDataSeeder.DemoPassword);

        var (_, users) = await admin.GetAsync<List<UserDto>>("/api/users");

        Assert.Equal(5, users!.Count);
        Assert.DoesNotContain(users, u => u.FullName.Contains('(') || u.FullName.Contains("Agent") || u.FullName.Contains("Employee"));
    }

    [Fact]
    public async Task Seeding_twice_changes_nothing_and_does_not_fail()
    {
        using var app = TestApp.With(seedDemo: true);

        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync(); // the app already seeded once at start-up
        }

        using var check = app.Services.CreateScope();
        check.ServiceProvider.GetRequiredService<TenantContext>().UseSystem();
        var db = check.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        Assert.Equal(2, await db.Tenants.CountAsync());
        Assert.Equal(16, await db.Tickets.IgnoreQueryFilters().CountAsync());
        Assert.Equal(10, await db.Users.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task The_first_sla_sweep_has_something_to_report_so_the_live_desk_is_not_empty()
    {
        using var app = TestApp.With(seedDemo: true);

        var raised = await app.Services.GetRequiredService<SlaMonitor>().RunOnceAsync();

        Assert.True(raised >= 3, $"expected the back-dated demo tickets to trigger alarms, got {raised}");
        Assert.Contains(app.Publisher.Events, e => e.Type == TicketEventType.SlaBreached);
        Assert.Contains(app.Publisher.Events, e => e.Type == TicketEventType.SlaWarning);
        Assert.Contains(app.Publisher.Events, e => e.Type == TicketEventType.SlaResponseBreached);
    }
}
