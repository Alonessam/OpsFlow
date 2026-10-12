using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;
using OpsFlow.Infrastructure.Sla;
using OpsFlow.Tests.Support;

namespace OpsFlow.Tests;

public class RealtimeTests
{
    private sealed class Listener : IAsyncDisposable
    {
        private readonly HubConnection _connection;
        private readonly ConcurrentQueue<JsonElement> _events = new();

        public Listener(TestApp app, string? token)
        {
            _connection = new HubConnectionBuilder()
                .WithUrl(new Uri(app.Server.BaseAddress, "/hubs/ops"), o =>
                {
                    o.HttpMessageHandlerFactory = _ => app.Server.CreateHandler();
                    o.Transports = HttpTransportType.LongPolling;
                    if (token is not null) o.AccessTokenProvider = () => Task.FromResult<string?>(token);
                })
                .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
                .Build();
            _connection.On<JsonElement>("ticketEvent", e => _events.Enqueue(e.Clone()));
        }

        public IReadOnlyList<JsonElement> Events => _events.ToArray();

        public Task StartAsync() => _connection.StartAsync();

        public List<string> Types(Guid ticketId) => Events
            .Where(e => e.GetProperty("ticketId").GetGuid() == ticketId)
            .Select(e => e.GetProperty("type").GetString()!)
            .ToList();

        public void Clear() { while (_events.TryDequeue(out _)) { } }

        /// <summary>Polls instead of sleeping: returns as soon as the condition holds.</summary>
        public static async Task<bool> UntilAsync(Func<bool> condition, int timeoutMs = 5000)
        {
            var until = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < until)
            {
                if (condition()) return true;
                await Task.Delay(20);
            }

            return condition();
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();
    }

    /// <summary>
    /// StartAsync returns once the handshake is done, which can be a moment before the server has put the
    /// connection into its groups. A probe event proves the listener is really subscribed before the test begins.
    /// </summary>
    private static async Task WarmUpAsync(Listener listener, Func<Task<TicketDto>> probe)
    {
        var ticket = await probe();
        Assert.True(await Listener.UntilAsync(() => listener.Types(ticket.Id).Count > 0), "listener never received its warm-up event");
    }

    private static async Task SettleAsync() => await Task.Delay(300); // only for NEGATIVE assertions: give a wrongly routed message time to show up

    [Fact]
    public async Task Staff_get_live_events_for_their_own_tenant_only_and_requesters_only_for_their_own_tickets()
    {
        using var app = TestApp.With(realPublisher: true);
        var acme = await app.CreateTenantAsync("rt-acme");
        var globex = await app.CreateTenantAsync("rt-globex");
        var acmeAgent = await acme.AddUserAsync(UserRole.Agent, "agent");
        var acmeEmp = await acme.AddUserAsync(UserRole.Employee, "emp");
        var acmeOther = await acme.AddUserAsync(UserRole.Employee, "other");
        var globexAgent = await globex.AddUserAsync(UserRole.Agent, "agent");
        var globexEmp = await globex.AddUserAsync(UserRole.Employee, "emp");

        await using var acmeStaff = new Listener(app, acmeAgent.Token);
        await using var globexStaff = new Listener(app, globexAgent.Token);
        await using var requester = new Listener(app, acmeEmp.Token);
        await using var bystander = new Listener(app, acmeOther.Token);
        await Task.WhenAll(acmeStaff.StartAsync(), globexStaff.StartAsync(), requester.StartAsync(), bystander.StartAsync());
        await WarmUpAsync(acmeStaff, () => acmeEmp.CreateTicketAsync("warm-up"));
        await WarmUpAsync(requester, () => acmeEmp.CreateTicketAsync("warm-up"));
        await WarmUpAsync(bystander, () => acmeOther.CreateTicketAsync("warm-up"));
        await WarmUpAsync(globexStaff, () => globexEmp.CreateTicketAsync("warm-up"));
        foreach (var l in new[] { acmeStaff, globexStaff, requester, bystander }) l.Clear();

        var ticket = await acmeEmp.CreateTicketAsync("Server room too hot", TicketPriority.High);
        await acmeAgent.TransitionAsync(ticket, TicketStatus.Assigned, acmeAgent.Id);
        await acmeAgent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest { Content = "customer is unhappy", IsInternalNote = true });

        Assert.True(await Listener.UntilAsync(() => acmeStaff.Types(ticket.Id).Count >= 3));
        Assert.True(await Listener.UntilAsync(() => requester.Types(ticket.Id).Count >= 2));
        await SettleAsync();

