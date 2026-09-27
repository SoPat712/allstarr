using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    /// <inheritdoc />
    public partial class AddAdminOidcLinks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_oidc_links",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BackendIdentityId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecretReferenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_oidc_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_admin_oidc_links_backend_identities_BackendIdentityId",
                        column: x => x.BackendIdentityId,
                        principalTable: "backend_identities",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_admin_oidc_links_secret_references_SecretReferenceId",
                        column: x => x.SecretReferenceId,
                        principalTable: "secret_references",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_oidc_links_BackendIdentityId",
                table: "admin_oidc_links",
                column: "BackendIdentityId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admin_oidc_links_SecretReferenceId",
                table: "admin_oidc_links",
                column: "SecretReferenceId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_oidc_links");
        }
    }
}
