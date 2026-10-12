using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Domain;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Tenancy;

namespace OpsFlow.Infrastructure.Sla;

public sealed class SlaMonitorOptions
{
    public int IntervalSeconds { get; set; } = 60;
    public int BatchSize { get; set; } = 2000;
}

/// <summary>
/// One sweep over every tenant's open tickets. Cross-tenant by design, so it is one of the very few
/// places allowed to use IgnoreQueryFilters (an architecture test pins that list).
/// Each condition is raised exactly once thanks to the *At markers stored on the ticket.
/// </summary>
public sealed class SlaMonitor(
    IServiceScopeFactory scopes,
    TimeProvider time,
    ILogger<SlaMonitor> logger,
    IOptions<SlaMonitorOptions> options)
{
    /// <returns>Number of SLA events raised during this sweep.</returns>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().UseSystem();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<ITicketEventPublisher>();

        var now = time.GetUtcNow();

        // Skip tickets with nothing left to report, and paused ones (the clock is stopped).
        var candidates = await db.Tickets
            .IgnoreQueryFilters()
            .Where(t => t.Status != TicketStatus.Resolved && t.Status != TicketStatus.Closed && t.SlaPausedAt == null)
            .Where(t => t.SlaBreachedAt == null || (t.FirstRespondedAt == null && t.ResponseBreachedAt == null))
            .OrderBy(t => t.ResolutionDeadline)
            .Take(options.Value.BatchSize)
            .ToListAsync(ct);

        if (candidates.Count == options.Value.BatchSize)
        {
            // Oldest deadlines are processed first, so urgent tickets are never starved, but say so loudly.
            logger.LogWarning("SLA sweep hit its batch limit ({Limit}); later tickets wait for the next sweep. Raise Sla:BatchSize.",
                options.Value.BatchSize);
        }

        var raised = new List<TicketEvent>();
        foreach (var t in candidates)
        {
            var verdict = t.Evaluate(now);

            if (verdict.ResponseBreached && t.ResponseBreachedAt is null)
            {
                t.ResponseBreachedAt = now;
                raised.Add(Event(TicketEventType.SlaResponseBreached, t, now, "First-response SLA breached"));
            }

            if (verdict.ResolutionBreached && t.SlaBreachedAt is null)
            {
                t.SlaBreachedAt = now;
                t.IsSlaBreached = true;
                raised.Add(Event(TicketEventType.SlaBreached, t, now, "Resolution SLA breached"));
            }
            else if (verdict.Warning && t.SlaWarningAt is null)
            {
                t.SlaWarningAt = now;
                raised.Add(Event(TicketEventType.SlaWarning, t, now, "Less than 20% of the resolution time is left"));
            }
        }

        if (raised.Count == 0)
        {
            return 0;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A human edited a ticket mid-sweep. Drop this batch; the next sweep re-evaluates it.
            logger.LogInformation("SLA sweep skipped a batch because tickets were modified concurrently.");
            return 0;
        }

        // Publish only what was actually persisted. The markers are already saved, so one failing push
        // must not swallow the rest: log it and carry on with the remaining events.
        foreach (var e in raised)
        {
            try
            {
                await publisher.PublishAsync(e, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not publish {EventType} for {Ticket}.", e.Type, e.TicketNumber);
            }
        }

        logger.LogInformation("SLA sweep raised {Count} event(s).", raised.Count);
        return raised.Count;
    }

    private static TicketEvent Event(TicketEventType type, Ticket t, DateTimeOffset now, string detail) =>
        new(type, t.TenantId, t.Id, t.TicketNumber, t.Title, t.Priority, t.Status, t.CreatedByUserId, now, detail, StaffOnly: true);
}

public sealed class SlaMonitoringWorker(
    SlaMonitor monitor,
    IOptions<SlaMonitorOptions> options,
    ILogger<SlaMonitoringWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(1, options.Value.IntervalSeconds)));
        do
        {
            try
            {
                await monitor.RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A failed sweep must never kill the worker; the next tick retries.
                logger.LogError(ex, "SLA sweep failed.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

