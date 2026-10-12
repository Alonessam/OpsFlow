using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;
using OpsFlow.Core.Sla;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Sla;
using OpsFlow.Infrastructure.Tenancy;
using OpsFlow.Tests.Support;

namespace OpsFlow.Tests;

public class SlaMonitorTests
{
    private static Task<int> Sweep(TestApp app) => app.Services.GetRequiredService<SlaMonitor>().RunOnceAsync();

    private static List<TicketEvent> SlaEventsFor(TestApp app, TicketDto t) =>
        app.Publisher.Events.Where(e => e.TicketId == t.Id && e.Type is TicketEventType.SlaWarning
            or TicketEventType.SlaBreached or TicketEventType.SlaResponseBreached).ToList();

    private static IEnumerable<TicketEventType> EventTypes(TestApp app, TicketDto t) => SlaEventsFor(app, t).Select(e => e.Type);

    private static async Task<(TenantHarness T, Actor Agent, Actor Employee)> SetupAsync(TestApp app, string slug)
    {
        var t = await app.CreateTenantAsync(slug);
        return (t, await t.AddUserAsync(UserRole.Agent, "agent"), await t.AddUserAsync(UserRole.Employee, "emp"));
    }

    /// <summary>A Critical ticket (30 min / 2 h) that an agent is already working on, so the first response is done.</summary>
    private static async Task<TicketDto> WorkedCriticalAsync(Actor agent, Actor employee)
    {
        var ticket = await employee.CreateTicketAsync(priority: TicketPriority.Critical);
        var (_, assigned) = await agent.TransitionAsync(ticket, TicketStatus.Assigned, agent.Id);
        return (await agent.TransitionAsync(assigned!, TicketStatus.InProgress)).Body!;
    }

    [Fact]
    public async Task Raises_warning_then_breach_each_exactly_once()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-a");
        var ticket = await WorkedCriticalAsync(agent, employee);

        Assert.Equal(0, await Sweep(app));

        app.Clock.Advance(TimeSpan.FromMinutes(97)); // 23 min left of 120: inside the final 20%
        Assert.Equal(1, await Sweep(app));
        Assert.Equal(0, await Sweep(app)); // idempotent

        app.Clock.Advance(TimeSpan.FromMinutes(30)); // 127 min: past the deadline
        Assert.Equal(1, await Sweep(app));
        Assert.Equal(0, await Sweep(app));
        app.Clock.Advance(TimeSpan.FromDays(3));
        Assert.Equal(0, await Sweep(app));

