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
            migrationBuilder.DropColumn(
                name: "BitDepth",
                table: "provider_download_artifacts");

            migrationBuilder.DropColumn(
                name: "Bitrate",
                table: "provider_download_artifacts");

            migrationBuilder.DropColumn(
                name: "Channels",
                table: "provider_download_artifacts");

            migrationBuilder.DropColumn(
                name: "Codec",
                table: "provider_download_artifacts");

            migrationBuilder.DropColumn(
                name: "Container",
                table: "provider_download_artifacts");

            migrationBuilder.DropColumn(
                name: "MimeType",
                table: "provider_download_artifacts");

            migrationBuilder.DropColumn(
                name: "SampleRate",
                table: "provider_download_artifacts");
        }
    }
}
