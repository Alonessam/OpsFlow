using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using OpsFlow.API.Auth;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;

namespace OpsFlow.API.Controllers;

[Route("api/auth")]
public sealed class AuthController(IAuthService auth, JwtTokenService tokens, ICurrentTenant me) : ApiControllerBase
{
    [HttpPost("login"), AllowAnonymous, EnableRateLimiting("login")]
    public async Task<ActionResult<LoginResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await auth.AuthenticateAsync(request, ct);
        if (!result.IsSuccess)
        {
            return ToProblem(result.Error!);
        }

        var (token, expires) = tokens.Issue(result.Value!.User, result.Value.Tenant);
        return new LoginResponse(token, expires, result.Value.User, result.Value.Tenant);
    }

    [HttpGet("me")]
    public ActionResult<object> Me() => new
    {
        userId = me.UserId,
        tenantId = me.TenantId,
        tenant = me.Slug,
        role = me.Role
    };
}

/// <summary>Platform-operator endpoint (provisioning a customer). Guarded by a static key, not a tenant JWT.</summary>
[Route("api/platform")]
public sealed class PlatformController(ITenantProvisioningService provisioning) : ApiControllerBase
{
    // The key is checked by PlatformKeyFilter before the body is even bound.
    [HttpPost("tenants"), AllowAnonymous, ServiceFilter(typeof(PlatformKeyFilter)), EnableRateLimiting("platform")]
    public async Task<ActionResult<TenantDto>> CreateTenant(CreateTenantRequest request, CancellationToken ct)
    {
        var result = await provisioning.CreateTenantAsync(request, ct);
        return result.IsSuccess ? StatusCode(StatusCodes.Status201Created, result.Value) : ToProblem(result.Error!);
    }
}

[Route("api/tickets")]
public sealed class TicketsController(ITicketService tickets) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<PagedResult<TicketDto>>> List([FromQuery] TicketQuery query, CancellationToken ct) =>
        FromResult(await tickets.ListAsync(query, ct));

    [HttpPost]
    public async Task<ActionResult<TicketDto>> Create(CreateTicketRequest request, CancellationToken ct)
    {
        var result = await tickets.CreateAsync(request, ct);
        return CreatedFrom(result, result.IsSuccess ? $"/api/tickets/{result.Value!.Id}" : "");
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<TicketDto>> Get(Guid id, CancellationToken ct) =>
        FromResult(await tickets.GetAsync(id, ct));

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<TicketDto>> Update(Guid id, UpdateTicketRequest request, CancellationToken ct) =>
        FromResult(await tickets.UpdateAsync(id, request, ct));

    [HttpPost("{id:guid}/transitions")]
    public async Task<ActionResult<TicketDto>> Transition(Guid id, TransitionRequest request, CancellationToken ct) =>
        FromResult(await tickets.TransitionAsync(id, request, ct));

    [HttpPost("{id:guid}/assign")]
    public async Task<ActionResult<TicketDto>> Assign(Guid id, AssignRequest request, CancellationToken ct) =>
        FromResult(await tickets.AssignAsync(id, request, ct));

    [HttpGet("{id:guid}/comments")]
    public async Task<ActionResult<IReadOnlyList<CommentDto>>> Comments(Guid id, CancellationToken ct) =>
        FromResult(await tickets.ListCommentsAsync(id, ct));

    [HttpPost("{id:guid}/comments")]
    public async Task<ActionResult<CommentDto>> AddComment(Guid id, AddCommentRequest request, CancellationToken ct)
    {
        var result = await tickets.AddCommentAsync(id, request, ct);
        return CreatedFrom(result, $"/api/tickets/{id}/comments");
    }

    [HttpGet("{id:guid}/history")]
    public async Task<ActionResult<IReadOnlyList<AuditEntryDto>>> History(Guid id, CancellationToken ct) =>
        FromResult(await tickets.GetHistoryAsync(id, ct));
}

[Route("api/dashboard")]
public sealed class DashboardController(ITicketService tickets) : ApiControllerBase
{
    [HttpGet("summary")]
    public async Task<ActionResult<DashboardSummaryDto>> Summary(CancellationToken ct) =>
        FromResult(await tickets.GetDashboardAsync(ct));
}

[Route("api/users")]
public sealed class UsersController(IUserService users) : ApiControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<UserDto>>> List([FromQuery] bool staffOnly, CancellationToken ct) =>
        FromResult(await users.ListAsync(staffOnly, ct));

    [HttpPost, Authorize(Roles = nameof(UserRole.TenantAdmin))]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request, CancellationToken ct)
    {
        var result = await users.CreateAsync(request, ct);
        return CreatedFrom(result, "/api/users");
    }

    [HttpPatch("{id:guid}/status"), Authorize(Roles = nameof(UserRole.TenantAdmin))]
    public async Task<ActionResult<UserDto>> SetActive(Guid id, SetUserActiveRequest request, CancellationToken ct) =>
        FromResult(await users.SetActiveAsync(id, request.IsActive, ct));
}

[Route("api/sla-policies")]
public sealed class SlaPoliciesController(ISlaPolicyService policies) : ApiControllerBase
{
    [HttpGet]
    public async Task<IReadOnlyList<SlaPolicyDto>> List(CancellationToken ct) => await policies.ListAsync(ct);

    [HttpPut("{priority}"), Authorize(Roles = nameof(UserRole.TenantAdmin))]
    public async Task<ActionResult<SlaPolicyDto>> Update(TicketPriority priority, UpdateSlaPolicyRequest request, CancellationToken ct) =>
        FromResult(await policies.UpdateAsync(priority, request, ct));
}


