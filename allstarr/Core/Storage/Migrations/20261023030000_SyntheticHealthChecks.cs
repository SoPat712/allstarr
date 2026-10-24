using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    public partial class SyntheticHealthChecks : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(name: "IsSynthetic", table: "users", type: "INTEGER", nullable: false, defaultValue: false);
            migrationBuilder.CreateTable(name: "health_smoke_runs", columns: table => new
            {
                Id = table.Column<Guid>(type: "TEXT", nullable: false),
                OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                BindingJson = table.Column<string>(type: "TEXT", nullable: false),
                ArtifactsJson = table.Column<string>(type: "TEXT", nullable: false),
                CompletedJson = table.Column<string>(type: "TEXT", nullable: true),
                AlertStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                CompletedAt = table.Column<long>(type: "INTEGER", nullable: true)
            }, constraints: table =>
            {
                table.PrimaryKey("PK_health_smoke_runs", item => item.Id);
                table.ForeignKey(name: "FK_health_smoke_runs_users_OwnerUserId", column: item => item.OwnerUserId,
                    principalTable: "users", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            });
            migrationBuilder.CreateIndex(name: "IX_health_smoke_runs_CreatedAt", table: "health_smoke_runs", column: "CreatedAt");
            migrationBuilder.CreateIndex(name: "IX_health_smoke_runs_OwnerUserId", table: "health_smoke_runs", column: "OwnerUserId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE synthetic_listener_downgrade_guard (
                    InUse INTEGER NOT NULL CONSTRAINT CK_synthetic_listeners_require_backup_restore CHECK (InUse=0));
                INSERT INTO synthetic_listener_downgrade_guard
                    SELECT EXISTS(SELECT 1 FROM users WHERE "IsSynthetic" = 1)
                        OR EXISTS(SELECT 1 FROM job_schedules WHERE "JobType" = 'health.smoke');
                DROP TABLE synthetic_listener_downgrade_guard;
                """);
            migrationBuilder.DropTable(name: "health_smoke_runs");
            migrationBuilder.Sql("ALTER TABLE users DROP COLUMN IsSynthetic;");
        }
    }
}
