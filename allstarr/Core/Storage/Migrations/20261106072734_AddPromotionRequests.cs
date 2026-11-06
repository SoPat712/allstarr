using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTrustedListener",
                table: "users",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "promotion_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    RequesterUserId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RecordingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    IdentityKey = table.Column<string>(type: "TEXT", nullable: false),
                    ReleaseIdentity = table.Column<string>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", nullable: false),
                    Year = table.Column<int>(type: "INTEGER", nullable: true),
                    SongCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CoverReference = table.Column<string>(type: "TEXT", nullable: true),
                    Unit = table.Column<string>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", nullable: false),
                    DecidedByUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DecidedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    DeclineNote = table.Column<string>(type: "TEXT", nullable: true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: false),
                    SongsJson = table.Column<string>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_promotion_requests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_promotion_requests_promotion_runs_RunId",
                        column: x => x.RunId,
                        principalTable: "promotion_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_promotion_requests_users_DecidedByUserId",
                        column: x => x.DecidedByUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_promotion_requests_users_RequesterUserId",
                        column: x => x.RequesterUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_promotion_requests_DecidedByUserId",
                table: "promotion_requests",
                column: "DecidedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_requests_RequesterUserId_IdentityKey",
                table: "promotion_requests",
                columns: new[] { "RequesterUserId", "IdentityKey" },
                unique: true,
                filter: "\"State\" IN ('pending', 'approved')");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_requests_RunId",
                table: "promotion_requests",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_promotion_requests_State",
                table: "promotion_requests",
                column: "State");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE promotion_request_downgrade_guard (
                    InUse INTEGER NOT NULL CONSTRAINT CK_promotion_requests_require_backup_restore CHECK(InUse=0));
                INSERT INTO promotion_request_downgrade_guard
                    SELECT EXISTS(SELECT 1 FROM promotion_requests) OR EXISTS(SELECT 1 FROM users WHERE IsTrustedListener = 1);
                DROP TABLE promotion_request_downgrade_guard;
                """);
            migrationBuilder.DropTable(
                name: "promotion_requests");

            migrationBuilder.Sql("ALTER TABLE users DROP COLUMN IsTrustedListener;");
        }
    }
}
