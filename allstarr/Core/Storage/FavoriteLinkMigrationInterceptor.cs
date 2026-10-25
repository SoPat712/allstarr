using System.Data.Common;
using System.Collections.Concurrent;
using allstarr.Core.Favorites;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace allstarr.Core.Storage;

internal sealed class FavoriteLinkMigrationInterceptor : DbCommandInterceptor
{
    internal static readonly FavoriteLinkMigrationInterceptor Instance = new();
    private static readonly ConcurrentDictionary<DbConnection, Rebuild> Rebuilds = new();
    private static readonly ConcurrentDictionary<DbConnection, bool> Functions = new();
    internal static readonly DbConnectionInterceptor ConnectionLifecycle = new ConnectionCleanup();
    private sealed record SchemaObject(string Type, string Name, string Table, string Sql);
    private sealed class Rebuild { internal List<SchemaObject>? Saved { get; set; } }
    private static void Register(DbCommand command)
    {
        if (command.Connection is not SqliteConnection connection) return;
        if (command.CommandText.Contains("/* favorite-link-rebuild */", StringComparison.Ordinal)) Rebuilds[connection] = new();
        if (command.CommandText.Contains("CREATE TABLE \"ef_temp_favorite_events\"", StringComparison.Ordinal) && Rebuilds.TryGetValue(connection, out var rebuild))
        {
            if (command.Transaction == null) throw new InvalidOperationException("Favorite schema rebuild requires its migration transaction.");
            using var read = connection.CreateCommand();
            read.Transaction = (SqliteTransaction)command.Transaction;
            read.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_schema WHERE sql IS NOT NULL AND (type IN ('trigger','view') OR type='table' AND name='favorite_events')";
            var schema = new List<SchemaObject>();
            using (var reader = read.ExecuteReader()) while (reader.Read()) schema.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            var dependent = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "favorite_events" };
            bool changed;
            do
            {
                changed = false;
                foreach (var view in schema.Where(item => item.Type == "view"))
                    if (!dependent.Contains(view.Name) && dependent.Any(name => view.Sql.Contains(name, StringComparison.OrdinalIgnoreCase)))
                        changed |= dependent.Add(view.Name);
            } while (changed);
            rebuild.Saved = schema.Where(item => item.Type == "view" && dependent.Contains(item.Name) || item.Type == "trigger" &&
                (dependent.Contains(item.Table) || dependent.Any(name => item.Sql.Contains(name, StringComparison.OrdinalIgnoreCase)))).ToList();
            foreach (var item in rebuild.Saved.OrderBy(item => item.Type == "trigger" ? 0 : 1)) Execute(command, $"DROP {item.Type.ToUpperInvariant()} \"{item.Name.Replace("\"", "\"\"", StringComparison.Ordinal)}\"");
            const string lineage = "FK_favorite_event_job_lineage";
            if (schema.Single(item => item.Type == "table").Sql.Contains(lineage, StringComparison.Ordinal) && !command.CommandText.Contains(lineage, StringComparison.Ordinal))
            {
                var end = command.CommandText.LastIndexOf(')');
                command.CommandText = command.CommandText.Insert(end,
                    ", CONSTRAINT \"FK_favorite_event_job_lineage\" FOREIGN KEY (\"JobId\", \"OwnerUserId\") REFERENCES \"durable_jobs\" (\"Id\", \"OwnerUserId\")");
            }
        }
        if (command.CommandText.Contains("/* favorite-link-legacy-intents */", StringComparison.Ordinal))
        {
            connection.CreateFunction<string, bool>("allstarr_valid_favorite_resource", item => FavoriteDisplaySnapshot.Resource(item) != null, isDeterministic: true);
            Functions[connection] = true;
        }
    }
    private static void Execute(DbCommand original, string sql)
    {
        using var command = original.Connection!.CreateCommand();
        command.Transaction = original.Transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
    private static void Remove(DbCommand command)
    {
        if (command.Connection is SqliteConnection connection && command.CommandText.Contains("/* favorite-link-legacy-intents */", StringComparison.Ordinal))
        {
            connection.CreateFunction<string, bool>("allstarr_valid_favorite_resource", null);
            Functions.TryRemove(connection, out _);
        }
    }
    private static void Complete(DbCommand command)
    {
        if (command.Connection != null && command.CommandText.Contains("ALTER TABLE \"ef_temp_favorite_events\" RENAME TO \"favorite_events\"", StringComparison.Ordinal) &&
            Rebuilds.TryRemove(command.Connection, out var rebuild) && rebuild.Saved != null)
            foreach (var item in rebuild.Saved.OrderBy(item => item.Type == "view" ? 0 : 1)) Execute(command, item.Sql);
        Remove(command);
    }
    private static void Failed(DbCommand command)
    {
        // The rebuild's migration transaction restores dropped definitions on rollback.
        if (command.Connection != null) Rebuilds.TryRemove(command.Connection, out _);
        Remove(command);
    }
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    { Register(command); return result; }
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
        InterceptionResult<int> result, CancellationToken cancellationToken = default)
    { Register(command); return ValueTask.FromResult(result); }
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    { Complete(command); return result; }
    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    { Complete(command); return ValueTask.FromResult(result); }
    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => Failed(command);
    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    { Failed(command); return Task.CompletedTask; }
    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) => Failed(command);
    public override Task CommandCanceledAsync(DbCommand command, CommandEndEventData eventData, CancellationToken cancellationToken = default)
    { Failed(command); return Task.CompletedTask; }
    private sealed class ConnectionCleanup : DbConnectionInterceptor
    {
        private static void Clear(DbConnection connection)
        {
            Rebuilds.TryRemove(connection, out _);
            if (Functions.TryRemove(connection, out _) && connection is SqliteConnection sqlite)
                sqlite.CreateFunction<string, bool>("allstarr_valid_favorite_resource", null);
        }
        public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) => Clear(connection);
        public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
        { Clear(connection); return Task.CompletedTask; }
    }
}
