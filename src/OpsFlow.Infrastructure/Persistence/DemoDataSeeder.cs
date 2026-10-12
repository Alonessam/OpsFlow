using Microsoft.EntityFrameworkCore;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;
using OpsFlow.Core.Sla;
using OpsFlow.Infrastructure.Services;
using OpsFlow.Infrastructure.Tenancy;

namespace OpsFlow.Infrastructure.Persistence;

/// <summary>
/// Two fictional companies on one database, so the tenant isolation can be seen (and tested) for real.
/// All data is simulated. Tickets are back-dated so the SLA worker has something to warn about on first run.
/// </summary>
public sealed class DemoDataSeeder(
    OpsFlowDbContext db,
    TenantContext tenantContext,
    ITenantProvisioningService provisioning,
    IPasswordHasher hasher,
    TimeProvider time)
{
    public const string DemoPassword = "Demo!Passw0rd1";

    public async Task SeedAsync(CancellationToken ct = default)
    {
        tenantContext.UseSystem();
        await SeedTenantAsync("acme", "Acme Logistics", "Ayşe Yıldız", "Ahmet Demir", "Elif Şahin", "Mert Aydın", "Zeynep Koç", ct);
        await SeedTenantAsync("globex", "Globex Retail", "Gizem Kaya", "Can Öztürk", "Deniz Arslan", "Selin Çelik", "Burak Kaya", ct);
    }

    private async Task SeedTenantAsync(string slug, string name, string adminName,
        string agent1, string agent2, string employee1, string employee2, CancellationToken ct)
    {
        if (await db.Tenants.AnyAsync(t => t.Slug == slug, ct))
        {
            return;
        }

        var created = await provisioning.CreateTenantAsync(new CreateTenantRequest
        {
            Name = name,
            Slug = slug,
            AdminEmail = $"admin@{slug}.test",
            AdminFullName = adminName,
            AdminPassword = DemoPassword
        }, ct);
        if (!created.IsSuccess)
        {
            throw new InvalidOperationException($"Could not create demo tenant '{slug}': {created.Error!.Message}");
        }

        var tenant = await db.Tenants.FirstAsync(t => t.Id == created.Value!.Id, ct);
        var now = time.GetUtcNow();

        User NewUser(string first, UserRole role, string label) => new()
        {
            TenantId = tenant.Id,
            Email = $"{label}@{slug}.test",
            FullName = first,
            Role = role,
            PasswordHash = hasher.Hash(DemoPassword),
            CreatedAt = now
        };

        var a1 = NewUser(agent1, UserRole.Agent, "agent1");
        var a2 = NewUser(agent2, UserRole.Agent, "agent2");
        var e1 = NewUser(employee1, UserRole.Employee, "employee1");
        var e2 = NewUser(employee2, UserRole.Employee, "employee2");
        db.Users.AddRange(a1, a2, e1, e2);

        var rows = new (string Title, string Description, TicketPriority P, TicketStatus S, int MinutesAgo, User Requester, User? Assignee)[]
        {
            ("Warehouse scanner offline", "Handheld scanners in bay 3 lost connection to the WMS.", TicketPriority.Critical, TicketStatus.InProgress, 185, e1, a1),
            ("Payment terminal rejects cards", "Terminal 2 returns error 51 for every card since the morning.", TicketPriority.Critical, TicketStatus.Assigned, 100, e2, a2),
            ("VPN drops every 10 minutes", "Remote staff are disconnected roughly every ten minutes.", TicketPriority.High, TicketStatus.New, 25, e1, null),
            ("New starter needs laptop", "Laptop and accounts for a new colleague starting Monday.", TicketPriority.Medium, TicketStatus.Assigned, 300, e2, a1),
            ("Printer queue stuck", "Finance printer shows 14 jobs that never finish.", TicketPriority.Low, TicketStatus.PendingClient, 600, e1, a2),
            ("Cannot open shared drive", "Access denied on \\\\files\\ops since the permission change.", TicketPriority.High, TicketStatus.Resolved, 400, e2, a1),
            ("Monitor flickers", "Second monitor flickers when docking.", TicketPriority.Low, TicketStatus.Closed, 4000, e1, a2),
            ("Slow reporting dashboard", "Daily report takes minutes to load.", TicketPriority.Medium, TicketStatus.New, 10, e2, null)
        };

        foreach (var r in rows)
        {
            tenant.TicketSequence++;
            var ticket = new Ticket
            {
                TenantId = tenant.Id,
                Sequence = tenant.TicketSequence,
                TicketNumber = $"{tenant.Slug.ToUpperInvariant()}-{tenant.TicketSequence:D4}",
                Title = r.Title,
                Description = r.Description,
                Priority = r.P,
                Status = r.S,
                CreatedByUserId = r.Requester.Id,
                AssignedToUserId = r.Assignee?.Id,
                CreatedAt = now.AddMinutes(-r.MinutesAgo)
            };
            ticket.ApplySla(SlaTargets.Default(r.P));

            // Only tickets someone has actually worked on have been answered (a merely Assigned one has not).
            if (r.S is not (TicketStatus.New or TicketStatus.Assigned))
            {
                ticket.FirstRespondedAt = ticket.CreatedAt.AddMinutes(10);
            }

            if (r.S == TicketStatus.PendingClient)
            {
                ticket.PauseSla(now.AddMinutes(-30));
            }

            if (r.S is TicketStatus.Resolved or TicketStatus.Closed)
            {
                ticket.ResolutionSummary = "Root cause identified and fixed; verified with the requester.";
                ticket.ResolvedAt = ticket.CreatedAt.AddMinutes(90);
                ticket.ClosedAt = r.S == TicketStatus.Closed ? ticket.ResolvedAt.Value.AddMinutes(30) : null;
            }

            db.Tickets.Add(ticket);
        }

        await db.SaveChangesAsync(ct);
    }
}
