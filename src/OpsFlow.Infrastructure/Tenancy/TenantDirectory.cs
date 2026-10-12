using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OpsFlow.Infrastructure.Persistence;

namespace OpsFlow.Infrastructure.Tenancy;

/// <summary>
/// A JWT stays valid for its lifetime, so suspending a tenant or deactivating a user must be
/// enforced server-side. The answer is cached briefly to keep this off the hot path; deactivating a
/// user evicts the entry on the node that handled the request (other nodes wait for the 30 s expiry).
/// </summary>
public sealed class TenantDirectory(OpsFlowDbContext db, IMemoryCache cache)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private static string Key(Guid tenantId, Guid userId) => $"access:{tenantId}:{userId}";

    public async Task<bool> IsAccessAllowedAsync(Guid tenantId, Guid userId, CancellationToken ct = default)
    {
        var key = Key(tenantId, userId);
        if (cache.TryGetValue(key, out bool allowed))
        {
            return allowed;
        }

        // Tenants has no filter; Users is filtered by the tenant already placed on the context.
        allowed = await db.Tenants.AnyAsync(t => t.Id == tenantId && t.IsActive, ct)
                  && await db.Users.AnyAsync(u => u.Id == userId && u.IsActive, ct);
        cache.Set(key, allowed, CacheDuration);
        return allowed;
    }

    public void Invalidate(Guid tenantId, Guid userId) => cache.Remove(Key(tenantId, userId));
}
