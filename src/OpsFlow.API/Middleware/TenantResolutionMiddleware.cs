using OpsFlow.API.Auth;
using OpsFlow.Core.Domain;
using OpsFlow.Infrastructure.Tenancy;

namespace OpsFlow.API.Middleware;

/// <summary>
/// Turns the signed JWT into the scoped tenant context that the DbContext's query filter reads.
/// The tenant is taken ONLY from the validated token. A client-supplied header such as X-Tenant-Id
/// would let any user pick any tenant; the token cannot be forged without the server's signing key.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, TenantContext tenantContext, TenantDirectory directory)
    {
        if (http.User.Identity?.IsAuthenticated == true)
        {
            var claims = http.User;
            if (!Guid.TryParse(claims.FindFirst(OpsClaims.TenantId)?.Value, out var tenantId)
                || !Guid.TryParse(claims.FindFirst(OpsClaims.UserId)?.Value, out var userId)
                || !Enum.TryParse<UserRole>(claims.FindFirst(OpsClaims.Role)?.Value, out var role) || !Enum.IsDefined(role)
                || claims.FindFirst(OpsClaims.TenantSlug)?.Value is not { Length: > 0 } slug)
            {
                await Reject(http, "invalid_token", "The token does not carry a valid tenant context.");
                return;
            }

            tenantContext.Use(tenantId, slug, userId, role);

            if (!await directory.IsAccessAllowedAsync(tenantId, userId, http.RequestAborted))
            {
                await Reject(http, "access_revoked", "This account or tenant is no longer active.");
                return;
            }
        }

        await next(http);
    }

    private static Task Reject(HttpContext http, string code, string message)
    {
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return http.Response.WriteAsJsonAsync(new { type = "about:blank", title = message, status = 401, code });
    }
}

