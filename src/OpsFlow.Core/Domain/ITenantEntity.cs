namespace OpsFlow.Core.Domain;

/// <summary>
/// Marker for every row that belongs to exactly one tenant. The DbContext discovers all
/// implementers by reflection and attaches the global query filter, so a new entity that
/// implements this interface is isolated automatically - nobody has to remember a WHERE clause.
/// </summary>
public interface ITenantEntity
{
    Guid TenantId { get; set; }
}

/// <summary>Marks a property whose changes must be written to the ticket audit trail.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class AuditedAttribute : Attribute;
