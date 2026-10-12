using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace OpsFlow.Infrastructure.Persistence;

public static class DbErrors
{
    /// <summary>PostgreSQL unique_violation. Two racing writers claiming the same ticket number surface this way.</summary>
    public static bool IsUniqueViolation(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
