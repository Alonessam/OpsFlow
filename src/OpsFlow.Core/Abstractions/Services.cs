using OpsFlow.Core.Common;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;

namespace OpsFlow.Core.Abstractions;

public sealed record LoginIdentity(UserDto User, TenantDto Tenant);

public interface IAuthService
{
    Task<Result<LoginIdentity>> AuthenticateAsync(LoginRequest request, CancellationToken ct = default);
}

public interface ITenantProvisioningService
{
    Task<Result<TenantDto>> CreateTenantAsync(CreateTenantRequest request, CancellationToken ct = default);
}

public interface IUserService
{
    Task<Result<IReadOnlyList<UserDto>>> ListAsync(bool staffOnly, CancellationToken ct = default);
    Task<Result<UserDto>> CreateAsync(CreateUserRequest request, CancellationToken ct = default);
    Task<Result<UserDto>> SetActiveAsync(Guid userId, bool isActive, CancellationToken ct = default);
}

public interface ISlaPolicyService
{
    Task<IReadOnlyList<SlaPolicyDto>> ListAsync(CancellationToken ct = default);
    Task<Result<SlaPolicyDto>> UpdateAsync(TicketPriority priority, UpdateSlaPolicyRequest request, CancellationToken ct = default);
}

public interface ITicketService
{
    Task<Result<TicketDto>> CreateAsync(CreateTicketRequest request, CancellationToken ct = default);
    Task<Result<TicketDto>> GetAsync(Guid id, CancellationToken ct = default);
    Task<Result<PagedResult<TicketDto>>> ListAsync(TicketQuery query, CancellationToken ct = default);
    Task<Result<TicketDto>> UpdateAsync(Guid id, UpdateTicketRequest request, CancellationToken ct = default);
    Task<Result<TicketDto>> TransitionAsync(Guid id, TransitionRequest request, CancellationToken ct = default);
    Task<Result<TicketDto>> AssignAsync(Guid id, AssignRequest request, CancellationToken ct = default);
    Task<Result<CommentDto>> AddCommentAsync(Guid id, AddCommentRequest request, CancellationToken ct = default);
    Task<Result<IReadOnlyList<CommentDto>>> ListCommentsAsync(Guid id, CancellationToken ct = default);
    Task<Result<IReadOnlyList<AuditEntryDto>>> GetHistoryAsync(Guid id, CancellationToken ct = default);
    Task<Result<DashboardSummaryDto>> GetDashboardAsync(CancellationToken ct = default);
}
