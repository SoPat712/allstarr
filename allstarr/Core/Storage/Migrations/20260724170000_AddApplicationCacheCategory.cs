using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260724170000_AddApplicationCacheCategory")]
public partial class AddApplicationCacheCategory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        var textType = "text";

        migrationBuilder.AddColumn<string>(
            name: "Category",
            table: "application_cache_entries",
            type: textType,
            maxLength: 50,
            nullable: false,
            defaultValue: "ProviderResponse");

        migrationBuilder.CreateIndex(
            name: "IX_application_cache_category_updated",
            table: "application_cache_entries",
            columns: new[] { "Category", "UpdatedAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_application_cache_category_updated",
            table: "application_cache_entries");

        migrationBuilder.DropColumn(
            name: "Category",
            table: "application_cache_entries");
    }
}
