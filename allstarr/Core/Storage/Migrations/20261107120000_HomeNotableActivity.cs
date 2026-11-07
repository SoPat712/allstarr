using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace allstarr.Core.Storage.Migrations;

[DbContext(typeof(AllstarrDbContext))]
[Migration("20261107120000_HomeNotableActivity")]
public sealed class HomeNotableActivity : AddPromotionRequests
{
    private const string JobsFilter = "\"State\" = 'Failed'\nOR (\"State\" = 'RetryScheduled' AND NOT (coalesce(\"LastAttemptOutcome\", '') = 'deferred' AND \"LastErrorCode\" IS NULL\n    AND lower(\"Type\") IN ('listening-history.resolve', 'recording.tags.refresh', 'recording.musicbrainz.enrich',\n        'recording.odesli.enrich', 'listening.musicbrainz.enrich', 'favorite.display-backfill', 'health.cleanup',\n        'recording.kept-cleanup', 'mixes.refresh', 'lyrics.refresh')))\nOR (\"State\" IN ('Running', 'Succeeded', 'Cancelled') AND lower(\"Type\") IN (\n    'library.refresh', 'library.promote', 'health.smoke', 'health.alert', 'storage.backup',\n    'favorite.process', 'spotify-likes.import', 'favorite.link-union', 'collection.migrate',\n    'playlist.materialize', 'playlist.provider-source-update', 'playlist.retain-track',\n    'listening-history.import', 'recording.keep', 'downloads.quality-check',\n    'extension.session.verify', 'problem-alert.delivery', 'recommendation.generate',\n    'storage.verify-files', 'configuration.legacy-media', 'releases.poll', 'library.index'))";
    private const string AuditsFilter = "\"Category\" != 'playback-start' AND (\n    lower(\"Outcome\") LIKE '%fail%' OR lower(\"Outcome\") LIKE '%error%'\n    OR lower(\"Outcome\") LIKE '%reject%' OR lower(\"Outcome\") LIKE '%unhealthy%'\n    OR lower(\"Outcome\") LIKE '%warn%' OR lower(\"Outcome\") LIKE '%degrad%'\n    OR lower(\"Outcome\") LIKE '%conflict%' OR lower(\"Outcome\") LIKE '%retry%'\n    OR lower(\"Outcome\") LIKE '%ambiguous%' OR lower(\"Outcome\") LIKE '%partial%'\n    OR lower(\"Outcome\") IN ('unresolved', 'blocked', 'unauthorized')\n    OR (lower(\"Outcome\") NOT IN ('pending', 'waiting', 'deferred')\n        AND \"Category\" NOT IN ('job-progress', 'job-attempt')\n        AND \"Action\" NOT IN ('track-match.rematch', 'automatic.review', 'recording.odesli.enrich') AND lower(replace(replace(replace(replace(CASE WHEN lower(trim(\"Category\")) = 'background-task'\n    THEN coalesce(CASE WHEN json_valid(\"DetailsJson\") THEN json_extract(\"DetailsJson\", '$.name') END, 'Background maintenance')\n    ELSE \"Action\" END, '.', ''), '-', ''), '_', ''), ' ', '')) NOT GLOB '*backgroundtask*' AND lower(replace(replace(replace(replace(CASE WHEN lower(trim(\"Category\")) = 'background-task'\n    THEN coalesce(CASE WHEN json_valid(\"DetailsJson\") THEN json_extract(\"DetailsJson\", '$.name') END, 'Background maintenance')\n    ELSE \"Action\" END, '.', ''), '-', ''), '_', ''), ' ', '')) NOT GLOB '*runtimesettingssourceuse*' AND lower(replace(replace(replace(replace(CASE WHEN lower(trim(\"Category\")) = 'background-task'\n    THEN coalesce(CASE WHEN json_valid(\"DetailsJson\") THEN json_extract(\"DetailsJson\", '$.name') END, 'Background maintenance')\n    ELSE \"Action\" END, '.', ''), '-', ''), '_', ''), ' ', '')) NOT GLOB '*recordlisten*' AND lower(replace(replace(replace(replace(CASE WHEN lower(trim(\"Category\")) = 'background-task'\n    THEN coalesce(CASE WHEN json_valid(\"DetailsJson\") THEN json_extract(\"DetailsJson\", '$.name') END, 'Background maintenance')\n    ELSE \"Action\" END, '.', ''), '-', ''), '_', ''), ' ', '')) NOT GLOB '*recordalisten*' AND lower(replace(replace(replace(replace(CASE WHEN lower(trim(\"Category\")) = 'background-task'\n    THEN coalesce(CASE WHEN json_valid(\"DetailsJson\") THEN json_extract(\"DetailsJson\", '$.name') END, 'Background maintenance')\n    ELSE \"Action\" END, '.', ''), '-', ''), '_', ''), ' ', '')) NOT GLOB '*playbacksignal*' AND lower(replace(replace(replace(replace(CASE WHEN lower(trim(\"Category\")) = 'background-task'\n    THEN coalesce(CASE WHEN json_valid(\"DetailsJson\") THEN json_extract(\"DetailsJson\", '$.name') END, 'Background maintenance')\n    ELSE \"Action\" END, '.', ''), '-', ''), '_', ''), ' ', '')) NOT GLOB '*trackidentity*' AND lower(replace(replace(replace(replace(CASE WHEN lower(trim(\"Category\")) = 'background-task'\n    THEN coalesce(CASE WHEN json_valid(\"DetailsJson\") THEN json_extract(\"DetailsJson\", '$.name') END, 'Background maintenance')\n    ELSE \"Action\" END, '.', ''), '-', ''), '_', ''), ' ', '')) NOT GLOB '*canonicalrecording*' AND lower(replace(replace(replace(replace(CASE WHEN lower(trim(\"Category\")) = 'background-task'\n    THEN coalesce(CASE WHEN json_valid(\"DetailsJson\") THEN json_extract(\"DetailsJson\", '$.name') END, 'Background maintenance')\n    ELSE \"Action\" END, '.', ''), '-', ''), '_', ''), ' ', '')) NOT GLOB '*historyresolution*'))";

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex("IX_home_notable_jobs", "durable_jobs",
            ["OwnerUserId", "UpdatedAt", "Id"], filter: JobsFilter);
        migrationBuilder.CreateIndex("IX_home_notable_audits", "audit_events",
            ["ActorUserId", "CreatedAt", "Id"], filter: AuditsFilter);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_home_notable_jobs", "durable_jobs");
        migrationBuilder.DropIndex("IX_home_notable_audits", "audit_events");
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
        modelBuilder.Entity("allstarr.Core.Storage.DurableJobRecord")
            .HasIndex("OwnerUserId", "UpdatedAt", "Id")
            .HasDatabaseName("IX_home_notable_jobs").HasFilter(JobsFilter);
        modelBuilder.Entity("allstarr.Core.Storage.AuditEventRecord")
            .HasIndex("ActorUserId", "CreatedAt", "Id")
            .HasDatabaseName("IX_home_notable_audits").HasFilter(AuditsFilter);
    }
}
