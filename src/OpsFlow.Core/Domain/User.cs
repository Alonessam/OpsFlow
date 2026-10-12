namespace OpsFlow.Core.Domain;

public sealed class User : ITenantEntity
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid TenantId { get; set; }

    /// <summary>Stored lower-cased; unique per tenant (the same person may exist in two tenants).</summary>
    public string Email { get; set; } = "";

    public string FullName { get; set; } = "";
    public UserRole Role { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsStaff => Role is UserRole.Agent or UserRole.TenantAdmin;
}
