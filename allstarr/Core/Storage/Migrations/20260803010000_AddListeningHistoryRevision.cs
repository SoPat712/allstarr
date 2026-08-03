using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260803010000_AddListeningHistoryRevision")]
public sealed class AddListeningHistoryRevision : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<long>(
            "Revision",
            "listening_events",
            "bigint",
            nullable: false,
            defaultValue: 1L);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn("Revision", "listening_events");
}
