using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Domain;

namespace OpsFlow.Infrastructure.Persistence;

/// <summary>Thrown when code tries to write a row that does not belong to the caller's tenant.</summary>
public sealed class TenantViolationException(string message) : InvalidOperationException(message);

/// <summary>
/// Runs on every SaveChanges: stamps new rows with the current tenant, refuses any write that
/// would cross a tenant boundary, and maintains UpdatedAt / the optimistic-concurrency Version.
/// </summary>
public sealed class EntityStampingInterceptor(ICurrentTenant tenant, TimeProvider time) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Stamp(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Stamp(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        foreach (var entry in context.ChangeTracker.Entries<ITenantEntity>().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    StampNew(entry);
                    break;
                case EntityState.Modified or EntityState.Deleted:
                    GuardExisting(entry);
                    break;
            }

            if (entry.Entity is not Ticket ticket)
            {
                continue;
            }

            if (entry.State == EntityState.Added)
            {
                ticket.Version = 1;
                ticket.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified && tenant.IsSystem)
            {
                // The SLA sweep deliberately does NOT bump Version: it only writes bookkeeping markers, and bumping
                // would hand every open editor a spurious 409 at the exact moment a ticket becomes urgent. It bumps its
                // own token instead, so overlapping sweeps still cannot both succeed, and the UPDATE still carries
                // WHERE Version = @loaded, so a sweep can never overwrite a human edit that landed first.
                ticket.SlaRevision = (int)entry.Property(nameof(Ticket.SlaRevision)).OriginalValue! + 1;
            }
            else if (entry.State == EntityState.Modified)
            {
                // Built from the version we LOADED, not the current in-memory one: the generated
                // UPDATE ... WHERE Version = @loaded matches no row if someone else saved first,
                // which EF reports as a concurrency exception.
                ticket.Version = (int)entry.Property(nameof(Ticket.Version)).OriginalValue! + 1;
                ticket.UpdatedAt = now;
            }
        }
    }

    private void StampNew(EntityEntry<ITenantEntity> entry)
    {
        var entity = entry.Entity;
        if (entity.TenantId == Guid.Empty)
        {
            entity.TenantId = tenant.TenantId
                ?? throw new TenantViolationException(
                    $"Cannot insert {entry.Metadata.ClrType.Name}: no tenant is resolved for this operation.");
            return;
        }

        if (!tenant.IsSystem && entity.TenantId != tenant.TenantId)
        {
            throw new TenantViolationException(
                $"Cannot insert {entry.Metadata.ClrType.Name} for tenant {entity.TenantId} from tenant {tenant.TenantId}.");
        }
    }

    private void GuardExisting(EntityEntry<ITenantEntity> entry)
    {
        if (entry.Property(nameof(ITenantEntity.TenantId)).IsModified)
        {
            throw new TenantViolationException($"{entry.Metadata.ClrType.Name}.TenantId is immutable.");
        }

        if (!tenant.IsSystem && entry.Entity.TenantId != tenant.TenantId)
        {
            throw new TenantViolationException(
                $"Cannot modify {entry.Metadata.ClrType.Name} of tenant {entry.Entity.TenantId} from tenant {tenant.TenantId}.");
        }
    }
}

/// <summary>
/// Writes one TicketAuditLog row per changed [Audited] property, inside the same transaction as the
/// change itself - there is no code path that edits a ticket without leaving a trace.
/// </summary>
public sealed class AuditInterceptor(ICurrentTenant tenant, TimeProvider time) : SaveChangesInterceptor
{
    private static readonly PropertyInfo[] AuditedProperties = typeof(Ticket)
        .GetProperties()
        .Where(p => p.GetCustomAttribute<AuditedAttribute>() is not null)
        .ToArray();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Audit(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Audit(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Audit(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var baseTime = time.GetUtcNow();
        var sequence = 0;

        // Rows written by one save share a clock reading. Nudge each by one microsecond (the resolution of
        // PostgreSQL timestamptz) so the timeline has a deterministic order instead of relying on GUID order.
        DateTimeOffset Next() => baseTime.AddTicks(10 * sequence++);

        foreach (var entry in context.ChangeTracker.Entries<Ticket>().ToList())
        {
            if (entry.State == EntityState.Added)
            {
                context.Set<TicketAuditLog>().Add(NewLog(entry.Entity, "Created", null, entry.Entity.TicketNumber, Next()));
                continue;
            }

            if (entry.State != EntityState.Modified)
            {
                continue;
            }

            foreach (var property in AuditedProperties)
            {
                var tracked = entry.Property(property.Name);
                if (!tracked.IsModified)
                {
                    continue;
                }

                var oldValue = Format(tracked.OriginalValue);
                var newValue = Format(tracked.CurrentValue);
                if (oldValue != newValue)
                {
                    context.Set<TicketAuditLog>().Add(NewLog(entry.Entity, property.Name, oldValue, newValue, Next()));
                }
            }
        }
    }

    private TicketAuditLog NewLog(Ticket ticket, string property, string? oldValue, string? newValue, DateTimeOffset now) => new()
    {
        TenantId = ticket.TenantId,
        TicketId = ticket.Id,
        ChangedByUserId = tenant.UserId,
        PropertyName = property,
        OldValue = oldValue,
        NewValue = newValue,
        Timestamp = now
    };

    private static string? Format(object? value) => value switch
    {
        null => null,
        bool b => b ? "true" : "false",
        _ => value.ToString()
    };
}


