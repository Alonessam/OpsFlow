using System.Net;
using System.Text;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;
using OpsFlow.Core.Sla;
using OpsFlow.Tests.Support;

namespace OpsFlow.Tests;

public class WorkflowApiTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;

    public WorkflowApiTests(TestApp app) => _app = app;

    private async Task<(TenantHarness T, Actor Agent, Actor Employee)> SetupAsync()
    {
        var t = await _app.CreateTenantAsync($"wf-{Guid.NewGuid():N}"[..14]);
        return (t, await t.AddUserAsync(UserRole.Agent, "agent"), await t.AddUserAsync(UserRole.Employee, "emp"));
    }

    private static async Task<TicketDto> DriveToStatusAsync(Actor agent, Actor employee, TicketStatus target)
    {
        var t = await employee.CreateTicketAsync();
        if (target == TicketStatus.New) return t;
        t = (await agent.TransitionAsync(t, TicketStatus.Assigned, agent.Id)).Body!;
        if (target == TicketStatus.Assigned) return t;
        t = (await agent.TransitionAsync(t, TicketStatus.InProgress)).Body!;
        if (target == TicketStatus.InProgress) return t;
        t = (await agent.TransitionAsync(t, TicketStatus.Resolved, summary: "Fixed and verified with requester.")).Body!;
        if (target == TicketStatus.Resolved) return t;
        return (await agent.TransitionAsync(t, TicketStatus.Closed)).Body!;
    }

    private static async Task<HttpStatusCode> StatusOf<T>(Task<(HttpResponseMessage Response, T? Body)> call) => (await call).Response.StatusCode;

    private static Task<HttpResponseMessage> RawPost(Actor who, string url, string json) =>
        who.Http.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

    // =====================================================================================================
    // Lifecycle and transitions
    // =====================================================================================================

    [Fact]
    public async Task A_ticket_travels_the_whole_workflow()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync("VPN down", TicketPriority.High);
        Assert.Equal(TicketStatus.New, ticket.Status);

        var (r1, assigned) = await agent.TransitionAsync(ticket, TicketStatus.Assigned, agent.Id);
        var (r2, inProgress) = await agent.TransitionAsync(assigned!, TicketStatus.InProgress);
        var (r3, pending) = await agent.TransitionAsync(inProgress!, TicketStatus.PendingClient);
        var (r4, back) = await agent.TransitionAsync(pending!, TicketStatus.InProgress);
        var (r5, resolved) = await agent.TransitionAsync(back!, TicketStatus.Resolved, summary: "Restarted the gateway and verified.");
        var (r6, closed) = await employee.TransitionAsync(resolved!, TicketStatus.Closed);

        Assert.All([r1, r2, r3, r4, r5, r6], r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        Assert.Equal(TicketStatus.Closed, closed!.Status);
        Assert.Equal(agent.Id, closed.AssignedTo!.Id);
        Assert.Equal(7, closed.Version); // created + 6 changes
    }

    [Fact]
    public async Task Skipping_a_step_returns_409_with_a_machine_readable_code()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();

        var (response, _) = await agent.TransitionAsync(ticket, TicketStatus.Closed);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("invalid_transition", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Missing_required_input_is_a_400_not_a_conflict()
    {
        var (_, agent, employee) = await SetupAsync();
        var fresh = await employee.CreateTicketAsync();
        var working = await DriveToStatusAsync(agent, employee, TicketStatus.InProgress);

        var noAssignee = await agent.TransitionAsync(fresh, TicketStatus.Assigned);
        var noSummary = await agent.TransitionAsync(working, TicketStatus.Resolved);

        Assert.Equal(HttpStatusCode.BadRequest, noAssignee.Response.StatusCode);
        Assert.Contains("assignee_required", await noAssignee.Response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, noSummary.Response.StatusCode);
        Assert.Contains("resolution_summary_required", await noSummary.Response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Only_the_admin_can_reopen_a_closed_ticket()
    {
        var (t, agent, employee) = await SetupAsync();
        var closed = await DriveToStatusAsync(agent, employee, TicketStatus.Closed);

        var byAgent = await agent.TransitionAsync(closed, TicketStatus.InProgress);
        var byEmployee = await employee.TransitionAsync(closed, TicketStatus.InProgress);
        var byAdmin = await t.Admin.TransitionAsync(closed, TicketStatus.InProgress);

        Assert.Equal(HttpStatusCode.Forbidden, byAgent.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byEmployee.Response.StatusCode);
        Assert.Equal(TicketStatus.InProgress, byAdmin.Body!.Status);
    }

    [Fact]
    public async Task The_requester_can_reject_a_resolution_and_the_sla_window_restarts()
    {
        var (_, agent, employee) = await SetupAsync();
        var resolved = await DriveToStatusAsync(agent, employee, TicketStatus.Resolved);
        _app.Clock.Advance(TimeSpan.FromDays(2)); // long after the original deadline

        var (response, reopened) = await employee.TransitionAsync(resolved, TicketStatus.InProgress);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(TicketStatus.InProgress, reopened!.Status);
        Assert.True(reopened.ResolutionDeadline > resolved.ResolutionDeadline.AddDays(1)); // a fresh window, not the expired one
        Assert.Equal(SlaState.Healthy, reopened.SlaState);
        Assert.False(reopened.IsSlaBreached || reopened.SlaState == SlaState.Breached);
    }

    [Fact]
    public async Task A_stale_version_yields_409_on_update_transition_and_assign()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();
        var winner = await agent.TransitionAsync(ticket, TicketStatus.Assigned, agent.Id); // moves the ticket to v2
        Assert.True(winner.Response.IsSuccessStatusCode);

        var patch = await agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}",
            new UpdateTicketRequest { Priority = TicketPriority.Critical, Version = ticket.Version });
        var transition = await agent.TransitionAsync(ticket, TicketStatus.InProgress); // still carries v1
        var assign = await agent.PostAsync<TicketDto>($"/api/tickets/{ticket.Id}/assign",
            new AssignRequest { AssigneeId = agent.Id, Version = ticket.Version });

        Assert.All([patch.Response, transition.Response, assign.Response], r => Assert.Equal(HttpStatusCode.Conflict, r.StatusCode));
        Assert.Contains("version_conflict", await patch.Response.Content.ReadAsStringAsync());
        var after = await agent.GetTicketAsync(ticket.Id);
        Assert.Equal(TicketPriority.Medium, after.Priority);
        Assert.Equal(TicketStatus.Assigned, after.Status);
        Assert.Equal(2, after.Version);
    }

    [Fact]
    public async Task A_rejected_stale_write_leaves_no_trace_in_the_audit_trail()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync("original");
        await agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Title = "winner", Version = ticket.Version });

        await agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Title = "loser", Version = ticket.Version });

        var (_, history) = await agent.GetAsync<List<AuditEntryDto>>($"/api/tickets/{ticket.Id}/history");
        var titleChanges = history!.Where(h => h.Field == nameof(Ticket.Title)).ToList();
        Assert.Equal("winner", Assert.Single(titleChanges).NewValue);
    }

    [Fact]
    public async Task Two_simultaneous_updates_cannot_both_win()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}",
                new UpdateTicketRequest { Title = $"writer {i}", Version = ticket.Version })));

        Assert.Equal(1, results.Count(r => r.Response.StatusCode == HttpStatusCode.OK));
        Assert.All(results.Where(r => r.Response.StatusCode != HttpStatusCode.OK),
            r => Assert.Equal(HttpStatusCode.Conflict, r.Response.StatusCode));
    }

    [Fact]
    public async Task A_first_response_comment_racing_with_an_edit_never_ends_in_a_server_error()
    {
        var (_, agent, employee) = await SetupAsync();

        for (var round = 0; round < 5; round++)
        {
            var ticket = await employee.CreateTicketAsync($"race {round}");
            var calls = new[]
            {
                StatusOf(agent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest { Content = "On it." })),
                StatusOf(agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Title = "renamed", Version = ticket.Version })),
                StatusOf(agent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest { Content = "Second message." })),
                StatusOf(agent.TransitionAsync(ticket, TicketStatus.Assigned, agent.Id)),
            };

            var statuses = await Task.WhenAll(calls);

            // (The "exactly the 201s were saved" check lives in PostgresTests: the in-memory provider has no
            // transactions, so a retried comment can legitimately leave a duplicate behind there.)
            Assert.DoesNotContain(statuses, s => (int)s >= 500);
        }
    }

    [Fact]
    public async Task Concurrent_creates_get_unique_gapless_ticket_numbers()
    {
        var (_, _, employee) = await SetupAsync();

        var tickets = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => employee.CreateTicketAsync($"parallel {i}")));

        var numbers = tickets.Select(t => int.Parse(t.TicketNumber[^4..])).Order().ToList();
        Assert.Equal(Enumerable.Range(1, 8), numbers);
    }

    // =====================================================================================================
    // Assignment
    // =====================================================================================================

    [Fact]
    public async Task Only_an_active_agent_or_admin_of_the_tenant_can_be_assigned()
    {
        var (t, agent, employee) = await SetupAsync();
        var inactive = await t.AddUserAsync(UserRole.Agent, "retired");
        await t.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{inactive.Id}/status", new { isActive = false });
        var ticket = await employee.CreateTicketAsync();

        foreach (var candidate in new[] { inactive.Id, employee.Id, Guid.NewGuid() })
        {
            var (response, _) = await agent.TransitionAsync(ticket, TicketStatus.Assigned, candidate);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("invalid_assignee", await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Reassigning_an_open_ticket_changes_the_assignee_and_is_audited_with_names()
    {
        var (t, agent, employee) = await SetupAsync();
        var second = await t.AddUserAsync(UserRole.Agent, "second");
        var working = await DriveToStatusAsync(agent, employee, TicketStatus.InProgress);
        _app.Clock.Advance(TimeSpan.FromMinutes(5)); // the reassignment happens later, so the timeline order is unambiguous

        var (response, reassigned) = await agent.PostAsync<TicketDto>($"/api/tickets/{working.Id}/assign",
            new AssignRequest { AssigneeId = second.Id, Version = working.Version });

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(second.Id, reassigned!.AssignedTo!.Id);
        Assert.Equal(TicketStatus.InProgress, reassigned.Status); // reassigning is not a status change
        var (_, history) = await agent.GetAsync<List<AuditEntryDto>>($"/api/tickets/{working.Id}/history");
        var change = history!.Last(h => h.Field == nameof(Ticket.AssignedToUserId));
        Assert.Equal(agent.User.FullName, change.OldValue);
        Assert.Equal(second.User.FullName, change.NewValue);
    }

    [Fact]
    public async Task Assigning_the_same_person_again_changes_nothing_and_publishes_nothing()
    {
        var (_, agent, employee) = await SetupAsync();
        var working = await DriveToStatusAsync(agent, employee, TicketStatus.InProgress);
        var eventsBefore = _app.Publisher.Events.Count;

        var (response, same) = await agent.PostAsync<TicketDto>($"/api/tickets/{working.Id}/assign",
            new AssignRequest { AssigneeId = agent.Id, Version = working.Version });

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(working.Version, same!.Version);
        Assert.Equal(eventsBefore, _app.Publisher.Events.Count);
    }

    [Fact]
    public async Task Resolved_and_closed_tickets_cannot_be_reassigned()
    {
        var (_, agent, employee) = await SetupAsync();
        foreach (var status in new[] { TicketStatus.Resolved, TicketStatus.Closed })
        {
            var ticket = await DriveToStatusAsync(agent, employee, status);

            var (response, _) = await agent.PostAsync<TicketDto>($"/api/tickets/{ticket.Id}/assign",
                new AssignRequest { AssigneeId = agent.Id, Version = ticket.Version });

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
    }

    [Fact]
    public async Task Unassigning_clears_the_assignee_and_working_again_keeps_it()
    {
        var (_, agent, employee) = await SetupAsync();
        var assigned = await DriveToStatusAsync(agent, employee, TicketStatus.Assigned);

        var (_, unassigned) = await agent.TransitionAsync(assigned, TicketStatus.New);
        Assert.Null(unassigned!.AssignedTo);

        var (_, again) = await agent.TransitionAsync(unassigned, TicketStatus.Assigned, agent.Id);
        var (_, working) = await agent.TransitionAsync(again!, TicketStatus.InProgress);
        var (_, backToAssigned) = await agent.TransitionAsync(working!, TicketStatus.Assigned); // no new assignee supplied

        Assert.Equal(agent.Id, backToAssigned!.AssignedTo!.Id);
    }

    // =====================================================================================================
    // Who may do what
    // =====================================================================================================

    [Fact]
    public async Task Employees_see_only_their_own_tickets_and_cannot_read_internal_notes()
    {
        var (t, agent, employee) = await SetupAsync();
        var other = await t.AddUserAsync(UserRole.Employee, "other");
        var mine = await employee.CreateTicketAsync("mine");
        var theirs = await other.CreateTicketAsync("theirs");

        var (_, list) = await employee.GetAsync<PagedResult<TicketDto>>("/api/tickets");

        Assert.Equal([mine.Id], list!.Items.Select(x => x.Id));
        Assert.Equal(HttpStatusCode.NotFound, (await employee.GetAsync<TicketDto>($"/api/tickets/{theirs.Id}")).Response.StatusCode);

        await agent.PostAsync<CommentDto>($"/api/tickets/{mine.Id}/comments", new AddCommentRequest { Content = "customer is rude", IsInternalNote = true });
        await agent.PostAsync<CommentDto>($"/api/tickets/{mine.Id}/comments", new AddCommentRequest { Content = "We are on it." });
        var (_, employeeView) = await employee.GetAsync<List<CommentDto>>($"/api/tickets/{mine.Id}/comments");
        var (_, agentView) = await agent.GetAsync<List<CommentDto>>($"/api/tickets/{mine.Id}/comments");

        Assert.Equal("We are on it.", Assert.Single(employeeView!).Content);
        Assert.Equal(2, agentView!.Count);
    }

    [Fact]
    public async Task Another_employees_ticket_is_invisible_on_every_endpoint()
    {
        var (t, _, employee) = await SetupAsync();
        var other = await t.AddUserAsync(UserRole.Employee, "other");
        var theirs = await other.CreateTicketAsync("theirs");
        var id = theirs.Id;

        var statuses = new[]
        {
            await StatusOf(employee.GetAsync<TicketDto>($"/api/tickets/{id}")),
            await StatusOf(employee.GetAsync<List<CommentDto>>($"/api/tickets/{id}/comments")),
            await StatusOf(employee.PostAsync<CommentDto>($"/api/tickets/{id}/comments", new AddCommentRequest { Content = "hi" })),
            await StatusOf(employee.GetAsync<List<AuditEntryDto>>($"/api/tickets/{id}/history")),
            await StatusOf(employee.PatchAsync<TicketDto>($"/api/tickets/{id}", new UpdateTicketRequest { Title = "mine now", Version = theirs.Version })),
            await StatusOf(employee.TransitionAsync(theirs, TicketStatus.Closed)),
        };

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.NotFound, s));
        Assert.Equal("theirs", (await other.GetTicketAsync(id)).Title);
    }

    [Fact]
    public async Task Employee_list_filters_never_widen_what_they_can_see()
    {
        var (t, agent, employee) = await SetupAsync();
        var other = await t.AddUserAsync(UserRole.Employee, "other");
        var mine = await employee.CreateTicketAsync("mine");
        var theirs = await other.CreateTicketAsync("theirs");
        await agent.TransitionAsync(theirs, TicketStatus.Assigned, agent.Id);

        foreach (var query in new[] { "mine=true", $"assignedToId={agent.Id}", "status=Assigned", "breached=true", "breached=false", "priority=Medium", "search=theirs" })
        {
            var (_, page) = await employee.GetAsync<PagedResult<TicketDto>>($"/api/tickets?{query}");

            Assert.DoesNotContain(page!.Items, x => x.Id == theirs.Id);
        }

        var (_, own) = await employee.GetAsync<PagedResult<TicketDto>>("/api/tickets?mine=true");
        Assert.Equal([mine.Id], own!.Items.Select(x => x.Id));
    }

    [Fact]
    public async Task Employees_can_edit_their_new_ticket_but_not_its_priority_and_not_after_it_is_picked_up()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync("typo titel");

        var edit = await employee.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Title = "typo title", Version = ticket.Version });
        var escalate = await employee.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Priority = TicketPriority.Critical, Version = edit.Body!.Version });
        await agent.TransitionAsync(edit.Body!, TicketStatus.Assigned, agent.Id);
        var afterPickup = await employee.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Title = "too late", Version = edit.Body!.Version + 1 });

        Assert.Equal("typo title", edit.Body!.Title);
        Assert.Equal(HttpStatusCode.Forbidden, escalate.Response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, afterPickup.Response.StatusCode);
    }

    [Fact]
    public async Task A_requester_may_state_the_urgency_when_opening_but_only_staff_can_change_it_later()
    {
        var (_, agent, employee) = await SetupAsync();

        var critical = await employee.CreateTicketAsync("server room on fire", TicketPriority.Critical);
        var raise = await employee.PatchAsync<TicketDto>($"/api/tickets/{critical.Id}", new UpdateTicketRequest { Priority = TicketPriority.Low, Version = critical.Version });
        var triage = await agent.PatchAsync<TicketDto>($"/api/tickets/{critical.Id}", new UpdateTicketRequest { Priority = TicketPriority.Low, Version = critical.Version });

        Assert.Equal(TicketPriority.Critical, critical.Priority);
        Assert.Equal(HttpStatusCode.Forbidden, raise.Response.StatusCode);
        Assert.Equal(TicketPriority.Low, triage.Body!.Priority); // staff re-triage
    }

    [Fact]
    public async Task Employees_cannot_write_internal_notes_assign_view_the_dashboard_or_manage_anything()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();

        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(employee.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest { Content = "x", IsInternalNote = true })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(employee.PostAsync<TicketDto>($"/api/tickets/{ticket.Id}/assign", new AssignRequest { AssigneeId = agent.Id, Version = ticket.Version })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(employee.GetAsync<DashboardSummaryDto>("/api/dashboard/summary")));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(employee.GetAsync<List<UserDto>>("/api/users")));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(employee.PostAsync<UserDto>("/api/users",
            new CreateUserRequest { Email = "x@y.test", FullName = "X Y", Role = UserRole.Agent, Password = TestApp.Password })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(employee.SendAsync<SlaPolicyDto>(HttpMethod.Put, "/api/sla-policies/Critical",
            new UpdateSlaPolicyRequest { FirstResponseMinutes = 1, ResolutionMinutes = 2 })));
        Assert.Equal(HttpStatusCode.OK, await StatusOf(employee.GetAsync<List<SlaPolicyDto>>("/api/sla-policies"))); // policies are readable
    }

    [Fact]
    public async Task Agents_cannot_manage_users_or_sla_policies_but_can_list_active_staff()
    {
        var (t, agent, _) = await SetupAsync();
        var retired = await t.AddUserAsync(UserRole.Agent, "retired");
        await t.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{retired.Id}/status", new { isActive = false });

        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(agent.PostAsync<UserDto>("/api/users",
            new CreateUserRequest { Email = "x@y.test", FullName = "X Y", Role = UserRole.Agent, Password = TestApp.Password })));
        Assert.Equal(HttpStatusCode.Forbidden, await StatusOf(agent.SendAsync<SlaPolicyDto>(HttpMethod.Put, "/api/sla-policies/Critical",
            new UpdateSlaPolicyRequest { FirstResponseMinutes = 1, ResolutionMinutes = 2 })));

        var (_, everyone) = await agent.GetAsync<List<UserDto>>("/api/users");
        var (_, staff) = await agent.GetAsync<List<UserDto>>("/api/users?staffOnly=true");
        Assert.Contains(everyone!, u => u.Role == UserRole.Employee);
        Assert.DoesNotContain(staff!, u => u.Role == UserRole.Employee || !u.IsActive);
        Assert.Contains(staff!, u => u.Id == agent.Id);
    }

    // =====================================================================================================
    // Comments, first response, closing
    // =====================================================================================================

    [Fact]
    public async Task First_response_is_recorded_when_staff_answers_publicly_but_not_for_internal_notes()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();

        await agent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest { Content = "note", IsInternalNote = true });
        Assert.Null((await agent.GetTicketAsync(ticket.Id)).FirstRespondedAt);

        await employee.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest { Content = "any news?" });
        Assert.Null((await agent.GetTicketAsync(ticket.Id)).FirstRespondedAt);

        await agent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest { Content = "Looking now." });
        Assert.NotNull((await agent.GetTicketAsync(ticket.Id)).FirstRespondedAt);
    }

    [Fact]
    public async Task A_closed_ticket_rejects_new_comments()
    {
        var (_, agent, employee) = await SetupAsync();
        var closed = await DriveToStatusAsync(agent, employee, TicketStatus.Closed);

        var (response, _) = await agent.PostAsync<CommentDto>($"/api/tickets/{closed.Id}/comments", new AddCommentRequest { Content = "late" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // =====================================================================================================
    // Updating a ticket
    // =====================================================================================================

    [Fact]
    public async Task Changing_priority_recomputes_the_sla_deadlines()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync(priority: TicketPriority.Low);

        var (_, updated) = await agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}",
            new UpdateTicketRequest { Priority = TicketPriority.Critical, Version = ticket.Version });

        Assert.Equal(ticket.CreatedAt.AddHours(2), updated!.ResolutionDeadline);
        Assert.Equal(ticket.CreatedAt.AddMinutes(30), updated.ResponseDeadline);
    }

    [Fact]
    public async Task An_edit_that_changes_nothing_is_a_no_op_without_version_bump_event_or_audit_row()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync("Same title");
        var eventsBefore = _app.Publisher.Events.Count;

        var (response, same) = await agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Title = "Same title", Version = ticket.Version });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ticket.Version, same!.Version);
        Assert.Equal(eventsBefore, _app.Publisher.Events.Count);
        var (_, history) = await agent.GetAsync<List<AuditEntryDto>>($"/api/tickets/{ticket.Id}/history");
        Assert.Single(history!);
    }

    [Fact]
    public async Task Resolved_tickets_cannot_be_edited()
    {
        var (_, agent, employee) = await SetupAsync();
        var resolved = await DriveToStatusAsync(agent, employee, TicketStatus.Resolved);

        var (response, _) = await agent.PatchAsync<TicketDto>($"/api/tickets/{resolved.Id}", new UpdateTicketRequest { Title = "edited", Version = resolved.Version });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // =====================================================================================================
    // SLA policies
    // =====================================================================================================

    [Fact]
    public async Task Tenant_admin_can_change_the_sla_policy_for_new_tickets_only()
    {
        var (t, agent, employee) = await SetupAsync();
        var before = await employee.CreateTicketAsync(priority: TicketPriority.Critical);

        var (response, policy) = await t.Admin.SendAsync<SlaPolicyDto>(HttpMethod.Put, "/api/sla-policies/Critical",
            new UpdateSlaPolicyRequest { FirstResponseMinutes = 10, ResolutionMinutes = 60 });
        var after = await employee.CreateTicketAsync(priority: TicketPriority.Critical);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(60, policy!.ResolutionMinutes);
        Assert.Equal(before.CreatedAt.AddHours(2), (await agent.GetTicketAsync(before.Id)).ResolutionDeadline);
        Assert.Equal(after.CreatedAt.AddHours(1), after.ResolutionDeadline);
    }

    [Theory]
    [InlineData("Critical", 90, 60)]     // first response longer than resolution
    [InlineData("Critical", 0, 60)]
    [InlineData("Critical", 10, 0)]
    [InlineData("Critical", -5, 60)]
    [InlineData("Critical", 10, 600_000)] // over the one-year cap
    [InlineData("Nonsense", 10, 60)]
    public async Task Invalid_sla_policies_are_rejected(string priority, int firstResponse, int resolution)
    {
        var (t, _, _) = await SetupAsync();

        var (response, _) = await t.Admin.SendAsync<SlaPolicyDto>(HttpMethod.Put, $"/api/sla-policies/{priority}",
            new UpdateSlaPolicyRequest { FirstResponseMinutes = firstResponse, ResolutionMinutes = resolution });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Sla_policies_are_listed_from_most_to_least_urgent()
    {
        var (_, agent, _) = await SetupAsync();

        var (_, policies) = await agent.GetAsync<List<SlaPolicyDto>>("/api/sla-policies");

        Assert.Equal([TicketPriority.Critical, TicketPriority.High, TicketPriority.Medium, TicketPriority.Low], policies!.Select(p => p.Priority));
    }

    // =====================================================================================================
    // Listing, filtering, paging
    // =====================================================================================================

    [Fact]
    public async Task The_list_is_newest_first_and_every_page_adds_new_tickets()
    {
        var (_, agent, employee) = await SetupAsync();
        var created = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            created.Add((await employee.CreateTicketAsync($"item {i}")).Id);
            _app.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var (_, p1) = await agent.GetAsync<PagedResult<TicketDto>>("/api/tickets?pageSize=2&page=1");
        var (_, p2) = await agent.GetAsync<PagedResult<TicketDto>>("/api/tickets?pageSize=2&page=2");
        var (_, p3) = await agent.GetAsync<PagedResult<TicketDto>>("/api/tickets?pageSize=2&page=3");

        Assert.Equal(5, p1!.TotalCount);
        Assert.Equal(created.AsEnumerable().Reverse(), p1.Items.Concat(p2!.Items).Concat(p3!.Items).Select(t => t.Id));
        Assert.Equal([2, 2, 1], [p1.Items.Count, p2.Items.Count, p3.Items.Count]);
    }

    [Fact]
    public async Task Pages_do_not_overlap_even_when_every_ticket_has_the_same_timestamp()
    {
        var (_, agent, employee) = await SetupAsync();
        for (var i = 0; i < 6; i++) await employee.CreateTicketAsync($"tie {i}"); // the clock does not move

        var ids = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var (_, p) = await agent.GetAsync<PagedResult<TicketDto>>($"/api/tickets?pageSize=2&page={page}");
            ids.AddRange(p!.Items.Select(t => t.Id));
        }

        Assert.Equal(6, ids.Distinct().Count());
    }

    [Fact]
    public async Task List_filters_work_alone_and_combined()
    {
        var (_, agent, employee) = await SetupAsync();
        var low = await employee.CreateTicketAsync("alpha low", TicketPriority.Low);
        var crit = await employee.CreateTicketAsync("beta critical", TicketPriority.Critical);
        var assigned = (await agent.TransitionAsync(crit, TicketStatus.Assigned, agent.Id)).Body!;

        async Task<List<Guid>> Ids(string q) => (await agent.GetAsync<PagedResult<TicketDto>>($"/api/tickets?{q}")).Body!.Items.Select(t => t.Id).ToList();

        Assert.Equal([crit.Id], await Ids("priority=Critical"));
        Assert.Equal([low.Id], await Ids("status=New"));
        Assert.Equal([crit.Id], await Ids($"assignedToId={agent.Id}"));
        Assert.Equal([crit.Id], await Ids("mine=true"));
        Assert.Equal([crit.Id], await Ids("status=Assigned&priority=Critical"));
        Assert.Empty(await Ids("status=Assigned&priority=Low"));
        Assert.Equal(2, (await Ids("breached=false")).Count);
        Assert.Empty(await Ids("breached=true"));
        Assert.Equal([crit.Id], await Ids($"search={assigned.TicketNumber.ToLowerInvariant()}")); // by ticket number, case-insensitive
        Assert.Equal([low.Id], await Ids("search=ALPHA"));
        Assert.Equal(2, (await Ids("search=%20%20")).Count); // whitespace-only search means "no search"
    }

    [Fact]
    public async Task Search_treats_percent_and_underscore_literally()
    {
        var (_, agent, employee) = await SetupAsync();
        var percent = await employee.CreateTicketAsync("Disk 100% full");
        await employee.CreateTicketAsync("Disk 100 full");
        var underscore = await employee.CreateTicketAsync("file_name missing");
        await employee.CreateTicketAsync("fileXname missing");

        var (_, byPercent) = await agent.GetAsync<PagedResult<TicketDto>>("/api/tickets?search=100%25");
        var (_, byUnderscore) = await agent.GetAsync<PagedResult<TicketDto>>("/api/tickets?search=file_name");

        Assert.Equal([percent.Id], byPercent!.Items.Select(t => t.Id));
        Assert.Equal([underscore.Id], byUnderscore!.Items.Select(t => t.Id));
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=-1")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=-5")]
    [InlineData("status=Nonsense")]
    [InlineData("assignedToId=not-a-guid")]
    public async Task Invalid_list_parameters_are_a_400(string query)
    {
        var (_, agent, _) = await SetupAsync();

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(agent.GetAsync<PagedResult<TicketDto>>($"/api/tickets?{query}")));
    }

    [Fact]
    public async Task A_page_past_the_end_is_empty_but_reports_the_true_total()
    {
        var (_, agent, employee) = await SetupAsync();
        await employee.CreateTicketAsync();

        var (response, page) = await agent.GetAsync<PagedResult<TicketDto>>("/api/tickets?page=99999&pageSize=100");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(page!.Items);
        Assert.Equal(1, page.TotalCount);
    }

    [Fact]
    public async Task Dashboard_summarises_the_tenant()
    {
        var (t, agent, employee) = await SetupAsync();
        await employee.CreateTicketAsync(priority: TicketPriority.Critical);
        var second = await employee.CreateTicketAsync();
        await agent.TransitionAsync(second, TicketStatus.Assigned, agent.Id);

        var (_, summary) = await t.Admin.GetAsync<DashboardSummaryDto>("/api/dashboard/summary");

        Assert.Equal(2, summary!.Open);
        Assert.Equal(1, summary.Unassigned);
        Assert.Equal(1, summary.Critical);
        Assert.Equal(1, summary.ByStatus[TicketStatus.New]);
        Assert.Equal(1, summary.ByStatus[TicketStatus.Assigned]);
    }

    [Fact]
    public async Task Dto_exposes_only_the_transitions_this_actor_may_take()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();

        Assert.Equal([TicketStatus.Assigned], (await agent.GetTicketAsync(ticket.Id)).AllowedTransitions);
        Assert.Empty((await employee.GetTicketAsync(ticket.Id)).AllowedTransitions);
        Assert.Equal(SlaState.Healthy, ticket.SlaState);
    }

    // =====================================================================================================
    // Input validation: one limit per test case, so a failure names the limit
    // =====================================================================================================

    [Theory]
    [InlineData("title too short", "ab", "valid description")]
    [InlineData("title whitespace only", "     ", "valid description")]
    [InlineData("title padded to short", "  ab  ", "valid description")]
    [InlineData("title too long", "T", "valid description")]
    [InlineData("description empty", "valid title", "")]
    [InlineData("description whitespace only", "valid title", "     ")]
    [InlineData("description too long", "valid title", "D")]
    public async Task Ticket_text_limits_are_enforced_on_create(string name, string title, string description)
    {
        var (_, _, employee) = await SetupAsync();
        if (name == "title too long") title = new string('T', 201);
        if (name == "description too long") description = new string('D', 8001);

        var (response, _) = await employee.PostAsync<TicketDto>("/api/tickets", new { title, description, priority = "Medium" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("  ab  ")]
    public async Task A_whitespace_only_or_padded_title_cannot_blank_a_ticket_on_edit(string title)
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync("Healthy title");

        var (response, _) = await agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new { title, version = ticket.Version });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Healthy title", (await agent.GetTicketAsync(ticket.Id)).Title);
    }

    [Fact]
    public async Task Comment_and_summary_limits_are_enforced()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();
        var working = await DriveToStatusAsync(agent, employee, TicketStatus.InProgress);

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(agent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new { content = "", isInternalNote = false })));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(agent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new { content = new string('c', 4001), isInternalNote = false })));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(agent.PostAsync<TicketDto>($"/api/tickets/{working.Id}/transitions",
            new { to = "Resolved", resolutionSummary = new string('s', 4001), version = working.Version })));
        Assert.Equal(HttpStatusCode.Created, await StatusOf(agent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new { content = new string('c', 4000), isInternalNote = false })));
    }

    [Fact]
    public async Task Required_fields_cannot_be_omitted()
    {
        var (_, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new { title = "no version" })));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(agent.PostAsync<TicketDto>($"/api/tickets/{ticket.Id}/transitions", new { version = ticket.Version }))); // no "to"
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(agent.PostAsync<TicketDto>($"/api/tickets/{ticket.Id}/transitions", new { to = "Assigned" })));      // no version
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(agent.PostAsync<TicketDto>($"/api/tickets/{ticket.Id}/assign", new { version = ticket.Version })));    // no assignee

        var after = await agent.GetTicketAsync(ticket.Id); // none of the rejected calls changed anything
        Assert.Equal(TicketStatus.New, after.Status);
        Assert.Equal(ticket.Version, after.Version);
    }

    [Fact]
    public async Task Enum_values_must_be_names_never_numbers_or_unknown_words()
    {
        var (t, agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();

        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(employee.PostAsync<TicketDto>("/api/tickets", new { title = "abc", description = "abc", priority = 99 })));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(employee.PostAsync<TicketDto>("/api/tickets", new { title = "abc", description = "abc", priority = 2 }))); // even a valid number
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(employee.PostAsync<TicketDto>("/api/tickets", new { title = "abc", description = "abc", priority = "Nonsense" })));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new { priority = 99, version = ticket.Version })));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(agent.PostAsync<TicketDto>($"/api/tickets/{ticket.Id}/transitions", new { to = 99, version = ticket.Version })));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(t.Admin.PostAsync<UserDto>("/api/users", new { email = "n@x.test", fullName = "Num Ber", role = 99, password = TestApp.Password })));
        Assert.Equal(HttpStatusCode.BadRequest, await StatusOf(t.Admin.PostAsync<UserDto>("/api/users", new { email = "m@x.test", fullName = "Num Ber", role = 1, password = TestApp.Password })));
    }

    [Fact]
    public async Task Malformed_or_empty_bodies_are_a_400_not_a_500()
    {
        var (_, _, employee) = await SetupAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await RawPost(employee, "/api/tickets", "{ this is not json")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RawPost(employee, "/api/tickets", "")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RawPost(employee, "/api/tickets", "null")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RawPost(employee, "/api/tickets", "[]")).StatusCode);
    }

    [Fact]
    public async Task Validation_problems_name_the_field()
    {
        var (_, _, employee) = await SetupAsync();

        var (response, _) = await employee.PostAsync<TicketDto>("/api/tickets", new { title = "ab", description = "valid", priority = "Medium" });
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Title", body, StringComparison.OrdinalIgnoreCase);
    }

    // =====================================================================================================
    // User management
    // =====================================================================================================

    [Fact]
    public async Task Duplicate_emails_in_a_tenant_are_a_409_whatever_the_casing()
    {
        var (t, _, _) = await SetupAsync();
        var first = await t.AddUserAsync(UserRole.Agent, "dupe");

        var (response, _) = await t.Admin.PostAsync<UserDto>("/api/users", new CreateUserRequest
        {
            Email = first.User.Email.ToUpperInvariant(), FullName = "Dupe Two", Role = UserRole.Agent, Password = TestApp.Password
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Emails_are_normalised_so_login_ignores_case()
    {
        var (t, _, _) = await SetupAsync();
        var (created, user) = await t.Admin.PostAsync<UserDto>("/api/users", new CreateUserRequest
        {
            Email = "MiXeD.Case@Example.TEST", FullName = "Mixed Case", Role = UserRole.Employee, Password = TestApp.Password
        });

        var login = await _app.LoginAsync(t.Tenant.Slug, "mixed.case@example.test");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("mixed.case@example.test", user!.Email);
        Assert.Equal(user.Id, login.Id);
    }

    [Theory]
    [InlineData("not-an-email", "Valid Name", "long-enough-pass")]
    [InlineData("ok@x.test", "A", "long-enough-pass")]
    [InlineData("ok@x.test", "Valid Name", "short")]
    [InlineData("ok@x.test", "Valid Name", "123456789")]
    public async Task Invalid_user_input_is_rejected(string email, string fullName, string password)
    {
        var (t, _, _) = await SetupAsync();

        var (response, _) = await t.Admin.PostAsync<UserDto>("/api/users", new CreateUserRequest { Email = email, FullName = fullName, Role = UserRole.Agent, Password = password });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task An_empty_status_body_does_not_silently_deactivate_a_user()
    {
        var (t, agent, _) = await SetupAsync();

        var (response, _) = await t.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{agent.Id}/status", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var (_, users) = await t.Admin.GetAsync<List<UserDto>>("/api/users");
        Assert.True(users!.Single(u => u.Id == agent.Id).IsActive);
    }

    [Fact]
    public async Task A_deactivated_user_can_be_reactivated_and_logs_in_again_unlocked()
    {
        var (t, agent, _) = await SetupAsync();
        for (var i = 0; i < 5; i++)
        {
            await _app.Anonymous().PostAsJsonAsync("/api/auth/login", new LoginRequest { TenantSlug = t.Tenant.Slug, Email = agent.User.Email, Password = "wrong-wrong-wrong" });
        }

        await t.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{agent.Id}/status", new { isActive = false });
        var (response, reactivated) = await t.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{agent.Id}/status", new { isActive = true });
        var login = await _app.Anonymous().PostAsJsonAsync("/api/auth/login", new LoginRequest { TenantSlug = t.Tenant.Slug, Email = agent.User.Email, Password = TestApp.Password });

        Assert.True(response.IsSuccessStatusCode);
        Assert.True(reactivated!.IsActive);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode); // the earlier lockout was cleared with the reactivation
    }

    [Fact]
    public async Task Changing_an_unknown_user_is_a_404_and_an_admin_can_deactivate_another_admin()
    {
        var (t, _, _) = await SetupAsync();
        var second = await t.AddUserAsync(UserRole.TenantAdmin, "admin2");

        var unknown = await t.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{Guid.NewGuid()}/status", new { isActive = false });
        var other = await t.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{second.Id}/status", new { isActive = false });

        Assert.Equal(HttpStatusCode.NotFound, unknown.Response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.Response.StatusCode);
    }

    // =====================================================================================================
    // Platform behaviour that is easy to forget
    // =====================================================================================================

    [Fact]
    public async Task Security_headers_are_on_pages_api_responses_and_errors_and_api_data_is_not_cacheable()
    {
        var (_, agent, _) = await SetupAsync();

        var page = await _app.Anonymous().GetAsync("/");
        var api = await agent.Http.GetAsync("/api/tickets");
        var unauthorised = await _app.Anonymous().GetAsync("/api/tickets");
        var missing = await agent.Http.GetAsync($"/api/tickets/{Guid.NewGuid()}");

        foreach (var response in new[] { page, api, unauthorised, missing })
        {
            Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        }

        foreach (var response in new[] { api, unauthorised, missing })
        {
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        }
    }

    [Fact]
    public async Task Health_is_public_and_meta_never_reveals_a_demo_password_when_demo_is_off()
    {
        var health = await _app.Anonymous().GetAsync("/health");
        var meta = await _app.Anonymous().GetStringAsync("/api/meta");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Contains("\"demo\":false", meta);
        Assert.DoesNotContain("Demo!", meta);
        Assert.DoesNotContain("demoPassword", meta);
    }

    [Fact]
    public async Task A_token_in_the_query_string_is_ignored_outside_the_hub()
    {
        var (_, agent, _) = await SetupAsync();

        var viaQuery = await _app.Anonymous().GetAsync($"/api/tickets?access_token={agent.Token}");

        Assert.Equal(HttpStatusCode.Unauthorized, viaQuery.StatusCode);
    }
}

