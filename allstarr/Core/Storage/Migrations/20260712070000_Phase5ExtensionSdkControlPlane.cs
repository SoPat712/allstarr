using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260712070000_Phase5ExtensionSdkControlPlane")]
public sealed class Phase5ExtensionSdkControlPlane : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
        var text = postgres ? "text" : "TEXT";
        var guid = postgres ? "uuid" : "TEXT";
        var integer = postgres ? "integer" : "INTEGER";
        var bigint = postgres ? "bigint" : "INTEGER";
        var boolean = postgres ? "boolean" : "INTEGER";

        migrationBuilder.CreateTable(
            name: "extension_registries",
            columns: table => new
            {
                Id = table.Column<Guid>(type: guid, nullable: false),
                Name = table.Column<string>(type: text, maxLength: 200, nullable: false),
                RegistryUrl = table.Column<string>(type: text, maxLength: 1000, nullable: false),
                Enabled = table.Column<bool>(type: boolean, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
                Revision = table.Column<long>(type: bigint, nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_extension_registries", item => item.Id));

        migrationBuilder.CreateTable(
            name: "extension_packages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: guid, nullable: false),
                RegistryId = table.Column<Guid>(type: guid, nullable: true),
                PreviousPackageId = table.Column<Guid>(type: guid, nullable: true),
                ExtensionId = table.Column<string>(type: text, maxLength: 128, nullable: false),
                DisplayName = table.Column<string>(type: text, maxLength: 200, nullable: false),
                Version = table.Column<string>(type: text, maxLength: 100, nullable: false),
                SdkVersion = table.Column<string>(type: text, maxLength: 32, nullable: false),
                Sha256 = table.Column<string>(type: text, maxLength: 64, nullable: false),
                ContentSha256 = table.Column<string>(type: text, maxLength: 64, nullable: false),
                PackagePath = table.Column<string>(type: text, maxLength: 1000, nullable: false),
                ManifestJson = table.Column<string>(type: text, nullable: false),
                State = table.Column<string>(type: text, maxLength: 32, nullable: false),
                FailureCode = table.Column<string>(type: text, maxLength: 100, nullable: true),
                StagedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
                ReviewedAt = table.Column<DateTimeOffset>(type: bigint, nullable: true),
                ActivatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: true),
                DisabledAt = table.Column<DateTimeOffset>(type: bigint, nullable: true),
                Revision = table.Column<long>(type: bigint, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_extension_packages", item => item.Id);
                table.CheckConstraint("CK_extension_packages_sha256", "length(\"Sha256\") = 64");
                table.CheckConstraint("CK_extension_packages_content_hash", "length(\"ContentSha256\") = 64");
                table.ForeignKey("FK_extension_packages_extension_registries_RegistryId", item => item.RegistryId, "extension_registries", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_extension_packages_extension_packages_PreviousPackageId", item => item.PreviousPackageId, "extension_packages", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "extension_logs",
            columns: table => new
            {
                Id = table.Column<Guid>(type: guid, nullable: false),
                ExtensionPackageId = table.Column<Guid>(type: guid, nullable: false),
                ExtensionId = table.Column<string>(type: text, maxLength: 128, nullable: false),
                Level = table.Column<string>(type: text, maxLength: 20, nullable: false),
                EventCode = table.Column<string>(type: text, maxLength: 100, nullable: false),
                Message = table.Column<string>(type: text, maxLength: 2000, nullable: false),
                CorrelationId = table.Column<string>(type: text, maxLength: 100, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_extension_logs", item => item.Id);
                table.ForeignKey("FK_extension_logs_extension_packages_ExtensionPackageId", item => item.ExtensionPackageId, "extension_packages", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "extension_permission_reviews",
            columns: table => new
            {
                Id = table.Column<Guid>(type: guid, nullable: false),
                ExtensionPackageId = table.Column<Guid>(type: guid, nullable: false),
                PermissionKind = table.Column<string>(type: text, maxLength: 32, nullable: false),
                PermissionValue = table.Column<string>(type: text, maxLength: 1000, nullable: false),
                Required = table.Column<bool>(type: boolean, nullable: false),
                Decision = table.Column<string>(type: text, maxLength: 32, nullable: false),
                ReviewedByUserId = table.Column<Guid>(type: guid, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: bigint, nullable: false),
                ReviewedAt = table.Column<DateTimeOffset>(type: bigint, nullable: true),
                Revision = table.Column<long>(type: bigint, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_extension_permission_reviews", item => item.Id);
                table.ForeignKey("FK_extension_permission_review_package", item => item.ExtensionPackageId, "extension_packages", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_extension_permission_reviews_users_ReviewedByUserId", item => item.ReviewedByUserId, "users", "Id", onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_extension_registries_RegistryUrl", "extension_registries", "RegistryUrl", unique: true);
        migrationBuilder.CreateIndex("IX_extension_packages_RegistryId", "extension_packages", "RegistryId");
        migrationBuilder.CreateIndex("IX_extension_packages_PreviousPackageId", "extension_packages", "PreviousPackageId");
        migrationBuilder.CreateIndex("IX_extension_packages_ExtensionId_State", "extension_packages", new[] { "ExtensionId", "State" });
        migrationBuilder.CreateIndex("IX_extension_packages_ExtensionId_Version_Sha256", "extension_packages", new[] { "ExtensionId", "Version", "Sha256" });
        migrationBuilder.CreateIndex("IX_extension_logs_ExtensionPackageId", "extension_logs", "ExtensionPackageId");
        migrationBuilder.CreateIndex("IX_extension_logs_ExtensionId_CreatedAt", "extension_logs", new[] { "ExtensionId", "CreatedAt" });
        migrationBuilder.CreateIndex("IX_extension_permission_reviews_ReviewedByUserId", "extension_permission_reviews", "ReviewedByUserId");
        migrationBuilder.CreateIndex("IX_extension_permission_review_key", "extension_permission_reviews", new[] { "ExtensionPackageId", "PermissionKind", "PermissionValue" }, unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("extension_logs");
        migrationBuilder.DropTable("extension_permission_reviews");
        migrationBuilder.DropTable("extension_packages");
        migrationBuilder.DropTable("extension_registries");
    }
}
