using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Phase4LibraryMatchingPlaylistFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
            var textType = postgres ? "text" : "TEXT";
            var guidType = postgres ? "uuid" : "TEXT";
            var integerType = postgres ? "integer" : "INTEGER";
            var bigintType = postgres ? "bigint" : "INTEGER";
            var booleanType = postgres ? "boolean" : "INTEGER";
            var doubleType = postgres ? "double precision" : "REAL";
            migrationBuilder.CreateTable(
                name: "external_metadata_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guidType, nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: guidType, nullable: false),
                    ProviderTrackIdentityId = table.Column<Guid>(type: guidType, nullable: true),
                    SourceJobId = table.Column<Guid>(type: guidType, nullable: true),
                    LibraryScopeId = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    BackendInstanceId = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    BackendPrincipalId = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    Protocol = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    ProviderId = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    ResourceKind = table.Column<string>(type: textType, maxLength: 50, nullable: false),
                    ExternalIdHash = table.Column<string>(type: textType, maxLength: 64, nullable: false),
                    SnapshotVersion = table.Column<int>(type: integerType, nullable: false),
                    ProviderRevision = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    PayloadJson = table.Column<string>(type: textType, nullable: false),
                    PayloadSha256 = table.Column<string>(type: textType, maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    RetrievedAt = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_metadata_snapshots", x => x.Id);
                    table.UniqueConstraint("AK_external_metadata_snapshots_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_external_snapshots_external_hash", "length(\"ExternalIdHash\") = 64");
                    table.CheckConstraint("CK_external_snapshots_payload_hash", "length(\"PayloadSha256\") = 64");
                    table.CheckConstraint("CK_external_snapshots_version", "\"SnapshotVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_ExternalMetadataSnapshot_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_external_metadata_snapshots_durable_jobs_SourceJobId",
                        column: x => x.SourceJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_external_snapshot_provider_account",
                        columns: x => new { x.ProviderAccountId, x.ProviderId },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "Id", "ProviderId" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_external_snapshot_provider_identity",
                        column: x => x.ProviderTrackIdentityId,
                        principalTable: "provider_track_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "job_schedules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guidType, nullable: false),
                    LibraryScopeId = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    JobType = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    CronExpression = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    TimeZoneId = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    OverlapPolicy = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    MisfirePolicy = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    RetryPolicyJson = table.Column<string>(type: textType, nullable: false),
                    NextRunAt = table.Column<long>(type: bigintType, nullable: true),
                    Enabled = table.Column<bool>(type: booleanType, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_schedules", x => x.Id);
                    table.UniqueConstraint("AK_job_schedules_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.ForeignKey(
                        name: "FK_JobSchedule_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "library_tracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guidType, nullable: false),
                    BackendIdentityId = table.Column<Guid>(type: guidType, nullable: false),
                    CanonicalRecordingId = table.Column<Guid>(type: guidType, nullable: true),
                    LibraryScopeId = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    Protocol = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    BackendInstanceId = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    BackendItemId = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                    FilePath = table.Column<string>(type: textType, maxLength: 2000, nullable: false),
                    Title = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                    Artist = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                    Album = table.Column<string>(type: textType, maxLength: 500, nullable: true),
                    AlbumArtist = table.Column<string>(type: textType, maxLength: 500, nullable: true),
                    DurationMilliseconds = table.Column<long>(type: bigintType, nullable: false),
                    Isrc = table.Column<string>(type: textType, maxLength: 32, nullable: true),
                    MusicBrainzRecordingId = table.Column<string>(type: textType, maxLength: 100, nullable: true),
                    MusicBrainzReleaseId = table.Column<string>(type: textType, maxLength: 100, nullable: true),
                    MusicBrainzArtistId = table.Column<string>(type: textType, maxLength: 100, nullable: true),
                    ProviderIdsJson = table.Column<string>(type: textType, nullable: false),
                    CoverArtReference = table.Column<string>(type: textType, maxLength: 1000, nullable: true),
                    AcceptedDecisionVersion = table.Column<int>(type: integerType, nullable: true),
                    IndexedAt = table.Column<long>(type: bigintType, nullable: false),
                    SourceModifiedAt = table.Column<long>(type: bigintType, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_library_tracks", x => x.Id);
                    table.UniqueConstraint("AK_library_tracks_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_library_tracks_decision_version", "\"AcceptedDecisionVersion\" IS NULL OR \"AcceptedDecisionVersion\" > 0");
                    table.CheckConstraint("CK_library_tracks_duration", "\"DurationMilliseconds\" >= 0");
                    table.CheckConstraint("CK_library_tracks_stable_artwork", "\"CoverArtReference\" IS NULL OR \"CoverArtReference\" NOT LIKE '%://%'");
                    table.ForeignKey(
                        name: "FK_LibraryTrack_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_library_tracks_backend_identities_BackendIdentityId",
                        column: x => x.BackendIdentityId,
                        principalTable: "backend_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_library_track_canonical_recording",
                        columns: x => new { x.TenantId, x.CanonicalRecordingId },
                        principalTable: "canonical_recordings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guidType, nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: guidType, nullable: false),
                    ScheduleId = table.Column<Guid>(type: guidType, nullable: true),
                    LibraryScopeId = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    SourceProviderId = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    SourcePlaylistId = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                    SourcePlaylistIdHash = table.Column<string>(type: textType, maxLength: 64, nullable: false),
                    TargetProtocol = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    TargetBackendInstanceId = table.Column<string>(type: textType, maxLength: 200, nullable: false),
                    TargetPlaylistId = table.Column<string>(type: textType, maxLength: 500, nullable: true),
                    Mode = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    MaterializationMode = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    MirrorStaleEntries = table.Column<bool>(type: booleanType, nullable: false),
                    PreserveManualEntries = table.Column<bool>(type: booleanType, nullable: false),
                    SyncName = table.Column<bool>(type: booleanType, nullable: false),
                    SyncDescription = table.Column<bool>(type: booleanType, nullable: false),
                    SyncArtwork = table.Column<bool>(type: booleanType, nullable: false),
                    RuleVersion = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    PolicyVersion = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_links", x => x.Id);
                    table.UniqueConstraint("AK_playlist_links_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_playlist_links_source_hash", "length(\"SourcePlaylistIdHash\") = 64");
                    table.ForeignKey(
                        name: "FK_PlaylistLink_JobSchedule",
                        columns: x => new { x.TenantId, x.ScheduleId },
                        principalTable: "job_schedules",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistLink_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_playlist_link_provider_account",
                        columns: x => new { x.ProviderAccountId, x.SourceProviderId },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "Id", "ProviderId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "manual_track_overrides",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guidType, nullable: false),
                    ExternalSnapshotId = table.Column<Guid>(type: guidType, nullable: false),
                    LibraryTrackId = table.Column<Guid>(type: guidType, nullable: true),
                    LibraryScopeId = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    Decision = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: textType, maxLength: 1000, nullable: false),
                    DecisionVersion = table.Column<int>(type: integerType, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    RevokedAt = table.Column<long>(type: bigintType, nullable: true),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_track_overrides", x => x.Id);
                    table.CheckConstraint("CK_manual_overrides_shape", "(\"Decision\" = 'Pin' AND \"LibraryTrackId\" IS NOT NULL) OR (\"Decision\" = 'Reject' AND \"LibraryTrackId\" IS NULL)");
                    table.CheckConstraint("CK_manual_overrides_version", "\"DecisionVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_ManualTrackOverride_ExternalMetadataSnapshot",
                        columns: x => new { x.TenantId, x.ExternalSnapshotId },
                        principalTable: "external_metadata_snapshots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManualTrackOverride_LibraryTrack",
                        columns: x => new { x.TenantId, x.LibraryTrackId },
                        principalTable: "library_tracks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ManualTrackOverride_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "track_matches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guidType, nullable: false),
                    ExternalSnapshotId = table.Column<Guid>(type: guidType, nullable: false),
                    LibraryTrackId = table.Column<Guid>(type: guidType, nullable: true),
                    CanonicalRecordingId = table.Column<Guid>(type: guidType, nullable: true),
                    LibraryScopeId = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    State = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    Confidence = table.Column<double>(type: doubleType, nullable: false),
                    Threshold = table.Column<double>(type: doubleType, nullable: false),
                    DecisionVersion = table.Column<int>(type: integerType, nullable: false),
                    PolicyVersion = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    CandidateResultsJson = table.Column<string>(type: textType, nullable: false),
                    ReasonsJson = table.Column<string>(type: textType, nullable: false),
                    WarningsJson = table.Column<string>(type: textType, nullable: false),
                    CorrelationId = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    DecidedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_track_matches", x => x.Id);
                    table.UniqueConstraint("AK_track_matches_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_track_matches_confidence", "\"Confidence\" >= 0 AND \"Confidence\" <= 1 AND \"Threshold\" >= 0 AND \"Threshold\" <= 1");
                    table.CheckConstraint("CK_track_matches_selected_shape", "(\"State\" IN ('Accepted', 'Pinned') AND \"LibraryTrackId\" IS NOT NULL) OR (\"State\" IN ('Unresolved', 'Suggested', 'Rejected', 'Ambiguous') AND \"LibraryTrackId\" IS NULL)");
                    table.CheckConstraint("CK_track_matches_version", "\"DecisionVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_TrackMatch_Canonicaling",
                        columns: x => new { x.TenantId, x.CanonicalRecordingId },
                        principalTable: "canonical_recordings",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrackMatch_ExternalMetadataSnapshot",
                        columns: x => new { x.TenantId, x.ExternalSnapshotId },
                        principalTable: "external_metadata_snapshots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrackMatch_LibraryTrack",
                        columns: x => new { x.TenantId, x.LibraryTrackId },
                        principalTable: "library_tracks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TrackMatch_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_source_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guidType, nullable: false),
                    PlaylistLinkId = table.Column<Guid>(type: guidType, nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: guidType, nullable: false),
                    SourceJobId = table.Column<Guid>(type: guidType, nullable: true),
                    SnapshotVersion = table.Column<int>(type: integerType, nullable: false),
                    ProviderRevision = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    ETag = table.Column<string>(type: textType, maxLength: 500, nullable: true),
                    Name = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: textType, maxLength: 4000, nullable: true),
                    ArtworkReferenceKey = table.Column<string>(type: textType, maxLength: 1000, nullable: true),
                    PayloadSha256 = table.Column<string>(type: textType, maxLength: 64, nullable: false),
                    CorrelationId = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    RetrievedAt = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_source_snapshots", x => x.Id);
                    table.UniqueConstraint("AK_playlist_source_snapshots_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_playlist_snapshots_payload_hash", "length(\"PayloadSha256\") = 64");
                    table.CheckConstraint("CK_playlist_snapshots_stable_artwork", "\"ArtworkReferenceKey\" IS NULL OR \"ArtworkReferenceKey\" NOT LIKE '%://%'");
                    table.CheckConstraint("CK_playlist_snapshots_version", "\"SnapshotVersion\" > 0");
                    table.ForeignKey(
                        name: "FK_PlaylistSourceSnapshot_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSourceSnapshot_PlaylistLink",
                        columns: x => new { x.TenantId, x.PlaylistLinkId },
                        principalTable: "playlist_links",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_playlist_snapshot_provider_account",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_playlist_source_snapshots_durable_jobs_SourceJobId",
                        column: x => x.SourceJobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_source_entries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    PlaylistSourceSnapshotId = table.Column<Guid>(type: guidType, nullable: false),
                    ExternalMetadataSnapshotId = table.Column<Guid>(type: guidType, nullable: false),
                    SourcePosition = table.Column<int>(type: integerType, nullable: false),
                    SourceEntryIdHash = table.Column<string>(type: textType, maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_source_entries", x => x.Id);
                    table.UniqueConstraint("AK_playlist_source_entries_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_playlist_source_entry_hash", "length(\"SourceEntryIdHash\") = 64");
                    table.CheckConstraint("CK_playlist_source_entry_position", "\"SourcePosition\" >= 0");
                    table.ForeignKey(
                        name: "FK_PlaylistSourceEntry_ExternalMetadataSnapshot",
                        columns: x => new { x.TenantId, x.ExternalMetadataSnapshotId },
                        principalTable: "external_metadata_snapshots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSourceEntry_PlaylistSourceSnapshot",
                        columns: x => new { x.TenantId, x.PlaylistSourceSnapshotId },
                        principalTable: "playlist_source_snapshots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_sync_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    OwnerUserId = table.Column<Guid>(type: guidType, nullable: false),
                    PlaylistLinkId = table.Column<Guid>(type: guidType, nullable: false),
                    PlaylistSourceSnapshotId = table.Column<Guid>(type: guidType, nullable: false),
                    ScheduleId = table.Column<Guid>(type: guidType, nullable: true),
                    JobId = table.Column<Guid>(type: guidType, nullable: true),
                    Generation = table.Column<long>(type: bigintType, nullable: false),
                    IdempotencyKey = table.Column<string>(type: textType, maxLength: 300, nullable: false),
                    RuleVersion = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    MaterializationMode = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    State = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    TargetRevisionBefore = table.Column<string>(type: textType, maxLength: 500, nullable: true),
                    TargetRevisionAfter = table.Column<string>(type: textType, maxLength: 500, nullable: true),
                    ConflictCode = table.Column<string>(type: textType, maxLength: 100, nullable: true),
                    StartedAt = table.Column<long>(type: bigintType, nullable: false),
                    CompletedAt = table.Column<long>(type: bigintType, nullable: true),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_sync_runs", x => x.Id);
                    table.UniqueConstraint("AK_playlist_sync_runs_TenantId_Id", x => new { x.TenantId, x.Id });
                    table.CheckConstraint("CK_playlist_sync_generation", "\"Generation\" > 0");
                    table.ForeignKey(
                        name: "FK_PlaylistSyncRun_JobSchedule",
                        columns: x => new { x.TenantId, x.ScheduleId },
                        principalTable: "job_schedules",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncRun_PlatformUser",
                        columns: x => new { x.TenantId, x.OwnerUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncRun_PlaylistLink",
                        columns: x => new { x.TenantId, x.PlaylistLinkId },
                        principalTable: "playlist_links",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncRun_PlaylistSourceSnapshot",
                        columns: x => new { x.TenantId, x.PlaylistSourceSnapshotId },
                        principalTable: "playlist_source_snapshots",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_playlist_sync_runs_durable_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "durable_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_sync_entry_results",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    PlaylistSyncRunId = table.Column<Guid>(type: guidType, nullable: false),
                    PlaylistSourceEntryId = table.Column<Guid>(type: guidType, nullable: false),
                    TrackMatchId = table.Column<Guid>(type: guidType, nullable: true),
                    LibraryTrackId = table.Column<Guid>(type: guidType, nullable: true),
                    SourcePosition = table.Column<int>(type: integerType, nullable: false),
                    TargetPosition = table.Column<int>(type: integerType, nullable: true),
                    Outcome = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    OutcomeCode = table.Column<string>(type: textType, maxLength: 100, nullable: true),
                    DetailsJson = table.Column<string>(type: textType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_sync_entry_results", x => x.Id);
                    table.CheckConstraint("CK_playlist_result_positions", "\"SourcePosition\" >= 0 AND (\"TargetPosition\" IS NULL OR \"TargetPosition\" >= 0)");
                    table.ForeignKey(
                        name: "FK_PlaylistSyncEntryResult_LibraryTrack",
                        columns: x => new { x.TenantId, x.LibraryTrackId },
                        principalTable: "library_tracks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncEntryResult_PlaylistSourceEntry",
                        columns: x => new { x.TenantId, x.PlaylistSourceEntryId },
                        principalTable: "playlist_source_entries",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncEntryResult_PlaylistSyncRun",
                        columns: x => new { x.TenantId, x.PlaylistSyncRunId },
                        principalTable: "playlist_sync_runs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistSyncEntryResult_TrackMatch",
                        columns: x => new { x.TenantId, x.TrackMatchId },
                        principalTable: "track_matches",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "playlist_target_memberships",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: false),
                    PlaylistLinkId = table.Column<Guid>(type: guidType, nullable: false),
                    LibraryTrackId = table.Column<Guid>(type: guidType, nullable: false),
                    CreatedBySyncRunId = table.Column<Guid>(type: guidType, nullable: false),
                    TargetEntryId = table.Column<string>(type: textType, maxLength: 500, nullable: false),
                    LastKnownPosition = table.Column<int>(type: integerType, nullable: false),
                    Active = table.Column<bool>(type: booleanType, nullable: false),
                    CreatedAt = table.Column<long>(type: bigintType, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_playlist_target_memberships", x => x.Id);
                    table.CheckConstraint("CK_playlist_membership_position", "\"LastKnownPosition\" >= 0");
                    table.ForeignKey(
                        name: "FK_PlaylistTargetMembership_LibraryTrack",
                        columns: x => new { x.TenantId, x.LibraryTrackId },
                        principalTable: "library_tracks",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistTargetMembership_PlaylistLink",
                        columns: x => new { x.TenantId, x.PlaylistLinkId },
                        principalTable: "playlist_links",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PlaylistTargetMembership_PlaylistSyncRun",
                        columns: x => new { x.TenantId, x.CreatedBySyncRunId },
                        principalTable: "playlist_sync_runs",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_external_metadata_snapshots_ProviderAccountId_ProviderId",
                table: "external_metadata_snapshots",
                columns: new[] { "ProviderAccountId", "ProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_external_metadata_snapshots_ProviderTrackIdentityId",
                table: "external_metadata_snapshots",
                column: "ProviderTrackIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_external_metadata_snapshots_SourceJobId",
                table: "external_metadata_snapshots",
                column: "SourceJobId");

            migrationBuilder.CreateIndex(
                name: "IX_external_metadata_snapshots_TenantId_OwnerUserId",
                table: "external_metadata_snapshots",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_external_snapshot_version",
                table: "external_metadata_snapshots",
                columns: new[] { "TenantId", "ProviderAccountId", "ResourceKind", "ExternalIdHash", "SnapshotVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_job_schedules_Enabled_NextRunAt",
                table: "job_schedules",
                columns: new[] { "Enabled", "NextRunAt" });

            migrationBuilder.CreateIndex(
                name: "IX_job_schedules_TenantId_OwnerUserId",
                table: "job_schedules",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_library_track_backend_item",
                table: "library_tracks",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "BackendInstanceId", "BackendItemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_library_track_scoped_isrc",
                table: "library_tracks",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "Isrc" });

            migrationBuilder.CreateIndex(
                name: "IX_library_track_scoped_musicbrainz",
                table: "library_tracks",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "MusicBrainzRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_library_tracks_BackendIdentityId",
                table: "library_tracks",
                column: "BackendIdentityId");

            migrationBuilder.CreateIndex(
                name: "IX_library_tracks_TenantId_CanonicalRecordingId",
                table: "library_tracks",
                columns: new[] { "TenantId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_manual_track_override_active",
                table: "manual_track_overrides",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "ExternalSnapshotId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_manual_track_overrides_TenantId_ExternalSnapshotId",
                table: "manual_track_overrides",
                columns: new[] { "TenantId", "ExternalSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_manual_track_overrides_TenantId_LibraryTrackId",
                table: "manual_track_overrides",
                columns: new[] { "TenantId", "LibraryTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_link_source_target",
                table: "playlist_links",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "SourceProviderId", "ProviderAccountId", "SourcePlaylistIdHash", "TargetProtocol", "TargetBackendInstanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_links_ProviderAccountId_SourceProviderId",
                table: "playlist_links",
                columns: new[] { "ProviderAccountId", "SourceProviderId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_links_TenantId_ScheduleId",
                table: "playlist_links",
                columns: new[] { "TenantId", "ScheduleId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_entries_TenantId_ExternalMetadataSnapshotId",
                table: "playlist_source_entries",
                columns: new[] { "TenantId", "ExternalMetadataSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_entry_position",
                table: "playlist_source_entries",
                columns: new[] { "TenantId", "PlaylistSourceSnapshotId", "SourcePosition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_snapshot_version",
                table: "playlist_source_snapshots",
                columns: new[] { "TenantId", "PlaylistLinkId", "SnapshotVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_snapshots_ProviderAccountId",
                table: "playlist_source_snapshots",
                column: "ProviderAccountId");

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_snapshots_SourceJobId",
                table: "playlist_source_snapshots",
                column: "SourceJobId");

            migrationBuilder.CreateIndex(
                name: "IX_playlist_source_snapshots_TenantId_OwnerUserId",
                table: "playlist_source_snapshots",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_result_run_position",
                table: "playlist_sync_entry_results",
                columns: new[] { "TenantId", "PlaylistSyncRunId", "SourcePosition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_entry_results_TenantId_LibraryTrackId",
                table: "playlist_sync_entry_results",
                columns: new[] { "TenantId", "LibraryTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_entry_results_TenantId_PlaylistSourceEntryId",
                table: "playlist_sync_entry_results",
                columns: new[] { "TenantId", "PlaylistSourceEntryId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_entry_results_TenantId_TrackMatchId",
                table: "playlist_sync_entry_results",
                columns: new[] { "TenantId", "TrackMatchId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_runs_JobId",
                table: "playlist_sync_runs",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_runs_TenantId_OwnerUserId",
                table: "playlist_sync_runs",
                columns: new[] { "TenantId", "OwnerUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_runs_TenantId_PlaylistLinkId_IdempotencyKey",
                table: "playlist_sync_runs",
                columns: new[] { "TenantId", "PlaylistLinkId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_runs_TenantId_PlaylistSourceSnapshotId",
                table: "playlist_sync_runs",
                columns: new[] { "TenantId", "PlaylistSourceSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_sync_runs_TenantId_ScheduleId",
                table: "playlist_sync_runs",
                columns: new[] { "TenantId", "ScheduleId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_membership_target_entry",
                table: "playlist_target_memberships",
                columns: new[] { "TenantId", "PlaylistLinkId", "TargetEntryId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_playlist_membership_track_active",
                table: "playlist_target_memberships",
                columns: new[] { "TenantId", "PlaylistLinkId", "LibraryTrackId", "Active" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_target_memberships_TenantId_CreatedBySyncRunId",
                table: "playlist_target_memberships",
                columns: new[] { "TenantId", "CreatedBySyncRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_playlist_target_memberships_TenantId_LibraryTrackId",
                table: "playlist_target_memberships",
                columns: new[] { "TenantId", "LibraryTrackId" });

            migrationBuilder.CreateIndex(
                name: "IX_track_match_scoped_decision",
                table: "track_matches",
                columns: new[] { "TenantId", "OwnerUserId", "LibraryScopeId", "ExternalSnapshotId", "DecisionVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_track_matches_TenantId_CanonicalRecordingId",
                table: "track_matches",
                columns: new[] { "TenantId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_track_matches_TenantId_ExternalSnapshotId",
                table: "track_matches",
                columns: new[] { "TenantId", "ExternalSnapshotId" });

            migrationBuilder.CreateIndex(
                name: "IX_track_matches_TenantId_LibraryTrackId",
                table: "track_matches",
                columns: new[] { "TenantId", "LibraryTrackId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "manual_track_overrides");

            migrationBuilder.DropTable(
                name: "playlist_sync_entry_results");

            migrationBuilder.DropTable(
                name: "playlist_target_memberships");

            migrationBuilder.DropTable(
                name: "playlist_source_entries");

            migrationBuilder.DropTable(
                name: "track_matches");

            migrationBuilder.DropTable(
                name: "playlist_sync_runs");

            migrationBuilder.DropTable(
                name: "external_metadata_snapshots");

            migrationBuilder.DropTable(
                name: "library_tracks");

            migrationBuilder.DropTable(
                name: "playlist_source_snapshots");

            migrationBuilder.DropTable(
                name: "playlist_links");

            migrationBuilder.DropTable(
                name: "job_schedules");
        }
    }
}
