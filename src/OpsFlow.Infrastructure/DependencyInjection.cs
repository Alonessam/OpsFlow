using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Core.Abstractions;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Security;
using OpsFlow.Infrastructure.Services;
using OpsFlow.Infrastructure.Sla;
using OpsFlow.Infrastructure.Tenancy;

namespace OpsFlow.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOpsFlowInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddMemoryCache();

        services.AddScoped<TenantContext>();
        services.AddScoped<ICurrentTenant>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantDirectory>();

        services.AddScoped<EntityStampingInterceptor>();
        services.AddScoped<AuditInterceptor>();

        var provider = config["Database:Provider"] ?? "Postgres";
        services.AddDbContext<OpsFlowDbContext>((sp, options) =>
        {
            switch (provider.ToLowerInvariant())
            {
                case "inmemory":
                    options.UseInMemoryDatabase(config["Database:InMemoryName"] ?? "opsflow");
                    break;
                case "postgres":
                    options.UseNpgsql(
                        PostgresConnectionString.Normalize(
                            config.GetConnectionString("Default") is { Length: > 0 } cs
                                ? cs
                                : throw new InvalidOperationException("ConnectionStrings:Default is not configured.")),
                        npgsql =>
                        {
                            npgsql.MigrationsAssembly(typeof(OpsFlowDbContext).Assembly.FullName);

                            // A serverless database (Neon, Azure burstable ...) may be asleep or briefly unreachable on the
                            // first request after idling. Retry transient failures instead of surfacing a 500.
                            // Concurrency and unique-violation errors are not transient and are never retried.
                            npgsql.EnableRetryOnFailure(maxRetryCount: 4, maxRetryDelay: TimeSpan.FromSeconds(8), errorCodesToAdd: null);
                        });
                    break;
                default:
                    throw new InvalidOperationException($"Unknown Database:Provider '{provider}'.");
            }

            // Order matters: stamp the tenant first, then audit (audit rows copy the stamped TenantId).
            options.AddInterceptors(
                sp.GetRequiredService<EntityStampingInterceptor>(),
                sp.GetRequiredService<AuditInterceptor>());
        });

        services.AddSingleton<IPasswordHasher>(new BCryptPasswordHasher(config.GetValue("Security:BcryptWorkFactor", 12)));

        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<ISlaPolicyService, SlaPolicyService>();
        services.AddScoped<ITicketService, TicketService>();

        services.TryAddSingleton<ITicketEventPublisher, NullTicketEventPublisher>();
        services.TryAddSingleton<IConnectionRevoker, NullConnectionRevoker>();

        services.Configure<SlaMonitorOptions>(config.GetSection("Sla"));
        services.AddSingleton<SlaMonitor>();
        if (config.GetValue("Sla:WorkerEnabled", true))
        {
            services.AddHostedService<SlaMonitoringWorker>();
        }

        services.AddTransient<DemoDataSeeder>();
        return services;
    }
}

public sealed class NullConnectionRevoker : IConnectionRevoker
{
    public Task RevokeAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

public sealed class NullTicketEventPublisher : ITicketEventPublisher
{
    public Task PublishAsync(TicketEvent ticketEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
