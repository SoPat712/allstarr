using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class RecordingLyricsSourceChoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recording_lyrics_choices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: true),
                    SourceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    RevokedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recording_lyrics_choices", x => x.Id);
                    table.CheckConstraint("CK_recording_lyrics_choices_revision", "\"Revision\" > 0");
                    table.ForeignKey(
                        name: "FK_recording_lyrics_choices_owner",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_recording_lyrics_choices_recording",
                        column: x => x.CanonicalRecordingId,
                        principalTable: "canonical_recordings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recording_lyrics_choices_CanonicalRecordingId_RevokedAt",
                table: "recording_lyrics_choices",
                columns: new[] { "CanonicalRecordingId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_recording_lyrics_choices_household_active",
                table: "recording_lyrics_choices",
                column: "CanonicalRecordingId",
                unique: true,
                filter: "\"RevokedAt\" IS NULL AND \"OwnerUserId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_recording_lyrics_choices_OwnerUserId",
                table: "recording_lyrics_choices",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_recording_lyrics_choices_personal_active",
                table: "recording_lyrics_choices",
                columns: new[] { "CanonicalRecordingId", "OwnerUserId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL AND \"OwnerUserId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recording_lyrics_choices");
        }
    }
}
