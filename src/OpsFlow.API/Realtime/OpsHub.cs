using System.Collections.Concurrent;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using OpsFlow.API.Auth;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Domain;

namespace OpsFlow.API.Realtime;

public static class HubGroups
{
    public static string Staff(Guid tenantId) => $"tenant:{tenantId:N}:staff";
    public static string User(Guid tenantId, Guid userId) => $"tenant:{tenantId:N}:user:{userId:N}";
}

/// <summary>Which live connections belong to which user, so a deactivated user can be cut off at once.</summary>
public sealed class ConnectionRegistry
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _byUser = new();

    public void Add(Guid userId, string connectionId) =>
        _byUser.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>())[connectionId] = 0;

    public void Remove(Guid userId, string connectionId)
    {
        if (_byUser.TryGetValue(userId, out var connections))
        {
            connections.TryRemove(connectionId, out _);
        }
    }

    public IReadOnlyCollection<string> For(Guid userId) =>
        _byUser.TryGetValue(userId, out var connections) ? connections.Keys.ToArray() : [];
}

/// <summary>
/// Server-to-client push only. Group membership is derived from the validated JWT at connect time;
/// the client cannot ask to join any group, so one tenant can never subscribe to another's events.
/// </summary>
[Authorize]
public sealed class OpsHub(ConnectionRegistry registry) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var user = Context.User!;
        if (Guid.TryParse(user.FindFirst(OpsClaims.TenantId)?.Value, out var tenantId)
            && Guid.TryParse(user.FindFirst(OpsClaims.UserId)?.Value, out var userId)
            && Enum.TryParse<UserRole>(user.FindFirst(OpsClaims.Role)?.Value, out var role) && Enum.IsDefined(role))
        {
            registry.Add(userId, Context.ConnectionId);

            if (role is UserRole.Agent or UserRole.TenantAdmin)
            {
                // Staff get every event of their tenant through this one group. They are deliberately NOT also
                // put in their personal group, otherwise an event about their own ticket would arrive twice.
                await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.Staff(tenantId));
            }
            else
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, HubGroups.User(tenantId, userId));
            }
        }

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        if (Guid.TryParse(Context.User?.FindFirst(OpsClaims.UserId)?.Value, out var userId))
        {
            registry.Remove(userId, Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}

public sealed class SignalRTicketEventPublisher(IHubContext<OpsHub> hub) : ITicketEventPublisher
{
    public async Task PublishAsync(TicketEvent e, CancellationToken cancellationToken = default)
    {
        await hub.Clients.Group(HubGroups.Staff(e.TenantId)).SendAsync("ticketEvent", e, cancellationToken);

        // The requester follows their own ticket; staff-only events (SLA alarms, internal notes) never reach them.
        if (!e.StaffOnly)
        {
            await hub.Clients.Group(HubGroups.User(e.TenantId, e.RequesterUserId)).SendAsync("ticketEvent", e, cancellationToken);
        }
    }
}

/// <summary>
/// Removes every live connection of a user from every group. Takes effect on this node immediately; on a
/// scaled-out deployment other nodes rely on the token lifetime (a Redis backplane would be needed to reach them).
/// </summary>
public sealed class HubConnectionRevoker(IHubContext<OpsHub> hub, ConnectionRegistry registry) : IConnectionRevoker
{
    public async Task RevokeAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default)
    {
        foreach (var connectionId in registry.For(userId))
        {
            await hub.Groups.RemoveFromGroupAsync(connectionId, HubGroups.Staff(tenantId), cancellationToken);
            await hub.Groups.RemoveFromGroupAsync(connectionId, HubGroups.User(tenantId, userId), cancellationToken);
        }
    }
}
