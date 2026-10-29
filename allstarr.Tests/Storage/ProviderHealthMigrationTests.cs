using System.IO.Compression;
using System.Text;
using System.Text.Json;
using allstarr.Core.Operations;
using allstarr.Core.Secrets;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ProviderHealthMigrationTests
{
    private const string Baseline = "20261008235223_UsersBaseline";
    private static readonly string[] Removed = ["provider_health_samples", "provider_health_rollups", "provider_circuits"];

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task PopulatedUpgradePreservesAllOtherRowsSchemaAndEncryptedCredentialsIncludingOldBackupRestore()
    {
        await using var database = await SqliteTestDatabase.CreateAsync(useTemplate: false);
        var factory = new TestDbContextFactory(database.Options);
        await using var db = await factory.CreateDbContextAsync();
        await db.GetService<IMigrator>().MigrateAsync(Baseline);
        var now = DateTimeOffset.UtcNow;
        var user = Guid.CreateVersion7();
        var account = Guid.CreateVersion7();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO users (Id, BackendType, BackendInstanceId, BackendPrincipalId, DisplayName, IsAdmin, Enabled, CreatedAt, UpdatedAt, LastSeenAt)
            VALUES ({user}, 'jellyfin', 'fixture', 'listener', 'Listener', 0, 1, {now.UtcTicks}, {now.UtcTicks}, {now.UtcTicks});
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO provider_accounts (Id,OwnerUserId,ProviderId,DisplayName,Enabled,CreatedAt,UpdatedAt,Revision)
            VALUES ({account},{user},'spotify','Saved account',1,{now.UtcTicks},{now.UtcTicks},9);
            """);
        var playlistId = Guid.CreateVersion7();
        var sourceHash = new string('a', 64);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO playlist_links
                (Id,OwnerUserId,ProviderAccountId,SourceProviderId,SourcePlaylistId,SourcePlaylistIdHash,
                 TargetProtocol,TargetBackendInstanceId,TargetPlaylistId,Mode,MaterializationMode,
                 MirrorStaleEntries,PreserveManualEntries,SyncName,SyncDescription,SyncArtwork,
                 RuleVersion,PolicyVersion,CreatedAt,UpdatedAt,Revision)
            VALUES ({playlistId},{user},{account},'spotify','saved-source',{sourceHash},
                    'jellyfin','fixture','saved-destination','Virtual','Reconcile',0,1,1,1,1,
                    '1','1',{now.UtcTicks},{now.UtcTicks},7);
            """);
        db.RuntimeSettings.Add(new RuntimeSettingRecord
        {
            Id = Guid.CreateVersion7(),
            OwnerUserId = user,
            Key = "Playback:ShowExternalLabel",
            ValueType = RuntimeSettingValueType.Boolean,
            ValueJson = "false",
            Source = "personal",
            Revision = 3,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var keys = new SecretStoreOptions { KeyRingPath = Path.Combine(database.StorageOptions.DataDirectory, "keyring.json") };
        var ring = new FileSecretKeyRingProvider(keys);
        await ring.CreateIfMissingAsync(false);
        var secrets = new EncryptedSecretStore(factory, ring, keys, new SystemPlatformClock());
        var purpose = $"provider-account:spotify:{account:N}";
        var secret = await secrets.StoreAsync(user, purpose, Encoding.UTF8.GetBytes("fixture-credential"));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE provider_accounts SET SecretReferenceId = {secret.Id} WHERE Id = {account};
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO provider_health_samples (Id,ProviderAccountId,Capability,State,ObservedAt,ExpiresAt)
            VALUES ({Guid.NewGuid()},{account},'playlist','Healthy',{now.UtcTicks},{now.AddMinutes(5).UtcTicks});
            INSERT INTO provider_circuits (Id,ProviderAccountId,Capability,State,ConsecutiveFailures,UpdatedAt,Revision)
            VALUES ({Guid.NewGuid()},{account},'playlist','Closed',0,{now.UtcTicks},1);
            INSERT INTO provider_health_rollups (Id,ProviderAccountId,Capability,WindowStart,WindowEnd,SampleCount,SuccessCount,FailureCount,SuccessRate,LastState,UpdatedAt,Revision)
            VALUES ({Guid.NewGuid()},{account},'playlist',{now.UtcTicks},{now.AddMinutes(15).UtcTicks},1,1,0,1,'Healthy',{now.UtcTicks},1);
            """);
        var beforeSchema = await Schema(db);
        var beforeRows = await Rows(db);
        Assert.Equal(32, beforeSchema.Count(row => row.Type == "trigger"));
        var originalKeyRing = await File.ReadAllBytesAsync(keys.KeyRingPath);
        var oldArchive = await CreateBaselineBackup(db, keys, database.StorageOptions.DataDirectory);

        await db.Database.MigrateAsync();
        Assert.Empty(await db.RecordingLyricsChoices.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.SubsonicPlayQueues.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.ProviderUserData.AsNoTracking().ToArrayAsync());
        Assert.Empty(await db.ProblemReports.AsNoTracking().ToArrayAsync());
        AssertSchemaPreserved(beforeSchema, await Schema(db));
        Assert.All(await db.DownloadedSongMappings.AsNoTracking().ToArrayAsync(), mapping => Assert.Null(mapping.CacheIdentityJson));
        Assert.All(await db.Users.AsNoTracking().ToArrayAsync(), listener => Assert.False(listener.IsSynthetic));
        Assert.Equal(beforeRows, await Rows(db));
        Assert.Equal("Existing", await db.Database.SqlQuery<string>($"SELECT DestinationMode AS Value FROM playlist_links").SingleAsync());
        Assert.Equal("{}", await db.Database.SqlQuery<string>($"SELECT SettingsJson AS Value FROM provider_accounts").SingleAsync());
        foreach (var table in Removed)
            Assert.False(await db.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM sqlite_master WHERE type='table' AND name={table}) AS Value").SingleAsync());
        Assert.Equal(originalKeyRing, await File.ReadAllBytesAsync(keys.KeyRingPath));
        using (var credential = await secrets.OpenAsync(secret.Id, new SecretAccessContext(user, purpose)))
            Assert.Equal("fixture-credential", credential.ReadUtf8());

        var state = new DurableStorageState(database.StorageOptions);
        state.Set(DurableStorageReadiness.Ready);
        var backups = new DurableBackupService(factory, database.StorageOptions, state, keys);
        var newBackup = await backups.CreateAsync();
        Assert.Equal("verified", newBackup.Status);
        Assert.Equal(db.Database.GetMigrations().Last(), newBackup.SchemaVersion);
        await using (var upload = File.OpenRead(oldArchive)) await backups.StageRestoreAsync(upload);
        await db.Database.CloseConnectionAsync();
        state.Set(DurableStorageReadiness.Initializing);
        await new DurableStorageInitializer(factory, database.StorageOptions, state,
            NullLogger<DurableStorageInitializer>.Instance, backups).StartAsync(default);
        Assert.Equal(DurableStorageReadiness.Ready, state.GetSnapshot().Readiness);
        await using var restored = await factory.CreateDbContextAsync();
        Assert.Empty(await restored.RecordingLyricsChoices.AsNoTracking().ToArrayAsync());
        Assert.Empty(await restored.SubsonicPlayQueues.AsNoTracking().ToArrayAsync());
        Assert.Empty(await restored.ProviderUserData.AsNoTracking().ToArrayAsync());
        Assert.Empty(await restored.ProblemReports.AsNoTracking().ToArrayAsync());
        AssertSchemaPreserved(beforeSchema, await Schema(restored));
        Assert.All(await restored.DownloadedSongMappings.AsNoTracking().ToArrayAsync(), mapping => Assert.Null(mapping.CacheIdentityJson));
        Assert.All(await restored.Users.AsNoTracking().ToArrayAsync(), listener => Assert.False(listener.IsSynthetic));
        Assert.Equal(beforeRows, await Rows(restored));
        Assert.Equal("Existing", await restored.Database.SqlQuery<string>($"SELECT DestinationMode AS Value FROM playlist_links").SingleAsync());
        Assert.Equal(originalKeyRing, await File.ReadAllBytesAsync(keys.KeyRingPath));
        using var restoredCredential = await new EncryptedSecretStore(factory, new FileSecretKeyRingProvider(keys), keys,
            new SystemPlatformClock()).OpenAsync(secret.Id, new SecretAccessContext(user, purpose));
        Assert.Equal("fixture-credential", restoredCredential.ReadUtf8());
    }

    private sealed class TestDbContextFactory(DbContextOptions<AllstarrDbContext> options) : IDbContextFactory<AllstarrDbContext>
    {
        public AllstarrDbContext CreateDbContext() => new(options);
        public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private static void AssertSchemaPreserved(SchemaObject[] before, SchemaObject[] after)
    {
        const string publicLinkIndex = "IX_playlist_link_public_source_target";
        const string publicSnapshotIndex = "IX_external_snapshot_public_version";
        const string subjectIndex = "IX_audit_subject_history";
        const string downloadArtifactIndex = "IX_download_artifact_job_provider";
        const string currentArtifactIndexSql = "CREATE UNIQUE INDEX \"IX_download_artifact_job_provider\" ON \"provider_download_artifacts\" (\"DurableJobId\", \"ProviderId\") WHERE \"SupersededAt\" IS NULL";
        Assert.Equal(currentArtifactIndexSql, CollapseSql(Assert.Single(after, row => row.Name == downloadArtifactIndex).Sql));
        Assert.Equal(currentArtifactIndexSql.Replace(" WHERE \"SupersededAt\" IS NULL", "", StringComparison.Ordinal),
            CollapseSql(Assert.Single(before, row => row.Name == downloadArtifactIndex).Sql));
        Assert.Contains(before, row => row.Name == "job_attempts");
        Assert.Contains(before, row => row.Name == "IX_job_attempts_JobId_AttemptNumber");
        Assert.DoesNotContain(after, row => row.Name is "job_attempts" or "IX_job_attempts_JobId_AttemptNumber");
        Assert.Equal(
            ["\"LeaseExpiresAt\" INTEGER NULL", "\"LeaseOwner\" TEXT NULL"],
            TableColumns(before, "durable_jobs").Except(TableColumns(after, "durable_jobs")).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["\"LastAttemptCompletedAt\" INTEGER NULL", "\"LastAttemptOutcome\" TEXT NULL", "\"LastAttemptStartedAt\" INTEGER NULL"],
            TableColumns(after, "durable_jobs").Except(TableColumns(before, "durable_jobs")).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["\"SubjectId\" TEXT NULL", "\"SubjectType\" TEXT NULL"],
            TableColumns(after, "audit_events").Except(TableColumns(before, "audit_events")).Order(StringComparer.Ordinal));
        Assert.Empty(TableColumns(before, "audit_events").Except(TableColumns(after, "audit_events")));
        Assert.Equal("CREATE INDEX \"IX_audit_subject_history\" ON \"audit_events\" (\"SubjectType\", \"SubjectId\", \"CreatedAt\")",
            CollapseSql(Assert.Single(after, row => row.Name == subjectIndex).Sql));
        Assert.DoesNotContain(before, row => row.Name is publicLinkIndex or publicSnapshotIndex);

        Assert.Equal(["\"DisplayMetadataJson\" TEXT NULL"],
            TableColumns(after, "favorite_states").Except(TableColumns(before, "favorite_states")).Order(StringComparer.Ordinal));
        Assert.Empty(TableColumns(before, "favorite_states").Except(TableColumns(after, "favorite_states")));
        Assert.Equal(["\"ResumePositionTicks\" INTEGER NULL"],
            TableColumns(after, "listening_events").Except(TableColumns(before, "listening_events")).Order(StringComparer.Ordinal));
        Assert.Empty(TableColumns(before, "listening_events").Except(TableColumns(after, "listening_events")));

        string[] keptObjects =
        [
            "recording_kept_routes", "IX_recording_kept_routes_ManagedFileId",
            "IX_recording_kept_routes_PublicationJobId", "IX_recording_kept_routes_PublishedByUserId",
            "IX_recording_kept_routes_SourceArtifactId", "TR_kept_publication_insert", "TR_kept_publication_update",
            "TR_kept_source_immutable", "TR_kept_file_immutable", "TR_kept_job_owner", "TR_kept_workspace_immutable"
        ];
        Assert.DoesNotContain(before, row => keptObjects.Contains(row.Name));
        Assert.Equal(keptObjects.Order(StringComparer.Ordinal),
            after.Where(row => keptObjects.Contains(row.Name)).Select(row => row.Name).Order(StringComparer.Ordinal));
        Assert.Equal(
            ["\"QualityCheckJson\" TEXT NULL", "\"SourceAccountRevision\" INTEGER NULL", "\"SourceExternalTrackId\" TEXT NULL", "\"SupersededAt\" INTEGER NULL"],
            TableColumns(after, "provider_download_artifacts").Except(TableColumns(before, "provider_download_artifacts")).Order(StringComparer.Ordinal));
        Assert.Empty(TableColumns(before, "provider_download_artifacts").Except(TableColumns(after, "provider_download_artifacts")));
        foreach (var column in new[] { "ManagedFileId", "PublicationJobId", "PublishedByUserId", "SourceArtifactId" })
            Assert.Equal($"CREATE INDEX \"IX_recording_kept_routes_{column}\" ON \"recording_kept_routes\" (\"{column}\")",
                CollapseSql(Assert.Single(after, row => row.Name == $"IX_recording_kept_routes_{column}").Sql));

        string[] adoptionObjects =
        [
            "legacy_media_adoptions", "IX_legacy_media_adoptions_CanonicalRecordingId", "IX_legacy_media_adoptions_ImportId",
            "IX_legacy_media_adoptions_ManagedFileId", "TR_legacy_media_provenance", "TR_legacy_media_immutable_update",
            "TR_legacy_media_immutable_delete", "TR_legacy_original_file", "TR_legacy_original_reference", "TR_legacy_original_reference_delete"
        ];
        Assert.DoesNotContain(before, row => adoptionObjects.Contains(row.Name));
        Assert.Equal(adoptionObjects.Order(StringComparer.Ordinal),
            after.Where(row => adoptionObjects.Contains(row.Name)).Select(row => row.Name).Order(StringComparer.Ordinal));

        const string storageIdentity = "managed_storage_identity";
        Assert.DoesNotContain(before, row => row.Name == storageIdentity);
        Assert.Single(after, row => row.Type == "table" && row.Name == storageIdentity);

        const string queueTable = "subsonic_play_queues";
        Assert.DoesNotContain(before, row => row.Name == queueTable);
        Assert.Single(after, row => row.Type == "table" && row.Name == queueTable);

        const string userDataTable = "provider_user_data";
        Assert.DoesNotContain(before, row => row.Name == userDataTable);
        Assert.Single(after, row => row.Type == "table" && row.Name == userDataTable);

        Assert.Equal(["\"IsSynthetic\" INTEGER NOT NULL DEFAULT 0"],
            TableColumns(after, "users").Except(TableColumns(before, "users")));
        Assert.Empty(TableColumns(before, "users").Except(TableColumns(after, "users")));
        Assert.Equal(["\"CacheIdentityJson\" TEXT NULL"],
            TableColumns(after, "downloaded_song_mappings").Except(TableColumns(before, "downloaded_song_mappings")));
        Assert.Empty(TableColumns(before, "downloaded_song_mappings").Except(TableColumns(after, "downloaded_song_mappings")));
        const string healthTable = "health_smoke_runs";
        Assert.DoesNotContain(before, row => row.Name == healthTable);
        Assert.Single(after, row => row.Type == "table" && row.Name == healthTable);
        Assert.Equal(["IX_health_smoke_runs_CreatedAt", "IX_health_smoke_runs_OwnerUserId"],
            after.Where(row => row.Type == "index" && row.Name.StartsWith("IX_health_smoke_runs_", StringComparison.Ordinal))
                .Select(row => row.Name).Order(StringComparer.Ordinal));

        string[] backupObjects = ["backup_artifacts", "IX_backup_artifacts_JobId", "IX_backup_artifacts_CreatedAt",
            "IX_household_backup_schedule", "TR_backup_schedule_owner_insert", "TR_backup_schedule_owner_update"];
        Assert.DoesNotContain(before, row => backupObjects.Contains(row.Name));
        Assert.Equal(backupObjects.Order(StringComparer.Ordinal), after.Where(row => backupObjects.Contains(row.Name))
            .Select(row => row.Name).Order(StringComparer.Ordinal));
        Assert.Contains("\"OwnerUserId\" TEXT NOT NULL", TableColumns(before, "job_schedules"));
        Assert.Contains("\"OwnerUserId\" TEXT NULL", TableColumns(after, "job_schedules"));

        string[] reportObjects = ["problem_reports", "IX_problem_reports_ActorUserId_RequestId", "IX_problem_reports_CreatedAt_Id",
            "IX_problem_reports_Status_CreatedAt_Id", "IX_problem_reports_SubjectUserId", "IX_problem_reports_StatusChangedByUserId"];
        Assert.DoesNotContain(before, row => reportObjects.Contains(row.Name));
        Assert.Equal(reportObjects.Order(StringComparer.Ordinal), after.Where(row => reportObjects.Contains(row.Name))
            .Select(row => row.Name).Order(StringComparer.Ordinal));
        var reportSql = CollapseSql(Assert.Single(after, row => row.Type == "table" && row.Name == "problem_reports").Sql);
        Assert.Equal(CollapseSql("""
            CREATE TABLE problem_reports (
                "Id" TEXT NOT NULL CONSTRAINT PK_problem_reports PRIMARY KEY,
                "ActorUserId" TEXT NOT NULL,
                "SubjectUserId" TEXT NULL,
                "RequestId" TEXT NOT NULL,
                "RequestFingerprint" TEXT NOT NULL,
                "Kind" TEXT NOT NULL,
                "Note" TEXT NOT NULL,
                "ReporterName" TEXT NOT NULL,
                "ContextJson" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "CreatedAt" INTEGER NOT NULL,
                "UpdatedAt" INTEGER NOT NULL,
                "StatusChangedByUserId" TEXT NULL,
                "Revision" INTEGER NOT NULL,
                CONSTRAINT CK_problem_report_status CHECK("Status" IN ('new','seen','fixed')),
                CONSTRAINT CK_problem_report_kind CHECK("Kind" IN ('playback','seek','wrong-match','wrong-lyrics','metadata','other')),
                CONSTRAINT CK_problem_report_bounds CHECK(length("Note") BETWEEN 1 AND 2000 AND length("ReporterName") BETWEEN 1 AND 200 AND length("RequestFingerprint")=64 AND length(CAST("ContextJson" AS BLOB))<=32768 AND "Revision">0),
                CONSTRAINT CK_problem_report_context CHECK(json_valid("ContextJson") AND coalesce(json_extract("ContextJson",'$.schemaVersion')=1,0)),
                CONSTRAINT FK_problem_reports_users_ActorUserId FOREIGN KEY("ActorUserId") REFERENCES users("Id") ON DELETE RESTRICT,
                CONSTRAINT FK_problem_reports_users_SubjectUserId FOREIGN KEY("SubjectUserId") REFERENCES users("Id") ON DELETE RESTRICT,
                CONSTRAINT FK_problem_reports_users_StatusChangedByUserId FOREIGN KEY("StatusChangedByUserId") REFERENCES users("Id") ON DELETE RESTRICT
            )
            """), reportSql);
        Assert.Contains("CONSTRAINT CK_problem_report_context CHECK(json_valid(\"ContextJson\") AND coalesce(json_extract(\"ContextJson\",'$.schemaVersion')=1,0))", reportSql);
        foreach (var ownerColumn in new[] { "ActorUserId", "SubjectUserId", "StatusChangedByUserId" })
            Assert.Contains($"CONSTRAINT FK_problem_reports_users_{ownerColumn} FOREIGN KEY(\"{ownerColumn}\") REFERENCES users(\"Id\") ON DELETE RESTRICT", reportSql);
        var reportIndexes = new Dictionary<string, string>
        {
            ["IX_problem_reports_ActorUserId_RequestId"] = "CREATE UNIQUE INDEX IX_problem_reports_ActorUserId_RequestId ON problem_reports(\"ActorUserId\",\"RequestId\")",
            ["IX_problem_reports_CreatedAt_Id"] = "CREATE INDEX IX_problem_reports_CreatedAt_Id ON problem_reports(\"CreatedAt\",\"Id\")",
            ["IX_problem_reports_Status_CreatedAt_Id"] = "CREATE INDEX IX_problem_reports_Status_CreatedAt_Id ON problem_reports(\"Status\",\"CreatedAt\",\"Id\")",
            ["IX_problem_reports_SubjectUserId"] = "CREATE INDEX IX_problem_reports_SubjectUserId ON problem_reports(\"SubjectUserId\")",
            ["IX_problem_reports_StatusChangedByUserId"] = "CREATE INDEX IX_problem_reports_StatusChangedByUserId ON problem_reports(\"StatusChangedByUserId\")"
        };
        foreach (var (name, sql) in reportIndexes)
            Assert.Equal(sql, CollapseSql(Assert.Single(after, row => row.Type == "index" && row.Name == name).Sql));

        var sharedFavoriteObjects = AssertSharedFavoriteSchema(before, after);
        string[] alertObjects =
        [
            "notification_channels", "IX_notification_channels_HealthScheduleId", "IX_notification_channels_OwnerUserId",
            "IX_notification_channels_DestinationReferenceId", "problem_incidents", "IX_problem_incidents_ScopeKey_Resource_Kind",
            "IX_problem_incidents_OwnerUserId", "notification_deliveries", "IX_notification_deliveries_IncidentId_Transition_ChannelId",
            "IX_notification_deliveries_JobId", "IX_notification_deliveries_ChannelId", "problem_alert_configuration"
        ];
        Assert.DoesNotContain(before, row => alertObjects.Contains(row.Name));
        Assert.Equal(alertObjects.Order(StringComparer.Ordinal), after.Where(row => alertObjects.Contains(row.Name))
            .Select(row => row.Name).Order(StringComparer.Ordinal));

        string[] promotionObjects =
        [
            "promotion_runs", "IX_promotion_runs_OwnerUserId_RequestKey", "IX_promotion_runs_JobId",
            "IX_promotion_runs_ManagedFileId", "promotion_steps"
        ];
        Assert.DoesNotContain(before, row => promotionObjects.Contains(row.Name));
        Assert.Equal(promotionObjects.Order(StringComparer.Ordinal), after.Where(row => promotionObjects.Contains(row.Name))
            .Select(row => row.Name).Order(StringComparer.Ordinal));

        string[] profileObjects = ["shared_tag_profiles", "taste_profiles"];
        Assert.DoesNotContain(before, row => profileObjects.Contains(row.Name));
        Assert.Equal(profileObjects.Order(StringComparer.Ordinal), after.Where(row => profileObjects.Contains(row.Name))
            .Select(row => row.Name).Order(StringComparer.Ordinal));
        Assert.Equal(NormalizeSchemaObject(new("table", "shared_tag_profiles", """
            CREATE TABLE "shared_tag_profiles" (
                "Key" TEXT NOT NULL CONSTRAINT "PK_shared_tag_profiles" PRIMARY KEY,
                "PayloadJson" TEXT NOT NULL,
                "RefreshedAt" INTEGER NOT NULL,
                "CapturedAt" INTEGER NOT NULL
            )
            """)), NormalizeSchemaObject(Assert.Single(after, row => row.Type == "table" && row.Name == "shared_tag_profiles")));
        Assert.Equal(NormalizeSchemaObject(new("table", "taste_profiles", """
            CREATE TABLE "taste_profiles" (
                "OwnerUserId" TEXT NOT NULL CONSTRAINT "PK_taste_profiles" PRIMARY KEY,
                "PayloadJson" TEXT NOT NULL,
                "Revision" INTEGER NOT NULL,
                CONSTRAINT "FK_taste_profiles_users_OwnerUserId" FOREIGN KEY ("OwnerUserId") REFERENCES "users" ("Id") ON DELETE CASCADE
            )
            """)), NormalizeSchemaObject(Assert.Single(after, row => row.Type == "table" && row.Name == "taste_profiles")));

        var oldSchema = before
            .Where(row => row.Name is not "job_attempts" and not "IX_job_attempts_JobId_AttemptNumber")
            .Select(row => row.Name == "runtime_settings" ? row with
            {
                Sql = row.Sql.Replace("'Playback:ShowExplicitLabel')", "'Playback:ShowExplicitLabel', 'Playback:StartPreference', 'Providers:LyricsOrder', 'Providers:UseModes')", StringComparison.Ordinal)
            } : row)
            .Select(row => row.Name == downloadArtifactIndex ? row with { Sql = currentArtifactIndexSql } : row)
            .Select(NormalizeSchemaObject)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var newSchema = after
            .Where(row => row.Name is not publicLinkIndex and not publicSnapshotIndex and not subjectIndex and not "IX_health_smoke_runs_CreatedAt" and not "IX_health_smoke_runs_OwnerUserId" && !keptObjects.Contains(row.Name) && !adoptionObjects.Contains(row.Name) && row.Name != storageIdentity && row.Name != queueTable && row.Name != userDataTable && row.Name != healthTable && !backupObjects.Contains(row.Name) && !reportObjects.Contains(row.Name) && !sharedFavoriteObjects.Contains(row.Name) && !alertObjects.Contains(row.Name) && !promotionObjects.Contains(row.Name) && !profileObjects.Contains(row.Name))
            .Select(NormalizeSchemaObject)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(oldSchema, newSchema);

        var publicIndexes = after
            .Where(row => row.Name is publicLinkIndex or publicSnapshotIndex)
            .Select(row => CollapseSql(row.Sql))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "CREATE UNIQUE INDEX \"IX_external_snapshot_public_version\" ON \"external_metadata_snapshots\" (\"OwnerUserId\", \"ProviderId\", \"ResourceKind\", \"ExternalIdHash\", \"SnapshotVersion\") WHERE \"ProviderAccountId\" IS NULL",
                "CREATE UNIQUE INDEX \"IX_playlist_link_public_source_target\" ON \"playlist_links\" (\"OwnerUserId\", \"SourceProviderId\", \"SourcePlaylistIdHash\", \"TargetProtocol\", \"TargetBackendInstanceId\") WHERE \"ProviderAccountId\" IS NULL"
            ],
            publicIndexes);
    }

    private static async Task<SchemaObject[]> Schema(AllstarrDbContext db)
    {
        const string sql = """
            SELECT type, name, coalesce(sql,'') FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EFMigrations%'
              AND tbl_name NOT IN ('provider_circuits','provider_health_rollups','provider_health_samples',
                'canonical_artists','canonical_catalog_aliases','canonical_recording_artists',
                'canonical_release_group_artists','canonical_release_groups','canonical_releases',
                'canonical_release_tracks','catalog_facts','recording_identifiers','canonical_recordings','recording_lyrics_choices')
            ORDER BY type,name
            """;
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenDone)
            await connection.OpenAsync();

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync();
            var schema = new List<SchemaObject>();
            while (await reader.ReadAsync())
                schema.Add(new SchemaObject(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return schema.ToArray();
        }
        finally
        {
            if (closeWhenDone)
                await connection.CloseAsync();
        }
    }

    private static async Task<string[]> Rows(AllstarrDbContext db)
    {
        var names = await db.Database.SqlQueryRaw<string>("""
            SELECT name AS Value FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'
              AND name NOT LIKE '__EFMigrations%' AND name NOT IN ('provider_circuits','provider_health_rollups','provider_health_samples',
                'canonical_artists','canonical_catalog_aliases','canonical_recording_artists',
                'canonical_release_group_artists','canonical_release_groups','canonical_releases',
                'canonical_release_tracks','catalog_facts','recording_identifiers','canonical_recordings','recording_lyrics_choices')
            ORDER BY name
            """).ToArrayAsync();
        var connection = db.Database.GetDbConnection();
        var closeWhenDone = connection.State != System.Data.ConnectionState.Open;
        if (closeWhenDone)
            await connection.OpenAsync();
        var rows = new List<string>();
        try
        {
            foreach (var name in names)
            {
                await using var command = connection.CreateCommand();
                command.CommandText = $"SELECT * FROM \"{name.Replace("\"", "\"\"")}\"";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var values = Enumerable.Range(0, reader.FieldCount)
                        .Where(index => !(name == "provider_accounts" && reader.GetName(index) == "SettingsJson") &&
                                        !(name == "playlist_links" && reader.GetName(index) == "DestinationMode") &&
                                        !(name == "users" && reader.GetName(index) == "IsSynthetic") &&
                                        !(name == "downloaded_song_mappings" && reader.GetName(index) == "CacheIdentityJson"))
                        .Select(index => $"{reader.GetName(index)}={CanonicalValue(reader.GetValue(index))}")
                        .Order(StringComparer.Ordinal)
                        .ToArray();
                    rows.Add(name + ":" + string.Join(",", values));
                }
            }
        }
        finally
        {
            if (closeWhenDone)
                await connection.CloseAsync();
        }
        return rows.Order(StringComparer.Ordinal).ToArray();
    }

    private static string NormalizeSchemaObject(SchemaObject row)
    {
        if (row.Type != "table")
            return $"{row.Type}|{row.Name}|{CollapseSql(row.Sql)}";

        var open = row.Sql.IndexOf('(');
        var close = row.Sql.LastIndexOf(')');
        if (open < 0 || close <= open)
            return $"{row.Type}|{row.Name}|{CollapseSql(row.Sql)}";

        var components = SplitTopLevel(row.Sql[(open + 1)..close])
            .Select(CollapseSql)
            .Where(component => !IsDeliberateNewColumn(row.Name, component))
            .Select(component => NormalizeNullability(row.Name, component))
            .Order(StringComparer.Ordinal);
        var normalizedSql = $"{CollapseSql(row.Sql[..open])}({string.Join(",", components)}){CollapseSql(row.Sql[(close + 1)..])}";
        return $"{row.Type}|{row.Name}|{normalizedSql}";
    }

    private static IEnumerable<string> TableColumns(SchemaObject[] schema, string table)
    {
        var sql = Assert.Single(schema, row => row.Type == "table" && row.Name == table).Sql;
        return SplitTopLevel(sql[(sql.IndexOf('(') + 1)..sql.LastIndexOf(')')]).Select(CollapseSql);
    }

    private static bool IsDeliberateNewColumn(string table, string component) =>
        (table == "favorite_events" && SharedFavoriteEventAdditions.Contains(component)) ||
        (table == "downloaded_song_mappings" && component == "\"CacheIdentityJson\" TEXT NULL") ||
        (table == "users" && component == "\"IsSynthetic\" INTEGER NOT NULL DEFAULT 0") ||
        (table == "provider_accounts" && component == "\"SettingsJson\" TEXT NOT NULL DEFAULT '{}'") ||
        (table == "playlist_links" && component is "\"DestinationMode\" TEXT NOT NULL DEFAULT 'Existing'" or "\"DestinationMode\" TEXT NOT NULL") ||
        (table == "playlist_target_memberships" && component == "\"RemovalConfirmedAt\" INTEGER NULL") ||
        (table == "durable_jobs" && component is "\"LeaseOwner\" TEXT NULL" or "\"LeaseExpiresAt\" INTEGER NULL" or
            "\"LastAttemptStartedAt\" INTEGER NULL" or "\"LastAttemptCompletedAt\" INTEGER NULL" or "\"LastAttemptOutcome\" TEXT NULL") ||
        (table == "provider_download_artifacts" && component is "\"QualityCheckJson\" TEXT NULL" or "\"SourceExternalTrackId\" TEXT NULL" or "\"SourceAccountRevision\" INTEGER NULL" or "\"SupersededAt\" INTEGER NULL") ||
        (table == "playback_delivery_checkpoints" && component is "\"AutomaticResendCount\" INTEGER NOT NULL DEFAULT 0" or "\"AttemptStartedAt\" INTEGER NULL" or
            "\"DestinationAccountId\" TEXT NULL" or "\"DestinationAccountRevision\" INTEGER NULL" or
            "\"DestinationJson\" TEXT NULL" or "\"ReplaySafe\" INTEGER NOT NULL DEFAULT 0" or
            "\"Revision\" INTEGER NOT NULL DEFAULT 0" or "\"SendCount\" INTEGER NOT NULL DEFAULT 0" or
            "\"SignalPayloadJson\" TEXT NULL" or "\"SubmissionJson\" TEXT NULL") ||
        (table == "favorite_states" && component == "\"DisplayMetadataJson\" TEXT NULL") ||
        (table == "listening_events" && component == "\"ResumePositionTicks\" INTEGER NULL") ||
        (table == "audit_events" && component is "\"SubjectId\" TEXT NULL" or "\"SubjectType\" TEXT NULL");

    private static readonly string[] SharedFavoriteEventAdditions =
    [
        "\"IsRealProviderIntent\" INTEGER NOT NULL DEFAULT 0", "\"SharedLifecycleVersion\" INTEGER NULL", "\"SharedLinkId\" TEXT NULL",
        "CONSTRAINT \"CK_favorite_event_shared_scope\" CHECK (((\"SharedLinkId\" IS NULL AND \"SharedLifecycleVersion\" IS NULL) OR (\"SharedLinkId\" IS NOT NULL AND \"SharedLifecycleVersion\" IS NOT NULL AND \"SharedLifecycleVersion\">=0 AND \"IsRealProviderIntent\"=1)))",
        "CONSTRAINT \"FK_favorite_events_provider_favorite_link_members_OwnerUserId_SharedLinkId\" FOREIGN KEY (\"OwnerUserId\", \"SharedLinkId\") REFERENCES \"provider_favorite_link_members\" (\"UserId\", \"LinkId\") ON DELETE RESTRICT"
    ];

    private static HashSet<string> AssertSharedFavoriteSchema(SchemaObject[] before, SchemaObject[] after)
    {
        var expected = new Dictionary<string, string>
        {
            ["IX_favorite_event_link_baseline"] = "CREATE INDEX \"IX_favorite_event_link_baseline\" ON \"favorite_events\" (\"OwnerUserId\", \"Protocol\", \"BackendInstanceId\", \"BackendPrincipalId\", \"SharedLinkId\", \"IsRealProviderIntent\", \"CreatedAt\", \"Id\")",
            ["IX_favorite_event_link_item"] = "CREATE INDEX \"IX_favorite_event_link_item\" ON \"favorite_events\" (\"OwnerUserId\", \"Protocol\", \"BackendInstanceId\", \"BackendPrincipalId\", \"ItemId\", \"CreatedAt\", \"Id\")",
            ["IX_favorite_events_OwnerUserId_SharedLinkId"] = "CREATE INDEX \"IX_favorite_events_OwnerUserId_SharedLinkId\" ON \"favorite_events\" (\"OwnerUserId\", \"SharedLinkId\")",
            ["IX_linked_provider_favorite_states_LastIntentEventId"] = "CREATE INDEX \"IX_linked_provider_favorite_states_LastIntentEventId\" ON \"linked_provider_favorite_states\" (\"LastIntentEventId\")",
            ["IX_provider_favorite_link_member_role"] = "CREATE UNIQUE INDEX \"IX_provider_favorite_link_member_role\" ON \"provider_favorite_link_members\" (\"LinkId\", \"Protocol\")",
            ["IX_provider_favorite_link_requests_JobId"] = "CREATE INDEX \"IX_provider_favorite_link_requests_JobId\" ON \"provider_favorite_link_requests\" (\"JobId\")",
            ["IX_provider_favorite_link_requests_LinkId"] = "CREATE INDEX \"IX_provider_favorite_link_requests_LinkId\" ON \"provider_favorite_link_requests\" (\"LinkId\")",
            ["IX_provider_favorite_link_created"] = "CREATE INDEX \"IX_provider_favorite_link_created\" ON \"provider_favorite_links\" (\"CreatedAt\", \"Id\")",
            ["IX_provider_favorite_link_pair"] = "CREATE UNIQUE INDEX \"IX_provider_favorite_link_pair\" ON \"provider_favorite_links\" (\"PairKey\")",
            ["IX_provider_favorite_links_ConfirmedByUserId"] = "CREATE INDEX \"IX_provider_favorite_links_ConfirmedByUserId\" ON \"provider_favorite_links\" (\"ConfirmedByUserId\")",
            ["IX_provider_favorite_links_UnionJobId"] = "CREATE INDEX \"IX_provider_favorite_links_UnionJobId\" ON \"provider_favorite_links\" (\"UnionJobId\")",
            ["TR_favorite_event_scope_immutable"] = """
                CREATE TRIGGER TR_favorite_event_scope_immutable BEFORE UPDATE OF "IsRealProviderIntent","SharedLinkId","SharedLifecycleVersion" ON favorite_events
                WHEN NEW."IsRealProviderIntent" IS NOT OLD."IsRealProviderIntent" OR NEW."SharedLinkId" IS NOT OLD."SharedLinkId" OR NEW."SharedLifecycleVersion" IS NOT OLD."SharedLifecycleVersion"
                BEGIN SELECT RAISE(ABORT,'CK_favorite_event_scope_immutable'); END
                """,
            ["TR_provider_favorite_link_member_immutable"] = "CREATE TRIGGER TR_provider_favorite_link_member_immutable BEFORE UPDATE ON provider_favorite_link_members BEGIN SELECT RAISE(ABORT,'CK_provider_favorite_link_member_immutable'); END"
        };
        foreach (var (name, sql) in expected)
        {
            Assert.DoesNotContain(before, item => item.Name == name);
            Assert.Equal(CollapseSql(sql), CollapseSql(Assert.Single(after, item => item.Name == name).Sql));
        }
        var columns = new Dictionary<string, string[]>
        {
            ["provider_favorite_links"] = ["Id TEXT NOT NULL CONSTRAINT PK_provider_favorite_links PRIMARY KEY", "PairKey TEXT NOT NULL", "ConfirmedByUserId TEXT NOT NULL", "ConfirmationVersion INTEGER NOT NULL", "CreatedAt INTEGER NOT NULL", "UnionJobId TEXT NOT NULL", "UnionAttempt INTEGER NOT NULL", "UnionAdministratorFingerprint TEXT NOT NULL", "Phase INTEGER NOT NULL", "CursorAt INTEGER NULL", "CursorId TEXT NULL", "ProcessedRows INTEGER NOT NULL", "SeededItems INTEGER NOT NULL", "CompletedAt INTEGER NULL", "Revision INTEGER NOT NULL"],
            ["provider_favorite_link_members"] = ["UserId TEXT NOT NULL CONSTRAINT PK_provider_favorite_link_members PRIMARY KEY", "LinkId TEXT NOT NULL", "Protocol TEXT NOT NULL", "BackendInstanceId TEXT NOT NULL", "BackendPrincipalId TEXT NOT NULL", "IdentityFingerprint TEXT NOT NULL", "ConfirmedDisplayName TEXT NOT NULL", "UpperAt INTEGER NULL", "UpperId TEXT NULL"],
            ["linked_provider_favorite_states"] = ["LinkId TEXT NOT NULL", "ProviderId TEXT COLLATE BINARY NOT NULL", "ResourceKind TEXT COLLATE BINARY NOT NULL", "ExternalId TEXT COLLATE BINARY NOT NULL", "IsFavorite INTEGER NOT NULL", "LifecycleVersion INTEGER NOT NULL", "Revision INTEGER NOT NULL", "EffectiveAt INTEGER NOT NULL", "LastIntentEventId TEXT NULL"],
            ["provider_favorite_link_requests"] = ["AdministratorUserId TEXT NOT NULL", "RequestId TEXT NOT NULL", "Operation TEXT NOT NULL", "RequestHash TEXT NOT NULL", "LinkId TEXT NOT NULL", "JobId TEXT NOT NULL", "Created INTEGER NOT NULL", "CreatedAt INTEGER NOT NULL"]
        };
        var constraints = new Dictionary<string, string[]>
        {
            ["provider_favorite_links"] = [
                "CONSTRAINT CK_favorite_link_progress CHECK (length(PairKey)=64 AND length(UnionAdministratorFingerprint)=64 AND ConfirmationVersion=1 AND UnionAttempt>0 AND Phase BETWEEN 0 AND 2 AND ProcessedRows>=0 AND SeededItems>=0 AND Revision>0 AND ((CursorAt IS NULL)=(CursorId IS NULL)))",
                "CONSTRAINT FK_provider_favorite_links_durable_jobs_UnionJobId FOREIGN KEY (UnionJobId) REFERENCES durable_jobs (Id) ON DELETE RESTRICT",
                "CONSTRAINT FK_provider_favorite_links_users_ConfirmedByUserId FOREIGN KEY (ConfirmedByUserId) REFERENCES users (Id) ON DELETE RESTRICT"],
            ["provider_favorite_link_members"] = ["CONSTRAINT AK_provider_favorite_link_members_UserId_LinkId UNIQUE (UserId, LinkId)",
                "CONSTRAINT CK_favorite_link_member CHECK (Protocol IN ('jellyfin','subsonic') AND length(BackendInstanceId) BETWEEN 1 AND 200 AND length(BackendPrincipalId) BETWEEN 1 AND 300 AND length(IdentityFingerprint)=64 AND length(ConfirmedDisplayName) BETWEEN 1 AND 200 AND ((UpperAt IS NULL)=(UpperId IS NULL)))",
                "CONSTRAINT FK_provider_favorite_link_members_provider_favorite_links_LinkId FOREIGN KEY (LinkId) REFERENCES provider_favorite_links (Id) ON DELETE RESTRICT",
                "CONSTRAINT FK_provider_favorite_link_members_users_UserId FOREIGN KEY (UserId) REFERENCES users (Id) ON DELETE RESTRICT"],
            ["linked_provider_favorite_states"] = ["CONSTRAINT PK_linked_provider_favorite_states PRIMARY KEY (LinkId, ProviderId, ResourceKind, ExternalId)",
                "CONSTRAINT CK_linked_provider_favorite_state CHECK (length(ProviderId) BETWEEN 1 AND 100 AND ResourceKind IN ('track','album','artist','playlist') AND length(ExternalId) BETWEEN 1 AND 500 AND LifecycleVersion>=0 AND Revision>0)",
                "CONSTRAINT FK_linked_provider_favorite_states_favorite_events_LastIntentEventId FOREIGN KEY (LastIntentEventId) REFERENCES favorite_events (Id) ON DELETE RESTRICT",
                "CONSTRAINT FK_linked_provider_favorite_states_provider_favorite_links_LinkId FOREIGN KEY (LinkId) REFERENCES provider_favorite_links (Id) ON DELETE RESTRICT"],
            ["provider_favorite_link_requests"] = ["CONSTRAINT PK_provider_favorite_link_requests PRIMARY KEY (AdministratorUserId, RequestId)",
                "CONSTRAINT CK_favorite_link_request CHECK (Operation IN ('create','retry') AND length(RequestHash)=64)",
                "CONSTRAINT FK_provider_favorite_link_requests_durable_jobs_JobId FOREIGN KEY (JobId) REFERENCES durable_jobs (Id) ON DELETE RESTRICT",
                "CONSTRAINT FK_provider_favorite_link_requests_provider_favorite_links_LinkId FOREIGN KEY (LinkId) REFERENCES provider_favorite_links (Id) ON DELETE RESTRICT",
                "CONSTRAINT FK_provider_favorite_link_requests_users_AdministratorUserId FOREIGN KEY (AdministratorUserId) REFERENCES users (Id) ON DELETE RESTRICT"]
        };
        foreach (var (table, expectedColumns) in columns)
        {
            Assert.DoesNotContain(before, item => item.Name == table);
            var parts = TableColumns(after, table).Select(value => value.Replace("\"", "", StringComparison.Ordinal)).ToArray();
            Assert.Equal(expectedColumns.Order(StringComparer.Ordinal), parts.Where(value => !value.StartsWith("CONSTRAINT ", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
            Assert.Equal(constraints[table].Order(StringComparer.Ordinal), parts.Where(value => value.StartsWith("CONSTRAINT ", StringComparison.Ordinal)).Order(StringComparer.Ordinal));
        }
        Assert.Equal(SharedFavoriteEventAdditions.Order(StringComparer.Ordinal), TableColumns(after, "favorite_events").Except(TableColumns(before, "favorite_events")).Order(StringComparer.Ordinal));
        Assert.Empty(TableColumns(before, "favorite_events").Except(TableColumns(after, "favorite_events")));
        Assert.Contains(TableColumns(after, "favorite_events"), part => part.StartsWith("CONSTRAINT \"FK_favorite_event_job_lineage\"", StringComparison.Ordinal));
        return expected.Keys.Concat(columns.Keys).ToHashSet(StringComparer.Ordinal);
    }

    private static string NormalizeNullability(string table, string component)
    {
        if (table == "job_schedules" && component == "\"OwnerUserId\" TEXT NULL")
            return "\"OwnerUserId\" TEXT NOT NULL";
        if ((table is "external_metadata_snapshots" or "playlist_links" or "playlist_source_snapshots") &&
            component.StartsWith("\"ProviderAccountId\" ", StringComparison.Ordinal))
            return component.Replace(" NOT NULL", string.Empty, StringComparison.Ordinal)
                .Replace(" NULL", string.Empty, StringComparison.Ordinal);
        return component;
    }

    private static string CollapseSql(string sql) =>
        string.Join(" ", sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string[] SplitTopLevel(string sql)
    {
        var components = new List<string>();
        var start = 0;
        var depth = 0;
        char quote = '\0';
        for (var index = 0; index < sql.Length; index++)
        {
            var current = sql[index];
            if (quote != '\0')
            {
                if (current == quote)
                {
                    if (index + 1 < sql.Length && sql[index + 1] == quote)
                        index++;
                    else
                        quote = '\0';
                }
                continue;
            }

            if (current is '\'' or '"' or '`')
            {
                quote = current;
                continue;
            }

            if (current == '(')
                depth++;
            else if (current == ')')
                depth--;
            else if (current == ',' && depth == 0)
            {
                components.Add(sql[start..index]);
                start = index + 1;
            }
        }

        components.Add(sql[start..]);
        return components.ToArray();
    }

    private static string CanonicalValue(object value) => value switch
    {
        DBNull => "<null>",
        byte[] bytes => Convert.ToHexString(bytes),
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty
    };

    private sealed record SchemaObject(string Type, string Name, string Sql);

    private static async Task<string> CreateBaselineBackup(AllstarrDbContext db, SecretStoreOptions keys, string root)
    {
        var folder = Path.Combine(root, "old-backup");
        Directory.CreateDirectory(folder);
        var snapshot = Path.Combine(folder, "allstarr.db");
        await db.Database.ExecuteSqlInterpolatedAsync($"VACUUM INTO {snapshot}");
        var copiedKey = Path.Combine(folder, "keyring.json");
        File.Copy(keys.KeyRingPath, copiedKey);
        var manifest = new DurableBackupService.BackupManifest(1, Guid.NewGuid(), "fixture", Baseline, DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["allstarr.db"] = await DurableBackupService.HashAsync(snapshot, default),
                ["keyring.json"] = await DurableBackupService.HashAsync(copiedKey, default)
            });
        await File.WriteAllTextAsync(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var archive = Path.Combine(root, "baseline-backup.zip");
        ZipFile.CreateFromDirectory(folder, archive);
        return archive;
    }
}