        Assert.Equal([TicketEventType.SlaWarning, TicketEventType.SlaBreached], EventTypes(app, ticket));
        var after = await agent.GetTicketAsync(ticket.Id);
        Assert.True(after.IsSlaBreached);
        Assert.Equal(SlaState.Breached, after.SlaState);
    }

    [Fact]
    public async Task Jumping_straight_past_the_deadline_raises_only_the_breach_not_a_pointless_warning()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-jump");
        var ticket = await WorkedCriticalAsync(agent, employee);

        app.Clock.Advance(TimeSpan.FromHours(5));
        await Sweep(app);

        Assert.Equal([TicketEventType.SlaBreached], EventTypes(app, ticket));
    }

    [Fact]
    public async Task Every_sla_event_is_staff_only()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-staff");
        var unanswered = await employee.CreateTicketAsync(priority: TicketPriority.Critical);
        var worked = await WorkedCriticalAsync(agent, employee);

        app.Clock.Advance(TimeSpan.FromMinutes(31));
        await Sweep(app);
        app.Clock.Advance(TimeSpan.FromMinutes(70));
        await Sweep(app);
        app.Clock.Advance(TimeSpan.FromHours(2));
        await Sweep(app);

        var events = SlaEventsFor(app, unanswered).Concat(SlaEventsFor(app, worked)).ToList();
        Assert.Contains(events, e => e.Type == TicketEventType.SlaResponseBreached);
        Assert.Contains(events, e => e.Type == TicketEventType.SlaWarning);
        Assert.Contains(events, e => e.Type == TicketEventType.SlaBreached);
        Assert.All(events, e => Assert.True(e.StaffOnly, $"{e.Type} must never reach the requester"));
    }

    [Fact]
    public async Task Overlapping_sweeps_raise_each_event_only_once()
    {
        // End-to-end smoke test. Real interleaving cannot be forced through the HTTP-free sweep on the in-memory provider
        // (it runs them one after another), so the property itself is pinned deterministically by the next three tests.
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-race");
        var ticket = await WorkedCriticalAsync(agent, employee);
        app.Clock.Advance(TimeSpan.FromHours(3));

        await Task.WhenAll(Sweep(app), Sweep(app), Sweep(app), Sweep(app));

        Assert.Single(app.Publisher.Events, e => e.TicketId == ticket.Id && e.Type == TicketEventType.SlaBreached);
    }

    [Fact]
    public async Task A_sweep_never_makes_an_open_editors_copy_of_the_ticket_look_stale()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-version");
        var ticket = await WorkedCriticalAsync(agent, employee); // the agent has this version open
        app.Clock.Advance(TimeSpan.FromHours(3));

        await Sweep(app); // the ticket becomes breached behind the agent's back

        var (response, updated) = await agent.TransitionAsync(ticket, TicketStatus.Resolved, summary: "Fixed it for real this time.");
        Assert.True(response.IsSuccessStatusCode, "the agent's version must still be accepted after a sweep");
        Assert.Equal(ticket.Version + 1, updated!.Version);
    }

    // ---- the concurrency guarantees behind "each event once" and "a sweep never clobbers an editor" -------------------
    // Forced deterministically: two system contexts load the same ticket BEFORE either saves, which is exactly the
    // interleaving two overlapping sweeps produce. No timing luck involved.

    private static async Task<(IServiceScope Scope, OpsFlowDbContext Db, Ticket Ticket)> LoadAsSystemAsync(TestApp app, Guid ticketId)
    {
        var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().UseSystem();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        return (scope, db, await db.Tickets.IgnoreQueryFilters().FirstAsync(t => t.Id == ticketId));
    }

    [Fact]
    public async Task Two_system_writers_that_loaded_the_same_ticket_cannot_both_save()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-tokens");
        var ticket = await WorkedCriticalAsync(agent, employee);
        app.Clock.Advance(TimeSpan.FromHours(3));
        var (s1, db1, first) = await LoadAsSystemAsync(app, ticket.Id);
        var (s2, db2, second) = await LoadAsSystemAsync(app, ticket.Id); // both see "not breached yet"
        using var _1 = s1;
        using var _2 = s2;

        first.IsSlaBreached = true; first.SlaBreachedAt = app.Clock.GetUtcNow();
        second.IsSlaBreached = true; second.SlaBreachedAt = app.Clock.GetUtcNow();

        await db1.SaveChangesAsync();
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync()); // the loser must not raise the event again
    }

    [Fact]
    public async Task A_system_write_never_overwrites_a_human_edit_that_landed_first()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-vs-human");
        var ticket = await WorkedCriticalAsync(agent, employee);
        app.Clock.Advance(TimeSpan.FromHours(3));
        var (scope, db, loaded) = await LoadAsSystemAsync(app, ticket.Id); // the sweep has read the ticket...
        using var _ = scope;

        var (response, _) = await agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Title = "edited by a human", Version = ticket.Version });
        loaded.SlaBreachedAt = app.Clock.GetUtcNow();                      // ...then a human edit lands, then the sweep saves

        Assert.True(response.IsSuccessStatusCode);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db.SaveChangesAsync());
        Assert.Equal("edited by a human", (await agent.GetTicketAsync(ticket.Id)).Title);
    }

    [Fact]
    public async Task System_writes_bump_the_sweep_token_and_leave_the_editors_version_alone()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-bump");
        var ticket = await WorkedCriticalAsync(agent, employee);
        app.Clock.Advance(TimeSpan.FromHours(3));
        var (scope, db, loaded) = await LoadAsSystemAsync(app, ticket.Id);
        using var _ = scope;
        var (version, revision) = (loaded.Version, loaded.SlaRevision);

        loaded.SlaBreachedAt = app.Clock.GetUtcNow();
        await db.SaveChangesAsync();

        Assert.Equal(version, loaded.Version);
        Assert.Equal(revision + 1, loaded.SlaRevision);
    }

    [Fact]
    public async Task Flags_a_missed_first_response_even_while_the_resolution_window_is_fine()
    {
        using var app = new TestApp();
        var (t, agent, employee) = await SetupAsync(app, "sla-b");
        var ticket = await employee.CreateTicketAsync(priority: TicketPriority.High); // 1 h / 8 h

        app.Clock.Advance(TimeSpan.FromMinutes(61));
        await Sweep(app);

        Assert.Equal([TicketEventType.SlaResponseBreached], EventTypes(app, ticket));
        var after = await agent.GetTicketAsync(ticket.Id);
        Assert.Equal(SlaState.Breached, after.SlaState);
        Assert.True(after.IsSlaBreached); // "a target was missed" - and it stays true after a late answer
        var (_, breached) = await agent.GetAsync<PagedResult<TicketDto>>("/api/tickets?breached=true");
        Assert.Contains(breached!.Items, x => x.Id == ticket.Id);
        var (_, summary) = await t.Admin.GetAsync<DashboardSummaryDto>("/api/dashboard/summary");
        Assert.Equal(1, summary!.Breached);

        await agent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest { Content = "Sorry for the wait." });
        Assert.Equal(SlaState.Breached, (await agent.GetTicketAsync(ticket.Id)).SlaState);
    }

    [Fact]
    public async Task A_timely_first_response_prevents_the_response_breach()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-c");
        var ticket = await employee.CreateTicketAsync(priority: TicketPriority.High);

        app.Clock.Advance(TimeSpan.FromMinutes(20));
        await agent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest { Content = "On it." });
        app.Clock.Advance(TimeSpan.FromMinutes(60));
        await Sweep(app);

        Assert.DoesNotContain(TicketEventType.SlaResponseBreached, EventTypes(app, ticket));
    }

    [Fact]
    public async Task Waiting_for_the_customer_pauses_the_clock()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-d");
        var working = await WorkedCriticalAsync(agent, employee);

        app.Clock.Advance(TimeSpan.FromMinutes(60));
        var (_, pending) = await agent.TransitionAsync(working, TicketStatus.PendingClient);
        Assert.Equal(SlaState.Paused, pending!.SlaState);

        app.Clock.Advance(TimeSpan.FromHours(10)); // customer is slow
        Assert.Equal(0, await Sweep(app));
        Assert.Empty(EventTypes(app, working));

        var (_, resumed) = await agent.TransitionAsync(pending, TicketStatus.InProgress);
        Assert.Equal(working.ResolutionDeadline.AddHours(10), resumed!.ResolutionDeadline);
        Assert.Equal(SlaState.Healthy, resumed.SlaState);

        app.Clock.Advance(TimeSpan.FromMinutes(61)); // 121 min of *working* time -> now it breaches
        await Sweep(app);
        Assert.Contains(TicketEventType.SlaBreached, EventTypes(app, working));
    }

    [Fact]
    public async Task Resolving_straight_out_of_pending_client_is_judged_with_the_pause_credited()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-pending-resolve");
        var working = await WorkedCriticalAsync(agent, employee); // 2 h window
        app.Clock.Advance(TimeSpan.FromMinutes(10));
        var (_, pending) = await agent.TransitionAsync(working, TicketStatus.PendingClient);
        app.Clock.Advance(TimeSpan.FromHours(10)); // far beyond the nominal deadline, but the clock was stopped

        var (response, resolved) = await agent.TransitionAsync(pending!, TicketStatus.Resolved, summary: "Customer confirmed, patch applied.");

        Assert.True(response.IsSuccessStatusCode);
        Assert.False(resolved!.IsSlaBreached);
        Assert.Equal(SlaState.Met, resolved.SlaState);
        Assert.Equal(0, await Sweep(app));
        var (_, history) = await agent.GetAsync<List<AuditEntryDto>>($"/api/tickets/{working.Id}/history");
        Assert.DoesNotContain(history!, h => h.Field == nameof(Ticket.IsSlaBreached));
    }

    [Fact]
    public async Task Resolved_tickets_are_ignored()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-e");
        var working = await WorkedCriticalAsync(agent, employee);
        var (_, resolved) = await agent.TransitionAsync(working, TicketStatus.Resolved, summary: "Replaced the part, verified.");

        app.Clock.Advance(TimeSpan.FromDays(2));

        Assert.Equal(0, await Sweep(app));
        Assert.Equal(SlaState.Met, resolved!.SlaState);
        var after = await agent.GetTicketAsync(working.Id);
        Assert.False(after.IsSlaBreached);
        Assert.Equal(SlaState.Met, after.SlaState); // re-read AFTER the sweep
    }

    [Fact]
    public async Task Resolving_after_the_deadline_marks_the_ticket_breached_even_if_the_worker_has_not_run()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-f");
        var working = await WorkedCriticalAsync(agent, employee);

        app.Clock.Advance(TimeSpan.FromHours(3));
        var (_, resolved) = await agent.TransitionAsync(working, TicketStatus.Resolved, summary: "Finally fixed, sorry for the delay.");

        Assert.True(resolved!.IsSlaBreached);
        Assert.Equal(SlaState.Breached, resolved.SlaState);
    }

    [Fact]
    public async Task Rejecting_an_on_time_resolution_days_later_does_not_raise_a_false_breach()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-reopen");
        var working = await WorkedCriticalAsync(agent, employee);
        app.Clock.Advance(TimeSpan.FromMinutes(10));
        var (_, resolved) = await agent.TransitionAsync(working, TicketStatus.Resolved, summary: "Replaced the part, verified.");
        Assert.False(resolved!.IsSlaBreached);

        app.Clock.Advance(TimeSpan.FromDays(1));
        var (_, reopened) = await employee.TransitionAsync(resolved, TicketStatus.InProgress); // "still broken"
        Assert.Equal(0, await Sweep(app));                                                    // not breached: a fresh 2 h window
        Assert.Empty(EventTypes(app, working));

        app.Clock.Advance(TimeSpan.FromMinutes(97));
        await Sweep(app);                                                                      // inside the NEW window's last 20 %
        app.Clock.Advance(TimeSpan.FromMinutes(30));
        await Sweep(app);                                                                      // and past it
        Assert.Equal([TicketEventType.SlaWarning, TicketEventType.SlaBreached], EventTypes(app, working));
        Assert.Equal(TicketStatus.InProgress, reopened!.Status);
    }

    [Fact]
    public async Task Lowering_the_priority_of_a_warned_ticket_rearms_the_warning()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-rearm");
        var working = await WorkedCriticalAsync(agent, employee);
        app.Clock.Advance(TimeSpan.FromMinutes(100));
        await Sweep(app); // warned
        var current = await agent.GetTicketAsync(working.Id);

        var (_, lowered) = await agent.PatchAsync<TicketDto>($"/api/tickets/{working.Id}", new UpdateTicketRequest { Priority = TicketPriority.Medium, Version = current.Version });
        Assert.Equal(SlaState.Healthy, lowered!.SlaState);
        var (_, summaryBefore) = await agent.GetAsync<DashboardSummaryDto>("/api/dashboard/summary");
        Assert.Equal(0, summaryBefore!.AtRisk); // no longer "at risk"

        app.Clock.Advance(TimeSpan.FromHours(20)); // deep into Medium's final 20 % (24 h window)
        await Sweep(app);

        Assert.Equal(2, EventTypes(app, working).Count(t => t == TicketEventType.SlaWarning)); // once per window
    }

    [Fact]
    public async Task Sweeps_all_tenants_and_tags_each_event_with_its_own_tenant()
    {
        using var app = new TestApp();
        var (acme, acmeAgent, acmeEmployee) = await SetupAsync(app, "sla-t1");
        var (globex, globexAgent, globexEmployee) = await SetupAsync(app, "sla-t2");
        var a = await acmeEmployee.CreateTicketAsync(priority: TicketPriority.Critical);
        var g = await globexEmployee.CreateTicketAsync(priority: TicketPriority.Critical);

        app.Clock.Advance(TimeSpan.FromHours(3));
        await Sweep(app);

        Assert.All(app.Publisher.Events.Where(e => e.TicketId == a.Id), e => Assert.Equal(acme.Tenant.Id, e.TenantId));
        Assert.All(app.Publisher.Events.Where(e => e.TicketId == g.Id), e => Assert.Equal(globex.Tenant.Id, e.TenantId));
        Assert.Contains(app.Publisher.Events, e => e.TicketId == a.Id && e.Type == TicketEventType.SlaBreached);
        Assert.Contains(app.Publisher.Events, e => e.TicketId == g.Id && e.Type == TicketEventType.SlaBreached);
        Assert.True((await acmeAgent.GetTicketAsync(a.Id)).IsSlaBreached);   // each tenant's own ticket, read back through its own API
        Assert.True((await globexAgent.GetTicketAsync(g.Id)).IsSlaBreached);
    }

    [Fact]
    public async Task The_breach_is_audited_as_a_system_change()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-g");
        var ticket = await WorkedCriticalAsync(agent, employee);

        app.Clock.Advance(TimeSpan.FromHours(3));
        await Sweep(app);

        var (_, history) = await agent.GetAsync<List<AuditEntryDto>>($"/api/tickets/{ticket.Id}/history");
        var entry = Assert.Single(history!, h => h.Field == nameof(Ticket.IsSlaBreached));
        Assert.Equal("false", entry.OldValue);
        Assert.Equal("true", entry.NewValue);
        Assert.Null(entry.ChangedBy);
    }

    [Fact]
    public async Task A_failing_push_notification_does_not_stop_the_sweep_or_lose_the_state()
    {
        using var app = new TestApp();
        var (_, agent, employee) = await SetupAsync(app, "sla-push");
        var ticket = await WorkedCriticalAsync(agent, employee);
        app.Publisher.FailNext = true; // the next publish throws, as a broken SignalR backplane would
        app.Clock.Advance(TimeSpan.FromHours(3));

        var raised = await Sweep(app); // must not throw

        Assert.True(raised >= 1);
        Assert.True((await agent.GetTicketAsync(ticket.Id)).IsSlaBreached); // the state was still saved
        Assert.Equal(0, await Sweep(app)); // and it is not raised again
    }
}
