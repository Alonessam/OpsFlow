namespace OpsFlow.Core.Domain;

/// <summary>A customer organisation. Deliberately NOT a tenant entity: it is the tenant.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";

    /// <summary>URL-safe unique key, also the prefix of ticket numbers (ACME-0001).</summary>
    public string Slug { get; set; } = "";

    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last ticket number handed out. Optimistic concurrency token - see TicketService.</summary>
    public int TicketSequence { get; set; }
}
