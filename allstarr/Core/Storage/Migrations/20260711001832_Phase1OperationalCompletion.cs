using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Phase1OperationalCompletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var textType = "text";
            var guidType = "uuid";
            var integerType = "integer";
            var bigintType = "bigint";

            migrationBuilder.AddColumn<long>(
                name: "FailedAt",
                table: "outbox_messages",
                type: bigintType,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxAttempts",
                table: "outbox_messages",
                type: integerType,
                nullable: false,
                defaultValue: 20);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                table: "durable_jobs",
                type: textType,
                maxLength: 100,
                nullable: false,
                defaultValue: "migration-context");

            migrationBuilder.AddColumn<string>(
                name: "LibraryScopeId",
                table: "durable_jobs",
                type: textType,
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicySnapshotJson",
                table: "durable_jobs",
                type: textType,
                nullable: false,
                defaultValue: "{\"Version\":1,\"AuthorizationRule\":\"initiator_only\",\"ProviderId\":null,\"Capability\":null,\"ProviderAccountScope\":null}");

            migrationBuilder.AddColumn<Guid>(
                name: "ProviderAccountId",
                table: "durable_jobs",
                type: guidType,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderCapability",
                table: "durable_jobs",
                type: textType,
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequestFingerprint",
                table: "durable_jobs",
                type: textType,
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "RestoreStatus",
                table: "backups",
                type: textType,
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RestoreVerifiedAt",
                table: "backups",
                type: bigintType,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_ProviderAccountId",
                table: "durable_jobs",
                column: "ProviderAccountId");

            migrationBuilder.AddForeignKey(
                name: "FK_durable_jobs_provider_accounts_ProviderAccountId",
                table: "durable_jobs",
                column: "ProviderAccountId",
                principalTable: "provider_accounts",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_durable_jobs_provider_accounts_ProviderAccountId",
                table: "durable_jobs");

            migrationBuilder.DropIndex(
                name: "IX_durable_jobs_ProviderAccountId",
                table: "durable_jobs");

            migrationBuilder.DropColumn(
                name: "FailedAt",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "MaxAttempts",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                table: "durable_jobs");

            migrationBuilder.DropColumn(
                name: "LibraryScopeId",
                table: "durable_jobs");

            migrationBuilder.DropColumn(
                name: "PolicySnapshotJson",
                table: "durable_jobs");

            migrationBuilder.DropColumn(
                name: "ProviderAccountId",
                table: "durable_jobs");

            migrationBuilder.DropColumn(
                name: "ProviderCapability",
                table: "durable_jobs");

            migrationBuilder.DropColumn(
                name: "RequestFingerprint",
                table: "durable_jobs");

            migrationBuilder.DropColumn(
                name: "RestoreStatus",
                table: "backups");

            migrationBuilder.DropColumn(
                name: "RestoreVerifiedAt",
                table: "backups");
        }
    }
}
