using Microsoft.EntityFrameworkCore;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Common;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;
using OpsFlow.Core.Sla;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Tenancy;

namespace OpsFlow.Infrastructure.Services;

internal static class Mapping
{
    public static UserDto ToDto(this User u) => new(u.Id, u.Email, u.FullName, u.Role, u.IsActive);
    public static TenantDto ToDto(this Tenant t) => new(t.Id, t.Name, t.Slug);
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}

public sealed class AuthService(
    OpsFlowDbContext db,
    TenantContext tenantContext,
    IPasswordHasher hasher,
    TimeProvider time) : IAuthService
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const int CounterRetries = 5;

    private static readonly Error InvalidCredentials =
        Error.Unauthorized("invalid_credentials", "Invalid tenant, e-mail or password.");

    public async Task<Result<LoginIdentity>> AuthenticateAsync(LoginRequest request, CancellationToken ct = default)
    {
        var slug = request.TenantSlug.Trim().ToLowerInvariant();
        var email = Mapping.NormalizeEmail(request.Email);

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug, ct);
        if (tenant is null || !tenant.IsActive)
        {
            hasher.VerifyAgainstDummy(request.Password);
            return InvalidCredentials;
        }

        // From here on the normal query filter is in force: the user lookup cannot leave this tenant.
        tenantContext.Use(tenant.Id, tenant.Slug);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        var now = time.GetUtcNow();

        // Every "cannot log in" path burns one bcrypt and answers identically, so neither the response
        // nor its timing reveals whether the account exists, is deactivated, or is locked.
        if (user is null || !user.IsActive || (user.LockedUntil is { } until && until > now))
        {
            hasher.VerifyAgainstDummy(request.Password);
            return InvalidCredentials;
        }

        if (!hasher.Verify(request.Password, user.PasswordHash))
        {
            await RecordFailureAsync(user, now, ct);
            return InvalidCredentials;
        }

        if (user.FailedLoginCount != 0 || user.LockedUntil is not null)
        {
            user.FailedLoginCount = 0;
            user.LockedUntil = null;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateConcurrencyException)
            {
                // A parallel failed attempt updated the counter first. The login itself is valid; leave the counter alone.
            }
        }

        return new LoginIdentity(user.ToDto(), tenant.ToDto());
    }

    /// <summary>
    /// The counter is a concurrency token, so parallel wrong guesses cannot all read "0" and write "1":
    /// the loser re-reads the current value and counts on top of it.
    /// </summary>
    private async Task RecordFailureAsync(User user, DateTimeOffset now, CancellationToken ct)
    {
        for (var attempt = 0; attempt < CounterRetries; attempt++)
        {
            user.FailedLoginCount++;
            if (user.FailedLoginCount >= MaxFailedAttempts)
            {
                user.LockedUntil = now + LockoutDuration;
                user.FailedLoginCount = 0;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateConcurrencyException)
            {
                await db.Entry(user).ReloadAsync(ct);
                if (user.LockedUntil is { } until && until > now)
                {
                    return; // a parallel attempt already tripped the lockout
                }
            }
        }
    }
}

public sealed class TenantProvisioningService(
    OpsFlowDbContext db,
    TenantContext tenantContext,
    IPasswordHasher hasher,
    TimeProvider time) : ITenantProvisioningService
{
    public async Task<Result<TenantDto>> CreateTenantAsync(CreateTenantRequest request, CancellationToken ct = default)
    {
        // Provisioning is by definition cross-tenant (it creates one), so it runs as the system.
        tenantContext.UseSystem();

        var slug = request.Slug.Trim().ToLowerInvariant();
        if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct))
        {
            return SlugTaken(slug);
        }

        var now = time.GetUtcNow();
        var tenant = new Tenant { Name = request.Name.Trim(), Slug = slug, CreatedAt = now };
        db.Tenants.Add(tenant);

        foreach (var priority in Enum.GetValues<TicketPriority>())
        {
            var targets = SlaTargets.Default(priority);
            db.SlaPolicies.Add(new SlaPolicy
            {
                TenantId = tenant.Id,
                Priority = priority,
                FirstResponseMinutes = targets.FirstResponseMinutes,
                ResolutionMinutes = targets.ResolutionMinutes
            });
        }

        db.Users.Add(new User
        {
            TenantId = tenant.Id,
            Email = Mapping.NormalizeEmail(request.AdminEmail),
            FullName = request.AdminFullName.Trim(),
            Role = UserRole.TenantAdmin,
            PasswordHash = hasher.Hash(request.AdminPassword),
            CreatedAt = now
        });

        try
        {
            // One SaveChanges = one transaction: a tenant is created completely or not at all.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return SlugTaken(slug); // lost a race with a parallel request for the same slug
        }

        return tenant.ToDto();
    }

    private static Error SlugTaken(string slug) => Error.Conflict("slug_taken", $"The slug '{slug}' is already in use.");
}

