using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class ProviderHealthRollups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
            var guidType = postgres ? "uuid" : "TEXT";
            var textType = postgres ? "text" : "TEXT";
            var integerType = postgres ? "integer" : "INTEGER";
            var bigintType = postgres ? "bigint" : "INTEGER";
            var realType = postgres ? "double precision" : "REAL";

            migrationBuilder.CreateTable(
                name: "provider_health_rollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guidType, nullable: false),
                    TenantId = table.Column<Guid>(type: guidType, nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: guidType, nullable: false),
                    Capability = table.Column<string>(type: textType, maxLength: 100, nullable: false),
                    WindowStart = table.Column<long>(type: bigintType, nullable: false),
                    WindowEnd = table.Column<long>(type: bigintType, nullable: false),
                    SampleCount = table.Column<int>(type: integerType, nullable: false),
                    SuccessCount = table.Column<int>(type: integerType, nullable: false),
                    FailureCount = table.Column<int>(type: integerType, nullable: false),
                    SuccessRate = table.Column<double>(type: realType, nullable: false),
                    P50LatencyMilliseconds = table.Column<long>(type: bigintType, nullable: true),
                    P95LatencyMilliseconds = table.Column<long>(type: bigintType, nullable: true),
                    LastState = table.Column<string>(type: textType, maxLength: 32, nullable: false),
                    LastFailureCode = table.Column<string>(type: textType, maxLength: 100, nullable: true),
                    UpdatedAt = table.Column<long>(type: bigintType, nullable: false),
                    Revision = table.Column<long>(type: bigintType, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_health_rollups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_health_rollups_provider_accounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_rollup_account_capability_window",
                table: "provider_health_rollups",
                columns: new[] { "ProviderAccountId", "Capability", "WindowStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_rollup_window_end",
                table: "provider_health_rollups",
                column: "WindowEnd");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_health_rollups");
        }
    }
}
