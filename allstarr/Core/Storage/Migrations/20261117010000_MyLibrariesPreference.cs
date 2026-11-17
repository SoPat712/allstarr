using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20261117010000_MyLibrariesPreference")]
public sealed class MyLibrariesPreference : HeartActionPreference
{
    private const string PreviousConstraint = "\"OwnerUserId\" IS NULL OR \"Key\" IN ('Library:ExplicitFilter', 'Playback:ShowExternalLabel', 'Playback:ShowExplicitLabel', 'Playback:StartPreference', 'Providers:LyricsOrder', 'Providers:UseModes', 'Library:HeartAction')";
    private const string CurrentConstraint = "\"OwnerUserId\" IS NULL OR \"Key\" IN ('Library:ExplicitFilter', 'Playback:ShowExternalLabel', 'Playback:ShowExplicitLabel', 'Playback:StartPreference', 'Providers:LyricsOrder', 'Providers:UseModes', 'Library:HeartAction', 'Library:MyLibraries')";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("CK_runtime_settings_personal_keys", "runtime_settings");
        migrationBuilder.AddCheckConstraint("CK_runtime_settings_personal_keys", "runtime_settings", CurrentConstraint);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TEMP TABLE my_libraries_downgrade_guard (
                InUse INTEGER NOT NULL CONSTRAINT CK_my_libraries_requires_backup_restore CHECK(InUse=0));
            INSERT INTO my_libraries_downgrade_guard
                SELECT EXISTS(SELECT 1 FROM runtime_settings WHERE "OwnerUserId" IS NOT NULL AND "Key" = 'Library:MyLibraries');
            DROP TABLE my_libraries_downgrade_guard;
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
