using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations
{
    [DbContext(typeof(AllstarrDbContext))]
    [Migration("20260724012448_ProbeCacheSnapshot")]
    public partial class ProbeCacheSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name = 'playlist_links'
                          AND column_name = 'Enabled'
                          AND data_type = 'integer'
                    ) THEN
                        ALTER TABLE playlist_links
                            ALTER COLUMN "Enabled" DROP DEFAULT,
                            ALTER COLUMN "Enabled" TYPE boolean USING ("Enabled" <> 0),
                            ALTER COLUMN "Enabled" SET DEFAULT TRUE;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM information_schema.columns
                        WHERE table_schema = current_schema()
                          AND table_name = 'playlist_links'
                          AND column_name = 'Enabled'
                          AND data_type = 'boolean'
                    ) THEN
                        ALTER TABLE playlist_links
                            ALTER COLUMN "Enabled" DROP DEFAULT,
                            ALTER COLUMN "Enabled" TYPE integer USING (CASE WHEN "Enabled" THEN 1 ELSE 0 END),
                            ALTER COLUMN "Enabled" SET DEFAULT 1;
                    END IF;
                END $$;
                """);
        }
    }
}
