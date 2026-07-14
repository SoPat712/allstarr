using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class Phase2ADurableRuntimeSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            var postgres = ActiveProvider.Contains("Npgsql", StringComparison.OrdinalIgnoreCase);
            var text = postgres ? "text" : "TEXT";
            var guid = postgres ? "uuid" : "TEXT";
            var bigint = postgres ? "bigint" : "INTEGER";

            migrationBuilder.CreateTable(
                name: "tenant_runtime_settings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: guid, nullable: false),
                    TenantId = table.Column<Guid>(type: guid, nullable: false),
                    Key = table.Column<string>(type: text, maxLength: 200, nullable: false),
                    ValueType = table.Column<string>(type: text, maxLength: 32, nullable: false),
                    ValueJson = table.Column<string>(type: text, maxLength: 4096, nullable: false),
                    Source = table.Column<string>(type: text, maxLength: 100, nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: guid, nullable: true),
                    CreatedAt = table.Column<long>(type: bigint, nullable: false),
                    UpdatedAt = table.Column<long>(type: bigint, nullable: false),
                    Revision = table.Column<long>(type: bigint, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_runtime_settings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tenant_runtime_settings_tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_tenant_runtime_settings_users_TenantId_UpdatedByUserId",
                        columns: x => new { x.TenantId, x.UpdatedByUserId },
                        principalTable: "users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_runtime_settings_TenantId_Key",
                table: "tenant_runtime_settings",
                columns: new[] { "TenantId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_runtime_settings_TenantId_UpdatedByUserId",
                table: "tenant_runtime_settings",
                columns: new[] { "TenantId", "UpdatedByUserId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant_runtime_settings");
        }
    }
}
