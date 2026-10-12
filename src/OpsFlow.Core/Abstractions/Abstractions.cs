using OpsFlow.Core.Domain;

namespace OpsFlow.Core.Abstractions;

/// <summary>
/// Who is calling and for which tenant. Populated once per request by the tenant-resolution
/// middleware from the signed JWT - never from a client-controlled header.
/// </summary>
public interface ICurrentTenant
{
    Guid? TenantId { get; }
    string? Slug { get; }
    Guid? UserId { get; }
    UserRole? Role { get; }

    /// <summary>True only inside background jobs that deliberately work across tenants.</summary>
    bool IsSystem { get; }
}

public interface IPasswordHasher
{
    string Hash(string password);
    bool Verify(string password, string hash);

    /// <summary>
    /// Spends the same CPU as a real verification. Called when the account does not exist so that
    /// response time cannot be used to enumerate valid e-mail addresses.
    /// </summary>
    void VerifyAgainstDummy(string password);
}

public enum TicketEventType
{
    TicketCreated,
    TicketUpdated,
    TicketTransitioned,
    CommentAdded,
    SlaWarning,
    SlaResponseBreached,
    SlaBreached
}

/// <summary>Domain event pushed to live dashboards.</summary>
public sealed record TicketEvent(
    TicketEventType Type,
    Guid TenantId,
    Guid TicketId,
    string TicketNumber,
    string Title,
    TicketPriority Priority,
    TicketStatus Status,
    Guid RequesterUserId,
    DateTimeOffset OccurredAt,
    string? Detail = null,
    bool StaffOnly = false);

/// <summary>
/// Cuts a user off from live updates the moment they are deactivated, without waiting for their
/// token (and therefore their socket) to expire.
/// </summary>
public interface IConnectionRevoker
{
    Task RevokeAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default);
}

public interface ITicketEventPublisher
{
    Task PublishAsync(TicketEvent ticketEvent, CancellationToken cancellationToken = default);
}
