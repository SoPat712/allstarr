using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260803030000_AllowDirectGeneratedSets")]
public sealed class AllowDirectGeneratedSets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            "FK_generated_sets_recommendation_runs_RunId_TenantId_OwnerUserId", "generated_sets");
        migrationBuilder.AlterColumn<Guid>(
            "RunId", "generated_sets", "uuid", nullable: true,
            oldClrType: typeof(Guid), oldType: "uuid");
        migrationBuilder.AddForeignKey(
            name: "FK_generated_sets_recommendation_runs_RunId_TenantId_OwnerUserId",
            table: "generated_sets", columns: new[] { "RunId", "TenantId", "OwnerUserId" },
            principalTable: "recommendation_runs", principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
            onDelete: ReferentialAction.Cascade);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM generated_set_entries
            WHERE "GeneratedSetId" IN (SELECT "Id" FROM generated_sets WHERE "RunId" IS NULL);
            DELETE FROM generated_sets WHERE "RunId" IS NULL;
            """);
        migrationBuilder.DropForeignKey(
            "FK_generated_sets_recommendation_runs_RunId_TenantId_OwnerUserId", "generated_sets");
        migrationBuilder.AlterColumn<Guid>(
            "RunId", "generated_sets", "uuid", nullable: false,
            oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
        migrationBuilder.AddForeignKey(
            name: "FK_generated_sets_recommendation_runs_RunId_TenantId_OwnerUserId",
            table: "generated_sets", columns: new[] { "RunId", "TenantId", "OwnerUserId" },
            principalTable: "recommendation_runs", principalColumns: new[] { "Id", "TenantId", "OwnerUserId" },
            onDelete: ReferentialAction.Cascade);
    }
}
