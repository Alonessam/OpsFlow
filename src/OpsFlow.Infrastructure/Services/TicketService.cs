using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Common;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;
using OpsFlow.Core.Sla;
using OpsFlow.Core.Workflow;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Services;

/// <summary>
/// Note what is NOT in this file: a single hand-written tenant predicate. Tenant isolation is the
/// DbContext's job. What IS here is the second, orthogonal boundary: row-level access *inside* a
/// tenant (an employee sees only their own tickets) and the workflow rules.
/// </summary>
public sealed class TicketService(
    OpsFlowDbContext db,
    ICurrentTenant me,
    TimeProvider time,
    ITicketEventPublisher events,
    ILogger<TicketService> logger) : ITicketService
{
    private const int SequenceRetries = 20;
    private const int CommentRetries = 3;
    private const int MinTextLength = 3;

    private Guid ActorId => me.UserId ?? throw new InvalidOperationException("No authenticated user.");
    private UserRole ActorRole => me.Role ?? throw new InvalidOperationException("No authenticated user.");
    private bool ActorIsStaff => ActorRole is UserRole.Agent or UserRole.TenantAdmin;

    // ---- create / read ---------------------------------------------------------------------

    public async Task<Result<TicketDto>> CreateAsync(CreateTicketRequest request, CancellationToken ct = default)
    {
        // Requesters state how urgent it is; staff re-triage afterwards. (Only staff can change the priority later.)
        var invalid = ValidateText(request.Title, "title") ?? ValidateText(request.Description, "description") ?? ValidatePriority(request.Priority);
        if (invalid is not null)
        {
            return invalid;
        }

        var now = time.GetUtcNow();
        var targets = await GetTargetsAsync(request.Priority, ct);
        Ticket? ticket = null;
        var saved = false;

        for (var attempt = 1; attempt <= SequenceRetries && !saved; attempt++)
        {
            // Ticket numbers are per tenant. The sequence lives on the tenant row and is guarded by a
            // concurrency token: two simultaneous creates cannot both claim the same number.
            var tenant = await db.Tenants.FirstAsync(t => t.Id == me.TenantId, ct);
            tenant.TicketSequence++;

            ticket = new Ticket
            {
                Sequence = tenant.TicketSequence,
                TicketNumber = $"{tenant.Slug.ToUpperInvariant()}-{tenant.TicketSequence:D4}",
                Title = request.Title.Trim(),
                Description = request.Description.Trim(),
                Priority = request.Priority,
                CreatedByUserId = ActorId,
                CreatedAt = now
            };
            ticket.ApplySla(targets);
            db.Tickets.Add(ticket);

            try
            {
                await db.SaveChangesAsync(ct);
                saved = true;
            }
            catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || ex.IsUniqueViolation())
            {
                // Someone else took this number. Start over with a fresh read, after a short randomised back-off
                // (jitter stops the losers from retrying in lockstep).
                db.ChangeTracker.Clear();
                await Task.Delay(attempt * 2 + RandomNumberGenerator.GetInt32(0, 8), ct);
            }
        }

        if (!saved)
        {
            return Error.Conflict("ticket_number_contention", "Too many tickets are being created at once. Please retry.");
        }

        await PublishSafelyAsync(ToEvent(TicketEventType.TicketCreated, ticket!, $"{ticket!.Priority} priority"), ct);
        return await ToDtoAsync(ticket!, ct);
    }

    public async Task<Result<TicketDto>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var ticket = await FindVisibleAsync(id, ct);
        return ticket is null ? Error.NotFound("Ticket") : await ToDtoAsync(ticket, ct);
    }

    public async Task<Result<PagedResult<TicketDto>>> ListAsync(TicketQuery q, CancellationToken ct = default)
    {
        var query = VisibleTickets();

        if (q.Status is { } status) query = query.Where(t => t.Status == status);
        if (q.Priority is { } priority) query = query.Where(t => t.Priority == priority);
        if (q.AssignedToId is { } assignee) query = query.Where(t => t.AssignedToUserId == assignee);
        if (q.Breached == true) query = query.Where(t => t.IsSlaBreached || t.ResponseBreachedAt != null);
        if (q.Breached == false) query = query.Where(t => !t.IsSlaBreached && t.ResponseBreachedAt == null);
        if (q.Mine == true)
        {
            var self = ActorId;
            query = ActorIsStaff ? query.Where(t => t.AssignedToUserId == self) : query.Where(t => t.CreatedByUserId == self);
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            var term = q.Search.Trim().ToLowerInvariant();
            query = query.Where(t => t.Title.ToLower().Contains(term) || t.TicketNumber.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var page = await query
            .OrderByDescending(t => t.CreatedAt)
            .ThenByDescending(t => t.Id) // tie-breaker: pages must not shift when timestamps are equal
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToListAsync(ct);

        return new PagedResult<TicketDto>(await ToDtosAsync(page, ct), q.Page, q.PageSize, total);
    }

    // ---- update ----------------------------------------------------------------------------

    public async Task<Result<TicketDto>> UpdateAsync(Guid id, UpdateTicketRequest request, CancellationToken ct = default)
    {
        var ticket = await FindVisibleAsync(id, ct);
        if (ticket is null)
        {
            return Error.NotFound("Ticket");
        }

        if (!ActorIsStaff)
        {
            if (ticket.Status != TicketStatus.New)
            {
                return Error.Forbidden("forbidden", "A ticket can only be edited by its requester while it is still New.");
            }

            if (request.Priority is not null && request.Priority != ticket.Priority)
            {
                return Error.Forbidden("forbidden", "Only staff can change the priority.");
            }
        }

        if (!ticket.IsOpen)
        {
            return Error.Conflict("ticket_not_open", "A resolved or closed ticket cannot be edited.");
        }

        var invalid = ValidateText(request.Title, "title") ?? ValidateText(request.Description, "description")
                      ?? (request.Priority is { } p ? ValidatePriority(p) : null);
        if (invalid is not null)
        {
            return invalid;
        }

        if (ticket.Version != request.Version)
        {
            return VersionConflict();
        }

        if (request.Title is not null) ticket.Title = request.Title.Trim();
        if (request.Description is not null) ticket.Description = request.Description.Trim();
        if (request.Priority is { } newPriority && newPriority != ticket.Priority)
        {
            ticket.Priority = newPriority;
            ticket.ApplySla(await GetTargetsAsync(newPriority, ct));
        }

        if (!db.ChangeTracker.HasChanges())
        {
            return await ToDtoAsync(ticket, ct); // nothing changed: no write, no version bump, no event
        }

        var conflict = await TrySaveAsync(ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await PublishSafelyAsync(ToEvent(TicketEventType.TicketUpdated, ticket), ct);
        return await ToDtoAsync(ticket, ct);
    }

    public async Task<Result<TicketDto>> TransitionAsync(Guid id, TransitionRequest request, CancellationToken ct = default)
    {
        var ticket = await FindVisibleAsync(id, ct);
        if (ticket is null)
        {
            return Error.NotFound("Ticket");
        }

        if (ticket.Version != request.Version)
        {
            return VersionConflict();
        }

        var assigneeId = request.AssigneeId ?? ticket.AssignedToUserId;
        var verdict = TicketStateMachine.Validate(new TransitionContext(
            ticket.Status,
            request.To,
            ActorRole,
            ticket.CreatedByUserId == ActorId,
            assigneeId is not null,
            request.ResolutionSummary));

        if (!verdict.IsAllowed)
        {
            var kind = verdict.Code switch
            {
                "forbidden" or "closed_reopen_forbidden" => ErrorKind.Forbidden,
                "assignee_required" or "resolution_summary_required" => ErrorKind.Validation, // missing input
                _ => ErrorKind.Conflict // the move itself is not allowed from this state
            };
            return new Error(kind, verdict.Code!, verdict.Message!);
        }

        if (request.To == TicketStatus.Assigned)
        {
            var assigneeCheck = await ValidateAssigneeAsync(assigneeId!.Value, ct);
            if (assigneeCheck is not null)
            {
                return assigneeCheck;
            }
        }

        var from = ticket.Status;
        ApplyTransition(ticket, from, request, assigneeId, time.GetUtcNow());

        var conflict = await TrySaveAsync(ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await PublishSafelyAsync(ToEvent(TicketEventType.TicketTransitioned, ticket, $"{from} -> {ticket.Status}"), ct);
        return await ToDtoAsync(ticket, ct);
    }

    public async Task<Result<TicketDto>> AssignAsync(Guid id, AssignRequest request, CancellationToken ct = default)
    {
        if (!ActorIsStaff)
        {
            return Error.Forbidden("forbidden", "Only staff can assign tickets.");
        }

        var ticket = await FindVisibleAsync(id, ct);
        if (ticket is null)
        {
            return Error.NotFound("Ticket");
        }

        if (!ticket.IsOpen)
        {
            return Error.Conflict("ticket_not_open", "A resolved or closed ticket cannot be reassigned.");
        }

        if (ticket.Status == TicketStatus.New)
        {
            return await TransitionAsync(id,
                new TransitionRequest { To = TicketStatus.Assigned, AssigneeId = request.AssigneeId, Version = request.Version }, ct);
        }

        if (ticket.Version != request.Version)
        {
            return VersionConflict();
        }

        var assigneeCheck = await ValidateAssigneeAsync(request.AssigneeId, ct);
        if (assigneeCheck is not null)
        {
            return assigneeCheck;
        }

        if (ticket.AssignedToUserId == request.AssigneeId)
        {
            return await ToDtoAsync(ticket, ct); // already assigned to them
        }

        ticket.AssignedToUserId = request.AssigneeId;

        var conflict = await TrySaveAsync(ct);
        if (conflict is not null)
        {
            return conflict;
        }

        await PublishSafelyAsync(ToEvent(TicketEventType.TicketUpdated, ticket, "reassigned"), ct);
        return await ToDtoAsync(ticket, ct);
    }

    // ---- comments / history / dashboard ----------------------------------------------------

    public async Task<Result<CommentDto>> AddCommentAsync(Guid id, AddCommentRequest request, CancellationToken ct = default)
    {
        if (request.IsInternalNote && !ActorIsStaff)
        {
            return Error.Forbidden("forbidden", "Only staff can write internal notes.");
        }

        Ticket? ticket = null;
        TicketComment? comment = null;
        var saved = false;

        for (var attempt = 1; attempt <= CommentRetries && !saved; attempt++)
        {
            ticket = await FindVisibleAsync(id, ct);
            if (ticket is null)
            {
                return Error.NotFound("Ticket");
            }

            if (ticket.Status == TicketStatus.Closed)
            {
                return Error.Conflict("ticket_closed", "A closed ticket cannot receive comments.");
            }

            var now = time.GetUtcNow();
            comment = new TicketComment
            {
                TicketId = ticket.Id,
                UserId = ActorId,
                Content = request.Content.Trim(),
                IsInternalNote = request.IsInternalNote,
                CreatedAt = now
            };
            db.TicketComments.Add(comment);

            // A public answer from someone other than the requester is the "first response" the SLA measures.
            // That touches the ticket row too, so a concurrent edit can collide: re-read and try again.
            if (ActorIsStaff && !request.IsInternalNote && ticket.CreatedByUserId != ActorId && ticket.FirstRespondedAt is null)
            {
                ticket.MarkFirstResponse(now);
            }

            try
            {
                await db.SaveChangesAsync(ct);
                saved = true;
            }
            catch (DbUpdateConcurrencyException)
            {
                db.ChangeTracker.Clear();
            }
        }

        if (!saved)
        {
            return Error.Conflict("version_conflict", "The ticket is being changed by someone else. Please retry.");
        }

        await PublishSafelyAsync(ToEvent(TicketEventType.CommentAdded, ticket!, request.IsInternalNote ? "internal note" : "comment") with
        {
            StaffOnly = request.IsInternalNote
        }, ct);

        var author = await db.Users.FirstAsync(u => u.Id == ActorId, ct);
        return new CommentDto(comment!.Id, new PersonRef(author.Id, author.FullName), comment.Content, comment.IsInternalNote, comment.CreatedAt);
    }

    public async Task<Result<IReadOnlyList<CommentDto>>> ListCommentsAsync(Guid id, CancellationToken ct = default)
    {
        var ticket = await FindVisibleAsync(id, ct);
        if (ticket is null)
        {
            return Error.NotFound("Ticket");
        }

        var query = db.TicketComments.Where(c => c.TicketId == ticket.Id);
        if (!ActorIsStaff)
        {
            query = query.Where(c => !c.IsInternalNote);
        }

        var comments = await query.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        var people = await LoadPeopleAsync(comments.Select(c => c.UserId), ct);
        return comments
            .Select(c => new CommentDto(c.Id, Person(people, c.UserId), c.Content, c.IsInternalNote, c.CreatedAt))
            .ToList();
    }

    public async Task<Result<IReadOnlyList<AuditEntryDto>>> GetHistoryAsync(Guid id, CancellationToken ct = default)
    {
        var ticket = await FindVisibleAsync(id, ct);
        if (ticket is null)
        {
            return Error.NotFound("Ticket");
        }

        var logs = await db.TicketAuditLogs.Where(a => a.TicketId == ticket.Id).OrderBy(a => a.Timestamp).ThenBy(a => a.Id).ToListAsync(ct);

        // The audit table stores raw ids (stable, never rewritten); names are resolved only for display.
        static Guid? AsId(string? value) => Guid.TryParse(value, out var parsed) ? parsed : null;
        var assigneeIds = logs.Where(l => l.PropertyName == nameof(Ticket.AssignedToUserId))
            .SelectMany(l => new[] { AsId(l.OldValue), AsId(l.NewValue) })
            .Where(v => v is not null)
            .Select(v => v!.Value);
        var people = await LoadPeopleAsync(
            logs.Where(l => l.ChangedByUserId is not null).Select(l => l.ChangedByUserId!.Value).Concat(assigneeIds), ct);

        string? Display(TicketAuditLog l, string? value) =>
            l.PropertyName == nameof(Ticket.AssignedToUserId) && AsId(value) is { } assignee ? Person(people, assignee).FullName : value;

        return logs
            .Select(l => new AuditEntryDto(l.Id, l.PropertyName, Display(l, l.OldValue), Display(l, l.NewValue),
                l.ChangedByUserId is { } uid ? Person(people, uid) : null, l.Timestamp))
            .ToList();
    }

    public async Task<Result<DashboardSummaryDto>> GetDashboardAsync(CancellationToken ct = default)
    {
        if (!ActorIsStaff)
        {
            return Error.Forbidden("forbidden", "Only staff can view the operations dashboard.");
        }

        var byStatusRows = await db.Tickets.GroupBy(t => t.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToListAsync(ct);
        var byStatus = Enum.GetValues<TicketStatus>().ToDictionary(s => s, s => byStatusRows.FirstOrDefault(r => r.Status == s)?.Count ?? 0);

        var open = db.Tickets.Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed);
        return new DashboardSummaryDto(
            byStatus,
            await open.CountAsync(ct),
            await open.CountAsync(t => t.AssignedToUserId == null, ct),
            await open.CountAsync(t => t.IsSlaBreached || t.ResponseBreachedAt != null, ct),
            await open.CountAsync(t => !t.IsSlaBreached && t.ResponseBreachedAt == null && t.SlaWarningAt != null && t.SlaPausedAt == null, ct),
            await open.CountAsync(t => t.Priority == TicketPriority.Critical, ct));
    }

    // ---- helpers ---------------------------------------------------------------------------

    private void ApplyTransition(Ticket ticket, TicketStatus from, TransitionRequest request, Guid? assigneeId, DateTimeOffset now)
    {
        // Credit the time spent waiting for the customer FIRST. Every deadline comparison below must see
        // the already-extended deadline, otherwise a ticket resolved straight out of PendingClient would
        // be judged against a deadline that does not yet include the pause.
        if (from == TicketStatus.PendingClient)
        {
            ticket.ResumeSla(now);
        }

        ticket.Status = request.To;

        switch (request.To)
        {
            case TicketStatus.New:
                ticket.AssignedToUserId = null;
                break;
            case TicketStatus.Assigned:
                ticket.AssignedToUserId = assigneeId;
                break;
            case TicketStatus.InProgress:
                // Picking the ticket up counts as the first response; a requester reopening does not.
                if (ActorIsStaff)
                {
                    ticket.MarkFirstResponse(now);
                }

                // Reopening (from Resolved/Closed) starts a fresh resolution window.
                if (from is TicketStatus.Resolved or TicketStatus.Closed)
                {
                    ticket.RestartResolutionWindow(now);
                }

                break;
            case TicketStatus.PendingClient:
                ticket.PauseSla(now);
                break;
            case TicketStatus.Resolved:
                ticket.ResolutionSummary = request.ResolutionSummary!.Trim();
                ticket.ResolvedAt = now;
                ticket.MarkFirstResponse(now);
                if (now > ticket.ResolutionDeadline)
                {
                    ticket.IsSlaBreached = true;
                    ticket.SlaBreachedAt ??= now;
                }

                break;
            case TicketStatus.Closed:
                ticket.ClosedAt = now;
                break;
        }
    }

    private async Task<Error?> ValidateAssigneeAsync(Guid assigneeId, CancellationToken ct)
    {
        // Filtered by tenant: an id from another tenant is simply "not found".
        var assignee = await db.Users.FirstOrDefaultAsync(u => u.Id == assigneeId, ct);
        if (assignee is null || !assignee.IsActive || !assignee.IsStaff)
        {
            return Error.Validation("invalid_assignee", "The assignee must be an active agent or admin of this tenant.");
        }

        return null;
    }

    private static Error? ValidateText(string? value, string field) =>
        value is not null && value.Trim().Length < MinTextLength
            ? Error.Validation($"{field}_too_short", $"The {field} must be at least {MinTextLength} characters (after trimming).")
            : null;

    private static Error? ValidatePriority(TicketPriority priority) =>
        Enum.IsDefined(priority) ? null : Error.Validation("invalid_priority", "Unknown priority.");

    private async Task<SlaTargets> GetTargetsAsync(TicketPriority priority, CancellationToken ct)
    {
        var policy = await db.SlaPolicies.FirstOrDefaultAsync(p => p.Priority == priority, ct);
        return policy is null ? SlaTargets.Default(priority) : new SlaTargets(policy.FirstResponseMinutes, policy.ResolutionMinutes);
    }

    /// <summary>Tickets this actor may see: everything in the tenant for staff, only their own otherwise.</summary>
    private IQueryable<Ticket> VisibleTickets()
    {
        if (ActorIsStaff)
        {
            return db.Tickets;
        }

        var self = ActorId;
        return db.Tickets.Where(t => t.CreatedByUserId == self);
    }

    private Task<Ticket?> FindVisibleAsync(Guid id, CancellationToken ct) =>
        VisibleTickets().FirstOrDefaultAsync(t => t.Id == id, ct);

    /// <summary>Returns a conflict error if another writer won the race, otherwise null.</summary>
    private async Task<Error?> TrySaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            return VersionConflict().Error;
        }
    }

    private static Result<TicketDto> VersionConflict() =>
        Error.Conflict("version_conflict", "The ticket was changed by someone else. Reload and try again.");

    /// <summary>A failed push notification must never turn an already-committed change into an error response.</summary>
    private async Task PublishSafelyAsync(TicketEvent ticketEvent, CancellationToken ct)
    {
        try
        {
            await events.PublishAsync(ticketEvent, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not publish {EventType} for {Ticket}.", ticketEvent.Type, ticketEvent.TicketNumber);
        }
    }

    private TicketEvent ToEvent(TicketEventType type, Ticket t, string? detail = null) => new(
        type, t.TenantId, t.Id, t.TicketNumber, t.Title, t.Priority, t.Status, t.CreatedByUserId, time.GetUtcNow(), detail);

    private async Task<Dictionary<Guid, User>> LoadPeopleAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        return await db.Users.Where(u => distinct.Contains(u.Id)).ToDictionaryAsync(u => u.Id, ct);
    }

    private static PersonRef Person(Dictionary<Guid, User> people, Guid id) =>
        people.TryGetValue(id, out var u) ? new PersonRef(u.Id, u.FullName) : new PersonRef(id, "Unknown user");

    private async Task<TicketDto> ToDtoAsync(Ticket ticket, CancellationToken ct) => (await ToDtosAsync([ticket], ct))[0];

    private async Task<IReadOnlyList<TicketDto>> ToDtosAsync(IReadOnlyList<Ticket> tickets, CancellationToken ct)
    {
        var ids = tickets.Select(t => t.CreatedByUserId)
            .Concat(tickets.Where(t => t.AssignedToUserId is not null).Select(t => t.AssignedToUserId!.Value));
        var people = await LoadPeopleAsync(ids, ct);
        var now = time.GetUtcNow();

        return tickets.Select(t => new TicketDto(
            t.Id, t.TicketNumber, t.Title, t.Description, t.Priority, t.Status,
            Person(people, t.CreatedByUserId),
            t.AssignedToUserId is { } a ? Person(people, a) : null,
            t.CreatedAt, t.UpdatedAt, t.ResponseDeadline, t.ResolutionDeadline, t.FirstRespondedAt,
            t.HasMissedTarget, t.GetSlaState(now), t.ResolutionSummary, t.Version, t.ResolutionMinutes,
            TicketStateMachine.AllowedFor(t.Status, ActorRole, t.CreatedByUserId == ActorId))).ToList();
    }
}

