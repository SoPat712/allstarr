using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace allstarr.Tests;

public sealed class DbErrorsTests
{
    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, true, false)]
    [InlineData(PostgresErrorCodes.SerializationFailure, false, true)]
    [InlineData(PostgresErrorCodes.DeadlockDetected, false, true)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, false, false)]
    [InlineData(PostgresErrorCodes.NotNullViolation, false, false)]
    public void ClassifiesNestedDatabaseErrors(string code, bool unique, bool transient)
    {
        var error = new InvalidOperationException("Operation failed",
            new DbUpdateException("Write failed", new PostgresException("Conflict", "ERROR", "ERROR", code)));
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
