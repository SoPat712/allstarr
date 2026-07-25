using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class JobFailureAndDeferralPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var integerType = "integer";
            migrationBuilder.AddColumn<int>(
                name: "DeferralCount",
                table: "durable_jobs",
                type: integerType,
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "FailureCount",
                table: "durable_jobs",
                type: integerType,
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MaxDeferrals",
                table: "durable_jobs",
                type: integerType,
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql(
                "UPDATE durable_jobs SET \"MaxDeferrals\" = 96 WHERE \"MaxDeferrals\" = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeferralCount",
                table: "durable_jobs");

            migrationBuilder.DropColumn(
                name: "FailureCount",
                table: "durable_jobs");

            migrationBuilder.DropColumn(
                name: "MaxDeferrals",
                table: "durable_jobs");
        }
    }
}
