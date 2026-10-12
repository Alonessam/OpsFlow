using System.ComponentModel.DataAnnotations;
using OpsFlow.Core.Domain;
using OpsFlow.Core.Sla;

namespace OpsFlow.Core.Contracts;

// ---- Auth / tenants / users -------------------------------------------------------------------

public sealed record LoginRequest
{
    [Required, StringLength(64)] public string TenantSlug { get; init; } = "";
    [Required, EmailAddress, StringLength(254)] public string Email { get; init; } = "";
    [Required, StringLength(128)] public string Password { get; init; } = "";
}

public sealed record UserDto(Guid Id, string Email, string FullName, UserRole Role, bool IsActive);

public sealed record TenantDto(Guid Id, string Name, string Slug);

public sealed record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, UserDto User, TenantDto Tenant);

public sealed record CreateUserRequest
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; init; } = "";
    [Required, StringLength(120, MinimumLength = 2)] public string FullName { get; init; } = "";
    [EnumDataType(typeof(UserRole))] public UserRole Role { get; init; } = UserRole.Employee;
    [Required, StringLength(128, MinimumLength = 10)] public string Password { get; init; } = "";
}

public sealed record CreateTenantRequest
{
    [Required, StringLength(120, MinimumLength = 2)] public string Name { get; init; } = "";

    [Required, RegularExpression("^[a-z0-9](?:[a-z0-9-]{1,38})[a-z0-9]$",
        ErrorMessage = "Slug must be 3-40 characters of a-z, 0-9 and '-'.")]
    public string Slug { get; init; } = "";

    [Required, EmailAddress, StringLength(254)] public string AdminEmail { get; init; } = "";
    [Required, StringLength(120, MinimumLength = 2)] public string AdminFullName { get; init; } = "";
    [Required, StringLength(128, MinimumLength = 10)] public string AdminPassword { get; init; } = "";
}

// ---- Tickets ----------------------------------------------------------------------------------

public sealed record CreateTicketRequest
{
    [Required, StringLength(200, MinimumLength = 3)] public string Title { get; init; } = "";
    [Required, StringLength(8000, MinimumLength = 3)] public string Description { get; init; } = "";
    [EnumDataType(typeof(TicketPriority))] public TicketPriority Priority { get; init; } = TicketPriority.Medium;
}

public sealed record UpdateTicketRequest
{
    [StringLength(200, MinimumLength = 3)] public string? Title { get; init; }
    [StringLength(8000, MinimumLength = 3)] public string? Description { get; init; }
    [EnumDataType(typeof(TicketPriority))] public TicketPriority? Priority { get; init; }

    /// <summary>The version the client last saw; a mismatch yields 409 instead of a lost update.</summary>
    public required int Version { get; init; }
}

public sealed record TransitionRequest
{
    [EnumDataType(typeof(TicketStatus))] public required TicketStatus To { get; init; }
    public Guid? AssigneeId { get; init; }
    [StringLength(4000)] public string? ResolutionSummary { get; init; }
    public required int Version { get; init; }
}

public sealed record AssignRequest
{
    public required Guid AssigneeId { get; init; }
    public required int Version { get; init; }
}

public sealed record AddCommentRequest
{
    [Required, StringLength(4000, MinimumLength = 1)] public string Content { get; init; } = "";
    public bool IsInternalNote { get; init; }
}

public sealed record TicketQuery
{
    [EnumDataType(typeof(TicketStatus))] public TicketStatus? Status { get; init; }
    [EnumDataType(typeof(TicketPriority))] public TicketPriority? Priority { get; init; }
    public Guid? AssignedToId { get; init; }
    public bool? Breached { get; init; }
    public bool? Mine { get; init; }
    [StringLength(100)] public string? Search { get; init; }
    [Range(1, 100_000)] public int Page { get; init; } = 1;
    [Range(1, 100)] public int PageSize { get; init; } = 25;
}

public sealed record PersonRef(Guid Id, string FullName);

public sealed record TicketDto(
    Guid Id,
    string TicketNumber,
    string Title,
    string Description,
    TicketPriority Priority,
    TicketStatus Status,
    PersonRef CreatedBy,
    PersonRef? AssignedTo,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset ResponseDeadline,
    DateTimeOffset ResolutionDeadline,
    DateTimeOffset? FirstRespondedAt,
    bool IsSlaBreached,
    SlaState SlaState,
    string? ResolutionSummary,
    int Version,
    int ResolutionMinutes,
    IReadOnlyList<TicketStatus> AllowedTransitions);

public sealed record CommentDto(Guid Id, PersonRef Author, string Content, bool IsInternalNote, DateTimeOffset CreatedAt);

public sealed record AuditEntryDto(
    Guid Id,
    string Field,
    string? OldValue,
    string? NewValue,
    PersonRef? ChangedBy,
    DateTimeOffset Timestamp);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record DashboardSummaryDto(
    IReadOnlyDictionary<TicketStatus, int> ByStatus,
    int Open,
    int Unassigned,
    int Breached,
    int AtRisk,
    int Critical);

// ---- SLA --------------------------------------------------------------------------------------

public sealed record SetUserActiveRequest
{
    public required bool IsActive { get; init; }
}

public sealed record SlaPolicyDto(TicketPriority Priority, int FirstResponseMinutes, int ResolutionMinutes);

public sealed record UpdateSlaPolicyRequest
{
    [Range(1, 60 * 24 * 30)] public int FirstResponseMinutes { get; init; }
    [Range(1, 60 * 24 * 365)] public int ResolutionMinutes { get; init; }
}

