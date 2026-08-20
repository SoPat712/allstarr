using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20260820173000_RepairProviderAccountCreatorIdentity")]
public sealed class RepairProviderAccountCreatorIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $repair$
            BEGIN
                IF EXISTS (
                    SELECT 1
                      FROM information_schema.columns
                     WHERE table_schema = 'public'
                       AND table_name = 'provider_accounts'
                       AND column_name = 'CreatedByUserId'
                       AND data_type = 'text') THEN
                    ALTER TABLE provider_accounts
                        ALTER COLUMN "CreatedByUserId" TYPE uuid
                        USING "CreatedByUserId"::uuid;
                END IF;
            END
            $repair$;

            CREATE INDEX IF NOT EXISTS "IX_provider_accounts_CreatedByUserId"
                ON provider_accounts ("CreatedByUserId");

            DO $repair$
            BEGIN
                IF NOT EXISTS (
                    SELECT 1
                      FROM pg_constraint
                     WHERE conrelid = 'provider_accounts'::regclass
                       AND conname = 'FK_provider_account_creator') THEN
                    ALTER TABLE provider_accounts
                        ADD CONSTRAINT "FK_provider_account_creator"
                        FOREIGN KEY ("CreatedByUserId") REFERENCES users ("Id")
                        ON DELETE SET NULL;
                END IF;
            END
            $repair$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The repaired UUID shape is the model expected by every supported build. Keeping it
        // also makes rollback and reapplication safe for databases that never had the drift.
    }
}
