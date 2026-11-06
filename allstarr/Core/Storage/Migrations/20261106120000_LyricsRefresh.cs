using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20261106120000_LyricsRefresh")]
public sealed class LyricsRefresh : AddPromotionRequests
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("lyrics_refresh_availability", table => new
        {
            Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
            PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
            Revision = table.Column<long>(type: "INTEGER", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_lyrics_refresh_availability", item => item.Key));
        migrationBuilder.CreateTable("recording_lyrics_refreshes", table => new
        {
            OwnerUserId = table.Column<Guid>(type: "TEXT", nullable: false),
            CanonicalRecordingId = table.Column<Guid>(type: "TEXT", nullable: false),
            SourceId = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
            PayloadJson = table.Column<string>(type: "TEXT", nullable: false),
            PreviousJson = table.Column<string>(type: "TEXT", nullable: true),
            Reason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
            Score = table.Column<double>(type: "REAL", nullable: false),
            TargetDurationMilliseconds = table.Column<long>(type: "INTEGER", nullable: true),
            WordSynced = table.Column<bool>(type: "INTEGER", nullable: false),
            Misses = table.Column<int>(type: "INTEGER", nullable: false),
            CheckedAt = table.Column<long>(type: "INTEGER", nullable: true),
            NextCheckAt = table.Column<long>(type: "INTEGER", nullable: false),
            Revision = table.Column<long>(type: "INTEGER", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_recording_lyrics_refreshes", item => new { item.OwnerUserId, item.CanonicalRecordingId });
            table.ForeignKey("FK_recording_lyrics_refreshes_users_OwnerUserId", item => item.OwnerUserId, "users", "Id", onDelete: ReferentialAction.Cascade);
            table.ForeignKey("FK_recording_lyrics_refreshes_canonical_recordings_CanonicalRecordingId", item => item.CanonicalRecordingId, "canonical_recordings", "Id", onDelete: ReferentialAction.Cascade);
        });
        migrationBuilder.CreateIndex("IX_recording_lyrics_refreshes_CanonicalRecordingId", "recording_lyrics_refreshes", "CanonicalRecordingId");
        migrationBuilder.CreateIndex("IX_recording_lyrics_refreshes_NextCheckAt", "recording_lyrics_refreshes", "NextCheckAt");
        UpdateScheduleGuards(migrationBuilder, true);
        migrationBuilder.Sql("CREATE UNIQUE INDEX IX_household_lyrics_schedule ON job_schedules(\"JobType\") WHERE \"JobType\"='lyrics.refresh' AND \"OwnerUserId\" IS NULL;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DELETE FROM job_schedules WHERE \"JobType\"='lyrics.refresh'; DROP INDEX IX_household_lyrics_schedule;");
        UpdateScheduleGuards(migrationBuilder, false);
        migrationBuilder.DropTable("recording_lyrics_refreshes");
        migrationBuilder.DropTable("lyrics_refresh_availability");
    }

    private static void UpdateScheduleGuards(MigrationBuilder migrationBuilder, bool lyrics)
    {
        var types = lyrics ? "'storage.backup','lyrics.refresh'" : "'storage.backup'";
        migrationBuilder.Sql($"""
            DROP TRIGGER TR_backup_schedule_owner_insert;
            DROP TRIGGER TR_backup_schedule_owner_update;
            CREATE TRIGGER TR_backup_schedule_owner_insert BEFORE INSERT ON job_schedules
            WHEN (NEW."JobType" IN ({types}) AND NEW."OwnerUserId" IS NOT NULL)
                OR (NEW."JobType" NOT IN ({types}) AND NEW."OwnerUserId" IS NULL)
            BEGIN SELECT RAISE(ABORT,'schedule_owner_invalid'); END;
            CREATE TRIGGER TR_backup_schedule_owner_update BEFORE UPDATE OF "OwnerUserId","JobType" ON job_schedules
            WHEN (NEW."JobType" IN ({types}) AND NEW."OwnerUserId" IS NOT NULL)
                OR (NEW."JobType" NOT IN ({types}) AND NEW."OwnerUserId" IS NULL)
            BEGIN SELECT RAISE(ABORT,'schedule_owner_invalid'); END;
            """);
    }

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        base.BuildTargetModel(modelBuilder);
        modelBuilder.Entity("allstarr.Core.Storage.LyricsRefreshAvailabilityRecord", b =>
            {
                b.Property<string>("Key")
                    .HasMaxLength(100)
                    .HasColumnType("TEXT");

                b.Property<string>("PayloadJson")
                    .IsRequired()
                    .HasColumnType("TEXT");

                b.Property<long>("Revision")
                    .IsConcurrencyToken()
                    .HasColumnType("INTEGER");

                b.HasKey("Key");

                b.ToTable("lyrics_refresh_availability");
            });

        modelBuilder.Entity("allstarr.Core.Storage.RecordingLyricsRefreshRecord", b =>
            {
                b.Property<Guid>("OwnerUserId")
                    .HasColumnType("TEXT");

                b.Property<Guid>("CanonicalRecordingId")
                    .HasColumnType("TEXT");

                b.Property<string>("SourceId")
                    .IsRequired()
                    .HasMaxLength(100)
                    .HasColumnType("TEXT");

                b.Property<string>("PayloadJson")
                    .IsRequired()
                    .HasColumnType("TEXT");

                b.Property<string>("PreviousJson")
                    .HasColumnType("TEXT");

                b.Property<string>("Reason")
                    .IsRequired()
                    .HasMaxLength(500)
                    .HasColumnType("TEXT");

                b.Property<long?>("TargetDurationMilliseconds")
                    .HasColumnType("INTEGER");

                b.Property<double>("Score")
                    .HasColumnType("REAL");

                b.Property<bool>("WordSynced")
                    .HasColumnType("INTEGER");

                b.Property<int>("Misses")
                    .HasColumnType("INTEGER");

                b.Property<long?>("CheckedAt")
                    .HasColumnType("INTEGER");

                b.Property<long>("NextCheckAt")
                    .HasColumnType("INTEGER");

                b.Property<long>("Revision")
                    .IsConcurrencyToken()
                    .HasColumnType("INTEGER");

                b.HasKey("OwnerUserId", "CanonicalRecordingId");

                b.HasIndex("CanonicalRecordingId");

                b.HasIndex("NextCheckAt");

                b.ToTable("recording_lyrics_refreshes");
            });

        modelBuilder.Entity("allstarr.Core.Storage.RecordingLyricsRefreshRecord", b =>
            {
                b.HasOne("allstarr.Core.Storage.UserRecord", null)
                    .WithMany()
                    .HasForeignKey("OwnerUserId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();

                b.HasOne("allstarr.Core.Storage.CanonicalRecordingRecord", null)
                    .WithMany()
                    .HasForeignKey("CanonicalRecordingId")
                    .OnDelete(DeleteBehavior.Cascade)
                    .IsRequired();
            });
    }
}
