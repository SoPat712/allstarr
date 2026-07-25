using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

/// <summary>
/// Aligns EF migration metadata with the PostgreSQL-only model. The previous snapshot
/// retained SQLite store types even though deployed databases were already PostgreSQL,
/// so the scaffolded conversion operations were intentionally omitted.
/// </summary>
public partial class AlignPostgresModelSnapshot : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
