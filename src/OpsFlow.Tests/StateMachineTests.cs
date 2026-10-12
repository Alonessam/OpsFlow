using OpsFlow.Core.Domain;
using OpsFlow.Core.Workflow;

namespace OpsFlow.Tests;

/// <summary>
/// The expected behaviour below is written out LITERALLY (the 11 legal edges, the role rules). It is
/// deliberately not derived from the production matrix: if someone adds or removes an edge, these tests fail.
/// </summary>
public class StateMachineTests
{
    private static readonly TicketStatus[] AllStatuses = Enum.GetValues<TicketStatus>();
    private static readonly UserRole[] AllRoles = Enum.GetValues<UserRole>();

    private const string ValidSummary = "Replaced the faulty power supply.";

    /// <summary>The workflow diagram from the README, edge by edge.</summary>
    private static readonly HashSet<(TicketStatus From, TicketStatus To)> LegalEdges =
    [
        (TicketStatus.New, TicketStatus.Assigned),
        (TicketStatus.Assigned, TicketStatus.InProgress),
        (TicketStatus.Assigned, TicketStatus.New),
        (TicketStatus.InProgress, TicketStatus.PendingClient),
        (TicketStatus.InProgress, TicketStatus.Resolved),
        (TicketStatus.InProgress, TicketStatus.Assigned),
        (TicketStatus.PendingClient, TicketStatus.InProgress),
        (TicketStatus.PendingClient, TicketStatus.Resolved),
        (TicketStatus.Resolved, TicketStatus.Closed),
        (TicketStatus.Resolved, TicketStatus.InProgress),
        (TicketStatus.Closed, TicketStatus.InProgress)
    ];

    /// <summary>Literal permission rules: who may take a legal edge.</summary>
    private static bool Expected(TicketStatus from, TicketStatus to, UserRole role, bool requester)
    {
        if (!LegalEdges.Contains((from, to)))
        {
            return false;
        }

        return role switch
        {
            UserRole.TenantAdmin => true,
            UserRole.Agent => from != TicketStatus.Closed, // only an admin reopens a closed ticket
            UserRole.Employee => requester && from == TicketStatus.Resolved && to is TicketStatus.Closed or TicketStatus.InProgress,
            _ => false
        };
    }

    private static TransitionContext Ctx(TicketStatus from, TicketStatus to, UserRole role = UserRole.Agent,
        bool requester = false, bool assignee = true, string? summary = ValidSummary) =>
        new(from, to, role, requester, assignee, summary);

    public static TheoryData<TicketStatus, TicketStatus, UserRole, bool> Everything()
    {
        var data = new TheoryData<TicketStatus, TicketStatus, UserRole, bool>();
        foreach (var from in AllStatuses)
            foreach (var to in AllStatuses)
                foreach (var role in AllRoles)
                    foreach (var requester in new[] { true, false })
                    {
                        data.Add(from, to, role, requester);
                    }

        return data;
    }

    [Fact]
    public void The_machine_has_exactly_the_eleven_documented_edges()
    {
        var actual = AllStatuses.SelectMany(from => TicketStateMachine.NextStates(from).Select(to => (from, to))).ToHashSet();

        Assert.Equal(11, LegalEdges.Count);
        Assert.True(LegalEdges.SetEquals(actual),
            $"Edges differ. Missing: {string.Join(", ", LegalEdges.Except(actual))}. Unexpected: {string.Join(", ", actual.Except(LegalEdges))}.");
    }

    [Theory]
    [MemberData(nameof(Everything))]
    public void Every_status_pair_for_every_role_and_requester_flag_matches_the_literal_rules(
        TicketStatus from, TicketStatus to, UserRole role, bool requester)
    {
        var result = TicketStateMachine.Validate(Ctx(from, to, role, requester));

        Assert.Equal(Expected(from, to, role, requester), result.IsAllowed);
    }

    [Theory]
    [InlineData(TicketStatus.New, TicketStatus.Closed)]
    [InlineData(TicketStatus.New, TicketStatus.Resolved)]
    [InlineData(TicketStatus.New, TicketStatus.InProgress)]
    [InlineData(TicketStatus.Assigned, TicketStatus.Closed)]
    [InlineData(TicketStatus.InProgress, TicketStatus.Closed)]
    public void Skipping_steps_is_rejected_even_for_the_admin(TicketStatus from, TicketStatus to)
    {
        var result = TicketStateMachine.Validate(Ctx(from, to, UserRole.TenantAdmin));

        Assert.False(result.IsAllowed);
        Assert.Equal("invalid_transition", result.Code);
    }

    [Fact]
    public void Same_status_is_rejected()
    {
        Assert.Equal("same_status", TicketStateMachine.Validate(Ctx(TicketStatus.New, TicketStatus.New)).Code);
    }

    [Fact]
    public void Assigning_requires_an_assignee()
    {
        var result = TicketStateMachine.Validate(Ctx(TicketStatus.New, TicketStatus.Assigned, assignee: false));

        Assert.Equal("assignee_required", result.Code);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("123456789", false)]            // 9 characters
    [InlineData("1234567890", true)]            // exactly 10
    [InlineData("      ab", false)]             // long only because of padding
    [InlineData("  1234567890  ", true)]        // 10 after trimming
    public void The_resolution_summary_boundary_is_ten_characters_after_trimming(string? summary, bool allowed)
    {
        var result = TicketStateMachine.Validate(Ctx(TicketStatus.InProgress, TicketStatus.Resolved, summary: summary));

        Assert.Equal(allowed, result.IsAllowed);
        if (!allowed)
        {
            Assert.Equal("resolution_summary_required", result.Code);
        }
    }

    [Theory]
    [InlineData(UserRole.Agent, false)]
    [InlineData(UserRole.Employee, true)]
    public void Closed_tickets_are_reopened_only_by_a_tenant_admin(UserRole role, bool requester)
    {
        Assert.Equal("closed_reopen_forbidden",
            TicketStateMachine.Validate(Ctx(TicketStatus.Closed, TicketStatus.InProgress, role, requester)).Code);
        Assert.True(TicketStateMachine.Validate(Ctx(TicketStatus.Closed, TicketStatus.InProgress, UserRole.TenantAdmin)).IsAllowed);
    }

    [Fact]
    public void An_employee_who_is_not_the_requester_gets_forbidden_not_invalid()
    {
        var result = TicketStateMachine.Validate(Ctx(TicketStatus.Resolved, TicketStatus.Closed, UserRole.Employee, requester: false));

        Assert.Equal("forbidden", result.Code);
    }

    [Fact]
    public void AllowedFor_agrees_with_the_literal_rules_for_every_state_role_and_requester_flag()
    {
        foreach (var from in AllStatuses)
            foreach (var role in AllRoles)
                foreach (var requester in new[] { true, false })
                {
                    var expected = AllStatuses.Where(to => Expected(from, to, role, requester)).ToHashSet();
                    var offered = TicketStateMachine.AllowedFor(from, role, requester).ToHashSet();

                    Assert.True(expected.SetEquals(offered), $"{from}/{role}/requester={requester}: expected [{string.Join(",", expected)}], offered [{string.Join(",", offered)}]");
                }
    }
}
