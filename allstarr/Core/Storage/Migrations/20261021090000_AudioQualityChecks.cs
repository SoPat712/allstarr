using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public partial class AudioQualityChecks : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "QualityCheckJson",
            table: "provider_download_artifacts",
            type: "TEXT",
            maxLength: 4000,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("ALTER TABLE provider_download_artifacts DROP COLUMN \"QualityCheckJson\";");
    }
}
