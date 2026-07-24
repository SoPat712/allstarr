using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public partial class ApplicationCacheFoundation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);

        migrationBuilder.CreateTable(
            name: "application_cache_entries",
            columns: table => new
            {
                Key = table.Column<string>(
                    type: postgres ? "character varying(512)" : "TEXT",
                    maxLength: 512,
                    nullable: false),
                Value = table.Column<string>(
                    type: postgres ? "text" : "TEXT",
                    nullable: false),
                PayloadBytes = table.Column<int>(
                    type: postgres ? "integer" : "INTEGER",
                    nullable: false),
                CreatedAt = table.Column<long>(
                    type: postgres ? "bigint" : "INTEGER",
                    nullable: false),
                UpdatedAt = table.Column<long>(
                    type: postgres ? "bigint" : "INTEGER",
                    nullable: false),
                ExpiresAt = table.Column<long>(
                    type: postgres ? "bigint" : "INTEGER",
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
