using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260725213000_RepairDownloadArtifactMediaFacts")]
public partial class RepairDownloadArtifactMediaFacts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // An early v3 build recorded AddDownloadArtifactMediaFacts after adding
        // one legacy MediaFacts column. Later builds expect the individual
        // searchable columns below. IF NOT EXISTS repairs those installations
        // while remaining a no-op for databases created from the corrected
        // migration sequence.
        migrationBuilder.Sql(
            """
            ALTER TABLE provider_download_artifacts
                ADD COLUMN IF NOT EXISTS "BitDepth" integer NULL,
                ADD COLUMN IF NOT EXISTS "Bitrate" integer NULL,
                ADD COLUMN IF NOT EXISTS "Channels" integer NULL,
                ADD COLUMN IF NOT EXISTS "Codec" text NULL,
                ADD COLUMN IF NOT EXISTS "Container" text NULL,
                ADD COLUMN IF NOT EXISTS "MimeType" text NULL,
                ADD COLUMN IF NOT EXISTS "SampleRate" integer NULL;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
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
