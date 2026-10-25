using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Core.Storage;

internal sealed class BackupMigrationCleanupInterceptor : DbCommandInterceptor
{
    internal static readonly BackupMigrationCleanupInterceptor Instance = new();
    private static void Reset(DbCommand command)
    {
        if (!command.CommandText.Contains("/* backup-owner-constraint */", StringComparison.Ordinal) || command.Connection == null) return;
        try
        {
            using var cleanup = command.Connection.CreateCommand();
            cleanup.Transaction = command.Transaction;
            cleanup.CommandText = "PRAGMA writable_schema=RESET;";
            cleanup.ExecuteNonQuery();
        }
        catch (Exception error) when (error is DbException or InvalidOperationException)
        {
            if (command.Connection is SqliteConnection sqlite)
            {
                SqliteConnection.ClearPool(sqlite);
                try { sqlite.Close(); } catch (SqliteException) { }
            }
        }
    }
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Reset(command);
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    { Reset(command); return Task.CompletedTask; }
    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) => Reset(command);
    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    { Reset(command); return Task.CompletedTask; }
}