public sealed class UserService(
    OpsFlowDbContext db,
    ICurrentTenant me,
    IPasswordHasher hasher,
    TimeProvider time,
    TenantDirectory directory,
    IConnectionRevoker revoker) : IUserService
{
    public async Task<Result<IReadOnlyList<UserDto>>> ListAsync(bool staffOnly, CancellationToken ct = default)
    {
        if (me.Role is null or UserRole.Employee)
        {
            return Error.Forbidden("forbidden", "Only staff can list users.");
        }

        var query = db.Users.AsQueryable();
        if (staffOnly)
        {
            query = query.Where(u => u.Role != UserRole.Employee && u.IsActive);
        }

        var users = await query.OrderBy(u => u.FullName).ThenBy(u => u.Id).ToListAsync(ct);
        return users.Select(u => u.ToDto()).ToList();
    }

    public async Task<Result<UserDto>> CreateAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        if (me.Role != UserRole.TenantAdmin)
        {
            return Error.Forbidden("forbidden", "Only a tenant admin can create users.");
        }

        if (!Enum.IsDefined(request.Role))
        {
            return Error.Validation("invalid_role", "Unknown role.");
        }

        var email = Mapping.NormalizeEmail(request.Email);
        if (await db.Users.AnyAsync(u => u.Email == email, ct))
        {
            return EmailTaken();
        }

        var user = new User
        {
            Email = email,
            FullName = request.FullName.Trim(),
            Role = request.Role,
            PasswordHash = hasher.Hash(request.Password),
            CreatedAt = time.GetUtcNow()
        };
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            return EmailTaken(); // lost a race with a parallel request for the same e-mail
        }

        return user.ToDto();
    }

    public async Task<Result<UserDto>> SetActiveAsync(Guid userId, bool isActive, CancellationToken ct = default)
    {
        if (me.Role != UserRole.TenantAdmin)
        {
            return Error.Forbidden("forbidden", "Only a tenant admin can change a user's status.");
        }

        if (userId == me.UserId && !isActive)
        {
            return Error.Conflict("cannot_deactivate_self", "You cannot deactivate your own account.");
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Error.NotFound("User");
        }

        user.IsActive = isActive;
        if (isActive)
        {
            user.FailedLoginCount = 0; // a reactivated user starts clean, not half-locked
            user.LockedUntil = null;
        }

        await db.SaveChangesAsync(ct);

        // Take effect on this node immediately instead of waiting for the access cache to expire.
        directory.Invalidate(user.TenantId, user.Id);
        if (!isActive)
        {
            await revoker.RevokeAsync(user.TenantId, user.Id, ct); // and stop any live socket they already hold
        }

        return user.ToDto();
    }

    private static Error EmailTaken() => Error.Conflict("email_taken", "A user with this e-mail already exists in this tenant.");
}

public sealed class SlaPolicyService(OpsFlowDbContext db, ICurrentTenant me) : ISlaPolicyService
{
    public async Task<IReadOnlyList<SlaPolicyDto>> ListAsync(CancellationToken ct = default)
    {
        // Sorted in memory on the enum's numeric value: priority is stored as text, and a database
        // ORDER BY would sort it alphabetically (Medium, Low, High, Critical).
        var policies = await db.SlaPolicies.ToListAsync(ct);
        return policies.OrderByDescending(p => p.Priority)
            .Select(p => new SlaPolicyDto(p.Priority, p.FirstResponseMinutes, p.ResolutionMinutes)).ToList();
    }

    public async Task<Result<SlaPolicyDto>> UpdateAsync(TicketPriority priority, UpdateSlaPolicyRequest request, CancellationToken ct = default)
    {
        if (me.Role != UserRole.TenantAdmin)
        {
            return Error.Forbidden("forbidden", "Only a tenant admin can change SLA policies.");
        }

        if (request.FirstResponseMinutes > request.ResolutionMinutes)
        {
            return Error.Validation("invalid_sla", "First response time cannot exceed the resolution time.");
        }

        var policy = await db.SlaPolicies.FirstOrDefaultAsync(p => p.Priority == priority, ct);
        if (policy is null)
        {
            return Error.NotFound("SLA policy");
        }

        // Applies to tickets created from now on; existing tickets keep their snapshot.
        policy.FirstResponseMinutes = request.FirstResponseMinutes;
        policy.ResolutionMinutes = request.ResolutionMinutes;
        await db.SaveChangesAsync(ct);
        return new SlaPolicyDto(policy.Priority, policy.FirstResponseMinutes, policy.ResolutionMinutes);
    }
}
