using System.Net;
using System.Net.Http.Headers;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;
using OpsFlow.Tests.Support;

namespace OpsFlow.Tests;

/// <summary>Two real tenants in one database. Nothing in tenant A may reach tenant B - by any route.</summary>
public class TenantIsolationTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;

    public TenantIsolationTests(TestApp app) => _app = app;

    private sealed record World(TenantHarness Acme, Actor AcmeAgent, Actor AcmeEmployee,
                                TenantHarness Globex, Actor GlobexAgent, Actor GlobexEmployee);

    private async Task<World> CreateWorldAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var acme = await _app.CreateTenantAsync($"acme-{suffix}");
        var globex = await _app.CreateTenantAsync($"globex-{suffix}");
        return new World(
            acme, await acme.AddUserAsync(UserRole.Agent, "agent"), await acme.AddUserAsync(UserRole.Employee, "emp"),
            globex, await globex.AddUserAsync(UserRole.Agent, "agent"), await globex.AddUserAsync(UserRole.Employee, "emp"));
    }

    [Fact]
    public async Task List_only_returns_the_callers_tenant()
    {
        var w = await CreateWorldAsync();
        var acmeTicket = await w.AcmeEmployee.CreateTicketAsync("acme ticket");
        await w.GlobexEmployee.CreateTicketAsync("globex ticket");

        var (_, acmeView) = await w.AcmeAgent.GetAsync<PagedResult<TicketDto>>("/api/tickets");
        var (_, globexView) = await w.GlobexAgent.GetAsync<PagedResult<TicketDto>>("/api/tickets");

        Assert.Equal([acmeTicket.Id], acmeView!.Items.Select(t => t.Id));
        Assert.Equal("globex ticket", Assert.Single(globexView!.Items).Title);
    }

    [Fact]
    public async Task A_tickets_id_from_another_tenant_is_indistinguishable_from_a_missing_one()
    {
        var w = await CreateWorldAsync();
        var foreign = await w.GlobexEmployee.CreateTicketAsync();

        // Even an admin of tenant A gets 404 (not 403): the row does not exist from their point of view.
        foreach (var actor in new[] { w.Acme.Admin, w.AcmeAgent, w.AcmeEmployee })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await actor.GetAsync<TicketDto>($"/api/tickets/{foreign.Id}")).Response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await actor.GetAsync<object>($"/api/tickets/{foreign.Id}/comments")).Response.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await actor.GetAsync<object>($"/api/tickets/{foreign.Id}/history")).Response.StatusCode);
        }
    }

    [Fact]
    public async Task Writes_against_another_tenants_ticket_are_rejected_and_change_nothing()
    {
        var w = await CreateWorldAsync();
        var foreign = await w.GlobexEmployee.CreateTicketAsync("original title");

        var update = await w.Acme.Admin.PatchAsync<TicketDto>($"/api/tickets/{foreign.Id}",
            new UpdateTicketRequest { Title = "hacked", Version = foreign.Version });
        var transition = await w.Acme.Admin.TransitionAsync(foreign, TicketStatus.Assigned, w.AcmeAgent.Id);
        var assign = await w.Acme.Admin.PostAsync<TicketDto>($"/api/tickets/{foreign.Id}/assign",
            new AssignRequest { AssigneeId = w.AcmeAgent.Id, Version = foreign.Version });
        var comment = await w.Acme.Admin.PostAsync<CommentDto>($"/api/tickets/{foreign.Id}/comments",
            new AddCommentRequest { Content = "hello from the other side" });

        Assert.All([update.Response, transition.Response, assign.Response, comment.Response],
            r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));

        var after = await w.GlobexEmployee.GetTicketAsync(foreign.Id);
        Assert.Equal("original title", after.Title);
        Assert.Equal(TicketStatus.New, after.Status);
        Assert.Equal(foreign.Version, after.Version);
    }

    [Fact]
    public async Task A_user_of_another_tenant_cannot_be_assigned()
    {
        var w = await CreateWorldAsync();
        var ticket = await w.AcmeEmployee.CreateTicketAsync();

        var (response, _) = await w.Acme.Admin.TransitionAsync(ticket, TicketStatus.Assigned, w.GlobexAgent.Id);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task User_listing_never_contains_another_tenants_people()
    {
        var w = await CreateWorldAsync();

        var (_, users) = await w.Acme.Admin.GetAsync<List<UserDto>>("/api/users");

        Assert.DoesNotContain(users!, u => u.Id == w.GlobexAgent.Id || u.Id == w.Globex.Admin.Id);
        Assert.Contains(users!, u => u.Id == w.AcmeAgent.Id);
    }

    [Fact]
    public async Task Ticket_numbers_are_sequenced_independently_per_tenant()
    {
        var w = await CreateWorldAsync();

        var a1 = await w.AcmeEmployee.CreateTicketAsync();
        var a2 = await w.AcmeEmployee.CreateTicketAsync();
        var g1 = await w.GlobexEmployee.CreateTicketAsync();

        Assert.EndsWith("-0001", a1.TicketNumber);
        Assert.EndsWith("-0002", a2.TicketNumber);
        Assert.EndsWith("-0001", g1.TicketNumber);
        Assert.StartsWith(w.Acme.Tenant.Slug.ToUpperInvariant(), a1.TicketNumber);
    }

    [Fact]
    public async Task The_same_email_can_exist_in_two_tenants_without_collision()
    {
        var w = await CreateWorldAsync();
        var (response, _) = await w.Globex.Admin.PostAsync<UserDto>("/api/users", new CreateUserRequest
        {
            Email = $"agent@{w.Acme.Tenant.Slug}.test", FullName = "Same Person", Role = UserRole.Agent, Password = TestApp.Password
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Logging_into_tenant_B_with_tenant_A_credentials_fails()
    {
        var w = await CreateWorldAsync();

        var response = await _app.Anonymous().PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            TenantSlug = w.Globex.Tenant.Slug, Email = $"agent@{w.Acme.Tenant.Slug}.test", Password = TestApp.Password
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_client_supplied_tenant_header_has_no_effect()
    {
        var w = await CreateWorldAsync();
        await w.GlobexEmployee.CreateTicketAsync("globex secret");

        foreach (var header in new[] { "X-Tenant-Id", "X-Tenant-Key", "X-Tenant" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/tickets");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", w.AcmeAgent.Token);
            request.Headers.Add(header, w.Globex.Tenant.Id.ToString());
            var response = await _app.Anonymous().SendAsync(request);
            var page = await response.Content.ReadFromJsonAsync<PagedResult<TicketDto>>(TestApp.Json);

            Assert.Empty(page!.Items);
        }
    }

    [Fact]
    public async Task A_token_with_a_tampered_tenant_claim_is_rejected()
    {
        var w = await CreateWorldAsync();
        var parts = w.AcmeAgent.Token.Split('.');
        var payload = System.Text.Encoding.UTF8.GetString(Base64Url(parts[1]));
        var forged = payload.Replace(w.Acme.Tenant.Id.ToString(), w.Globex.Tenant.Id.ToString());
        var forgedToken = $"{parts[0]}.{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(forged)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.{parts[2]}";

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/tickets");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", forgedToken);
        var response = await _app.Anonymous().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_denied_by_default()
    {
        var anon = _app.Anonymous();

        foreach (var url in new[] { "/api/tickets", "/api/users", "/api/dashboard/summary", "/api/sla-policies", "/api/auth/me" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(url)).StatusCode);
        }
    }

    [Fact]
    public async Task An_admin_cannot_change_a_user_of_another_tenant()
    {
        var w = await CreateWorldAsync();

        var (response, _) = await w.Acme.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{w.GlobexAgent.Id}/status", new { isActive = false });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var (_, users) = await w.Globex.Admin.GetAsync<List<UserDto>>("/api/users");
        Assert.True(users!.Single(u => u.Id == w.GlobexAgent.Id).IsActive);
    }

    [Fact]
    public async Task Changing_an_sla_policy_in_one_tenant_leaves_the_other_untouched()
    {
        var w = await CreateWorldAsync();

        await w.Acme.Admin.SendAsync<SlaPolicyDto>(HttpMethod.Put, "/api/sla-policies/Critical", new UpdateSlaPolicyRequest { FirstResponseMinutes = 5, ResolutionMinutes = 10 });

        var (_, acme) = await w.AcmeAgent.GetAsync<List<SlaPolicyDto>>("/api/sla-policies");
        var (_, globex) = await w.GlobexAgent.GetAsync<List<SlaPolicyDto>>("/api/sla-policies");
        Assert.Equal(new SlaPolicyDto(TicketPriority.Critical, 5, 10), acme!.Single(p => p.Priority == TicketPriority.Critical));
        Assert.Equal(new SlaPolicyDto(TicketPriority.Critical, 30, 120), globex!.Single(p => p.Priority == TicketPriority.Critical));
        Assert.Equal(4, globex!.Count);
    }

    [Fact]
    public async Task The_dashboard_counts_only_the_callers_tenant()
    {
        var w = await CreateWorldAsync();
        await w.AcmeEmployee.CreateTicketAsync(priority: TicketPriority.Critical);
        for (var i = 0; i < 3; i++) await w.GlobexEmployee.CreateTicketAsync();

        var (_, acme) = await w.Acme.Admin.GetAsync<DashboardSummaryDto>("/api/dashboard/summary");
        var (_, globex) = await w.Globex.Admin.GetAsync<DashboardSummaryDto>("/api/dashboard/summary");

        Assert.Equal(1, acme!.Open);
        Assert.Equal(1, acme.Critical);
        Assert.Equal(3, globex!.Open);
        Assert.Equal(0, globex.Critical);
    }

    [Fact]
    public async Task A_tenant_header_cannot_redirect_a_write_into_another_tenant()
    {
        var w = await CreateWorldAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/tickets")
        {
            Content = JsonContent.Create(new CreateTicketRequest { Title = "from acme", Description = "header spoof attempt" }, options: TestApp.Json)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", w.AcmeEmployee.Token);
        request.Headers.Add("X-Tenant-Id", w.Globex.Tenant.Id.ToString());

        var response = await _app.Anonymous().SendAsync(request);
        var created = await response.Content.ReadFromJsonAsync<TicketDto>(TestApp.Json);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.StartsWith(w.Acme.Tenant.Slug.ToUpperInvariant(), created!.TicketNumber);
        var (_, globexList) = await w.GlobexAgent.GetAsync<PagedResult<TicketDto>>("/api/tickets");
        Assert.Empty(globexList!.Items);
    }

    private static byte[] Base64Url(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }
}


