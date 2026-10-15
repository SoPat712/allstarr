using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class SupersedeDownloadArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_download_artifact_job_provider",
                table: "provider_download_artifacts");

            migrationBuilder.AddColumn<long>(
                name: "SupersededAt",
                table: "provider_download_artifacts",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_download_artifact_job_provider",
                table: "provider_download_artifacts",
                columns: new[] { "DurableJobId", "ProviderId" },
                unique: true,
                filter: "\"SupersededAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE download_artifact_downgrade_guard (
                    InUse INTEGER NOT NULL CONSTRAINT CK_download_history_requires_backup_restore CHECK (InUse=0));
                INSERT INTO download_artifact_downgrade_guard
                    SELECT EXISTS(SELECT 1 FROM provider_download_artifacts WHERE "SupersededAt" IS NOT NULL);
                DROP TABLE download_artifact_downgrade_guard;
                """);
            migrationBuilder.DropIndex(
                name: "IX_download_artifact_job_provider",
                table: "provider_download_artifacts");

            migrationBuilder.Sql("ALTER TABLE provider_download_artifacts DROP COLUMN \"SupersededAt\";");

            migrationBuilder.CreateIndex(
                name: "IX_download_artifact_job_provider",
                table: "provider_download_artifacts",
                columns: new[] { "DurableJobId", "ProviderId" },
                unique: true);
        }
    }
}
