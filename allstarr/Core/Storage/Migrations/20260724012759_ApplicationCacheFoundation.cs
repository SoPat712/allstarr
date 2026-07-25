using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public partial class ApplicationCacheFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {

        migrationBuilder.CreateTable(
            name: "application_cache_entries",
            columns: table => new
            {
                Key = table.Column<string>(
                    type: "character varying(512)",
                    maxLength: 512,
                    nullable: false),
                Value = table.Column<string>(
                    type: "text",
                    nullable: false),
                PayloadBytes = table.Column<int>(
                    type: "integer",
                    nullable: false),
                CreatedAt = table.Column<long>(
                    type: "bigint",
                    nullable: false),
                UpdatedAt = table.Column<long>(
                    type: "bigint",
                    nullable: false),
                ExpiresAt = table.Column<long>(
                    type: "bigint",
                    nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_application_cache_entries", item => item.Key);
                table.CheckConstraint(
                    "CK_application_cache_payload_bytes",
                    "\"PayloadBytes\" >= 0 AND \"PayloadBytes\" <= 1048576");
            });

        migrationBuilder.CreateIndex(
            name: "IX_application_cache_expires_at",
            table: "application_cache_entries",
            column: "ExpiresAt");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "application_cache_entries");
    }
}
