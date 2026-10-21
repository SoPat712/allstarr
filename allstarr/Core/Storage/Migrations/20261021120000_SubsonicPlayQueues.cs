using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public partial class SubsonicPlayQueues : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "subsonic_play_queues",
            columns: table => new
            {
                BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                BackendPrincipalId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                PayloadJson = table.Column<string>(type: "TEXT", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_subsonic_play_queues",
                item => new { item.BackendInstanceId, item.BackendPrincipalId }));
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "subsonic_play_queues");
}
