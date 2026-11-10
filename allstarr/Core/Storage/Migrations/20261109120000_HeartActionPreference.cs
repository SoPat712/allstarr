using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20261109120000_HeartActionPreference")]
public sealed class HeartActionPreference : PlaylistKinds
{
    private const string PreviousConstraint = "\"OwnerUserId\" IS NULL OR \"Key\" IN ('Library:ExplicitFilter', 'Playback:ShowExternalLabel', 'Playback:ShowExplicitLabel', 'Playback:StartPreference', 'Providers:LyricsOrder', 'Providers:UseModes')";
    private const string CurrentConstraint = "\"OwnerUserId\" IS NULL OR \"Key\" IN ('Library:ExplicitFilter', 'Playback:ShowExternalLabel', 'Playback:ShowExplicitLabel', 'Playback:StartPreference', 'Providers:LyricsOrder', 'Providers:UseModes', 'Library:HeartAction')";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("CK_runtime_settings_personal_keys", "runtime_settings");
        migrationBuilder.AddCheckConstraint("CK_runtime_settings_personal_keys", "runtime_settings", CurrentConstraint);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE heart_preference_downgrade_guard (
                InUse INTEGER NOT NULL CONSTRAINT CK_heart_preference_requires_backup_restore CHECK(InUse=0));
            INSERT INTO heart_preference_downgrade_guard
                SELECT EXISTS(SELECT 1 FROM runtime_settings WHERE "OwnerUserId" IS NOT NULL AND "Key" = 'Library:HeartAction');
            DROP TABLE heart_preference_downgrade_guard;
            """);
        migrationBuilder.DropCheckConstraint("CK_runtime_settings_personal_keys", "runtime_settings");
        migrationBuilder.AddCheckConstraint("CK_runtime_settings_personal_keys", "runtime_settings", PreviousConstraint);
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        modelBuilder.Entity("allstarr.Core.Settings.RuntimeSettingRecord", entity =>
            entity.ToTable("runtime_settings", table => table.HasCheckConstraint("CK_runtime_settings_personal_keys", CurrentConstraint)));
    }
}
