using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Domain;

namespace OpsFlow.Infrastructure.Persistence;

/// <summary>Used only by `dotnet ef` to build migrations; never contacts a database.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<OpsFlowDbContext>
{
    public OpsFlowDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<OpsFlowDbContext>()
            .UseNpgsql("Host=localhost;Database=opsflow_design_time", n => n.MigrationsAssembly(typeof(OpsFlowDbContext).Assembly.FullName))
            .Options;
        return new OpsFlowDbContext(options, new NoTenant());
    }

    private sealed class NoTenant : ICurrentTenant
    {
        public Guid? TenantId => null;
        public string? Slug => null;
        public Guid? UserId => null;
        public UserRole? Role => null;
        public bool IsSystem => false;
    }
}
