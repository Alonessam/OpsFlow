using System.Reflection;
using Microsoft.EntityFrameworkCore;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Domain;

namespace OpsFlow.Infrastructure.Persistence;

public sealed class OpsFlowDbContext(DbContextOptions<OpsFlowDbContext> options, ICurrentTenant currentTenant)
    : DbContext(options)
{
    private static readonly MethodInfo ApplyTenantFilterMethod = typeof(OpsFlowDbContext)
        .GetMethod(nameof(ApplyTenantFilter), BindingFlags.NonPublic | BindingFlags.Instance)!;

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<User> Users => Set<User>();
    public DbSet<SlaPolicy> SlaPolicies => Set<SlaPolicy>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketComment> TicketComments => Set<TicketComment>();
    public DbSet<TicketAuditLog> TicketAuditLogs => Set<TicketAuditLog>();

    /// <summary>
    /// Read by the query filter on every query. Fail-closed: no tenant resolved means
    /// <see cref="Guid.Empty"/>, which no row can carry, so the result set is simply empty.
    /// </summary>
    private Guid CurrentTenantId => currentTenant.TenantId ?? Guid.Empty;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Tenant>(e =>
        {
            e.HasIndex(t => t.Slug).IsUnique();
            e.Property(t => t.Name).HasMaxLength(120);
            e.Property(t => t.Slug).HasMaxLength(40);
            e.Property(t => t.TicketSequence).IsConcurrencyToken();
        });

        b.Entity<User>(e =>
        {
            e.HasAlternateKey(u => new { u.TenantId, u.Id });
            e.HasIndex(u => new { u.TenantId, u.Email }).IsUnique();
            e.Property(u => u.Email).HasMaxLength(254);
            e.Property(u => u.FullName).HasMaxLength(120);
            e.Property(u => u.PasswordHash).HasMaxLength(100);
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
            // Parallel wrong-password attempts must each be counted, so the lockout cannot be dodged by racing.
            e.Property(u => u.FailedLoginCount).IsConcurrencyToken();
        });

        b.Entity<SlaPolicy>(e =>
        {
            e.HasIndex(p => new { p.TenantId, p.Priority }).IsUnique();
            e.Property(p => p.Priority).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Ticket>(e =>
        {
            e.HasAlternateKey(t => new { t.TenantId, t.Id });
            e.HasIndex(t => new { t.TenantId, t.TicketNumber }).IsUnique();
            e.HasIndex(t => new { t.TenantId, t.Sequence }).IsUnique();
            e.HasIndex(t => new { t.TenantId, t.Status });
            e.HasIndex(t => new { t.TenantId, t.AssignedToUserId });
            e.HasIndex(t => new { t.TenantId, t.CreatedByUserId });
            e.HasIndex(t => new { t.Status, t.SlaPausedAt, t.ResolutionDeadline });
            e.Property(t => t.TicketNumber).HasMaxLength(60);
            e.Property(t => t.Title).HasMaxLength(200);
            e.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.Version).IsConcurrencyToken();
            e.Property(t => t.SlaRevision).IsConcurrencyToken();

            // Composite (TenantId, UserId) foreign keys: the database itself refuses a ticket that
            // points at a user of another tenant, even if every application-level check were bypassed.
            e.HasOne<User>().WithMany()
                .HasForeignKey(t => new { t.TenantId, t.CreatedByUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany()
                .HasForeignKey(t => new { t.TenantId, t.AssignedToUserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TicketComment>(e =>
        {
            e.HasIndex(c => new { c.TenantId, c.TicketId, c.CreatedAt });
            e.Property(c => c.Content).HasMaxLength(4000);
            e.HasOne<Ticket>().WithMany()
                .HasForeignKey(c => new { c.TenantId, c.TicketId })
                .HasPrincipalKey(t => new { t.TenantId, t.Id })
                .OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany()
                .HasForeignKey(c => new { c.TenantId, c.UserId })
                .HasPrincipalKey(u => new { u.TenantId, u.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<TicketAuditLog>(e =>
        {
            e.HasIndex(a => new { a.TenantId, a.TicketId, a.Timestamp });
            e.Property(a => a.PropertyName).HasMaxLength(60);
            e.HasOne<Ticket>().WithMany()
                .HasForeignKey(a => new { a.TenantId, a.TicketId })
                .HasPrincipalKey(t => new { t.TenantId, t.Id })
                .OnDelete(DeleteBehavior.Restrict);
        });

        // Every tenant-owned entity is discovered here, so a future entity cannot ship unfiltered.
        foreach (var entityType in b.Model.GetEntityTypes().Where(t => typeof(ITenantEntity).IsAssignableFrom(t.ClrType)).ToList())
        {
            b.Entity(entityType.ClrType).HasOne(typeof(Tenant)).WithMany()
                .HasForeignKey(nameof(ITenantEntity.TenantId)).OnDelete(DeleteBehavior.Restrict);
            ApplyTenantFilterMethod.MakeGenericMethod(entityType.ClrType).Invoke(this, [b]);
        }
    }

    private void ApplyTenantFilter<T>(ModelBuilder b) where T : class, ITenantEntity =>
        b.Entity<T>().HasQueryFilter(e => e.TenantId == CurrentTenantId);
}


