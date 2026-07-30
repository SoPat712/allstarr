using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260730001000_RepairDownloadedSongMappingId")]
public sealed class RepairDownloadedSongMappingId : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(
        """
        DO $$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'downloaded_song_mappings'
                  AND column_name = 'id')
               AND NOT EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_schema = 'public'
                  AND table_name = 'downloaded_song_mappings'
                  AND column_name = 'Id')
            THEN
                ALTER TABLE downloaded_song_mappings RENAME COLUMN id TO "Id";
            END IF;
        END $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Compatibility repair: restoring the invalid legacy casing would break current binaries.
    }
}
