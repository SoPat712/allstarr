using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class PersonalLyricsOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_runtime_settings_personal_keys",
                table: "runtime_settings");

            migrationBuilder.AddCheckConstraint(
                name: "CK_runtime_settings_personal_keys",
                table: "runtime_settings",
                sql: "\"OwnerUserId\" IS NULL OR \"Key\" IN ('Library:ExplicitFilter', 'Playback:ShowExternalLabel', 'Playback:ShowExplicitLabel', 'Providers:LyricsOrder')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM runtime_settings WHERE OwnerUserId IS NOT NULL AND Key = 'Providers:LyricsOrder';");

            migrationBuilder.DropCheckConstraint(
                name: "CK_runtime_settings_personal_keys",
                table: "runtime_settings");

            migrationBuilder.AddCheckConstraint(
                name: "CK_runtime_settings_personal_keys",
                table: "runtime_settings",
                sql: "\"OwnerUserId\" IS NULL OR \"Key\" IN ('Library:ExplicitFilter', 'Playback:ShowExternalLabel', 'Playback:ShowExplicitLabel')");
        }
    }
}
