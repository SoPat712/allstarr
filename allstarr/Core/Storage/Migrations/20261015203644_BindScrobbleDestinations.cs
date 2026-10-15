using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class BindScrobbleDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "AttemptStartedAt",
                table: "playback_delivery_checkpoints",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DestinationAccountId",
                table: "playback_delivery_checkpoints",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "DestinationAccountRevision",
                table: "playback_delivery_checkpoints",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DestinationJson",
                table: "playback_delivery_checkpoints",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ReplaySafe",
                table: "playback_delivery_checkpoints",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "Revision",
                table: "playback_delivery_checkpoints",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "SendCount",
                table: "playback_delivery_checkpoints",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SignalPayloadJson",
                table: "playback_delivery_checkpoints",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubmissionJson",
                table: "playback_delivery_checkpoints",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var column in new[] { "AttemptStartedAt", "DestinationAccountId", "DestinationAccountRevision",
                "DestinationJson", "ReplaySafe", "Revision", "SendCount", "SignalPayloadJson", "SubmissionJson" })
                migrationBuilder.Sql($"ALTER TABLE playback_delivery_checkpoints DROP COLUMN \"{column}\";");
        }
    }
}
