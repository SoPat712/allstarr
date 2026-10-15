using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RetainKeptRecordingDisplayMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DisplayMetadataJson",
                table: "recording_kept_routes",
                type: "TEXT",
                maxLength: 16000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Preserve the existing SQL ownership guards while removing only this column.
            migrationBuilder.Sql("ALTER TABLE recording_kept_routes DROP COLUMN DisplayMetadataJson;");
        }
    }
}
