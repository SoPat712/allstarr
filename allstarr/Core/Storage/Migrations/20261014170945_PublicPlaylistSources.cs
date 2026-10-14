using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PublicPlaylistSources : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "ProviderAccountId",
                table: "playlist_source_snapshots",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProviderAccountId",
                table: "playlist_links",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProviderAccountId",
                table: "external_metadata_snapshots",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "TEXT");

            migrationBuilder.CreateIndex(
                name: "IX_playlist_link_public_source_target",
                table: "playlist_links",
                columns: new[] { "OwnerUserId", "SourceProviderId", "SourcePlaylistIdHash", "TargetProtocol", "TargetBackendInstanceId" },
                unique: true,
                filter: "\"ProviderAccountId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_external_snapshot_public_version",
                table: "external_metadata_snapshots",
                columns: new[] { "OwnerUserId", "ProviderId", "ResourceKind", "ExternalIdHash", "SnapshotVersion" },
                unique: true,
                filter: "\"ProviderAccountId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE public_playlist_source_downgrade_guard
                    (Allowed INTEGER NOT NULL CONSTRAINT CK_public_playlist_source_downgrade CHECK (Allowed = 1));
                INSERT INTO public_playlist_source_downgrade_guard
                    SELECT CASE WHEN EXISTS (SELECT 1 FROM playlist_links WHERE ProviderAccountId IS NULL)
                        OR EXISTS (SELECT 1 FROM playlist_source_snapshots WHERE ProviderAccountId IS NULL)
                        OR EXISTS (SELECT 1 FROM external_metadata_snapshots WHERE ProviderAccountId IS NULL)
                        THEN 0 ELSE 1 END;
                DROP TABLE public_playlist_source_downgrade_guard;
                """);

            migrationBuilder.DropIndex(
                name: "IX_playlist_link_public_source_target",
                table: "playlist_links");

            migrationBuilder.DropIndex(
                name: "IX_external_snapshot_public_version",
                table: "external_metadata_snapshots");

            migrationBuilder.AlterColumn<Guid>(
                name: "ProviderAccountId",
                table: "playlist_source_snapshots",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ProviderAccountId",
                table: "playlist_links",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ProviderAccountId",
                table: "external_metadata_snapshots",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
