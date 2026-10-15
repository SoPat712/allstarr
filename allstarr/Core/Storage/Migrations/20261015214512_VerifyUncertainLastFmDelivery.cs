using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class VerifyUncertainLastFmDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AutomaticResendCount",
                table: "playback_delivery_checkpoints",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE scrobble_verification_downgrade_guard (
                    InUse INTEGER NOT NULL CONSTRAINT CK_scrobble_resend_requires_backup_restore CHECK (InUse=0));
                INSERT INTO scrobble_verification_downgrade_guard
                    SELECT EXISTS(SELECT 1 FROM playback_delivery_checkpoints WHERE "AutomaticResendCount" > 0);
                DROP TABLE scrobble_verification_downgrade_guard;
                """);
            migrationBuilder.Sql("ALTER TABLE playback_delivery_checkpoints DROP COLUMN \"AutomaticResendCount\";");
        }
    }
}
