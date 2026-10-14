using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ConfirmPlaylistMembershipRemoval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RemovalConfirmedAt",
                table: "playlist_target_memberships",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE playlist_target_memberships
                SET RemovalConfirmedAt = UpdatedAt
                WHERE Active = 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RemovalConfirmedAt",
                table: "playlist_target_memberships");
        }
    }
}
