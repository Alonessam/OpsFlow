using OpsFlow.Core.Domain;

namespace OpsFlow.Core.Sla;

public readonly record struct SlaTargets(int FirstResponseMinutes, int ResolutionMinutes)
{
    public TimeSpan FirstResponse => TimeSpan.FromMinutes(FirstResponseMinutes);
    public TimeSpan Resolution => TimeSpan.FromMinutes(ResolutionMinutes);

    /// <summary>Policy every new tenant starts with; tenant admins may change it later.</summary>
    public static SlaTargets Default(TicketPriority priority) => priority switch
    {
        TicketPriority.Critical => new SlaTargets(30, 2 * 60),
        TicketPriority.High => new SlaTargets(60, 8 * 60),
        TicketPriority.Medium => new SlaTargets(4 * 60, 24 * 60),
        TicketPriority.Low => new SlaTargets(8 * 60, 72 * 60),
        _ => throw new ArgumentOutOfRangeException(nameof(priority), priority, null)
    };
}

/// <summary>What the monitor concluded about one open ticket at one instant.</summary>
public readonly record struct SlaEvaluation(bool ResponseBreached, bool ResolutionBreached, bool Warning)
{
    public static SlaEvaluation Healthy => default;
}

/// <summary>Display state for the UI/API. Distinct from the persisted flags.</summary>
public enum SlaState
{
    Healthy,
    Warning,
    Breached,
    Paused,
    Met
}
