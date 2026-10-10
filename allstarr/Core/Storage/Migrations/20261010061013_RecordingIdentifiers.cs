using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RecordingIdentifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "MergedIntoId",
                table: "canonical_recordings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PublicId",
                table: "canonical_recordings",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql("""
                UPDATE canonical_recordings
                SET PublicId = 'ext-allstarr-song-' || lower(Id)
                WHERE PublicId IS NULL OR PublicId = '';
                """);

            migrationBuilder.CreateTable(
                name: "recording_identifiers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Value = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recording_identifiers", x => x.Id);
                    table.ForeignKey(
                        name: "FK_recording_identifiers_canonical_recordings_RecordingId",
                        column: x => x.RecordingId,
                        principalTable: "canonical_recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO recording_identifiers (Id, RecordingId, Kind, Value, Source, CreatedAt)
                SELECT
                  lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2)
                    || '-' || substr('89ab', 1 + abs(random()) % 4, 1) || substr(hex(randomblob(2)), 2)
                    || '-' || hex(randomblob(6))),
                  Id, 'isrc', Isrc, 'migration', CreatedAt
                FROM canonical_recordings
                WHERE Isrc IS NOT NULL AND length(trim(Isrc)) > 0;

                INSERT INTO recording_identifiers (Id, RecordingId, Kind, Value, Source, CreatedAt)
                SELECT
                  lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2)
                    || '-' || substr('89ab', 1 + abs(random()) % 4, 1) || substr(hex(randomblob(2)), 2)
                    || '-' || hex(randomblob(6))),
                  Id, 'musicbrainz', MusicBrainzRecordingId, 'migration', CreatedAt
                FROM canonical_recordings
                WHERE MusicBrainzRecordingId IS NOT NULL AND length(trim(MusicBrainzRecordingId)) > 0;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recordings_MergedIntoId",
                table: "canonical_recordings",
                column: "MergedIntoId");

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recordings_PublicId",
                table: "canonical_recordings",
                column: "PublicId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recording_identifiers_Kind_Value",
                table: "recording_identifiers",
                columns: new[] { "Kind", "Value" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recording_identifiers_RecordingId_Kind",
                table: "recording_identifiers",
                columns: new[] { "RecordingId", "Kind" });

            migrationBuilder.AddForeignKey(
                name: "FK_canonical_recordings_canonical_recordings_MergedIntoId",
                table: "canonical_recordings",
                column: "MergedIntoId",
                principalTable: "canonical_recordings",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.DropIndex(
                name: "IX_canonical_recordings_Isrc",
                table: "canonical_recordings");

            migrationBuilder.DropIndex(
                name: "IX_canonical_recordings_MusicBrainzRecordingId",
                table: "canonical_recordings");

            migrationBuilder.DropColumn(
                name: "Isrc",
                table: "canonical_recordings");

            migrationBuilder.DropColumn(
                name: "MusicBrainzRecordingId",
                table: "canonical_recordings");

            migrationBuilder.DropTable(
                name: "canonical_catalog_aliases");

            migrationBuilder.DropTable(
                name: "canonical_recording_artists");

            migrationBuilder.DropTable(
                name: "canonical_release_group_artists");

            migrationBuilder.DropTable(
                name: "canonical_release_tracks");

            migrationBuilder.DropTable(
                name: "catalog_facts");

            migrationBuilder.DropTable(
                name: "canonical_artists");

            migrationBuilder.DropTable(
                name: "canonical_releases");

            migrationBuilder.DropTable(
                name: "canonical_release_groups");

            migrationBuilder.Sql("""
                UPDATE durable_jobs
                SET State = 'Cancelled',
                    CompletedAt = UpdatedAt,
                    CancellationRequestedAt = UpdatedAt,
                    LastErrorCode = 'catalog-removed',
                    LastErrorMessage = 'MusicBrainz catalog crawl was removed.',
                    Revision = Revision + 1
                WHERE Type LIKE 'catalog.musicbrainz.%'
                  AND State IN ('Pending', 'Running', 'RetryScheduled');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_canonical_recordings_canonical_recordings_MergedIntoId",
                table: "canonical_recordings");

            migrationBuilder.DropTable(
                name: "recording_identifiers");

            migrationBuilder.DropIndex(
                name: "IX_canonical_recordings_MergedIntoId",
                table: "canonical_recordings");

            migrationBuilder.DropIndex(
                name: "IX_canonical_recordings_PublicId",
                table: "canonical_recordings");

            migrationBuilder.DropColumn(
                name: "MergedIntoId",
                table: "canonical_recordings");

            migrationBuilder.DropColumn(
                name: "PublicId",
                table: "canonical_recordings");

            migrationBuilder.AddColumn<string>(
                name: "Isrc",
                table: "canonical_recordings",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MusicBrainzRecordingId",
                table: "canonical_recordings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "canonical_artists",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Disambiguation = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsProvisional = table.Column<bool>(type: "INTEGER", nullable: false),
                    MusicBrainzArtistId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    SortName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_artists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "canonical_catalog_aliases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalEntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    EntityKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    ExternalId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    ExternalIdHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    LastSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Namespace = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_catalog_aliases", x => x.Id);
                    table.CheckConstraint("CK_canonical_catalog_alias_hash", "length(\"ExternalIdHash\") = 64");
                });

            migrationBuilder.CreateTable(
                name: "canonical_release_groups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FirstReleaseDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    IsProvisional = table.Column<bool>(type: "INTEGER", nullable: false),
                    MusicBrainzReleaseGroupId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    PrimaryType = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    SecondaryTypesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_release_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "catalog_facts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalEntityId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Confidence = table.Column<double>(type: "REAL", nullable: false),
                    EntityKind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FieldName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PayloadSha256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RefreshAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SourceRevision = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    SupersededAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ValueJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_catalog_facts", x => x.Id);
                    table.CheckConstraint("CK_catalog_fact_confidence", "\"Confidence\" >= 0 AND \"Confidence\" <= 1");
                    table.CheckConstraint("CK_catalog_fact_payload_hash", "length(\"PayloadSha256\") = 64");
                });

            migrationBuilder.CreateTable(
                name: "canonical_recording_artists",
                columns: table => new
                {
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    CanonicalArtistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreditName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    JoinPhrase = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_recording_artists", x => new { x.CanonicalRecordingId, x.Position });
                    table.CheckConstraint("CK_canonical_recording_artist_position", "\"Position\" >= 0");
                    table.ForeignKey(
                        name: "FK_canonical_recording_artists_canonical_artists_CanonicalArtistId",
                        column: x => x.CanonicalArtistId,
                        principalTable: "canonical_artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_canonical_recording_artists_canonical_recordings_CanonicalRecordingId",
                        column: x => x.CanonicalRecordingId,
                        principalTable: "canonical_recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "canonical_release_group_artists",
                columns: table => new
                {
                    CanonicalReleaseGroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Position = table.Column<int>(type: "INTEGER", nullable: false),
                    CanonicalArtistId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreditName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    JoinPhrase = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_release_group_artists", x => new { x.CanonicalReleaseGroupId, x.Position });
                    table.CheckConstraint("CK_canonical_release_group_artist_position", "\"Position\" >= 0");
                    table.ForeignKey(
                        name: "FK_canonical_release_group_artists_canonical_artists_CanonicalArtistId",
                        column: x => x.CanonicalArtistId,
                        principalTable: "canonical_artists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_canonical_release_group_artists_canonical_release_groups_CanonicalReleaseGroupId",
                        column: x => x.CanonicalReleaseGroupId,
                        principalTable: "canonical_release_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "canonical_releases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Barcode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CanonicalReleaseGroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CountryCode = table.Column<string>(type: "TEXT", maxLength: 2, nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Disambiguation = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsProvisional = table.Column<bool>(type: "INTEGER", nullable: false),
                    MusicBrainzReleaseId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ReleaseDate = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_releases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_canonical_releases_canonical_release_groups_CanonicalReleaseGroupId",
                        column: x => x.CanonicalReleaseGroupId,
                        principalTable: "canonical_release_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "canonical_release_tracks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalReleaseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    DurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    MediumPosition = table.Column<int>(type: "INTEGER", nullable: false),
                    MusicBrainzTrackId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    TrackPosition = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_canonical_release_tracks", x => x.Id);
                    table.CheckConstraint("CK_canonical_release_track_position", "\"MediumPosition\" > 0 AND \"TrackPosition\" > 0");
                    table.ForeignKey(
                        name: "FK_canonical_release_tracks_canonical_recordings_CanonicalRecordingId",
                        column: x => x.CanonicalRecordingId,
                        principalTable: "canonical_recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_canonical_release_tracks_canonical_releases_CanonicalReleaseId",
                        column: x => x.CanonicalReleaseId,
                        principalTable: "canonical_releases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recordings_Isrc",
                table: "canonical_recordings",
                column: "Isrc",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recordings_MusicBrainzRecordingId",
                table: "canonical_recordings",
                column: "MusicBrainzRecordingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_artists_MusicBrainzArtistId",
                table: "canonical_artists",
                column: "MusicBrainzArtistId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_artists_SortName_Id",
                table: "canonical_artists",
                columns: new[] { "SortName", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_catalog_aliases_EntityKind_CanonicalEntityId",
                table: "canonical_catalog_aliases",
                columns: new[] { "EntityKind", "CanonicalEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_catalog_aliases_Namespace_EntityKind_ExternalIdHash",
                table: "canonical_catalog_aliases",
                columns: new[] { "Namespace", "EntityKind", "ExternalIdHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_recording_artists_CanonicalArtistId_CanonicalRecordingId",
                table: "canonical_recording_artists",
                columns: new[] { "CanonicalArtistId", "CanonicalRecordingId" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_group_artists_CanonicalArtistId_CanonicalReleaseGroupId",
                table: "canonical_release_group_artists",
                columns: new[] { "CanonicalArtistId", "CanonicalReleaseGroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_groups_MusicBrainzReleaseGroupId",
                table: "canonical_release_groups",
                column: "MusicBrainzReleaseGroupId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_groups_Title_Id",
                table: "canonical_release_groups",
                columns: new[] { "Title", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_tracks_CanonicalRecordingId",
                table: "canonical_release_tracks",
                column: "CanonicalRecordingId");

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_tracks_CanonicalReleaseId_MediumPosition_TrackPosition",
                table: "canonical_release_tracks",
                columns: new[] { "CanonicalReleaseId", "MediumPosition", "TrackPosition" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_release_tracks_MusicBrainzTrackId",
                table: "canonical_release_tracks",
                column: "MusicBrainzTrackId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_canonical_releases_CanonicalReleaseGroupId_ReleaseDate",
                table: "canonical_releases",
                columns: new[] { "CanonicalReleaseGroupId", "ReleaseDate" });

            migrationBuilder.CreateIndex(
                name: "IX_canonical_releases_MusicBrainzReleaseId",
                table: "canonical_releases",
                column: "MusicBrainzReleaseId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_catalog_facts_EntityKind_CanonicalEntityId_FieldName",
                table: "catalog_facts",
                columns: new[] { "EntityKind", "CanonicalEntityId", "FieldName" });

            migrationBuilder.CreateIndex(
                name: "IX_catalog_facts_SourceId_RefreshAfter",
                table: "catalog_facts",
                columns: new[] { "SourceId", "RefreshAfter" });
        }
    }
}
