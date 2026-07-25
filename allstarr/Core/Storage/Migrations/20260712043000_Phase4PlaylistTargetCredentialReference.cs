using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260712043000_Phase4PlaylistTargetCredentialReference")]
public partial class Phase4PlaylistTargetCredentialReference : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var guidType = "uuid";
        migrationBuilder.AddColumn<Guid>(
            name: "TargetCredentialReferenceId",
            table: "playlist_links",
            type: guidType,
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "TargetCredentialReferenceId",
            table: "playlist_links");
    }
}
