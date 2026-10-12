using OpsFlow.Core.Sla;

namespace OpsFlow.Core.Domain;

public sealed class Ticket : ITenantEntity
{
    /// <summary>Remaining fraction of the resolution window at which a warning is raised.</summary>
    public const double WarningThreshold = 0.20;

    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }

    public int Sequence { get; set; }
    public string TicketNumber { get; set; } = "";

    [Audited] public string Title { get; set; } = "";
    [Audited] public string Description { get; set; } = "";
    [Audited] public TicketPriority Priority { get; set; }
    [Audited] public TicketStatus Status { get; set; } = TicketStatus.New;
    [Audited] public Guid? AssignedToUserId { get; set; }
    [Audited] public string? ResolutionSummary { get; set; }
    [Audited] public bool IsSlaBreached { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }

    // --- SLA ---------------------------------------------------------------------------------
    // The policy is snapshotted onto the ticket so editing a policy never rewrites history.
    public int FirstResponseMinutes { get; set; }
    public int ResolutionMinutes { get; set; }
    public DateTimeOffset ResponseDeadline { get; set; }
    public DateTimeOffset ResolutionDeadline { get; set; }
    public DateTimeOffset? FirstRespondedAt { get; set; }
    public DateTimeOffset? SlaPausedAt { get; set; }
    public long SlaPausedTotalSeconds { get; set; }

    /// <summary>
    /// When the current resolution window started: the creation time, or the moment the ticket was
    /// last reopened. A reopened ticket gets a fresh window instead of inheriting a long-expired deadline.
    /// </summary>
    public DateTimeOffset SlaWindowStartedAt { get; set; }

    // One-shot markers so the worker raises each event once per window.
    public DateTimeOffset? SlaWarningAt { get; set; }
    public DateTimeOffset? ResponseBreachedAt { get; set; }
    public DateTimeOffset? SlaBreachedAt { get; set; }

    /// <summary>Optimistic concurrency token, incremented on every human update.</summary>
    public int Version { get; set; }

    /// <summary>
    /// Concurrency token owned by the SLA sweep (incremented on each of its writes). It keeps two overlapping
    /// sweeps from both raising the same event, without touching <see cref="Version"/> - so a sweep never makes
    /// an editor's copy of the ticket look stale.
    /// </summary>
    public int SlaRevision { get; set; }

    public bool IsOpen => Status is not (TicketStatus.Resolved or TicketStatus.Closed);

    /// <summary>True once any SLA target (first response or resolution) has been missed.</summary>
    public bool HasMissedTarget => IsSlaBreached || ResponseBreachedAt is not null;

    /// <summary>(Re)computes deadlines from the window start, honouring time already spent paused.</summary>
    public void ApplySla(SlaTargets targets)
    {
        if (SlaWindowStartedAt == default)
        {
            SlaWindowStartedAt = CreatedAt;
        }

        FirstResponseMinutes = targets.FirstResponseMinutes;
        ResolutionMinutes = targets.ResolutionMinutes;
        var paused = TimeSpan.FromSeconds(SlaPausedTotalSeconds);
        ResponseDeadline = CreatedAt + targets.FirstResponse + paused;
        ResolutionDeadline = SlaWindowStartedAt + targets.Resolution + paused;

        // The deadline moved, so a warning raised against the old one no longer applies.
        SlaWarningAt = null;
    }

    /// <summary>The clock stops while we wait for the customer (PendingClient).</summary>
    public void PauseSla(DateTimeOffset now) => SlaPausedAt ??= now;

    public void ResumeSla(DateTimeOffset now)
    {
        if (SlaPausedAt is null)
        {
            return;
        }

        var paused = TimeSpan.FromSeconds(Math.Max(0, Math.Floor((now - SlaPausedAt.Value).TotalSeconds)));
        SlaPausedTotalSeconds += (long)paused.TotalSeconds;
        ResolutionDeadline += paused;
        if (FirstRespondedAt is null)
        {
            ResponseDeadline += paused;
        }

        SlaPausedAt = null;
        SlaWarningAt = null; // deadlines moved out; if still inside the final 20 % the worker warns again
    }

    public void MarkFirstResponse(DateTimeOffset now) => FirstRespondedAt ??= now;

    /// <summary>
    /// Reopening a resolved/closed ticket starts a new resolution window from <paramref name="now"/>.
    /// The previous breach (if any) stays visible in the audit trail; the new window starts clean.
    /// The first-response history is kept: the customer was already answered once.
    /// </summary>
    public void RestartResolutionWindow(DateTimeOffset now)
    {
        SlaWindowStartedAt = now;
        SlaPausedAt = null;
        SlaPausedTotalSeconds = 0;
        ResolutionDeadline = now + TimeSpan.FromMinutes(ResolutionMinutes);
        SlaWarningAt = null;
        SlaBreachedAt = null;
        IsSlaBreached = false;
        ResolvedAt = null;
        ClosedAt = null;
    }

    /// <summary>Pure SLA verdict for an open, running ticket. Paused/closed tickets are always healthy.</summary>
    public SlaEvaluation Evaluate(DateTimeOffset now)
    {
        if (!IsOpen || SlaPausedAt is not null)
        {
            return SlaEvaluation.Healthy;
        }

        var responseBreached = FirstRespondedAt is null && now > ResponseDeadline;
        var resolutionBreached = now > ResolutionDeadline;
        var remaining = ResolutionDeadline - now;
        var warning = !resolutionBreached
                      && remaining <= TimeSpan.FromMinutes(ResolutionMinutes * WarningThreshold);
        return new SlaEvaluation(responseBreached, resolutionBreached, warning);
    }

    /// <summary>Single place that turns a ticket into the label the UI shows.</summary>
    public SlaState GetSlaState(DateTimeOffset now)
    {
        if (!IsOpen)
        {
            var finishedAt = ResolvedAt ?? ClosedAt ?? now;
            return HasMissedTarget || finishedAt > ResolutionDeadline ? SlaState.Breached : SlaState.Met;
        }

        if (SlaPausedAt is not null)
        {
            return SlaState.Paused;
        }

        var e = Evaluate(now);
        if (HasMissedTarget || e.ResolutionBreached || e.ResponseBreached)
        {
            return SlaState.Breached;
        }

        return e.Warning ? SlaState.Warning : SlaState.Healthy;
    }
}

