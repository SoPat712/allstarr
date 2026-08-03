using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260803020000_AddListeningHistoryImports")]
public sealed class AddListeningHistoryImports : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "listening_history_imports",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                Protocol = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                BackendInstanceId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                LibraryScopeId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                DisplayFileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                Format = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                PreviewJson = table.Column<string>(type: "text", nullable: false),
                PreviewRevision = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: true),
                ApplyGeneration = table.Column<int>(type: "integer", nullable: false),
                NextSequence = table.Column<long>(type: "bigint", nullable: false),
                ImportedRows = table.Column<long>(type: "bigint", nullable: false),
                DuplicateRows = table.Column<long>(type: "bigint", nullable: false),
                ResolvedRows = table.Column<long>(type: "bigint", nullable: false),
                UnresolvedRows = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<long>(type: "bigint", nullable: false),
                UpdatedAt = table.Column<long>(type: "bigint", nullable: false),
                ExpiresAt = table.Column<long>(type: "bigint", nullable: false),
                CompletedAt = table.Column<long>(type: "bigint", nullable: true),
                Revision = table.Column<long>(type: "bigint", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_listening_history_imports", item => item.Id);
                table.CheckConstraint("CK_listening_history_import_counts", "\"NextSequence\" >= 0 AND \"ImportedRows\" >= 0 AND \"DuplicateRows\" >= 0 AND \"ResolvedRows\" >= 0 AND \"UnresolvedRows\" >= 0");
                table.CheckConstraint("CK_listening_history_import_size", "\"SizeBytes\" > 0");
                table.CheckConstraint("CK_listening_history_import_state", "\"State\" IN ('Previewed', 'Pending', 'Running', 'Completed', 'Cancelled', 'Failed', 'Expired')");
                table.ForeignKey("FK_listening_history_imports_tenants_TenantId",
                    item => item.TenantId, "tenants", "Id", onDelete: ReferentialAction.Restrict);
                table.ForeignKey("FK_listening_history_imports_users_TenantId_OwnerUserId",
                    item => new { item.TenantId, item.OwnerUserId }, "users", new[] { "TenantId", "Id" }, onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_listening_history_import_content", "listening_history_imports", new[] { "TenantId", "OwnerUserId", "ContentSha256" });
        migrationBuilder.CreateIndex("IX_listening_history_import_job", "listening_history_imports", "JobId");
        migrationBuilder.CreateIndex("IX_listening_history_import_scope", "listening_history_imports", new[] { "TenantId", "OwnerUserId", "Protocol", "BackendInstanceId", "LibraryScopeId", "CreatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable("listening_history_imports");
}
