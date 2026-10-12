using OpsFlow.Core.Domain;
using OpsFlow.Core.Sla;

namespace OpsFlow.Tests;

public class SlaTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private static Ticket NewTicket(TicketPriority priority = TicketPriority.Critical, TicketStatus status = TicketStatus.InProgress)
    {
        var t = new Ticket { Priority = priority, Status = status, CreatedAt = T0 };
        t.ApplySla(SlaTargets.Default(priority));
        return t;
    }

    [Theory]
    [InlineData(TicketPriority.Critical, 30, 120)]
    [InlineData(TicketPriority.High, 60, 480)]
    [InlineData(TicketPriority.Medium, 240, 1440)]
    [InlineData(TicketPriority.Low, 480, 4320)]
    public void Default_policy_matches_the_documented_targets(TicketPriority p, int response, int resolution)
    {
        Assert.Equal(new SlaTargets(response, resolution), SlaTargets.Default(p));
    }

    [Fact]
    public void Deadlines_are_computed_from_creation_time()
    {
        var t = NewTicket();

        Assert.Equal(T0.AddMinutes(30), t.ResponseDeadline);
        Assert.Equal(T0.AddHours(2), t.ResolutionDeadline);
        Assert.Equal(T0, t.SlaWindowStartedAt);
    }

    [Fact]
    public void Healthy_until_20_percent_of_the_window_is_left_then_warning_then_breach()
    {
        var t = NewTicket();
        t.MarkFirstResponse(T0.AddMinutes(5));

        Assert.False(t.Evaluate(T0.AddMinutes(95)).Warning);   // 25 min left of 120 (20.8%)
        Assert.True(t.Evaluate(T0.AddMinutes(96)).Warning);    // exactly 24 min left = 20%
        Assert.False(t.Evaluate(T0.AddMinutes(120)).ResolutionBreached); // deadline itself is not a breach
        Assert.True(t.Evaluate(T0.AddMinutes(121)).ResolutionBreached);
        Assert.False(t.Evaluate(T0.AddMinutes(121)).Warning);  // breach supersedes warning
    }

    [Fact]
    public void First_response_breach_only_applies_until_someone_responds()
    {
        var t = NewTicket();

        Assert.True(t.Evaluate(T0.AddMinutes(31)).ResponseBreached);

        t.MarkFirstResponse(T0.AddMinutes(10));
        Assert.False(t.Evaluate(T0.AddMinutes(31)).ResponseBreached);
    }

    [Fact]
    public void Marking_first_response_twice_keeps_the_original_time()
    {
        var t = NewTicket();
        t.MarkFirstResponse(T0.AddMinutes(5));
        t.MarkFirstResponse(T0.AddMinutes(50));

        Assert.Equal(T0.AddMinutes(5), t.FirstRespondedAt);
    }

    [Fact]
    public void Pausing_stops_the_clock_and_resuming_pushes_the_deadlines_out()
    {
        var t = NewTicket();
        t.PauseSla(T0.AddMinutes(60));

        Assert.False(t.Evaluate(T0.AddDays(5)).ResolutionBreached); // paused: never breaches
        Assert.Equal(SlaState.Paused, t.GetSlaState(T0.AddDays(5)));

        t.ResumeSla(T0.AddMinutes(180)); // waited 2 hours for the customer

        Assert.Equal(T0.AddHours(4), t.ResolutionDeadline);
        Assert.Equal(T0.AddMinutes(150), t.ResponseDeadline);
        Assert.Null(t.SlaPausedAt);
        Assert.Equal(7200, t.SlaPausedTotalSeconds);
    }

    [Fact]
    public void Pause_is_idempotent_and_resume_without_pause_is_a_no_op()
    {
        var t = NewTicket();
        t.ResumeSla(T0.AddHours(1));
        Assert.Equal(T0.AddHours(2), t.ResolutionDeadline);

        t.PauseSla(T0.AddMinutes(10));
        t.PauseSla(T0.AddMinutes(20)); // second call must not move the pause start
        Assert.Equal(T0.AddMinutes(10), t.SlaPausedAt);
    }

    [Fact]
    public void Changing_priority_recomputes_deadlines_but_keeps_paused_time()
    {
        var t = NewTicket(TicketPriority.Low);
        t.PauseSla(T0.AddHours(1));
        t.ResumeSla(T0.AddHours(3));

        t.Priority = TicketPriority.Critical;
        t.ApplySla(SlaTargets.Default(TicketPriority.Critical));

        Assert.Equal(T0.AddHours(2) + TimeSpan.FromHours(2), t.ResolutionDeadline);
    }

    [Fact]
    public void A_warning_raised_against_an_old_deadline_is_rearmed_when_the_deadline_moves()
    {
        var t = NewTicket();
        t.SlaWarningAt = T0.AddMinutes(100);

        t.ApplySla(SlaTargets.Default(TicketPriority.Medium)); // priority lowered: new, later deadline
        Assert.Null(t.SlaWarningAt);

        t.SlaWarningAt = T0.AddMinutes(100);
        t.PauseSla(T0.AddMinutes(101));
        t.ResumeSla(T0.AddMinutes(200));
        Assert.Null(t.SlaWarningAt);
    }

    [Fact]
    public void Resolved_tickets_are_never_evaluated_as_breaching()
    {
        var t = NewTicket(status: TicketStatus.Resolved);

        Assert.Equal(SlaEvaluation.Healthy, t.Evaluate(T0.AddDays(30)));
    }

    [Fact]
    public void Display_state_for_finished_tickets_reflects_whether_the_deadline_was_met()
    {
        var met = NewTicket(status: TicketStatus.Resolved);
        met.ResolvedAt = T0.AddMinutes(100);
        var late = NewTicket(status: TicketStatus.Closed);
        late.ResolvedAt = T0.AddMinutes(130);

        Assert.Equal(SlaState.Met, met.GetSlaState(T0.AddDays(1)));
        Assert.Equal(SlaState.Breached, late.GetSlaState(T0.AddDays(1)));
    }

    [Fact]
    public void A_missed_first_response_stays_visible_after_the_late_answer()
    {
        var t = NewTicket();
        t.ResponseBreachedAt = T0.AddMinutes(31); // the worker raised it
        t.MarkFirstResponse(T0.AddMinutes(45));   // ...then someone finally answered

        Assert.True(t.HasMissedTarget);
        Assert.Equal(SlaState.Breached, t.GetSlaState(T0.AddMinutes(50)));
    }

    [Fact]
    public void Reopening_starts_a_fresh_resolution_window_and_clears_the_old_breach()
    {
        var t = NewTicket();
        t.MarkFirstResponse(T0.AddMinutes(5));
        t.IsSlaBreached = true;
        t.SlaBreachedAt = T0.AddHours(3);
        t.SlaWarningAt = T0.AddMinutes(100);
        t.Status = TicketStatus.Closed;
        t.ResolvedAt = T0.AddHours(3);
        t.ClosedAt = T0.AddHours(4);
        t.SlaPausedTotalSeconds = 600;

        var reopened = T0.AddDays(2);
        t.Status = TicketStatus.InProgress;
        t.RestartResolutionWindow(reopened);

        Assert.Equal(reopened.AddHours(2), t.ResolutionDeadline);
        Assert.Equal(reopened, t.SlaWindowStartedAt);
        Assert.False(t.IsSlaBreached);
        Assert.Null(t.SlaBreachedAt);
        Assert.Null(t.SlaWarningAt);
        Assert.Null(t.ResolvedAt);
        Assert.Null(t.ClosedAt);
        Assert.Equal(0, t.SlaPausedTotalSeconds);
        Assert.Equal(T0.AddMinutes(5), t.FirstRespondedAt); // the customer was already answered once
        Assert.False(t.Evaluate(reopened.AddMinutes(30)).ResolutionBreached);
    }

    [Fact]
    public void A_priority_change_after_a_reopen_is_measured_from_the_reopen_not_from_creation()
    {
        var t = NewTicket();
        var reopened = T0.AddDays(2);
        t.RestartResolutionWindow(reopened);

        t.Priority = TicketPriority.High;
        t.ApplySla(SlaTargets.Default(TicketPriority.High));

        Assert.Equal(reopened.AddHours(8), t.ResolutionDeadline);
    }
}
