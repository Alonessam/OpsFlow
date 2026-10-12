using OpsFlow.Core.Domain;

namespace OpsFlow.Core.Workflow;

public readonly record struct TransitionContext(
    TicketStatus From,
    TicketStatus To,
    UserRole ActorRole,
    bool ActorIsRequester,
    bool HasAssignee,
    string? ResolutionSummary);

public readonly record struct TransitionResult(bool IsAllowed, string? Code = null, string? Message = null)
{
    public static TransitionResult Allowed => new(true);
    public static TransitionResult Deny(string code, string message) => new(false, code, message);
}

/// <summary>
/// The ticket workflow as a transition matrix plus a handful of guard rules. Pure: no I/O, no clock,
/// so the entire rule set is unit-tested exhaustively (every from/to pair, every role).
/// </summary>
public static class TicketStateMachine
{
    public const int MinResolutionSummaryLength = 10;

    private static readonly IReadOnlyDictionary<TicketStatus, TicketStatus[]> Matrix =
        new Dictionary<TicketStatus, TicketStatus[]>
        {
            [TicketStatus.New] = [TicketStatus.Assigned],
            [TicketStatus.Assigned] = [TicketStatus.InProgress, TicketStatus.New],
            [TicketStatus.InProgress] = [TicketStatus.PendingClient, TicketStatus.Resolved, TicketStatus.Assigned],
            [TicketStatus.PendingClient] = [TicketStatus.InProgress, TicketStatus.Resolved],
            [TicketStatus.Resolved] = [TicketStatus.Closed, TicketStatus.InProgress],
            [TicketStatus.Closed] = [TicketStatus.InProgress]
        };

    /// <summary>Structurally reachable states, ignoring who is asking and what data they supplied.</summary>
    public static IReadOnlyList<TicketStatus> NextStates(TicketStatus from) => Matrix[from];

    /// <summary>States this actor may actually move the ticket to (used to drive the UI buttons).</summary>
    public static IReadOnlyList<TicketStatus> AllowedFor(TicketStatus from, UserRole role, bool isRequester) =>
        Matrix[from].Where(to => RoleMayTransition(from, to, role, isRequester) is null).ToArray();

    public static TransitionResult Validate(TransitionContext c)
    {
        if (c.From == c.To)
        {
            return TransitionResult.Deny("same_status", $"Ticket is already {c.To}.");
        }

        if (!Matrix[c.From].Contains(c.To))
        {
            return TransitionResult.Deny("invalid_transition", $"A ticket cannot move from {c.From} to {c.To}.");
        }

        var roleProblem = RoleMayTransition(c.From, c.To, c.ActorRole, c.ActorIsRequester);
        if (roleProblem is not null)
        {
            return roleProblem.Value;
        }

        if (c.To == TicketStatus.Assigned && !c.HasAssignee)
        {
            return TransitionResult.Deny("assignee_required", "Assigning a ticket requires an assignee.");
        }

        if (c.To == TicketStatus.Resolved
            && (c.ResolutionSummary is null || c.ResolutionSummary.Trim().Length < MinResolutionSummaryLength))
        {
            return TransitionResult.Deny("resolution_summary_required",
                $"Resolving a ticket requires a resolution summary of at least {MinResolutionSummaryLength} characters.");
        }

        return TransitionResult.Allowed;
    }

    private static TransitionResult? RoleMayTransition(TicketStatus from, TicketStatus to, UserRole role, bool isRequester)
    {
        if (from == TicketStatus.Closed && role != UserRole.TenantAdmin)
        {
            return TransitionResult.Deny("closed_reopen_forbidden", "Only a tenant admin can reopen a closed ticket.");
        }

        if (role is UserRole.Agent or UserRole.TenantAdmin)
        {
            return null;
        }

        // Employees: only the requester, and only to confirm (close) or reject (reopen) a resolution.
        var employeeMove = from == TicketStatus.Resolved && to is TicketStatus.Closed or TicketStatus.InProgress;
        return isRequester && employeeMove
            ? null
            : TransitionResult.Deny("forbidden", "You are not allowed to make this transition.");
    }
}
