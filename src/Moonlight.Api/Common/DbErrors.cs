using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Moonlight.Api.Common;

public static class DbErrors
{
    public static bool IsUniqueViolation(this DbUpdateException ex, string? constraint = null) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg
        && (constraint is null || pg.ConstraintName == constraint);

    public static bool IsExclusionViolation(this DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.ExclusionViolation };
}
