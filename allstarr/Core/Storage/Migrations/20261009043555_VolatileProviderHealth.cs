using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class VolatileProviderHealth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_circuits");

            migrationBuilder.DropTable(
                name: "provider_health_rollups");

            migrationBuilder.DropTable(
                name: "provider_health_samples");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_circuits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Capability = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                    OpenedAt = table.Column<long>(type: "INTEGER", nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RetryAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_circuits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_circuits_provider_accounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_health_rollups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Capability = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FailureCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastFailureCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LastState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    P50LatencyMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    P95LatencyMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Revision = table.Column<long>(type: "INTEGER", nullable: false),
                    SampleCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessCount = table.Column<int>(type: "INTEGER", nullable: false),
                    SuccessRate = table.Column<double>(type: "REAL", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    WindowEnd = table.Column<long>(type: "INTEGER", nullable: false),
                    WindowStart = table.Column<long>(type: "INTEGER", nullable: false)
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

            migrationBuilder.CreateTable(
                name: "provider_health_samples",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Capability = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FailureCode = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    LatencyMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
                    ObservedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ProviderAccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_health_samples", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provider_health_samples_provider_accounts_ProviderAccountId",
                        column: x => x.ProviderAccountId,
                        principalTable: "provider_accounts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provider_circuits_ProviderAccountId_Capability",
                table: "provider_circuits",
                columns: new[] { "ProviderAccountId", "Capability" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_rollup_account_capability_window",
                table: "provider_health_rollups",
                columns: new[] { "ProviderAccountId", "Capability", "WindowStart" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_rollup_window_end",
                table: "provider_health_rollups",
                column: "WindowEnd");

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_account_capability_observed",
                table: "provider_health_samples",
                columns: new[] { "ProviderAccountId", "Capability", "ObservedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provider_health_updates",
                table: "provider_health_samples",
                columns: new[] { "ObservedAt", "Id" });
        }
    }
}
