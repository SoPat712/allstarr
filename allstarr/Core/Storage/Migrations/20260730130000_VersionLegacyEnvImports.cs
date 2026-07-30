using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260730130000_VersionLegacyEnvImports")]
public sealed class VersionLegacyEnvImports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_legacy_env_imports_TenantId_SourceSha256",
            table: "legacy_env_imports");
        migrationBuilder.CreateIndex(
            name: "IX_legacy_env_imports_TenantId_SourceSha256_SchemaVersion",
            table: "legacy_env_imports",
            columns: new[] { "TenantId", "SourceSha256", "SchemaVersion" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_legacy_env_imports_TenantId_SourceSha256_SchemaVersion",
            table: "legacy_env_imports");
        migrationBuilder.CreateIndex(
            name: "IX_legacy_env_imports_TenantId_SourceSha256",
            table: "legacy_env_imports",
            columns: new[] { "TenantId", "SourceSha256" },
            unique: true);
    }
}
