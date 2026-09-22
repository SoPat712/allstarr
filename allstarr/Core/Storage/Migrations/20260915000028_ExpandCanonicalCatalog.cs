using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public partial class ExpandCanonicalCatalog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>("Title", "canonical_recordings", "character varying(500)", maxLength: 500, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>("Disambiguation", "canonical_recordings", "character varying(500)", maxLength: 500, nullable: true);
        migrationBuilder.AddColumn<long>("DurationMilliseconds", "canonical_recordings", "bigint", nullable: true);
        migrationBuilder.AddColumn<bool>("IsExplicit", "canonical_recordings", "boolean", nullable: true);
        migrationBuilder.AddColumn<bool>("IsProvisional", "canonical_recordings", "boolean", nullable: false, defaultValue: false);
        migrationBuilder.CreateIndex(
            name: "IX_canonical_recordings_TenantId_Title_Id",
            table: "canonical_recordings",
            columns: new[] { "TenantId", "Title", "Id" });

        migrationBuilder.Sql(
            """
            CREATE TABLE "canonical_artists" (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "Name" character varying(500) NOT NULL,
                "SortName" character varying(500) NOT NULL,
                "Disambiguation" character varying(500),
                "MusicBrainzArtistId" character varying(100),
                "IsProvisional" boolean NOT NULL,
                "CreatedAt" bigint NOT NULL,
                "UpdatedAt" bigint NOT NULL,
                "Revision" bigint NOT NULL,
                CONSTRAINT "PK_canonical_artists" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_canonical_artists_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "FK_canonical_artists_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "tenants" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_canonical_artists_TenantId_MusicBrainzArtistId" ON "canonical_artists" ("TenantId", "MusicBrainzArtistId");
            CREATE INDEX "IX_canonical_artists_TenantId_SortName_Id" ON "canonical_artists" ("TenantId", "SortName", "Id");

            CREATE TABLE "canonical_release_groups" (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "Title" character varying(500) NOT NULL,
                "PrimaryType" character varying(100),
                "SecondaryTypesJson" jsonb NOT NULL,
                "FirstReleaseDate" character varying(10),
                "MusicBrainzReleaseGroupId" character varying(100),
                "IsProvisional" boolean NOT NULL,
                "CreatedAt" bigint NOT NULL,
                "UpdatedAt" bigint NOT NULL,
                "Revision" bigint NOT NULL,
                CONSTRAINT "PK_canonical_release_groups" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_canonical_release_groups_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "FK_canonical_release_groups_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "tenants" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_canonical_release_groups_TenantId_MusicBrainzReleaseGroupId" ON "canonical_release_groups" ("TenantId", "MusicBrainzReleaseGroupId");
            CREATE INDEX "IX_canonical_release_groups_TenantId_Title_Id" ON "canonical_release_groups" ("TenantId", "Title", "Id");

            CREATE TABLE "canonical_catalog_aliases" (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "EntityKind" character varying(32) NOT NULL,
                "CanonicalEntityId" uuid NOT NULL,
                "Namespace" character varying(100) NOT NULL,
                "ExternalId" character varying(500) NOT NULL,
                "ExternalIdHash" character varying(64) NOT NULL,
                "CreatedAt" bigint NOT NULL,
                "LastSeenAt" bigint NOT NULL,
                CONSTRAINT "PK_canonical_catalog_aliases" PRIMARY KEY ("Id"),
                CONSTRAINT "CK_canonical_catalog_alias_hash" CHECK (length("ExternalIdHash") = 64),
                CONSTRAINT "FK_canonical_catalog_aliases_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "tenants" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_canonical_catalog_aliases_TenantId_Namespace_EntityKind_ExternalIdHash" ON "canonical_catalog_aliases" ("TenantId", "Namespace", "EntityKind", "ExternalIdHash");
            CREATE INDEX "IX_canonical_catalog_aliases_TenantId_EntityKind_CanonicalEntityId" ON "canonical_catalog_aliases" ("TenantId", "EntityKind", "CanonicalEntityId");

            CREATE TABLE "catalog_facts" (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "EntityKind" character varying(32) NOT NULL,
                "CanonicalEntityId" uuid NOT NULL,
                "FieldName" character varying(100) NOT NULL,
                "ValueJson" jsonb NOT NULL,
                "SourceId" character varying(100) NOT NULL,
                "SourceRevision" character varying(500),
                "PayloadSha256" character varying(64) NOT NULL,
                "Confidence" double precision NOT NULL,
                "ObservedAt" bigint NOT NULL,
                "RefreshAfter" bigint,
                "SupersededAt" bigint,
                CONSTRAINT "PK_catalog_facts" PRIMARY KEY ("Id"),
                CONSTRAINT "CK_catalog_fact_confidence" CHECK ("Confidence" >= 0 AND "Confidence" <= 1),
                CONSTRAINT "CK_catalog_fact_payload_hash" CHECK (length("PayloadSha256") = 64),
                CONSTRAINT "FK_catalog_facts_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "tenants" ("Id") ON DELETE RESTRICT
            );
            CREATE INDEX "IX_catalog_facts_TenantId_EntityKind_CanonicalEntityId_FieldName" ON "catalog_facts" ("TenantId", "EntityKind", "CanonicalEntityId", "FieldName");
            CREATE INDEX "IX_catalog_facts_TenantId_SourceId_RefreshAfter" ON "catalog_facts" ("TenantId", "SourceId", "RefreshAfter");

            CREATE TABLE "canonical_releases" (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "CanonicalReleaseGroupId" uuid NOT NULL,
                "Title" character varying(500) NOT NULL,
                "Disambiguation" character varying(500),
                "Status" character varying(100),
                "CountryCode" character varying(2),
                "ReleaseDate" character varying(10),
                "Barcode" character varying(100),
                "MusicBrainzReleaseId" character varying(100),
                "IsProvisional" boolean NOT NULL,
                "CreatedAt" bigint NOT NULL,
                "UpdatedAt" bigint NOT NULL,
                "Revision" bigint NOT NULL,
                CONSTRAINT "PK_canonical_releases" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_canonical_releases_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "FK_canonical_releases_canonical_release_groups" FOREIGN KEY ("TenantId", "CanonicalReleaseGroupId") REFERENCES "canonical_release_groups" ("TenantId", "Id") ON DELETE CASCADE,
                CONSTRAINT "FK_canonical_releases_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "tenants" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_canonical_releases_TenantId_MusicBrainzReleaseId" ON "canonical_releases" ("TenantId", "MusicBrainzReleaseId");
            CREATE INDEX "IX_canonical_releases_TenantId_CanonicalReleaseGroupId_ReleaseDate" ON "canonical_releases" ("TenantId", "CanonicalReleaseGroupId", "ReleaseDate");

            CREATE TABLE "canonical_recording_artists" (
                "TenantId" uuid NOT NULL,
                "CanonicalRecordingId" uuid NOT NULL,
                "CanonicalArtistId" uuid NOT NULL,
                "Position" integer NOT NULL,
                "CreditName" character varying(500),
                "JoinPhrase" character varying(50) NOT NULL,
                CONSTRAINT "PK_canonical_recording_artists" PRIMARY KEY ("TenantId", "CanonicalRecordingId", "Position"),
                CONSTRAINT "CK_canonical_recording_artist_position" CHECK ("Position" >= 0),
                CONSTRAINT "FK_canonical_recording_artists_artist" FOREIGN KEY ("TenantId", "CanonicalArtistId") REFERENCES "canonical_artists" ("TenantId", "Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_canonical_recording_artists_recording" FOREIGN KEY ("TenantId", "CanonicalRecordingId") REFERENCES "canonical_recordings" ("TenantId", "Id") ON DELETE CASCADE
            );
            CREATE INDEX "IX_canonical_recording_artists_TenantId_CanonicalArtistId_CanonicalRecordingId" ON "canonical_recording_artists" ("TenantId", "CanonicalArtistId", "CanonicalRecordingId");

            CREATE TABLE "canonical_release_group_artists" (
                "TenantId" uuid NOT NULL,
                "CanonicalReleaseGroupId" uuid NOT NULL,
                "CanonicalArtistId" uuid NOT NULL,
                "Position" integer NOT NULL,
                "CreditName" character varying(500),
                "JoinPhrase" character varying(50) NOT NULL,
                CONSTRAINT "PK_canonical_release_group_artists" PRIMARY KEY ("TenantId", "CanonicalReleaseGroupId", "Position"),
                CONSTRAINT "CK_canonical_release_group_artist_position" CHECK ("Position" >= 0),
                CONSTRAINT "FK_canonical_release_group_artists_artist" FOREIGN KEY ("TenantId", "CanonicalArtistId") REFERENCES "canonical_artists" ("TenantId", "Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_canonical_release_group_artists_release_group" FOREIGN KEY ("TenantId", "CanonicalReleaseGroupId") REFERENCES "canonical_release_groups" ("TenantId", "Id") ON DELETE CASCADE
            );
            CREATE INDEX "IX_canonical_release_group_artists_TenantId_CanonicalArtistId_CanonicalReleaseGroupId" ON "canonical_release_group_artists" ("TenantId", "CanonicalArtistId", "CanonicalReleaseGroupId");

            CREATE TABLE "canonical_release_tracks" (
                "Id" uuid NOT NULL,
                "TenantId" uuid NOT NULL,
                "CanonicalReleaseId" uuid NOT NULL,
                "CanonicalRecordingId" uuid NOT NULL,
                "MediumPosition" integer NOT NULL,
                "TrackPosition" integer NOT NULL,
                "Title" character varying(500) NOT NULL,
                "DurationMilliseconds" bigint,
                "MusicBrainzTrackId" character varying(100),
                "CreatedAt" bigint NOT NULL,
                "UpdatedAt" bigint NOT NULL,
                "Revision" bigint NOT NULL,
                CONSTRAINT "PK_canonical_release_tracks" PRIMARY KEY ("Id"),
                CONSTRAINT "AK_canonical_release_tracks_TenantId_Id" UNIQUE ("TenantId", "Id"),
                CONSTRAINT "CK_canonical_release_track_position" CHECK ("MediumPosition" > 0 AND "TrackPosition" > 0),
                CONSTRAINT "FK_canonical_release_tracks_recording" FOREIGN KEY ("TenantId", "CanonicalRecordingId") REFERENCES "canonical_recordings" ("TenantId", "Id") ON DELETE RESTRICT,
                CONSTRAINT "FK_canonical_release_tracks_release" FOREIGN KEY ("TenantId", "CanonicalReleaseId") REFERENCES "canonical_releases" ("TenantId", "Id") ON DELETE CASCADE,
                CONSTRAINT "FK_canonical_release_tracks_tenants_TenantId" FOREIGN KEY ("TenantId") REFERENCES "tenants" ("Id") ON DELETE RESTRICT
            );
            CREATE UNIQUE INDEX "IX_canonical_release_tracks_TenantId_MusicBrainzTrackId" ON "canonical_release_tracks" ("TenantId", "MusicBrainzTrackId");
            CREATE UNIQUE INDEX "IX_canonical_release_tracks_TenantId_CanonicalReleaseId_MediumPosition_TrackPosition" ON "canonical_release_tracks" ("TenantId", "CanonicalReleaseId", "MediumPosition", "TrackPosition");
            CREATE INDEX "IX_canonical_release_tracks_TenantId_CanonicalRecordingId" ON "canonical_release_tracks" ("TenantId", "CanonicalRecordingId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "canonical_recording_artists");
        migrationBuilder.DropTable(name: "canonical_release_group_artists");
        migrationBuilder.DropTable(name: "canonical_release_tracks");
        migrationBuilder.DropTable(name: "canonical_catalog_aliases");
        migrationBuilder.DropTable(name: "catalog_facts");
        migrationBuilder.DropTable(name: "canonical_artists");
        migrationBuilder.DropTable(name: "canonical_releases");
        migrationBuilder.DropTable(name: "canonical_release_groups");
        migrationBuilder.DropIndex(name: "IX_canonical_recordings_TenantId_Title_Id", table: "canonical_recordings");
        migrationBuilder.DropColumn(name: "Title", table: "canonical_recordings");
        migrationBuilder.DropColumn(name: "Disambiguation", table: "canonical_recordings");
        migrationBuilder.DropColumn(name: "DurationMilliseconds", table: "canonical_recordings");
        migrationBuilder.DropColumn(name: "IsExplicit", table: "canonical_recordings");
        migrationBuilder.DropColumn(name: "IsProvisional", table: "canonical_recordings");
    }
}
