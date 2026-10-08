using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace allstarr.Core.Storage;

public static class DbErrors
{
    public static bool IsUniqueViolation(Exception exception) =>
        Chain(exception).OfType<SqliteException>()
            .Any(error => error.SqliteErrorCode == 19 && error.SqliteExtendedErrorCode is 2067 or 1555);

    public static bool IsTransientConflict(Exception exception) =>
        Chain(exception).Any(error => error is DbUpdateConcurrencyException or SqliteException
        {
            SqliteErrorCode: 5 or 6
        });

    internal static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
            yield return current;
    }
}
