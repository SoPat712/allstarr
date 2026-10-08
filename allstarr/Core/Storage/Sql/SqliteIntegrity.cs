using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace allstarr.Core.Storage.Sql;

internal static class SqliteIntegrity
{
    public static void ConfigureBaseline(MigrationBuilder migration)
    {
        AddLineage(migration, "managed_files", "durable_jobs", "SourceJobId,TenantId", "Id,TenantId", "FK_managed_file_job_tenant_lineage");
        AddLineage(migration, "managed_files", "durable_jobs", "SourceJobId,TenantId,OwnerUserId", "Id,TenantId,OwnerUserId", "FK_managed_file_job_owner_lineage");
        AddLineage(migration, "favorite_events", "durable_jobs", "JobId,TenantId,OwnerUserId", "Id,TenantId,OwnerUserId", "FK_favorite_event_job_lineage");
        AddLineage(migration, "provider_download_workspaces", "durable_jobs", "DurableJobId,TenantId", "Id,TenantId", "FK_download_workspace_job_tenant_lineage");
        AddLineage(migration, "provider_download_workspaces", "durable_jobs", "DurableJobId,TenantId,OwnerUserId", "Id,TenantId,OwnerUserId", "FK_download_workspace_job_owner_lineage");
        AddLineage(migration, "metadata_enrichment_plans", "durable_jobs", "LineageJobId,TenantId,OwnerUserId", "Id,TenantId,OwnerUserId", "FK_enrichment_plan_job_lineage");
        AddLineage(migration, "metadata_enrichment_plans", "managed_files", "ManagedArtifactId,TenantId,OwnerUserId", "Id,TenantId,OwnerUserId", "FK_enrichment_plan_file_lineage");
        AddLineage(migration, "metadata_enrichment_applications", "durable_jobs", "LineageJobId,TenantId,OwnerUserId", "Id,TenantId,OwnerUserId", "FK_enrichment_application_job_lineage");

        ValidateBoth(migration, "durable_job_account_scope", "durable_jobs",
            $"NOT {AccountMatches("NEW.\"ProviderAccountId\"", "NEW.\"TenantId\"", "NEW.\"OwnerUserId\"", "NEW.\"LibraryScopeId\"")}",
            "ProviderAccountId,TenantId,OwnerUserId,LibraryScopeId", "CK_durable_job_account_scope");

        ValidateBoth(migration, "managed_file_reference_lineage", "managed_file_references",
            "NOT EXISTS (SELECT 1 FROM managed_files f WHERE f.\"Id\"=NEW.\"ManagedFileId\" AND f.\"TenantId\"=NEW.\"TenantId\" AND f.\"OwnerUserId\" IS NEW.\"OwnerUserId\" AND f.\"ScopeKey\"=NEW.\"ScopeKey\")",
            "ManagedFileId,TenantId,OwnerUserId,ScopeKey", "FK_managed_file_reference_lineage");
        Guard(migration, "managed_file_saved_reference_lineage", "managed_files", "Id,TenantId,OwnerUserId,ScopeKey",
            "EXISTS (SELECT 1 FROM managed_file_references r WHERE r.\"ManagedFileId\"=OLD.\"Id\" AND (NEW.\"TenantId\" IS NOT r.\"TenantId\" OR NEW.\"OwnerUserId\" IS NOT r.\"OwnerUserId\" OR NEW.\"ScopeKey\" IS NOT r.\"ScopeKey\"))",
            "FK_managed_file_saved_reference_lineage");
        migration.Sql($"""
            CREATE TRIGGER "TR_managed_file_reference_count_insert" AFTER INSERT ON managed_file_references BEGIN
                {SyncCount("NEW.\"ManagedFileId\"")}
            END;
            CREATE TRIGGER "TR_managed_file_reference_count_update" AFTER UPDATE OF "ManagedFileId","ReleasedAt" ON managed_file_references BEGIN
                {SyncCount("OLD.\"ManagedFileId\"")}
                {SyncCount("NEW.\"ManagedFileId\"", "NEW.\"ManagedFileId\" IS NOT OLD.\"ManagedFileId\"")}
            END;
            CREATE TRIGGER "TR_managed_file_reference_count_delete" AFTER DELETE ON managed_file_references BEGIN
                {SyncCount("OLD.\"ManagedFileId\"")}
            END;
            CREATE TRIGGER "TR_managed_file_reference_count_guard" BEFORE UPDATE OF "ReferenceCount","RemovedAt" ON managed_files BEGIN
                SELECT CASE WHEN NEW."ReferenceCount" IS NOT (SELECT count(*) FROM managed_file_references r WHERE r."ManagedFileId"=NEW."Id" AND r."ReleasedAt" IS NULL)
                    THEN RAISE(ABORT, 'CK_managed_file_reference_count') END;
                SELECT CASE WHEN NEW."RemovedAt" IS NOT NULL AND EXISTS (SELECT 1 FROM managed_file_references r WHERE r."ManagedFileId"=NEW."Id" AND r."ReleasedAt" IS NULL)
                    THEN RAISE(ABORT, 'CK_managed_file_removed_references') END;
            END;
            CREATE TRIGGER "TR_managed_file_reference_count_initialize" AFTER INSERT ON managed_files BEGIN
                UPDATE managed_files SET "ReferenceCount"=0, "Revision"="Revision"+1 WHERE "Id"=NEW."Id" AND "ReferenceCount"<>0;
            END;
            """);
        ValidateBoth(migration, "download_artifact_file_lineage", "provider_download_artifacts",
            "NEW.\"ManagedFileId\" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM managed_files f WHERE f.\"Id\"=NEW.\"ManagedFileId\" AND f.\"TenantId\"=NEW.\"TenantId\" AND f.\"OwnerUserId\" IS NEW.\"OwnerUserId\" AND f.\"LibraryScopeId\" IS NEW.\"LibraryScopeId\")",
            "ManagedFileId,TenantId,OwnerUserId,LibraryScopeId", "FK_download_artifact_managed_file_lineage");
        Guard(migration, "download_artifact_saved_file_lineage", "managed_files", "Id,TenantId,OwnerUserId,LibraryScopeId",
            "EXISTS (SELECT 1 FROM provider_download_artifacts a WHERE a.\"ManagedFileId\"=OLD.\"Id\" AND (a.\"TenantId\" IS NOT NEW.\"TenantId\" OR a.\"OwnerUserId\" IS NOT NEW.\"OwnerUserId\" OR a.\"LibraryScopeId\" IS NOT NEW.\"LibraryScopeId\"))",
            "FK_download_artifact_saved_file_lineage");

        var savedAccountScope = $"EXISTS (SELECT 1 FROM durable_jobs j WHERE j.\"ProviderAccountId\"=OLD.\"Id\" AND NOT {ScopeMatches("NEW", "j.\"TenantId\"", "j.\"OwnerUserId\"", "j.\"LibraryScopeId\"")})";
        Guard(migration, "provider_account_saved_lineage", "provider_accounts", "Scope,TenantId,OwnerUserId,LibraryScopeId",
            savedAccountScope, "CK_provider_account_saved_lineage");
    }

    private static void AddLineage(MigrationBuilder migration, string table, string principal,
        string columns, string principalColumns, string name)
    {
        var operation = migration.Operations.OfType<CreateTableOperation>().Single(item => item.Name == table);
        operation.ForeignKeys.Add(new AddForeignKeyOperation
        {
            Name = name,
            Table = table,
            Columns = columns.Split(','),
            PrincipalTable = principal,
            PrincipalColumns = principalColumns.Split(','),
            OnDelete = ReferentialAction.NoAction
        });
    }

    private static string AccountMatches(string account, string tenant, string owner, string library) =>
        $"({account} IS NULL OR EXISTS (SELECT 1 FROM provider_accounts a WHERE a.\"Id\"={account} AND {ScopeMatches("a", tenant, owner, library)}))";

    private static string ScopeMatches(string account, string tenant, string owner, string library) =>
        $"COALESCE((({account}.\"Scope\"='Global' AND {account}.\"TenantId\" IS NULL AND {account}.\"OwnerUserId\" IS NULL AND {account}.\"LibraryScopeId\" IS NULL) OR " +
        $"({account}.\"Scope\"='User' AND {tenant} IS NOT NULL AND {owner} IS NOT NULL AND {account}.\"TenantId\"={tenant} AND {account}.\"OwnerUserId\"={owner}) OR " +
        $"({account}.\"Scope\"='Library' AND {tenant} IS NOT NULL AND {library} IS NOT NULL AND {account}.\"TenantId\"={tenant} AND {account}.\"OwnerUserId\" IS NULL AND {account}.\"LibraryScopeId\"={library})), 0)";

    private static string SyncCount(string file, string condition = "1") =>
        $"UPDATE managed_files SET \"ReferenceCount\"=(SELECT count(*) FROM managed_file_references r WHERE r.\"ManagedFileId\"={file} AND r.\"ReleasedAt\" IS NULL), \"Revision\"=\"Revision\"+1 " +
        $"WHERE \"Id\"={file} AND ({condition}) AND \"ReferenceCount\" IS NOT (SELECT count(*) FROM managed_file_references r WHERE r.\"ManagedFileId\"={file} AND r.\"ReleasedAt\" IS NULL);";

    private static void ValidateBoth(MigrationBuilder migration, string name, string table, string condition,
        string columns, string error)
    {
        migration.Sql($"CREATE TRIGGER \"TR_{name}_insert\" BEFORE INSERT ON \"{table}\" WHEN {condition} BEGIN SELECT RAISE(ABORT, '{error}'); END;");
        Guard(migration, name + "_update", table, columns, condition, error);
    }

    private static void Guard(MigrationBuilder migration, string name, string table, string columns,
        string condition, string error) =>
        migration.Sql($"CREATE TRIGGER \"TR_{name}\" BEFORE UPDATE OF {string.Join(',', columns.Split(',').Select(column => $"\"{column}\""))} ON \"{table}\" WHEN {condition} BEGIN SELECT RAISE(ABORT, '{error}'); END;");
}
