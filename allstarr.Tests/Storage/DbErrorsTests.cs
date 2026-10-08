using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;

namespace allstarr.Tests;

public sealed class DbErrorsTests
{
    [Theory]
    [InlineData(19, 2067, true, false)]
    [InlineData(19, 1555, true, false)]
    [InlineData(5, 5, false, true)]
    [InlineData(6, 6, false, true)]
    [InlineData(19, 787, false, false)]
    [InlineData(19, 1299, false, false)]
    [InlineData(1, 1, false, false)]
    public void ClassifiesNestedDatabaseErrors(int code, int extendedCode, bool unique, bool transient)
    {
        var error = new InvalidOperationException("Operation failed",
            new DbUpdateException("Write failed", new SqliteException("Conflict", code, extendedCode)));
        Assert.Equal(unique, DbErrors.IsUniqueViolation(error));
        Assert.Equal(transient, DbErrors.IsTransientConflict(error));
    }

    [Fact]
    public void PreservesOptimisticConcurrencyWithoutRetryingUnrelatedFailures()
    {
        var concurrency = new InvalidOperationException("Operation failed", new DbUpdateConcurrencyException());
        Assert.True(DbErrors.IsTransientConflict(concurrency));
        Assert.False(DbErrors.IsUniqueViolation(concurrency));
        Assert.False(DbErrors.IsTransientConflict(new InvalidOperationException("Operation failed")));
        Assert.False(DbErrors.IsUniqueViolation(new InvalidOperationException("Operation failed")));
    }
}
