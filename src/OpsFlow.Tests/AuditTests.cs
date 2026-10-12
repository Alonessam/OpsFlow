using System.Net;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;
using OpsFlow.Tests.Support;

namespace OpsFlow.Tests;

public class AuditTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;

    public AuditTests(TestApp app) => _app = app;

    private async Task<(Actor Agent, Actor Employee)> SetupAsync()
    {
        var t = await _app.CreateTenantAsync($"audit-{Guid.NewGuid():N}"[..14]);
        return (await t.AddUserAsync(UserRole.Agent, "agent"), await t.AddUserAsync(UserRole.Employee, "emp"));
    }

    private static async Task<List<AuditEntryDto>> HistoryAsync(Actor who, Guid ticketId) =>
        (await who.GetAsync<List<AuditEntryDto>>($"/api/tickets/{ticketId}/history")).Body!;

    [Fact]
    public async Task Creation_and_every_audited_field_change_are_recorded_with_old_new_and_actor()
    {
        var (agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync("Laptop dead", TicketPriority.Medium);

        await agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Priority = TicketPriority.Critical, Version = ticket.Version });
        var current = await agent.GetTicketAsync(ticket.Id);
        await agent.TransitionAsync(current, TicketStatus.Assigned, agent.Id);

        var history = await HistoryAsync(agent, ticket.Id);

        Assert.Equal("Created", history[0].Field);
        Assert.Equal(employee.Id, history[0].ChangedBy!.Id);

        var priority = Assert.Single(history, h => h.Field == nameof(Ticket.Priority));
        Assert.Equal(("Medium", "Critical"), (priority.OldValue, priority.NewValue));
        Assert.Equal(agent.Id, priority.ChangedBy!.Id);

        var status = Assert.Single(history, h => h.Field == nameof(Ticket.Status));
        Assert.Equal(("New", "Assigned"), (status.OldValue, status.NewValue));

        var assignee = Assert.Single(history, h => h.Field == nameof(Ticket.AssignedToUserId));
        Assert.Null(assignee.OldValue);
        Assert.Equal(agent.User.FullName, assignee.NewValue); // ids are shown as names
    }

    [Fact]
    public async Task Title_and_description_changes_are_recorded()
    {
        var (agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync("Old title");

        await agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Title = "New title", Description = "New description", Version = ticket.Version });

        var history = await HistoryAsync(agent, ticket.Id);
        Assert.Equal(("Old title", "New title"), history.Where(h => h.Field == nameof(Ticket.Title)).Select(h => (h.OldValue, h.NewValue)).Single());
        Assert.Equal("New description", history.Single(h => h.Field == nameof(Ticket.Description)).NewValue);
    }

    [Fact]
    public async Task Resolution_summary_is_part_of_the_trail()
    {
        var (agent, employee) = await SetupAsync();
        var t = await employee.CreateTicketAsync();
        t = (await agent.TransitionAsync(t, TicketStatus.Assigned, agent.Id)).Body!;
        t = (await agent.TransitionAsync(t, TicketStatus.InProgress)).Body!;
        await agent.TransitionAsync(t, TicketStatus.Resolved, summary: "Swapped the cable, tested OK.");

        var history = await HistoryAsync(agent, t.Id);

        Assert.Equal("Swapped the cable, tested OK.", Assert.Single(history, h => h.Field == nameof(Ticket.ResolutionSummary)).NewValue);
    }

    [Fact]
    public async Task Rows_written_by_one_save_have_a_stable_chronological_order()
    {
        var (agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();

        // One save changes status AND assignee AND (via the first response) nothing else audited.
        await agent.TransitionAsync(ticket, TicketStatus.Assigned, agent.Id);

        for (var i = 0; i < 3; i++) // the same answer every time we read it
        {
            var history = await HistoryAsync(agent, ticket.Id);
            Assert.Equal(["Created", "Status", "AssignedToUserId"], history.Select(h => h.Field));
            // The two rows written by the SAME save are told apart by their timestamps, not by luck.
            Assert.True(history[1].Timestamp < history[2].Timestamp, "rows of one save must have strictly increasing timestamps");
        }
    }

    [Fact]
    public async Task Saving_without_changing_anything_writes_no_audit_rows()
    {
        var (agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync("Same title");

        var (response, unchanged) = await agent.PatchAsync<TicketDto>($"/api/tickets/{ticket.Id}", new UpdateTicketRequest { Title = "Same title", Version = ticket.Version });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ticket.Version, unchanged!.Version);
        Assert.Single(await HistoryAsync(agent, ticket.Id)); // just "Created"
    }

    [Fact]
    public async Task Comments_and_sla_bookkeeping_do_not_pollute_the_trail()
    {
        var (agent, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();

        var (response, _) = await agent.PostAsync<CommentDto>($"/api/tickets/{ticket.Id}/comments", new AddCommentRequest { Content = "Hello" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var history = await HistoryAsync(agent, ticket.Id);
        Assert.Equal(["Created"], history.Select(h => h.Field)); // nothing else: no FirstRespondedAt, deadlines, version, timestamps...
    }

    [Fact]
    public async Task The_requester_can_read_their_own_timeline()
    {
        var (_, employee) = await SetupAsync();
        var ticket = await employee.CreateTicketAsync();

        var own = await employee.GetAsync<List<AuditEntryDto>>($"/api/tickets/{ticket.Id}/history");

        Assert.True(own.Response.IsSuccessStatusCode);
        Assert.NotEmpty(own.Body!);
    }
}

