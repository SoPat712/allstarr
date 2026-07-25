using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderAccountCreatorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CreatedByUserId",
                table: "provider_accounts",
                type: "uuid",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "provider_accounts"
                SET "CreatedByUserId" = "OwnerUserId"
                WHERE "CreatedByUserId" IS NULL
                  AND "OwnerUserId" IS NOT NULL
                """);

            migrationBuilder.CreateIndex(
                name: "IX_provider_accounts_CreatedByUserId",
                table: "provider_accounts",
                column: "CreatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_provider_account_creator",
                table: "provider_accounts",
                column: "CreatedByUserId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_provider_account_creator",
                table: "provider_accounts");

            migrationBuilder.DropIndex(
                name: "IX_provider_accounts_CreatedByUserId",
                table: "provider_accounts");

            migrationBuilder.DropColumn(
                name: "CreatedByUserId",
                table: "provider_accounts");
        }
    }
}
