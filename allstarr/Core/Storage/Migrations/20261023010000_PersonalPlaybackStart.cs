using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    public partial class PersonalPlaybackStart : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(name: "CK_runtime_settings_personal_keys", table: "runtime_settings");
            migrationBuilder.AddCheckConstraint(name: "CK_runtime_settings_personal_keys", table: "runtime_settings",
                sql: "\"OwnerUserId\" IS NULL OR \"Key\" IN ('Library:ExplicitFilter', 'Playback:ShowExternalLabel', 'Playback:ShowExplicitLabel', 'Playback:StartPreference', 'Providers:LyricsOrder', 'Providers:UseModes')");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE playback_preferences_downgrade_guard (
                    InUse INTEGER NOT NULL CONSTRAINT CK_playback_preferences_requires_backup_restore CHECK (InUse=0));
                INSERT INTO playback_preferences_downgrade_guard
                    SELECT EXISTS(SELECT 1 FROM runtime_settings WHERE "OwnerUserId" IS NOT NULL AND "Key" = 'Playback:StartPreference');
                DROP TABLE playback_preferences_downgrade_guard;
                """);
            migrationBuilder.DropCheckConstraint(name: "CK_runtime_settings_personal_keys", table: "runtime_settings");
            migrationBuilder.AddCheckConstraint(name: "CK_runtime_settings_personal_keys", table: "runtime_settings",
                sql: "\"OwnerUserId\" IS NULL OR \"Key\" IN ('Library:ExplicitFilter', 'Playback:ShowExternalLabel', 'Playback:ShowExplicitLabel', 'Providers:LyricsOrder', 'Providers:UseModes')");
        }
    }
}
