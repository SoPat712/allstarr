using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

public partial class ProviderUserData : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "ResumePositionTicks", table: "listening_events",
            type: "INTEGER", nullable: true);
        migrationBuilder.CreateTable(
            name: "provider_user_data",
            columns: table => new
            {
                OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                Protocol = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                BackendInstanceId = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                ItemId = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Rating = table.Column<int>(type: "INTEGER", nullable: true),
                Played = table.Column<bool>(type: "INTEGER", nullable: true),
                HistoryResetAt = table.Column<long>(type: "INTEGER", nullable: true),
                PlayedAt = table.Column<long>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_provider_user_data", item =>
                    new { item.OwnerUserId, item.Protocol, item.BackendInstanceId, item.ItemId });
                table.CheckConstraint("CK_provider_user_data_rating", "\"Rating\" IS NULL OR (\"Rating\" >= 0 AND \"Rating\" <= 5)");
                table.ForeignKey("FK_provider_user_data_users_OwnerUserId", item => item.OwnerUserId,
                    principalTable: "users", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "provider_user_data");
        migrationBuilder.DropColumn(name: "ResumePositionTicks", table: "listening_events");
    }
}
