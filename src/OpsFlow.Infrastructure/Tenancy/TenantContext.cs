using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Domain;

namespace OpsFlow.Infrastructure.Tenancy;

/// <summary>
/// Per-scope (per-request / per-job) tenant state. The only writers are the tenant-resolution
/// middleware, the login flow, and explicitly system-level jobs - an architecture test keeps it so.
/// </summary>
public sealed class TenantContext : ICurrentTenant
{
    public Guid? TenantId { get; private set; }
    public string? Slug { get; private set; }
    public Guid? UserId { get; private set; }
    public UserRole? Role { get; private set; }
    public bool IsSystem { get; private set; }

    public void Use(Guid tenantId, string slug, Guid? userId = null, UserRole? role = null)
    {
        TenantId = tenantId;
        Slug = slug;
        UserId = userId;
        Role = role;
    }

    /// <summary>Marks this scope as a cross-tenant background job. Never call from request code.</summary>
    public void UseSystem() => IsSystem = true;
}
