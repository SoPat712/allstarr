using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddDownloadArtifactMediaFacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BitDepth",
                table: "provider_download_artifacts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Bitrate",
                table: "provider_download_artifacts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Channels",
                table: "provider_download_artifacts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Codec",
                table: "provider_download_artifacts",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Container",
                table: "provider_download_artifacts",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MimeType",
                table: "provider_download_artifacts",
                type: "TEXT",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SampleRate",
                table: "provider_download_artifacts",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE provider_download_artifacts
                    DROP COLUMN IF EXISTS "BitDepth",
                    DROP COLUMN IF EXISTS "Bitrate",
                    DROP COLUMN IF EXISTS "Channels",
                    DROP COLUMN IF EXISTS "Codec",
                    DROP COLUMN IF EXISTS "Container",
                    DROP COLUMN IF EXISTS "MimeType",
                    DROP COLUMN IF EXISTS "SampleRate";
                """);
        }
    }
}
