using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddLegacyEnvImportProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProvenanceJson",
                table: "legacy_env_imports",
                type: "text",
                nullable: false,
                defaultValue: """{"settings":[],"providerAccounts":[]}""");

            migrationBuilder.AddColumn<string>(
                name: "SchemaVersion",
                table: "legacy_env_imports",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "legacy-env-import-v1");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProvenanceJson",
                table: "legacy_env_imports");

            migrationBuilder.DropColumn(
                name: "SchemaVersion",
                table: "legacy_env_imports");
        }
    }
}