        Assert.Equal(["TicketCreated", "TicketTransitioned", "CommentAdded"], acmeStaff.Types(ticket.Id));
        Assert.Equal(["TicketCreated", "TicketTransitioned"], requester.Types(ticket.Id)); // the internal note is staff-only
        Assert.Empty(globexStaff.Events);   // another tenant's staff hears nothing
        Assert.Empty(bystander.Events);     // another employee of the same tenant hears nothing
    }

    [Fact]
    public async Task A_tenant_admin_receives_the_staff_events_too()
    {
        using var app = TestApp.With(realPublisher: true);
        var tenant = await app.CreateTenantAsync("rt-admin");
        var employee = await tenant.AddUserAsync(UserRole.Employee, "emp");
        await using var admin = new Listener(app, tenant.Admin.Token);
        await admin.StartAsync();
        await WarmUpAsync(admin, () => employee.CreateTicketAsync("warm-up"));
        admin.Clear();

        var ticket = await employee.CreateTicketAsync("for the admin");

        Assert.True(await Listener.UntilAsync(() => admin.Types(ticket.Id).Count == 1));
    }

    [Fact]
    public async Task A_staff_member_who_creates_a_ticket_hears_about_it_once_not_twice()
    {
        using var app = TestApp.With(realPublisher: true);
        var tenant = await app.CreateTenantAsync("rt-once");
        var agent = await tenant.AddUserAsync(UserRole.Agent, "agent");
        var employee = await tenant.AddUserAsync(UserRole.Employee, "emp");
        await using var listener = new Listener(app, agent.Token);
        await listener.StartAsync();
        await WarmUpAsync(listener, () => employee.CreateTicketAsync("warm-up"));
        listener.Clear();

        var own = await agent.CreateTicketAsync("raised by the agent themself");
        Assert.True(await Listener.UntilAsync(() => listener.Types(own.Id).Count >= 1));
        await SettleAsync();

        Assert.Equal(["TicketCreated"], listener.Types(own.Id));
    }

    [Fact]
    public async Task Sla_alarms_reach_staff_but_never_the_requester()
    {
        using var app = TestApp.With(realPublisher: true);
        var tenant = await app.CreateTenantAsync("rt-sla");
        var agent = await tenant.AddUserAsync(UserRole.Agent, "agent");
        var employee = await tenant.AddUserAsync(UserRole.Employee, "emp");
        await using var staff = new Listener(app, agent.Token);
        await using var requester = new Listener(app, employee.Token);
        await Task.WhenAll(staff.StartAsync(), requester.StartAsync());
        await WarmUpAsync(staff, () => employee.CreateTicketAsync("warm-up"));
        await WarmUpAsync(requester, () => employee.CreateTicketAsync("warm-up"));
        var ticket = await employee.CreateTicketAsync("urgent", TicketPriority.Critical);
        Assert.True(await Listener.UntilAsync(() => requester.Types(ticket.Id).Contains("TicketCreated")));

        app.Clock.Advance(TimeSpan.FromMinutes(31)); // first response (30 min) missed
        await app.Services.GetRequiredService<SlaMonitor>().RunOnceAsync();
        app.Clock.Advance(TimeSpan.FromHours(3));    // and the resolution
        await app.Services.GetRequiredService<SlaMonitor>().RunOnceAsync();

        Assert.True(await Listener.UntilAsync(() => staff.Types(ticket.Id).Contains("SlaResponseBreached") && staff.Types(ticket.Id).Contains("SlaBreached")));
        await SettleAsync();
        Assert.Equal(["TicketCreated"], requester.Types(ticket.Id)); // not a single alarm
    }

    [Fact]
    public async Task A_deactivated_user_cannot_open_a_live_connection()
    {
        using var app = TestApp.With(realPublisher: true);
        var tenant = await app.CreateTenantAsync("rt-revoked");
        var agent = await tenant.AddUserAsync(UserRole.Agent, "agent");
        await tenant.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{agent.Id}/status", new { isActive = false });

        await using var listener = new Listener(app, agent.Token);

        await Assert.ThrowsAnyAsync<Exception>(() => listener.StartAsync());
    }

    [Fact]
    public async Task Deactivating_a_user_cuts_the_live_connection_they_already_hold()
    {
        using var app = TestApp.With(realPublisher: true);
        var tenant = await app.CreateTenantAsync("rt-cut");
        var agent = await tenant.AddUserAsync(UserRole.Agent, "agent");
        var employee = await tenant.AddUserAsync(UserRole.Employee, "emp");
        await using var former = new Listener(app, agent.Token);
        await using var admin = new Listener(app, tenant.Admin.Token); // control: someone who must keep hearing events
        await Task.WhenAll(former.StartAsync(), admin.StartAsync());
        await WarmUpAsync(former, () => employee.CreateTicketAsync("warm-up"));
        await WarmUpAsync(admin, () => employee.CreateTicketAsync("warm-up"));
        former.Clear();
        admin.Clear();

        await tenant.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{agent.Id}/status", new { isActive = false });
        var ticket = await employee.CreateTicketAsync("created after the agent left");

        Assert.True(await Listener.UntilAsync(() => admin.Types(ticket.Id).Count == 1)); // the live desk still works for others
        await SettleAsync();
        Assert.Empty(former.Types(ticket.Id));                                          // but not for the deactivated agent's open socket
    }

    [Theory]
    [InlineData("not-a-token")]
    [InlineData(null)]
    public async Task The_hub_rejects_connections_without_a_valid_token(string? token)
    {
        using var app = TestApp.With(realPublisher: true);

        await using var anonymous = new Listener(app, token);

        await Assert.ThrowsAnyAsync<Exception>(() => anonymous.StartAsync());
    }
}
