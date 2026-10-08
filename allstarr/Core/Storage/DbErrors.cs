using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace allstarr.Core.Storage;

public static class DbErrors
{
    public static bool IsUniqueViolation(Exception exception) =>
        Chain(exception).OfType<PostgresException>()
            .Any(error => error.SqlState == PostgresErrorCodes.UniqueViolation);

    public static bool IsTransientConflict(Exception exception) =>
        Chain(exception).Any(error => error is DbUpdateConcurrencyException or PostgresException
        {
            SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
        });

    internal static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
            yield return current;
    }
}
