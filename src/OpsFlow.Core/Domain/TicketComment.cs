namespace OpsFlow.Core.Domain;

public sealed class TicketComment : ITenantEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid TicketId { get; set; }
    public Guid UserId { get; set; }
    public string Content { get; set; } = "";

    /// <summary>Visible to staff only - never returned to the requesting employee.</summary>
    public bool IsInternalNote { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TicketAuditLog : ITenantEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }
    public Guid TicketId { get; set; }

    /// <summary>Null means the change was made by the system (SLA worker).</summary>
    public Guid? ChangedByUserId { get; set; }

    public string PropertyName { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
    public DateTimeOffset Timestamp { get; set; }
}
