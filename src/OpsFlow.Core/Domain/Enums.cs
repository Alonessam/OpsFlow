namespace OpsFlow.Core.Domain;

public enum UserRole
{
    Employee = 0,
    Agent = 1,
    TenantAdmin = 2
}

public enum TicketPriority
{
    Low = 0,
    Medium = 1,
    High = 2,
    Critical = 3
}

public enum TicketStatus
{
    New = 0,
    Assigned = 1,
    InProgress = 2,
    PendingClient = 3,
    Resolved = 4,
    Closed = 5
}
